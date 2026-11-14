namespace CodexClient.AppServer.Workflows;

public sealed record AppServerWorkflowContext(
    IReadOnlyList<AppServerWorkflowStepResult> Steps,
    IReadOnlyDictionary<string, string> Threads)
{
    public AppServerWorkflowStepResult GetStep(string name)
        => Steps.FirstOrDefault(step => step.Name == name)
            ?? throw new KeyNotFoundException($"Step '{name}' has not completed.");
}
