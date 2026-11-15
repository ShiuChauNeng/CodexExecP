using System.Text.Json;

namespace CodexClient.AppServer;

public interface IAppServerClient
{
    // Fields
    /// <summary>
    /// 最近一次 turn 的診斷，包含失敗或取消；不支援的實作可回傳 null。
    /// </summary>
    AppServerTurnDiagnostics? LastTurnDiagnostics => null;


    // Methods
    Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在指定 thread 執行一個 turn。
    /// </summary>
    /// <param name="threadId">目標 thread 識別碼。</param>
    /// <param name="prompt">要傳送給模型的提示文字。</param>
    /// <param name="outputSchema">選用的結構化輸出描述。</param>
    /// <param name="options">App Server 執行選項。</param>
    /// <param name="cancellationToken">用於取消 turn 的權杖。</param>
    /// <returns>非同步回傳 turn 執行結果。</returns>
    Task<TurnResult> RunTurnAsync(
        string threadId,
        string prompt,
        JsonElement? outputSchema = null,
        AppServerOptions? options = null,
        CancellationToken cancellationToken = default);
}
