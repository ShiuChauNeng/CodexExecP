namespace CodexClient.AppServer;

public sealed record TurnResult(string ThreadId, string TurnId, string Status, string Response);
