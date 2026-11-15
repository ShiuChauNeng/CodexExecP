namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 指定 Workflow 步驟如何使用 App Server thread。
/// </summary>
public enum AppServerThreadMode
{
    /// <summary>
    /// 不共用 thread，每個步驟使用獨立 thread。
    /// </summary>
    None,

    /// 
    /// <summary>建立並儲存可供後續步驟使用的 thread。
    /// </summary>
    /// 
    Create,

    /// 
    /// <summary>使用先前建立的 thread。
    /// </summary>
    Use
}

/// <summary>None 表示該步驟使用獨立的新 thread；Create/Use 在本次 workflow 執行內共用 thread。</summary>
public sealed class AppServerThread
{
    // Properties
    public AppServerThreadMode Mode { get; }
    public string? Key { get; }

    /// <summary>
    /// 表示不共用 thread 的設定。
    /// </summary>
    public static AppServerThread None { get; } = new(AppServerThreadMode.None);


    // Constructors
    private AppServerThread(AppServerThreadMode mode, string? key = null)
        => (Mode, Key) = (mode, key);


    // Methods
    /// <summary>
    /// 建立一個會建立並儲存 thread 的設定。
    /// </summary>
    /// <param name="key">要儲存 thread 識別碼的鍵值。</param>
    /// <returns>新的 thread 使用設定。</returns>
    public static AppServerThread Create(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return new(AppServerThreadMode.Create, key);
    }
    /// <summary>
    /// 建立一個會使用既有 thread 的設定。
    /// </summary>
    /// <param name="key">要查找 thread 識別碼的鍵值。</param>
    /// <returns>新的 thread 使用設定。</returns>
    public static AppServerThread Use(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return new(AppServerThreadMode.Use, key);
    }
}
