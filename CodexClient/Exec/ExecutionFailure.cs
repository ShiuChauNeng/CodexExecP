using System;
using System.Collections.Generic;
using System.Text;

namespace CodexClient.Exec;

/// <summary>
/// 指出 Codex 執行失敗的來源類型。
/// </summary>
public enum ExecutionFailureKind
{
    /// <summary>
    /// Codex 程序本身失敗。
    /// </summary>
    Process,

    /// <summary>
    /// 工具執行失敗。
    /// </summary>
    Tool,

    /// <summary>
    /// 代理程式項目失敗。
    /// </summary>
    AgentItem,

    /// <summary>
    /// 事件串流處理失敗。
    /// </summary>
    Stream,

    /// <summary>
    /// Codex 回合執行失敗。
    /// </summary>
    Turn
}

/// <summary>
/// 表示 Codex 執行期間發生的單一失敗資訊。
/// </summary>
/// <param name="Kind">失敗來源類型。</param>
/// <param name="Message">失敗訊息。</param>
/// <param name="ItemId">相關事件項目的識別碼。</param>
/// <param name="ItemType">相關事件項目的類型。</param>
/// <param name="ItemStatus">相關事件項目的狀態。</param>
/// <param name="Command">相關命令內容。</param>
/// <param name="ToolName">相關工具名稱。</param>
/// <param name="ExitCode">相關程序的結束代碼。</param>
public sealed record ExecutionFailure(
    ExecutionFailureKind Kind,
    string Message,
    string? ItemId = null,
    string? ItemType = null,
    string? ItemStatus = null,
    string? Command = null,
    string? ToolName = null,
    int? ExitCode = null);
