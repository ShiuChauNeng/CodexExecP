namespace CodexClient.AppServer.Workflows;

public enum AppServerWorkflowStatus
{
    Starting,
    CreatingThread,
    Running,
    WaitingForInput,
    Validating,
    StepCompleted,
    StepFailed,
    Stopped,
    Completed,
    Cancelled,
    Faulted
}

/// <summary>本機流程的狀態；定期通知表示仍在等待，不代表遠端 AI 一定有進展。</summary>
public sealed record AppServerWorkflowProgress(
    DateTimeOffset Timestamp,
    AppServerWorkflowStatus Status,
    string? StepName,
    string? ThreadId,
    string Message,
    TimeSpan Elapsed,
    bool IsHeartbeat = false);
