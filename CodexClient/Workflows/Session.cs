namespace CodexClient.Workflows;

/// <summary>
/// 定義工作流程步驟如何使用 Codex 工作階段。
/// </summary>
public enum SessionMode
{
    /// <summary>不使用工作階段。</summary>
    None,
    /// <summary>建立並保存新的工作階段。</summary>
    Create,
    /// <summary>使用先前建立的工作階段。</summary>
    Use
}

/// <summary>
/// 指定工作流程步驟的 Codex 工作階段設定。
/// </summary>
public sealed class Session
{
    // Properties
    /// <summary>
    /// 取得工作階段的使用模式。
    /// </summary>
    public SessionMode Mode { get; }

    /// <summary>
    /// 取得用於識別工作階段的索引鍵；不使用工作階段時為 <see langword="null"/>。
    /// </summary>
    public string? Key { get; }

    private Session(SessionMode mode, string? key = null)
    {
        Mode = mode;
        Key = key;
    }

    /// <summary>
    /// 取得不使用工作階段的設定。
    /// </summary>
    public static Session None { get; } = new(SessionMode.None);


    // Methods
    /// <summary>
    /// 建立一個會建立新工作階段的設定。
    /// </summary>
    /// <param name="key">用於保存新工作階段識別碼的索引鍵。</param>
    /// <returns>新的工作階段設定。</returns>
    public static Session Create(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return new Session(SessionMode.Create, key);
    }

    /// <summary>
    /// 建立一個會使用既有工作階段的設定。
    /// </summary>
    /// <param name="key">要使用的工作階段索引鍵。</param>
    /// <returns>新的工作階段設定。</returns>
    public static Session Use(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return new Session(SessionMode.Use, key);
    }
}
