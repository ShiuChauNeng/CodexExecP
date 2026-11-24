namespace CodexClient.AppServer.Workflows;

/// <summary>
/// Succeeded 表示流程正常走到結尾，且各已執行驗收步驟的最後一次結果均通過。
/// Steps 保留重跑前驗收不通過的結果，不包含跳過的步驟。
/// </summary>
public sealed record AppServerWorkflowResult(
    bool Succeeded,
    string? FailedStep,
    IReadOnlyList<AppServerWorkflowStepResult> Steps,
    IReadOnlyDictionary<string, string> Threads)
{
    /// <summary>
    /// 流程控制或步驟錯誤；因 StopOnIssues、無法完成驗收或結尾驗收檢查停止時可為 null。
    /// </summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// 流程停止的原因；成功完成時為 null，不依賴 Error 是否存在。
    /// </summary>
    public string? StopReason { get; init; }

    /// <summary>
    /// 停止原因的顯示分類；不影響 Succeeded 或 Error，未提供時由 Reporter 依 Error 選色。
    /// </summary>
    public AppServerWorkflowMessageKind? StopMessageKind { get; init; }

    /// <summary>
    /// 本次執行已確認的需求決策快照，包含重跑或失敗步驟中已確認的紀錄。
    /// </summary>
    public IReadOnlyList<AppServerWorkflowDecision> Decisions { get; init; } = [];
}
