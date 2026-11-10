namespace CodexClient.Workflows;

public sealed record WorkflowResult(bool Succeeded, string? FailedStep, IReadOnlyList<WorkflowStepResult> Steps)
{
    /// <summary>
    /// 取得工作流程是否成功完成。
    /// </summary>
    public bool Succeeded { get; init; } = Succeeded;

    /// <summary>
    /// 取得失敗或停止執行的步驟名稱。
    /// </summary>
    public string? FailedStep { get; init; } = FailedStep;

    /// <summary>
    /// 取得所有已執行步驟的結果。
    /// </summary>
    public IReadOnlyList<WorkflowStepResult> Steps { get; init; } = Steps;
}
