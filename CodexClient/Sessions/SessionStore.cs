using System.Text.Json;

namespace CodexClient.Sessions;

/// <summary>
/// 提供工作階段資料的檔案儲存與管理功能。
/// </summary>
public sealed class SessionStore
{
    // Fields
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _directory;


    // Constructors
    public SessionStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniCodingAgent", "Sessions");

        Directory.CreateDirectory(_directory);
    }


    // Methods
    /// <summary>
    /// 非同步載入指定名稱的工作階段。
    /// </summary>
    /// <param name="name">工作階段名稱。</param>
    /// <param name="cancellationToken">用於取消載入作業的權杖。</param>
    /// <returns>載入的工作階段；找不到指定檔案時回傳 <see langword="null"/>。</returns>
    public async Task<SessionInfo?> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        string path = GetSessionPath(name);

        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);

        return await JsonSerializer.DeserializeAsync<SessionInfo>(stream, JsonOptions, cancellationToken);
    }

    /// <summary>
    /// 非同步儲存工作階段資料。
    /// </summary>
    /// <param name="session">要儲存的工作階段。</param>
    /// <param name="cancellationToken">用於取消儲存作業的權杖。</param>
    /// <returns>表示儲存作業的非同步工作。</returns>
    public async Task SaveAsync(SessionInfo session, CancellationToken cancellationToken = default)
    {
        string path = GetSessionPath(session.Name);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken);

                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// 刪除指定名稱的工作階段檔案。
    /// </summary>
    /// <param name="name">要刪除的工作階段名稱。</param>
    public void Delete(string name)
    {
        string path = GetSessionPath(name);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 取得指定工作階段名稱對應的檔案路徑。
    /// </summary>
    /// <param name="name">工作階段名稱。</param>
    /// <returns>工作階段檔案的完整路徑。</returns>
    /// <exception cref="ArgumentException">工作階段名稱無效時擲出。</exception>
    private string GetSessionPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') ||
            name.Contains('\\'))
        {
            throw new ArgumentException("Invalid session name.", nameof(name));
        }

        return Path.Combine(_directory, $"{name}.json");
    }
}

