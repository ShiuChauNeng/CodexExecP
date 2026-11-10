using CodexClient.Exec;

namespace CodexClient.Workflows;

public sealed record WorkflowStepResult(string Name, ExecutionResult Execution)
{
    /// <summary>
    /// 取得步驟名稱。
    /// </summary>
    public string Name { get; init; } = Name;

    /// <summary>
    /// 取得 Codex 執行結果。
    /// </summary>
    public ExecutionResult Execution { get; init; } = Execution;
}
