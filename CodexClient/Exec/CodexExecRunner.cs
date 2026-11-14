using CodexClient.Exec.Internal;
using CodexClient.Internal;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace CodexClient.Exec;

/// <summary>
/// 啟動並管理 Codex 命令列程序的執行器。
/// </summary>
public sealed class CodexExecRunner
{
    // Fields
    private readonly string _codexPath;
    private readonly ILogger _logger;


    // Constructors
    public CodexExecRunner(ILogger logger, string? executablePath = null)
    {
        _logger = logger;
        _codexPath = CodexExecutableResolver.Resolve(executablePath);
    }


    // Methods
    /// <summary>
    /// 在指定工作區執行 Codex。
    /// </summary>
    /// <param name="workspaceRoot">Codex 執行時使用的工作區路徑。</param>
    /// <param name="prompt">要傳送給 Codex 的提示文字。</param>
    /// <param name="options">Codex 執行選項。</param>
    /// <param name="cancellationToken">用於取消執行的權杖。</param>
    /// <returns>非同步回傳 Codex 執行結果。</returns>
    public async Task<ExecutionResult> ExecuteAsync(string workspaceRoot, string prompt, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        options ??= new ExecutionOptions();
        options.Validate();

        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException(workspaceRoot);
        }

        var startInfo = CodexProcessStartInfoFactory.Create(_codexPath, workspaceRoot);
        ConfigureArguments(startInfo, options);

