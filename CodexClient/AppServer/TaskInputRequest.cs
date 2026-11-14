namespace CodexClient.AppServer;

public sealed record TaskInputRequest(
    string ThreadId,
    string TurnId,
    string Message,
    IReadOnlyList<string> Questions);
