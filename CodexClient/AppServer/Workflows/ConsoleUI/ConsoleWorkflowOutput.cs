namespace CodexClient.AppServer.Workflows.ConsoleUI;

/// <summary>
/// 同步 Workflow 的主控台輸出，並在每次輸出後還原文字顏色。
/// </summary>
internal static class ConsoleWorkflowOutput
{
    private static readonly object Sync = new();
    private static object? _progressKey;
    private static (int Left, int Top, int Width) _progressEnd;

    internal static void WriteQuestionLine(string text)
        => WriteCore(() => Console.WriteLine(text), ConsoleColor.Magenta, "\u001b[38;2;225;145;215m");

    internal static void WriteAnswerPrompt(string text)
        => WriteCore(() => Console.Write(text), ConsoleColor.Blue, "\u001b[38;2;135;190;250m");

    internal static void WriteLine(string text, ConsoleColor? color = null)
        => WriteCore(() => Console.WriteLine(text), color);

    internal static void Write(string text, ConsoleColor? color = null)
        => WriteCore(() => Console.Write(text), color);

    internal static void WriteOrangeLine(string text)
        => WriteCore(() => Console.WriteLine(text), ConsoleColor.DarkYellow, "\u001b[38;2;255;165;0m");

    internal static void WriteOrange(string text)
        => WriteCore(() => Console.Write(text), ConsoleColor.DarkYellow, "\u001b[38;2;255;165;0m");

    /// <summary>更新同一個等待行；導向檔案或無法取得游標時維持逐行輸出。</summary>
    internal static void WriteProgressLine(object key, string text, ConsoleColor? color)
    {
        lock (Sync)
        {
            if (Console.IsOutputRedirected || !TryGetCursor(out var cursor))
            {
                FinishProgressLine();
                WriteColored(() => Console.WriteLine(text), color);
                return;
            }

            // 只有游標仍在上次輸出結尾才覆寫，保留其他來源插入的輸出。
            if (ReferenceEquals(_progressKey, key) && cursor == _progressEnd)
            {
                Console.Write('\r');
                Console.Write(new string(' ', cursor.Left));
                Console.Write('\r');
            }
            else
            {
                FinishProgressLine();
                if (TryGetCursor(out var current) && current.Left != 0)
                {
                    Console.WriteLine();
                }
            }

            _progressKey = null;
            if (!TryGetCursor(out var start))
            {
                WriteColored(() => Console.WriteLine(text), color);
                return;
            }
            WriteColored(() => Console.Write(text), color);
            if (TryGetCursor(out var end) && end.Top == start.Top && end.Left >= text.Length &&
                text.Length < start.Width && end.Width == start.Width && text.IndexOfAny(['\r', '\n', '\t']) < 0)
            {
                _progressKey = key;
                _progressEnd = end;
            }
            else
            {
                // 視窗太窄導致換行時不回頭覆寫，避免破壞已捲動的內容。
                Console.WriteLine();
            }
        }
    }

    private static void WriteCore(Action write, ConsoleColor? color, string? ansiColor = null)
    {
        lock (Sync)
        {
            FinishProgressLine();
            WriteColored(write, color, ansiColor);
        }
    }

    private static void FinishProgressLine()
    {
        if (_progressKey is null)
        {
            return;
        }
        _progressKey = null;
        if (!TryGetCursor(out var cursor) || cursor.Left != 0)
        {
            Console.WriteLine();
        }
    }

    private static bool TryGetCursor(out (int Left, int Top, int Width) cursor)
    {
        try
        {
            var position = Console.GetCursorPosition();
            cursor = (position.Left, position.Top, Console.BufferWidth);
            return cursor.Width > 0;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
            cursor = default;
            return false;
        }
    }

    private static void WriteColored(Action write, ConsoleColor? color, string? ansiColor = null)
    {
        if (Console.IsOutputRedirected || color is null)
        {
            write();
            return;
        }

        if (color == ConsoleColor.Green && ansiColor is null)
        {
            ansiColor = "\u001b[38;2;120;225;150m";
        }

        ConsoleColor originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color.Value;
            if (ansiColor is not null)
            {
                Console.Write(ansiColor);
            }
            write();
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }
}
