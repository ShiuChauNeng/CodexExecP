namespace CodexClient.Exec;

/// <summary>
/// 表示一次 Codex 執行的完整結果。
/// </summary>
/// <param name="SessionId">Codex 工作階段識別碼。</param>
/// <param name="FinalResponse">Codex 最後回覆的文字。</param>
/// <param name="Outcome">執行結果分類。</param>
/// <param name="ExitCode">Codex 程序的結束代碼。</param>
/// <param name="Events">此次執行收到的事件集合。</param>
/// <param name="Failures">此次執行記錄的失敗集合。</param>
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