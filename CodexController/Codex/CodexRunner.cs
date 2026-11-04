using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace CodexController.Codex;

public sealed record CodexResult(string? SessionId, string? FinalResponse, bool Completed, int ExitCode, IReadOnlyList<CodexExecutionEvent> Events);

public sealed class CodexRunner
{
    // Fields
    private readonly string _codexPath;
    private readonly ILogger _logger;


    // Constructors
    public CodexRunner(string codexPath, ILogger logger)
    {
        _codexPath = codexPath;
        _logger = logger;
    }


    // Methods
    public async Task<CodexResult> ExecuteAsync(string workspaceRoot, string prompt, CodexExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new CodexExecutionOptions();

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

        using var observer = new CodexExecutionObserver(_logger);
        var events = new List<CodexExecutionEvent>();

        try
        {
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(
                    cancellationToken);

                if (line is null)
                {
                    break;
                }

                CodexExecutionEvent executionEvent = CodexEventParser.Parse(line);

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

            observer.Complete(process.ExitCode);
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

        string? finalResponse = events
            .Where(x => x.Type == "item.completed" && x.ItemType == "agent_message")
            .Select(x => x.Text)
            .LastOrDefault();

        return new CodexResult(observer.SessionId, finalResponse, observer.State == CodexExecutionState.Completed, process.ExitCode, events.AsReadOnly());
    }

    private static void ConfigureArguments(ProcessStartInfo startInfo, CodexExecutionOptions options)
    {
        var arguments = startInfo.ArgumentList;

        arguments.Add("exec");
        arguments.Add("--json");

        if (options.SkipGitRepoCheck)
        {
            arguments.Add("--skip-git-repo-check");
        }

        arguments.Add("--sandbox");
        arguments.Add(options.Sandbox);

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            arguments.Add("--model");
            arguments.Add(options.Model);
        }

        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
        {
            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort=\"{options.ReasoningEffort}\"");
        }

        if (options.OutputSchemaPath is not null)
        {
            string schemaPath = Path.GetFullPath(options.OutputSchemaPath);

            if (!File.Exists(schemaPath))
            {
                throw new FileNotFoundException("Codex output schema not found.", schemaPath);
            }

            arguments.Add("--output-schema");
            arguments.Add(schemaPath);
        }

        arguments.Add("-");
    }

    private static async Task ReadErrorsAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Console.Error.WriteLine(line);
        }
    }
}