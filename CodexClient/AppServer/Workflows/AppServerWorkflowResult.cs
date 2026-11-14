namespace CodexClient.AppServer.Workflows;

/// <summary>Succeeded 表示所有步驟通過各自的接受條件；StopOnIssues=false 時允許帶 issues 完成。</summary>
public sealed record AppServerWorkflowResult(
    bool Succeeded,
    string? FailedStep,
    IReadOnlyList<AppServerWorkflowStepResult> Steps,
    IReadOnlyDictionary<string, string> Threads);
