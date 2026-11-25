using System.Globalization;
using System.Text;

namespace CodexClient.AppServer.Workflows.ConsoleUI;

/// <summary>
/// 同步 Workflow 的主控台輸出，並在每次輸出後還原文字顏色。
/// </summary>
internal static class ConsoleWorkflowOutput
{
    private static readonly object Sync = new();
    private static object? _progressKey;
    private static (int Left, int Top, int Width, int Height, int BufferWidth) _progressEnd;
    private static string _progressText = string.Empty;
    private static bool _progressIsSingleRow;

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

            bool sameProgress = ReferenceEquals(_progressKey, key);
            bool redrawing = false;
            if (sameProgress)
            {
                if (_progressIsSingleRow && cursor == _progressEnd)
                {
                    Console.Write('\r');
                    Console.Write(new string(' ', cursor.Left));
                    Console.Write('\r');
                    redrawing = true;
                }
                else
                {
                    // 縮放或游標變動後，以實際畫面內容確認所有權，不推算舊文字占了幾行。
                    redrawing = WindowsConsoleProgress.TryClear(_progressText, cursor.Left, cursor.Top, cursor.BufferWidth);
                }
            }
            if (!redrawing)
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
            string displayedText = text.IndexOfAny(['\r', '\n', '\t']) < 0
                ? FitProgressLine(text, Math.Max(0, start.Width - 1))
                : text;
            int displayedWidth = displayedText.EnumerateRunes().Sum(GetColumnWidth);
            WriteColored(() => Console.Write(displayedText), color, ansiColor);
            if (text.IndexOfAny(['\r', '\n', '\t']) < 0 && TryGetCursor(out var end))
            {
                _progressKey = key;
                _progressEnd = end;
                _progressText = displayedText;
                _progressIsSingleRow = start.Left == 0 && end.Top == start.Top && end.Left == displayedWidth &&
                    displayedWidth < start.Width && end.Width == start.Width && end.Height == start.Height &&
                    end.BufferWidth == start.BufferWidth;
            }
            else
            {
                Console.WriteLine();
            }
        }
    }

    /// <summary>
    /// 依顯示欄寬縮短等待行的中段，保留開頭的步驟資訊與結尾的耗時，避免窄視窗每次刷新都換行。
    /// </summary>
    internal static string FitProgressLine(string text, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return string.Empty;
        }
        var characters = text.EnumerateRunes().ToArray();
        if (characters.Sum(GetColumnWidth) <= maxWidth)
        {
            return text;
        }
        if (maxWidth <= 3)
        {
            return new string('.', maxWidth);
        }

        int leftBudget = (maxWidth - 3) / 2;
        int rightBudget = maxWidth - 3 - leftBudget;
        int left = 0;
        int right = characters.Length;
        int width = 0;
        while (left < right && width + GetColumnWidth(characters[left]) <= leftBudget)
        {
            width += GetColumnWidth(characters[left++]);
        }
        width = 0;
        while (right > left && width + GetColumnWidth(characters[right - 1]) <= rightBudget)
        {
            width += GetColumnWidth(characters[--right]);
        }

        var result = new StringBuilder();
        for (int index = 0; index < left; index++)
        {
            result.Append(characters[index].ToString());
        }
        result.Append("...");
        for (int index = right; index < characters.Length; index++)
        {
            result.Append(characters[index].ToString());
        }
        return result.ToString();
    }

    /// <summary>
    /// 計算常見終端機中的中英文與 emoji 欄寬；合併字元及格式控制字元不另占欄位。
    /// </summary>
    private static int GetColumnWidth(Rune character)
    {
        if (Rune.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }
        return character.Value is >= 0x2E80 and <= 0xA4CF or >= 0xF900 and <= 0xFAFF or
            >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or
            >= 0x20000 and <= 0x3FFFD ? 2 : 1;
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
        _progressIsSingleRow = false;
        if (!TryGetCursor(out var cursor) || cursor.Left != 0)
        {
            Console.WriteLine();
        }
    }

    /// <summary>
    /// 嘗試取得游標與可見視窗尺寸；縮放後的尺寸用於判斷舊游標位置是否仍可覆寫。
    /// </summary>
    private static bool TryGetCursor(out (int Left, int Top, int Width, int Height, int BufferWidth) cursor)
    {
        try
        {
            var position = Console.GetCursorPosition();
            int bufferWidth = Console.BufferWidth;
            cursor = (position.Left, position.Top, Math.Min(bufferWidth, Console.WindowWidth), Console.WindowHeight, bufferWidth);
            return cursor.Width > 0 && cursor.Height > 0;
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
