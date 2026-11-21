
using System.Diagnostics;
using System.Text;

namespace CodexClient.Internal;

internal static class CodexProcessStartInfoFactory
{
    /// <summary>
    /// 建立重新導向標準輸入、輸出與錯誤的 UTF-8 程序啟動設定。
    /// </summary>
    public static ProcessStartInfo Create(string executablePath, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        return startInfo;
    }
}
