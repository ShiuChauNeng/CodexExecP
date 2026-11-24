namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 僅供顯示的訊息分類，不參與流程狀態、驗收或重試判定。
/// </summary>
public enum AppServerWorkflowMessageKind
{
    /// <summary>
    /// 一般資訊。
    /// </summary>
    Information,
    /// <summary>
    /// 執行或等待中的提示。
    /// </summary>
    Waiting,
    /// <summary>
    /// 需要使用者回答。
    /// </summary>
    Question,
    /// <summary>
    /// 已成功完成。
    /// </summary>
    Success,
    /// <summary>
    /// 未解決問題、需修正、補查或其他提醒。
    /// </summary>
    Warning,
    /// <summary>
    /// 執行或流程控制錯誤。
    /// </summary>
    Error
}

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
    /// 選填的顯示分類；未指定時由 Console 依原有 Status 選色，不影響狀態與心跳。
    /// </summary>
    public AppServerWorkflowMessageKind? MessageKind { get; init; }

    /// <summary>
    /// 只有執行中的階段提供定期提醒與同一行刷新，等待回答及結束狀態不包含在內。
    /// </summary>
    internal bool SupportsHeartbeat => Status is AppServerWorkflowStatus.Starting or AppServerWorkflowStatus.CreatingThread
        or AppServerWorkflowStatus.Running or AppServerWorkflowStatus.Validating;
}
