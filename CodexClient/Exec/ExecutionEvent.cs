namespace CodexClient.Exec;


/// <summary>
/// 表示 Codex 執行期間收到的事件。
/// </summary>
/// <param name="Type">事件類型。</param>
/// <param name="ItemId">事件項目的識別碼。</param>
/// <param name="ItemType">事件項目的類型。</param>
/// <param name="Command">命令執行內容。</param>
/// <param name="ExitCode">命令的結束代碼。</param>
/// <param name="Text">事件所包含的文字內容。</param>
/// <param name="ThreadId">Codex 執行緒識別碼。</param>
/// <param name="ItemStatus">事件項目的狀態。</param>
/// <param name="ErrorMessage">事件中的錯誤訊息。</param>
/// <param name="ToolName">事件中使用的工具名稱。</param>
public sealed record ExecutionEvent(
    string Type,
    string? ItemId = null,
    string? ItemType = null,
    string? Command = null,
    int? ExitCode = null,
    string? Text = null,
    string? ThreadId = null,
    string? ItemStatus = null,
    string? ErrorMessage = null,
    string? ToolName = null);