namespace CodexClient.Sessions;

public record SessionInfo(string Name, string SessionId, string WorkspaceRoot, DateTimeOffset UpdatedAt);