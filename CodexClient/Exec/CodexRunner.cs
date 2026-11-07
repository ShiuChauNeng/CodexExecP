using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using CodexClient.Exec.Internal;

namespace CodexClient.Exec;

public sealed class CodexRunner
{
    // Fields
    private readonly string _codexPath;
    private readonly ILogger _logger;


    // Constructors
    public CodexRunner(ILogger logger)
    {
        _codexPath = ResolveCodexPath();
        _logger = logger;
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

        using var process = new Process
        {
            StartInfo = startInfo
        };

        cancellationToken.ThrowIfCancellationRequested();

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Codex.");
        }

        Task errorTask = ReadErrorsAsync(process.StandardError);

        using var observer = new ExecutionObserver(_logger);
        var events = new List<ExecutionEvent>();

        try
        {
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
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

            await process.WaitForExitAsync(cancellationToken);
            await errorTask;

            ExecutionResult result = ExecutionOutcomeEvaluator.Evaluate(process.ExitCode, events);
            observer.Complete(result.Outcome, result.ExitCode);
            return result;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await errorTask;
            throw;
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
    /// 非同步讀取並輸出 Codex 的錯誤輸出內容。
    /// </summary>
    /// <param name="reader">Codex 錯誤輸出資料流讀取器。</param>
    /// <returns>表示讀取作業的非同步工作。</returns>
    private static async Task ReadErrorsAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Console.Error.WriteLine(line);
        }
    }


    //private static ExecutionOutcome GetExecutionOutcome(int processExitCode, IReadOnlyList<ExecutionEvent> events)
    //{
    //    if (processExitCode != 0)
    //    {
    //        return ExecutionOutcome.ProcessFailed;
    //    }

    //    if (events.Any(x => x.Type == "turn.failed"))
    //    {
    //        return ExecutionOutcome.TurnFailed;
    //    }

    //    if (!events.Any(x => x.Type == "turn.completed"))
    //    {
    //        return ExecutionOutcome.Incomplete;
    //    }

    //    bool hasToolFailures = events.Any(x => x.Type == "item.completed" && x.ItemType == "command_execution" && x.ExitCode is int exitCode && exitCode != 0);

    //    return hasToolFailures ? ExecutionOutcome.CompletedWithToolFailures : ExecutionOutcome.Completed;
    //}

}