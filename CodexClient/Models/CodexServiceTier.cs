namespace CodexClient.Models;

/// <summary>
/// Codex exec 與 App Server 共用的服務速度設定；實際支援依模型與帳號而定。
/// </summary>
public enum CodexServiceTier
{
    /// <summary>
    /// 使用一般速度，明確關閉 Fast 模式。
    /// </summary>
    Standard,

    /// <summary>
    /// 請求較快的服務速度，可能增加額度或點數用量；不改變推理強度。
    /// </summary>
    Fast
}
