namespace CodexClient.Sessions;

/// <summary>
/// 表示可恢復的 Codex 工作階段資訊。
/// </summary>
/// <param name="Name">工作階段名稱。</param>
/// <param name="SessionId">Codex 工作階段識別碼。</param>
/// <param name="WorkspaceRoot">工作階段所屬工作區的根目錄。</param>
/// <param name="UpdatedAt">工作階段最後更新時間。</param>
public record SessionInfo(string Name, string SessionId, string WorkspaceRoot, DateTimeOffset UpdatedAt);
