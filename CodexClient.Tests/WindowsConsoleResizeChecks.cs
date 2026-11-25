using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CodexClient.AppServer.Workflows.ConsoleUI;

[SupportedOSPlatform("windows")]
internal static class WindowsConsoleResizeChecks
{
    /// <summary>
    /// 啟動隱藏的獨立 Windows 主控台，實際驗證縮放、刷新與其他輸出的保留。
    /// </summary>
    internal static async Task<int> RunAsync()
    {
        string report = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--console-resize-check");
            start.ArgumentList.Add(report);
            using var child = Process.Start(start) ?? throw new Exception("Console resize check did not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await child.WaitForExitAsync(deadline.Token);
                string result = await File.ReadAllTextAsync(report);
                if (child.ExitCode != 0 || result != "PASS")
                {
                    throw new Exception("Native console resize check failed: " + result);
                }
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
        }
        finally
        {
            File.Delete(report);
        }
        Console.WriteLine("PASS: real hidden Windows console keeps one updating line across viewport and buffer resizes");
        Console.WriteLine("PASS: exact-width wrapping and rapid shrink/restore continue refreshing without duplicate records");
        Console.WriteLine("PASS: native redraw preserves history and intervening output instead of clearing an inferred range");
        Console.WriteLine("PASS: reading history suppresses heartbeat drawing, scrolling back resumes it and a new stage follows the cursor");
        return 4;
    }

    /// <summary>
    /// 在自己的隱藏 Console 執行測試，結果寫檔，避免操作使用者的執行視窗。
    /// </summary>
    internal static void RunChild(string report)
    {
        string stage = "initialization";
        try
        {
            Assert(!Console.IsOutputRedirected, "Expected an independent console.");
            var key = Reset();
            int seconds = 6;
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(seconds), null);
            foreach (int width in new[] { 40, 80, 55, 60 })
            {
                stage = "viewport width " + width;
                Console.SetWindowSize(width, 12);
                ConsoleWorkflowOutput.WriteProgressLine(key, Message(++seconds), null);
                CheckFrame(seconds);
            }
            foreach (int height in new[] { 8, 16, 12 })
            {
                stage = "viewport height " + height;
                Console.SetWindowSize(60, height);
                ConsoleWorkflowOutput.WriteProgressLine(key, Message(++seconds), null);
                CheckFrame(seconds);
            }

            foreach (int width in new[] { 28, 40, 80, 55, 60 })
            {
                stage = "buffer width " + width;
                ResizeBuffer(width);
                ConsoleWorkflowOutput.WriteProgressLine(key, Message(++seconds), null);
                CheckFrame(seconds);
            }
            stage = "rapid shrink and restore";
            ResizeBuffer(40);
            ResizeBuffer(60);
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(++seconds), null);
            CheckFrame(seconds);

            stage = "exact-width wrapping";
            key = Reset();
            ConsoleWorkflowOutput.WriteProgressLine(key, "BOUNDARY" + new string('x', 32), null);
            ResizeBuffer(20);
            ConsoleWorkflowOutput.WriteProgressLine(key, "BOUNDARY" + new string('y', 32), null);
            string boundary = ReadScreen();
            Assert(Count(boundary, "BOUNDARY") == 1 && boundary.Contains("yyyy") && !boundary.Contains("xxxx"), "Exact-width redraw is stale or duplicated.");

