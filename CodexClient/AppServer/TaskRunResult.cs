namespace CodexClient.AppServer;

public sealed record TaskRunResult(string ThreadId, string TurnId, string Message)
{
    public System.Text.Json.JsonElement? Data { get; init; }
    /// <summary>AI 回報的未解決問題；實際產出仍應由驗證 callback 檢查。</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
    public IReadOnlyList<AppServerTurnDiagnostics> Diagnostics { get; init; } = [];
}
