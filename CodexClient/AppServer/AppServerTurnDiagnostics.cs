using System.Text.Json;

namespace CodexClient.AppServer;

/// <summary>
/// 執行紀錄不代表未解決的問題；失敗操作可能已由後續操作修復。
/// CompletedItems 保留原始完成事件，含工具狀態；StandardError 是連線層級尾端，可能包含先前 turn 的內容。
/// </summary>
public sealed record AppServerTurnDiagnostics(
    string ThreadId,
    string? TurnId,
    IReadOnlyList<JsonElement> CompletedItems,
    IReadOnlyList<JsonElement> Errors,
    string StandardError);
