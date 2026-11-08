namespace CodexClient.Exec;

/// <summary>
/// 表示 Codex 執行目前所處的狀態。
/// </summary>
public enum ExecutionState
{
    /// <summary>
    /// 已建立但尚未開始執行。
    /// </summary>
    Created,

    /// <summary>
    /// 正在執行。
    /// </summary>
    Running,

    /// <summary>
    /// 已完成執行。
    /// </summary>
    Completed,

    /// <summary>
    /// 執行失敗。
    /// </summary>
    Failed,

    /// <summary>
    /// 執行已取消。
    /// </summary>
    Cancelled,

    /// <summary>
    /// 執行因逾時而結束。
    /// </summary>
    TimedOut,

    /// <summary>
    /// 執行未完整結束。
    /// </summary>
    Incomplete
}