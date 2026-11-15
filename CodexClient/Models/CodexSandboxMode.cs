namespace CodexClient.Models;

/// <summary>
/// Codex 可使用的沙箱權限模式。
/// </summary>
public enum CodexSandboxMode
{
    /// <summary>
    /// 僅允許讀取工作區。
    /// </summary>
    ReadOnly,

    /// <summary>
    /// 允許寫入工作區。
    /// </summary>
    WorkspaceWrite,
}
