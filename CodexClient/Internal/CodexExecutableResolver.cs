
namespace CodexClient.Internal;

internal static class CodexExecutableResolver
{
    public static string Resolve(string? executablePath = null)
    {
        if (executablePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            string fullPath = Path.GetFullPath(executablePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到指定的 Codex 執行檔。", fullPath);
            }

            return fullPath;
        }

        string[] fileNames = OperatingSystem.IsWindows() ? ["codex.exe", "codex.cmd"] : ["codex"];

        string pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (string entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = entry.Trim('"');

            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (string fileName in fileNames)
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new FileNotFoundException("找不到 Codex。請確認已安裝並將執行檔目錄加入 PATH，" + "或透過 executablePath 指定完整路徑。");
    }
}
