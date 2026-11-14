namespace CodexClient.AppServer.Workflows;

public enum AppServerThreadMode { None, Create, Use }

/// <summary>None 表示該步驟使用獨立的新 thread；Create/Use 在本次 workflow 執行內共用 thread。</summary>
public sealed class AppServerThread
{
    // Properties
    public AppServerThreadMode Mode { get; }
    public string? Key { get; }
    private AppServerThread(AppServerThreadMode mode, string? key = null) => (Mode, Key) = (mode, key);
    public static AppServerThread None { get; } = new(AppServerThreadMode.None);

    // Methods
    public static AppServerThread Create(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new(AppServerThreadMode.Create, key);
    }
    public static AppServerThread Use(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new(AppServerThreadMode.Use, key);
    }
}
