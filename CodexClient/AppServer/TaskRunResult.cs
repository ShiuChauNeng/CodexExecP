namespace CodexClient.AppServer;

/// <summary>
/// 表示單一任務的執行結果。
/// </summary>
/// <param name="ThreadId">執行任務的 thread 識別碼。</param>
/// <param name="TurnId">最後一次 turn 的識別碼。</param>
/// <param name="Message">任務結果摘要。</param>
public sealed record TaskRunResult(string ThreadId, string TurnId, string Message)
{
    public System.Text.Json.JsonElement? Data { get; init; }
    /// <summary>
    /// AI 回報的未解決問題；實際產出仍應由驗證 callback 檢查。
    /// </summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
    public IReadOnlyList<AppServerTurnDiagnostics> Diagnostics { get; init; } = [];
}
