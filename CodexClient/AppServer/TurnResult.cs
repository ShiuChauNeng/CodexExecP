namespace CodexClient.AppServer;

/// <summary>
/// 表示 App Server 單次 turn 的結果。
/// </summary>
/// <param name="ThreadId">執行 turn 的 thread 識別碼。</param>
/// <param name="TurnId">turn 識別碼。</param>
/// <param name="Status">turn 結束狀態。</param>
/// <param name="Response">turn 回傳的 JSON 文字。</param>
public sealed record TurnResult(string ThreadId, string TurnId, string Status, string Response)
{
    public string? Error { get; init; }
    public AppServerTurnDiagnostics? Diagnostics { get; init; }
}
