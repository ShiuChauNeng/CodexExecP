namespace CodexClient.Exec;


public sealed record ExecutionResult(string? SessionId, string? FinalResponse, ExecutionOutcome Outcome,
    int ExitCode, IReadOnlyList<ExecutionEvent> Events, IReadOnlyList<ExecutionFailure> Failures)
{
    /// <summary>
    /// Codex Turn 是否完成
    /// </summary>
    public bool Completed => Outcome is ExecutionOutcome.Completed or ExecutionOutcome.CompletedWithIssues;

    /// <summary>
    /// 過程中是否記錄到失敗或錯誤
    /// </summary>
    public bool HasIssues => Failures.Count > 0;

    /// <summary>
    /// 執行層級是否正常結束且未觀測到問題
    /// </summary>
    public bool Succeeded => Outcome == ExecutionOutcome.Completed;
}