using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using CodexClient.Exec.Internal;

namespace CodexClient.Exec;

/// <summary>
/// 啟動並管理 Codex 命令列程序的執行器。
/// </summary>
public sealed class CodexRunner
{
    // Fields
    private readonly string _codexPath;
    private readonly ILogger _logger;


    // Constructors
    public CodexRunner(ILogger logger, string? executablePath = null)
    {
        _logger = logger;
        _codexPath = executablePath ?? ResolveCodexPath();
        if (!File.Exists(_codexPath))
        {
            throw new FileNotFoundException("Codex executable not found.", _codexPath);
        }
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

        var startInfo = new ProcessStartInfo
        {
            FileName = _codexPath,
            WorkingDirectory = workspaceRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
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
        Task? errorTask = null;
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
            if (errorTask is not null)
            {
                await errorTask;
            }

            executionToken.ThrowIfCancellationRequested();
            ExecutionResult result = ExecutionOutcomeEvaluator.Evaluate(process.ExitCode, events);
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
    /// 尋找最近更新的 Codex 可執行檔案。
    /// </summary>
    /// <returns>Codex 可執行檔案的完整路徑。</returns>
    private static string ResolveCodexPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string binPath = Path.Combine(localAppData, "OpenAI", "Codex", "bin");

        if (!Directory.Exists(binPath))
        {
            throw new DirectoryNotFoundException(binPath);
        }

        string? codexPath = Directory.EnumerateFiles(binPath, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

        return codexPath ?? throw new FileNotFoundException("Codex executable not found.", binPath);
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
            CodexSandboxMode.DangerFullAccess => "danger-full-access",
            _ => throw new ArgumentOutOfRangeException(nameof(options.Sandbox))
        });

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
    /// 非同步讀取並記錄 Codex 的標準錯誤輸出。
    /// </summary>
    /// <param name="reader">Codex 標準錯誤輸出的讀取器。</param>
    /// <param name="cancellationToken">用於取消讀取作業的權杖。</param>
    /// <returns>表示讀取作業的非同步工作。</returns>
    private async Task ReadStandardErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                break;
            }

            _logger.LogDebug("Codex stderr: {Message}", line);
        }
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