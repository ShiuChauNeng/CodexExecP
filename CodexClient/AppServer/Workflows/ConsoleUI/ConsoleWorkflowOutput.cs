namespace CodexClient.AppServer.Workflows.ConsoleUI;

/// <summary>
/// 同步 Workflow 的主控台輸出，並在每次輸出後還原文字顏色。
/// </summary>
internal static class ConsoleWorkflowOutput
{
    private static readonly object Sync = new();
    private static object? _progressKey;
    private static (int Left, int Top, int Width) _progressEnd;

    /// <summary>
    /// 以問題提示色輸出文字並換行。
    /// </summary>
    internal static void WriteQuestionLine(string text)
        => WriteMessageLine(text, AppServerWorkflowMessageKind.Question);

    /// <summary>
    /// 以回答提示色輸出文字，保留游標供使用者輸入。
    /// </summary>
    internal static void WriteAnswerPrompt(string text)
        => WriteCore(() => Console.Write(text), ConsoleColor.Blue, "\u001b[38;2;135;190;250m");

    /// <summary>
    /// 以指定顏色輸出文字並換行，結束既有等待行。
    /// </summary>
    internal static void WriteLine(string text, ConsoleColor? color = null)
        => WriteCore(() => Console.WriteLine(text), color);

    /// <summary>
    /// 以指定顏色輸出文字但不換行，結束既有等待行。
    /// </summary>
    internal static void Write(string text, ConsoleColor? color = null)
        => WriteCore(() => Console.Write(text), color);

    /// <summary>
    /// 以橘色輸出文字並換行。
    /// </summary>
    internal static void WriteOrangeLine(string text)
        => WriteMessageLine(text, AppServerWorkflowMessageKind.Warning);

    /// <summary>
    /// 以橘色輸出文字但不換行。
    /// </summary>
    internal static void WriteOrange(string text)
        => WriteMessage(text, AppServerWorkflowMessageKind.Warning);

    /// <summary>
    /// 依共用配色規則輸出訊息，不換行。
    /// </summary>
    internal static void WriteMessage(string text, AppServerWorkflowMessageKind kind)
    {
        var style = GetMessageStyle(kind);
        WriteCore(() => Console.Write(text), style.Color, style.AnsiColor);
    }

    /// <summary>
    /// 依共用配色規則輸出訊息並換行。
    /// </summary>
    internal static void WriteMessageLine(string text, AppServerWorkflowMessageKind kind)
    {
        var style = GetMessageStyle(kind);
        WriteCore(() => Console.WriteLine(text), style.Color, style.AnsiColor);
    }

    /// <summary>
    /// 依共用配色規則更新原有等待行，不改變刷新或游標處理。
    /// </summary>
    internal static void WriteMessageProgressLine(object key, string text, AppServerWorkflowMessageKind kind)
    {
        var style = GetMessageStyle(kind);
        WriteProgressLine(key, text, style.Color, style.AnsiColor);
    }

    /// <summary>
    /// 取得首次輸出、心跳與最終報告共用的 Console 及 ANSI 顏色。
    /// </summary>
    internal static (ConsoleColor? Color, string? AnsiColor) GetMessageStyle(AppServerWorkflowMessageKind kind)
        => kind switch
        {
            AppServerWorkflowMessageKind.Waiting => (ConsoleColor.DarkGray, null),
            AppServerWorkflowMessageKind.Question => (ConsoleColor.Magenta, "\u001b[38;2;225;145;215m"),
            AppServerWorkflowMessageKind.Success => (ConsoleColor.Green, null),
            AppServerWorkflowMessageKind.Warning => (ConsoleColor.DarkYellow, "\u001b[38;2;255;165;0m"),
            AppServerWorkflowMessageKind.Error => (ConsoleColor.Red, null),
            _ => (null, null)
        };

    /// <summary>
    /// 更新同一個等待行；導向檔案或無法取得游標時維持逐行輸出。
    /// </summary>
    internal static void WriteProgressLine(object key, string text, ConsoleColor? color, string? ansiColor = null)
    {
        lock (Sync)
        {
            if (Console.IsOutputRedirected || !TryGetCursor(out var cursor))
            {
                FinishProgressLine();
                WriteColored(() => Console.WriteLine(text), color, ansiColor);
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
                WriteColored(() => Console.WriteLine(text), color, ansiColor);
                return;
            }
            WriteColored(() => Console.Write(text), color, ansiColor);
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

    /// <summary>
    /// 在同步鎖內結束等待行，再以指定顏色輸出內容。
    /// </summary>
    private static void WriteCore(Action write, ConsoleColor? color, string? ansiColor = null)
    {
        lock (Sync)
        {
            FinishProgressLine();
            WriteColored(write, color, ansiColor);
        }
    }

    /// <summary>
    /// 結束目前可覆寫的等待行，必要時換行以保護後續輸出。
    /// </summary>
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

    /// <summary>
    /// 嘗試取得游標位置及緩衝區寬度；不支援或讀取失敗時回傳 false。
    /// </summary>
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

    /// <summary>
    /// 暫時套用輸出顏色並於結束後還原；重新導向時只輸出純文字。
    /// </summary>
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
