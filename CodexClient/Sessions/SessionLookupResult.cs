namespace CodexClient.Sessions;

/// <summary>
/// 表示工作階段查詢的結果。
/// </summary>
/// <param name="Status">查詢結果的狀態。</param>
/// <param name="Session">符合查詢條件的工作階段；查詢未找到工作階段或發生工作區衝突時為 <see langword="null"/>。</param>
public sealed record SessionLookupResult(SessionLookupStatus Status, SessionInfo? Session);

/// <summary>
/// 定義工作階段查詢可能回傳的狀態。
/// </summary>
public enum SessionLookupStatus
{
    /// <summary>
    /// 找不到指定的工作階段。
    /// </summary>
    NotFound,

    /// <summary>
    /// 找到符合條件的工作階段。
    /// </summary>
    Matched,

    /// <summary>
    /// 找到工作階段，但其工作區與查詢的工作區不一致。
    /// </summary>
    WorkspaceConflict
}