        using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Timeout is { } timeout)
        {
            executionSource.CancelAfter(timeout);
        }

        CancellationToken executionToken = executionSource.Token;
        using var process = new Process
        {
            StartInfo = startInfo
        };
        using var observer = new ExecutionObserver(_logger);

        var events = new List<ExecutionEvent>();
        bool started = false;
        Task<string>? errorTask = null;
        try
        {
            executionToken.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started)
            {
                throw new InvalidOperationException("Unable to start Codex.");
            }

            errorTask = ReadStandardErrorAsync(process.StandardError, executionToken);
            await process.StandardInput.WriteAsync(prompt.AsMemory(), executionToken);
            await process.StandardInput.FlushAsync(executionToken);
            process.StandardInput.Close();

            while (true)
            {
                executionToken.ThrowIfCancellationRequested();

                string? line = await process.StandardOutput.ReadLineAsync(executionToken);
                if (line is null)
                {
                    break;
                }

                ExecutionEvent executionEvent = EventParser.Parse(line);
                events.Add(executionEvent);
                observer.Record(executionEvent);

                Console.WriteLine($"Event: {executionEvent.Type}");
                if (executionEvent.ItemType == "command_execution")
                {
                    Console.WriteLine($"Command: {executionEvent.Command}");
                    if (executionEvent.ExitCode is int exitCode)
                    {
                        Console.WriteLine($"ExitCode: {exitCode}");
                    }
                }
            }

            await process.WaitForExitAsync(executionToken);
            string? standardError = null;
            if (errorTask is not null)
            {
                standardError = await errorTask;
            }

            executionToken.ThrowIfCancellationRequested();
            ExecutionResult result = ExecutionOutcomeEvaluator.Evaluate(process.ExitCode, events, standardError);
            observer.Complete(result.Outcome, result.ExitCode);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            observer.Interrupt(timedOut: false);
            throw new OperationCanceledException("Codex execution was cancelled.", cancellationToken);
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            observer.Interrupt(timedOut: true);
            throw new TimeoutException($"Codex execution exceeded the timeout of {options.Timeout}.");
        }
        finally
        {
            if (started)
            {
                await StopProcessAsync(process);
                if (errorTask is not null)
                {
                    try
                    {
                        await errorTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Codex stderr reader stopped during cleanup.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 依執行選項設定 Codex 程序參數。
    /// </summary>
    /// <param name="startInfo">要設定的程序啟動資訊。</param>
    /// <param name="options">Codex 執行選項。</param>
    private static void ConfigureArguments(ProcessStartInfo startInfo, ExecutionOptions options)
    {
        var arguments = startInfo.ArgumentList;

        arguments.Add("exec");
        arguments.Add("--json");

        if (options.SkipGitRepoCheck)
        {
            arguments.Add("--skip-git-repo-check");
        }

        arguments.Add("--sandbox");
        arguments.Add(options.Sandbox switch
        {
            CodexSandboxMode.ReadOnly => "read-only",
            CodexSandboxMode.WorkspaceWrite => "workspace-write",
            _ => throw new ArgumentOutOfRangeException(nameof(options.Sandbox))
        });

        if (options.NetworkAccess is { } networkAccess)
        {
            arguments.Add("-c");
            arguments.Add($"sandbox_workspace_write.network_access={(networkAccess ? "true" : "false")}");
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            arguments.Add("--model");
            arguments.Add(options.Model);
        }

        if (options.ReasoningEffort is { } effort)
        {
            string value = effort switch
            {
                CodexReasoningEffort.Minimal => "minimal",
                CodexReasoningEffort.Low => "low",
                CodexReasoningEffort.Medium => "medium",
                CodexReasoningEffort.High => "high",
                CodexReasoningEffort.XHigh => "xhigh",
                _ => throw new ArgumentOutOfRangeException(nameof(options.ReasoningEffort))
            };

            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort=\"{value}\"");
        }

        if (options.SessionId is not null)
        {
            if (options.OutputSchemaPath is not null)
            {
                throw new NotSupportedException("OutputSchemaPath is not supported by this Resume configuration.");
            }

            arguments.Add("resume");
            arguments.Add(options.SessionId);
        }
        else if (options.OutputSchemaPath is not null)
        {
            arguments.Add("--output-schema");
            arguments.Add(Path.GetFullPath(options.OutputSchemaPath));
        }

        arguments.Add("-");
    }

    /// <summary>
    /// 非同步讀取並記錄 Codex 的標準錯誤輸出，保留最後 16 Ki 個字元。
    /// </summary>
    /// <param name="reader">Codex 標準錯誤輸出的讀取器。</param>
    /// <param name="cancellationToken">用於取消讀取作業的權杖。</param>
    /// <returns>保留的標準錯誤輸出；截斷時包含提示文字。</returns>
    private async Task<string> ReadStandardErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maxCharacters = 16 * 1024;
        var standardError = new StringBuilder();
        bool truncated = false;
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                break;
            }

            _logger.LogDebug("Codex stderr: {Message}", line);
            truncated |= AppendBoundedLine(standardError, line, maxCharacters);
        }

        return truncated
            ? $"[stderr truncated; showing last {maxCharacters} characters]{Environment.NewLine}{standardError}"
            : standardError.ToString();
    }

    /// <summary>
    /// 附加一行文字，超過長度上限時移除開頭內容以保留尾端文字。
    /// </summary>
    /// <param name="buffer">要附加文字的緩衝區。</param>
    /// <param name="line">要附加的文字。</param>
    /// <param name="maxCharacters">保留的字元數上限。</param>
    /// <returns>是否因超過長度上限而截斷內容。</returns>
    private static bool AppendBoundedLine(StringBuilder buffer, string line, int maxCharacters)
    {
        buffer.AppendLine(line);
        if (buffer.Length <= maxCharacters)
        {
            return false;
        }

        buffer.Remove(0, buffer.Length - maxCharacters);
        return true;
    }

    /// <summary>
    /// 嘗試停止指定的 Codex 程序及其子程序。
    /// </summary>
    /// <param name="process">要停止的程序。</param>
    /// <returns>表示停止作業的非同步工作。</returns>
    private async Task StopProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // Process 已在 Kill 前自行結束。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to terminate Codex process {ProcessId}.", process.Id);
        }
    }
}
