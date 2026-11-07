using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CodexClient.Exec.Internal;

internal sealed class ExecutionObserver : IDisposable
{
    // Fields
    public const string ActivitySourceName = "CodexClient.Exec";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Activity? _activity;
    private readonly ILogger _logger;
    private readonly IDisposable? _logScope;
    private bool _turnCompleted;
    private bool _turnFailed;


    // Properties
    /// <summary>
    /// 取得此次執行的唯一識別碼。
    /// </summary>
    public Guid ExecutionId { get; } = Guid.NewGuid();

    /// <summary>
    /// 取得 Codex 工作階段識別碼。
    /// </summary>
    public string? SessionId { get; private set; }

    /// <summary>
    /// 取得目前的執行狀態。
    /// </summary>
    public ExecutionState State { get; private set; } = ExecutionState.Created;


    // Constructors
    public ExecutionObserver(ILogger logger)
    {
        _logger = logger;
        _logScope = logger.BeginScope("ExecutionId: {ExecutionId}", ExecutionId);
        _activity = ActivitySource.StartActivity("Codex Execution");
        _activity?.SetTag("codex.execution_id", ExecutionId.ToString());
    }


    // Methods
    /// <summary>
    /// 記錄一個 Codex 執行事件並更新執行狀態。
    /// </summary>
    /// <param name="executionEvent">要記錄的執行事件。</param>
    public void Record(ExecutionEvent executionEvent)
    {
        if (State is ExecutionState.Completed or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.Incomplete)
        {
            throw new InvalidOperationException("Execution has already ended.");
        }

        switch (executionEvent.Type)
        {
            case "thread.started":
                SessionId = executionEvent.ThreadId;
                _activity?.SetTag("codex.session_id", SessionId);
                _logger.LogInformation("Codex thread started: {SessionId}", SessionId);
                break;

            case "turn.started":
                State = ExecutionState.Running;
                _logger.LogInformation("Codex turn started");
                break;

            case "item.started":
                RecordItemStarted(executionEvent);
                break;

            case "item.completed":
                RecordItemCompleted(executionEvent);
                break;

            case "turn.completed":
                _turnCompleted = true;
                _logger.LogInformation("Codex turn completed");
                break;

            case "turn.failed":
                _turnFailed = true;
                _logger.LogError("Codex turn failed");
                break;

            case "error":
                _logger.LogWarning("Codex reported an error event");
                break;
        }
    }

    /// <summary>
    /// 依事件與程序結束代碼完成此次執行。
    /// </summary>
    /// <param name="exitCode">Codex 程序的結束代碼。</param>
    public void Complete(ExecutionOutcome outcome, int exitCode)
    {
        State = outcome switch
        {
            ExecutionOutcome.Completed or
            ExecutionOutcome.CompletedWithIssues => ExecutionState.Completed,

            ExecutionOutcome.Incomplete => ExecutionState.Incomplete,

            _ => ExecutionState.Failed
        };

        _activity?.SetTag("codex.execution.outcome", outcome.ToString());
        _activity?.SetTag("codex.process.exit_code", exitCode);

        if (State == ExecutionState.Failed)
        {
            _activity?.SetStatus(ActivityStatusCode.Error);
        }

        _logger.LogInformation("Codex execution finished. Outcome: {Outcome}, ExitCode: {ExitCode}", outcome, exitCode);

        Dispose();
    }

    /// <summary>
    /// 記錄命令項目開始執行的事件。
    /// </summary>
    /// <param name="executionEvent">項目開始事件。</param>
    private void RecordItemStarted(ExecutionEvent executionEvent)
    {
        if (executionEvent.ItemType != "command_execution")
        {
            return;
        }

        _logger.LogInformation("Command started: {ItemId}, {Command}", executionEvent.ItemId, executionEvent.Command);
        _activity?.AddEvent(new ActivityEvent("codex.command.started"));
    }

    /// <summary>
    /// 記錄命令項目完成執行的事件。
    /// </summary>
    /// <param name="executionEvent">項目完成事件。</param>
    private void RecordItemCompleted(ExecutionEvent executionEvent)
    {
        if (executionEvent.ItemType != "command_execution")
        {
            return;
        }

        _logger.LogInformation("Command completed: {ItemId}, ExitCode: {ExitCode}", executionEvent.ItemId, executionEvent.ExitCode);
        _activity?.AddEvent(new ActivityEvent("codex.command.completed"));
    }

    /// <summary>
    /// 釋放執行觀察器使用的追蹤與記錄資源。
    /// </summary>
    public void Dispose()
    {
        _activity?.Dispose();
        _logScope?.Dispose();
    }
}