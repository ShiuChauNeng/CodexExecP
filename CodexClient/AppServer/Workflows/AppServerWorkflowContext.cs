namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 提供 Workflow 步驟建立提示時使用的執行內容。
/// </summary>
/// <param name="Steps">已完成的步驟結果。</param>
/// <param name="Threads">已建立的 thread 對應表。</param>
public sealed record AppServerWorkflowContext(IReadOnlyList<AppServerWorkflowStepResult> Steps, IReadOnlyDictionary<string, string> Threads)
{
    /// <summary>取得指定步驟最近一次執行的結果；Steps 保留所有執行歷程。</summary>
    public AppServerWorkflowStepResult GetStep(string name)
        => Steps.LastOrDefault(step => step.Name == name) ?? throw new KeyNotFoundException($"Step '{name}' has not completed.");
}