            stage = "intervening output";
            key = Reset();
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(6), null);
            string other = new('Y', Console.CursorLeft);
            Console.Write("\n" + other);
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(7), null);
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(8), null);
            string screen = ReadScreen();
            Assert(screen.Contains(other) && screen.Contains("HISTORY SENTINEL"), "Redraw erased another output source.");
            Assert(Count(screen, "[15:35:41]") == 2 && screen.Contains("1分8秒"), "Waiting output did not continue after an intervening message.");
            stage = "scrolling history during waiting";
            key = Reset();
            for (int index = 0; index < 40; index++)
            {
                Console.WriteLine($"HISTORY {index}");
            }
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(6), null);
            var waitingCursor = Console.GetCursorPosition();
            Console.SetWindowPosition(0, 0);
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(7), null);
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(8), null);
            Assert(Console.WindowTop == 0 && Console.GetCursorPosition() == waitingCursor, "Heartbeat interrupted history scrolling.");
            Assert(ReadScreen(60).Contains("1分6秒") && !ReadScreen(60).Contains("1分8秒"), "Heartbeat redrew an off-screen waiting line.");

            Console.SetWindowPosition(0, waitingCursor.Top - Console.WindowHeight + 1);
            ConsoleWorkflowOutput.WriteProgressLine(key, Message(9), null);
            Assert(ReadScreen(60).Contains("1分9秒") && Count(ReadScreen(60), "[15:35:41]") == 1, "Scrolling back did not resume the latest elapsed time.");

            Console.SetWindowPosition(0, 0);
            ConsoleWorkflowOutput.WriteProgressLine(new object(), "[15:35:42] [Implement] Next stage", null);
            Assert(Console.WindowTop > 0 && !WindowsConsoleProgress.IsViewingHistory(), "A new stage did not return to the current output.");
            Console.SetWindowPosition(0, 0);
            ConsoleWorkflowOutput.WriteLine("Question or completion message");
            Assert(!WindowsConsoleProgress.IsViewingHistory(), "A normal message did not return to the current output.");
            File.WriteAllText(report, "PASS");
        }
        catch (Exception error)
        {
            File.WriteAllText(report, stage + Environment.NewLine + error);
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// 重設測試自己的 Console 與等待行，不碰使用者視窗。
    /// </summary>
    private static object Reset()
    {
        ConsoleWorkflowOutput.WriteLine(string.Empty);
        Console.SetWindowSize(Math.Min(Console.WindowWidth, 60), Math.Min(Console.WindowHeight, 12));
        Console.SetBufferSize(100, 200);
        Console.SetWindowSize(60, 12);
        Console.Clear();
        Console.WriteLine("HISTORY SENTINEL");
        return new object();
    }

    /// <summary>
    /// 依合法順序調整視窗與緩衝區，讓主控台實際重新換行。
    /// </summary>
    private static void ResizeBuffer(int width)
    {
        Console.SetWindowSize(Math.Min(width, Console.WindowWidth), 12);
        Console.SetBufferSize(width, 200);
        Console.SetWindowSize(width, 12);
    }

    /// <summary>
    /// 建立每次秒數不同的等待文字，以確認真的有刷新而非只保留舊畫面。
    /// </summary>
    private static string Message(int seconds)
        => $"[15:35:41] [Acceptance] 等待 AI 執行結果。（累計1分{seconds}秒）";

    /// <summary>
    /// 驗證等待訊息只有一筆、秒數已更新，且歷史紀錄仍在。
    /// </summary>
    private static void CheckFrame(int seconds)
    {
        string screen = ReadScreen();
        Assert(Count(screen, "[15:35:41]") == 1 && screen.Contains($"1分{seconds}秒"), "Waiting output is duplicated or stale: " + screen);
        Assert(screen.Contains("HISTORY SENTINEL") && !screen.Contains('\u001b'), "History or clear-line output was corrupted.");
    }

    /// <summary>
    /// 讀取指定列數的實際畫面，僅採用 Windows API 回報的有效字元數。
    /// </summary>
    private static string ReadScreen(int rows = 8)
    {
        var result = new StringBuilder();
        var line = new StringBuilder(Console.BufferWidth + 1);
        for (short row = 0; row < rows; row++)
        {
            line.Clear();
            Assert(ReadConsoleOutputCharacter(GetStdHandle(-11), line, (uint)Console.BufferWidth, new Coordinate(0, row), out uint read), "Cannot read console buffer.");
            result.Append(line.ToString(0, Math.Min(line.Length, (int)read)).TrimEnd());
        }
        return result.ToString();
    }

    /// <summary>
    /// 計算實際畫面中的訊息筆數。
    /// </summary>
    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// 確認真實 Console 結果，不符時附上原因。
    /// </summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coordinate(short X, short Y);

    /// <summary>
    /// 取得測試自己的標準輸出。
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    /// <summary>
    /// 從測試自己的 Windows 主控台讀取實際文字。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleOutputCharacterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacter(IntPtr output, StringBuilder text, uint length, Coordinate position, out uint read);
}
