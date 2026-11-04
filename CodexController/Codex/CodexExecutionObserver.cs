using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CodexController.Codex;

public class CodexExecutionObserver : IDisposable
{
    // Fields
    private static readonly ActivitySource ActivitySource = new("CodexController");
    private readonly Activity? _activity;
    private readonly ILogger _logger;
    private readonly IDisposable? _logScope;
    private bool _turnCompleted;
    private bool _turnFailed;


    // Properties
    public Guid ExecutionId { get; } = Guid.NewGuid();
    public string? SessionId { get; private set; }
    public CodexExecutionState State { get; private set; } = CodexExecutionState.Created;


    // Constructors
    public CodexExecutionObserver(ILogger logger)
    {
        _logger = logger;
        _logScope = logger.BeginScope("ExecutionId: {ExecutionId}", ExecutionId);
        _activity = ActivitySource.StartActivity("Codex Execution");
        _activity?.SetTag("codex.execution_id", ExecutionId.ToString());
    }


    // Methods
    public void Record(CodexExecutionEvent executionEvent)
    {
        if (State is CodexExecutionState.Completed or CodexExecutionState.Failed or CodexExecutionState.Cancelled or CodexExecutionState.Incomplete)
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
                State = CodexExecutionState.Running;
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

    public void Complete(int exitCode)
    {
        if (_turnFailed || exitCode != 0)
        {
            State = CodexExecutionState.Failed;
        }
        else if (_turnCompleted)
        {
            State = CodexExecutionState.Completed;
        }
        else
        {
            State = CodexExecutionState.Incomplete;
        }

        _activity?.SetTag("codex.execution_state", State.ToString());

        if (State != CodexExecutionState.Completed)
        {
            _activity?.SetStatus(ActivityStatusCode.Error, State.ToString());
        }

        _logger.LogInformation("Codex execution ended: {State}, ExitCode: {ExitCode}", State, exitCode);

        Dispose();
    }

    private void RecordItemStarted(CodexExecutionEvent executionEvent)
    {
        if (executionEvent.ItemType != "command_execution")
        {
            return;
        }

        _logger.LogInformation("Command started: {ItemId}, {Command}", executionEvent.ItemId, executionEvent.Command);
        _activity?.AddEvent(new ActivityEvent("codex.command.started"));
    }

    private void RecordItemCompleted(CodexExecutionEvent executionEvent)
    {
        if (executionEvent.ItemType != "command_execution")
        {
            return;
        }

        _logger.LogInformation("Command completed: {ItemId}, ExitCode: {ExitCode}", executionEvent.ItemId, executionEvent.ExitCode);
        _activity?.AddEvent(new ActivityEvent("codex.command.completed"));
    }

    public void Dispose()
    {
        _activity?.Dispose();
        _logScope?.Dispose();
    }
}