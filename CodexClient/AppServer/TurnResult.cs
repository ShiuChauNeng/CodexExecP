namespace CodexClient.AppServer;

public sealed record TurnResult(string ThreadId, string TurnId, string Status, string Response)
{
    public string? Error { get; init; }
    public AppServerTurnDiagnostics? Diagnostics { get; init; }
}
