using System.Text.Json;

namespace CodexClient.AppServer;

/// <summary>Workflow 所需的 App Server 操作；呼叫端負責啟動與釋放實際 Client。</summary>
public interface IAppServerClient
{
    /// <summary>最近一次 turn 的診斷，包含失敗或取消；不支援的實作可回傳 null。</summary>
    AppServerTurnDiagnostics? LastTurnDiagnostics => null;

    Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default);

    Task<TurnResult> RunTurnAsync(
        string threadId,
        string prompt,
        JsonElement? outputSchema = null,
        AppServerOptions? options = null,
        CancellationToken cancellationToken = default);
}
