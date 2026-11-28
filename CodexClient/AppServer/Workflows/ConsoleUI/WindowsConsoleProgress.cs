using System.Runtime.InteropServices;
using System.Text;

namespace CodexClient.AppServer.Workflows.ConsoleUI;

/// <summary>
/// 以 Windows 主控台的實際緩衝區內容確認並清除等待文字，不依字元欄寬推算行數。
/// </summary>
internal static class WindowsConsoleProgress
{
    /// <summary>
    /// 將游標帶回可見範圍。心跳期間若視窗在游標上方，保留歷史畫面並回傳 false。
    /// </summary>
    internal static bool ShowLatestOutput(bool preserveHistory = false)
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
        {
            return true;
        }
        try
        {
            int top = Console.WindowTop;
            int cursor = Console.CursorTop;
            int height = Console.WindowHeight;
            if (preserveHistory && cursor >= top + height)
            {
                return false;
            }
            if (cursor < top || cursor >= top + height)
            {
                int latestTop = Math.Clamp(cursor - height + 1, 0, Math.Max(0, Console.BufferHeight - height));
                Console.SetWindowPosition(Console.WindowLeft, latestTop);
            }
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            // 尺寸同時變動時，保留正常輸出，不讓捲動控制影響流程。
        }
        return true;
    }

    /// <summary>
    /// 保留等待刷新前的可見範圍，避免主控台因覆寫游標而自動捲至底部。
    /// </summary>
    internal static IDisposable? PreserveViewport()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
        {
            return null;
        }
        try
        {
            return new Viewport(Console.WindowLeft, Console.WindowTop, Console.WindowWidth, Console.WindowHeight,
                Console.BufferWidth, Console.BufferHeight);
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private sealed record Viewport(int Left, int Top, int Width, int Height, int BufferWidth, int BufferHeight) : IDisposable
    {
        /// <summary>
        /// 尺寸未變時還原可見範圍；刷新途中若發生縮放，交由主控台保留新的配置。
        /// </summary>
        public void Dispose()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            try
            {
                if (Console.WindowWidth == Width && Console.WindowHeight == Height &&
                    Console.BufferWidth == BufferWidth && Console.BufferHeight == BufferHeight &&
                    (Console.WindowLeft != Left || Console.WindowTop != Top))
                {
                    Console.SetWindowPosition(Left, Top);
                }
            }
            catch (Exception exception) when (exception is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
            {
                // 縮放可能與還原同時發生，不讓顯示錯誤影響流程。
            }
        }
    }

    /// <summary>
    /// 只有從游標尾端回讀的文字與上次輸出一致時才清除；不支援或內容已變更時交還一般輸出。
    /// </summary>
    internal static bool TryClear(string previousText, int cursorLeft, int cursorTop, int bufferWidth)
    {
        if (!OperatingSystem.IsWindows() || bufferWidth <= 0 || cursorLeft < 0 || cursorLeft >= bufferWidth ||
            cursorTop < 0 || cursorTop > short.MaxValue)
        {
            return false;
        }
        try
        {
            return TryClearCore(previousText, cursorLeft, cursorTop, bufferWidth);
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// 由游標尾端回讀實際文字，核對成功後才清除該段並回到起點。
    /// </summary>
    private static bool TryClearCore(string previousText, int cursorLeft, int cursorTop, int bufferWidth)
    {
        string expected = Normalize(previousText);
        if (expected.Length == 0)
        {
            return false;
        }
        IntPtr output = GetStdHandle(-11);
        if (output == IntPtr.Zero || output == new IntPtr(-1))
        {
            return false;
        }

        string actual = string.Empty;
        var line = new StringBuilder(bufferWidth + 1);
        for (int row = cursorTop; row >= Math.Max(0, cursorTop - previousText.Length); row--)
        {
            line.Clear();
            int columns = row == cursorTop ? cursorLeft : bufferWidth;
            uint read = 0;
            if (columns > 0 && !ReadConsoleOutputCharacter(output, line, (uint)columns, new Coordinate(0, (short)row), out read))
            {
                return false;
            }
            actual = Normalize(line.ToString(0, Math.Min(line.Length, (int)read))) + actual;
            if (actual == expected)
            {
                var cursor = Console.GetCursorPosition();
                if (cursor.Left != cursorLeft || cursor.Top != cursorTop || Console.BufferWidth != bufferWidth)
                {
                    return false;
                }
                uint count = (uint)((cursorTop - row) * bufferWidth + cursorLeft);
                if (count > 0 && (!FillConsoleOutputCharacter(output, ' ', count, new Coordinate(0, (short)row), out uint written) || written != count))
                {
                    return false;
                }
                Console.SetCursorPosition(0, row);
                return true;
            }
            if (actual.Length > expected.Length)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// 排除主控台重新換行補上的空白；保留全部非空白文字供內容核對。
    /// </summary>
    private static string Normalize(string text)
        => string.Concat(text.Where(character => !char.IsWhiteSpace(character) && character != '\0'));

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coordinate(short X, short Y);

    /// <summary>
    /// 取得標準輸出控制代碼。
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    /// <summary>
    /// 讀取主控台緩衝區中的實際 Unicode 文字。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleOutputCharacterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacter(IntPtr output, StringBuilder text, uint length, Coordinate position, out uint read);

    /// <summary>
    /// 清除已確認的等待文字範圍，不移動游標、不產生換行。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "FillConsoleOutputCharacterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillConsoleOutputCharacter(IntPtr output, char character, uint length, Coordinate position, out uint written);
}
