namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 同步 Workflow 的主控台輸出，並在每次輸出後還原文字顏色。
/// </summary>
internal static class ConsoleWorkflowOutput
{
    private static readonly object Sync = new();

    internal static void WriteLine(string text, ConsoleColor? color = null)
        => WriteCore(() => Console.WriteLine(text), color);

    internal static void Write(string text, ConsoleColor? color = null)
        => WriteCore(() => Console.Write(text), color);

    internal static void WriteOrangeLine(string text)
        => WriteCore(() => Console.WriteLine(text), ConsoleColor.DarkYellow, "\u001b[38;2;255;165;0m");

    internal static void WriteOrange(string text)
        => WriteCore(() => Console.Write(text), ConsoleColor.DarkYellow, "\u001b[38;2;255;165;0m");

    private static void WriteCore(Action write, ConsoleColor? color, string? ansiColor = null)
    {
        lock (Sync)
        {
            if (Console.IsOutputRedirected || color is null)
            {
                write();
                return;
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
}
