namespace CodexClient.AppServer.Workflows;

public sealed record AppServerWorkflowContext(
    IReadOnlyList<AppServerWorkflowStepResult> Steps,
    IReadOnlyDictionary<string, string> Threads)
{
    /// <summary>取得指定步驟最近一次執行的結果；Steps 保留所有執行歷程。</summary>
    public AppServerWorkflowStepResult GetStep(string name)
        => Steps.LastOrDefault(step => step.Name == name)
            ?? throw new KeyNotFoundException($"Step '{name}' has not completed.");
}
