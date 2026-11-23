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
    Faulted,
    NeedsRevision
}

/// <summary>
/// 本機流程的狀態；定期通知表示仍在等待，不代表遠端 AI 一定有進展。
/// </summary>
public sealed record AppServerWorkflowProgress(
    DateTimeOffset Timestamp,
    AppServerWorkflowStatus Status,
    string? StepName,
    string? ThreadId,
    string Message,
    TimeSpan Elapsed,
    bool IsHeartbeat = false)
{
    /// <summary>
    /// 步驟結束時的本次總耗時；其他進度為 null，與流程累計 Elapsed 分開。
    /// </summary>
    public TimeSpan? StepDuration { get; init; }

    /// <summary>
    /// 只有執行中的階段提供定期提醒與同一行刷新，等待回答及結束狀態不包含在內。
    /// </summary>
    internal bool SupportsHeartbeat => Status is AppServerWorkflowStatus.Starting or AppServerWorkflowStatus.CreatingThread
        or AppServerWorkflowStatus.Running or AppServerWorkflowStatus.Validating;
}
