namespace CodexClient.AppServer.Workflows;

/// <summary>Succeeded 表示流程正常走到結尾；Steps 保留重跑前驗收不通過的結果，不包含跳過的步驟。</summary>
public sealed record AppServerWorkflowResult(
    bool Succeeded,
    string? FailedStep,
    IReadOnlyList<AppServerWorkflowStepResult> Steps,
    IReadOnlyDictionary<string, string> Threads)
{
    /// <summary>流程控制或步驟錯誤；因 StopOnIssues 或無法完成驗收而停止時可為 null。</summary>
    public Exception? Error { get; init; }
}
