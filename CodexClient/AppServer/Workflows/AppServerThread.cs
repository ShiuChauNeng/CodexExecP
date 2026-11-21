namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 指定 Workflow 步驟如何使用 App Server thread。
/// </summary>
public enum AppServerThreadMode
{
    /// <summary>
    /// 每次執行都建立獨立的新 thread。
    /// </summary>
    New,

    /// 
    /// <summary>建立並儲存可供後續步驟使用的 thread。
    /// </summary>
    /// 
    Create,

    /// 
    /// <summary>使用先前建立的 thread。
    /// </summary>
    Use,

    /// <summary>
    /// 本次 Workflow 執行內，同一步驟重複執行時重用自己的 thread。
    /// </summary>
    PerStep,

    /// <summary>
    /// 使用指定鍵值的 thread；不存在時建立並儲存。
    /// </summary>
    GetOrCreate
}

/// <summary>
/// PerStep 重用步驟專屬 thread；New 每次建立新 thread；Create/Use/GetOrCreate 依鍵值共用 thread。
/// </summary>
public sealed class AppServerThread
{
    // Properties
    public AppServerThreadMode Mode { get; }
    public string? Key { get; }

    /// <summary>
    /// 每次執行都建立獨立的新 thread。
    /// </summary>
    public static AppServerThread New { get; } = new(AppServerThreadMode.New);

    /// <summary>
    /// 第一次執行步驟時建立 thread，返回該步驟時重用；不同步驟互不共用。
    /// </summary>
    public static AppServerThread PerStep { get; } = new(AppServerThreadMode.PerStep);


    // Constructors
    /// <summary>
    /// 建立 thread 模式及其選填的共用識別名稱。
    /// </summary>
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

    /// <summary>
    /// 使用指定鍵值的 thread；本次 Workflow 尚未建立時才建立並儲存。
    /// </summary>
    /// <param name="key">要查找或儲存 thread 識別碼的鍵值。</param>
    /// <returns>新的 thread 使用設定。</returns>
    public static AppServerThread GetOrCreate(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return new(AppServerThreadMode.GetOrCreate, key);
    }
}
