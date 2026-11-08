namespace CodexClient.Exec;

/// <summary>
/// 表示 Codex 執行完成時的結果分類。
/// </summary>
public enum ExecutionOutcome
{
    /// <summary>執行成功完成且沒有問題。</summary>
    Completed,
    /// <summary>執行完成但觀察到工具或其他問題。</summary>
    CompletedWithIssues,
    /// <summary>Codex 回合執行失敗。</summary>
    TurnFailed,
    /// <summary>事件串流處理失敗。</summary>
    StreamFailed,
    /// <summary>Codex 程序執行失敗。</summary>
    ProcessFailed,
    /// <summary>執行未完整結束。</summary>
    Incomplete
}
