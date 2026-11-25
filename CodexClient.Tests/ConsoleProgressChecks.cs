using System.Diagnostics;
using System.Text;
using CodexClient.AppServer.Workflows;
using CodexClient.AppServer.Workflows.ConsoleUI;

internal static class ConsoleProgressChecks
{
    /// <summary>
    /// 以子程序擷取 Console 輸出，驗證等待、問答與重新導向行為。
    /// </summary>
    internal static async Task<int> RunAsync()
    {
        string executable = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "CodexClient.Tests.exe" : "CodexClient.Tests");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--demo-progress");
        start.ArgumentList.Add("--fast");
        using var process = Process.Start(start) ?? throw new Exception("Progress demo did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string output = await outputTask;
            string error = await errorTask;
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error) || output.Contains('\u001b') || output.Contains('\a') ||
                output.Replace("\r\n", "\n").Contains('\r') || output.Contains("仍在等待") ||
                lines.Count(line => line.Contains("[Spec] 等待 AI 執行結果")) != 4 ||
                !lines.Any(line => line.Contains("累計0分3秒")) ||
                !lines.Any(line => line.Contains("[Spec] 等待使用者回答")) ||
                !lines.Any(line => line.StartsWith("請輸入答案：demo")) ||
                !lines.Last().Contains("Workflow 已完成"))
            {
                throw new Exception($"Redirected progress output was invalid.\n{output}\n{error}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        Console.WriteLine("PASS: redirected progress preserves heartbeat records and separates questions without terminal controls");
        VerifyElapsedFormatting();
        VerifyProgressWidth();
        return 3;
    }

    /// <summary>
    /// 驗證視窗縮窄時保留等待行頭尾，且不切斷中文字元或 emoji；放大後仍可顯示完整文字。
    /// </summary>
    private static void VerifyProgressWidth()
    {
        const string line = "[12:00:00] [Acceptance] 正在等待 AI 執行驗收並更新結果。（累計2分15秒）";
        string narrow = ConsoleWorkflowOutput.FitProgressLine(line, 64);
        if (!narrow.StartsWith("[12:00:00]", StringComparison.Ordinal) || !narrow.EndsWith("（累計2分15秒）", StringComparison.Ordinal) ||
            !narrow.Contains("...") || ConsoleWorkflowOutput.FitProgressLine(line, 200) != line ||
            ConsoleWorkflowOutput.FitProgressLine("測試文字", 7) != "測...字" ||
            ConsoleWorkflowOutput.FitProgressLine("😀abcd😀", 7) != "😀...😀" ||
            ConsoleWorkflowOutput.FitProgressLine("abcdef", 4) != "...f")
        {
            throw new Exception("Progress line did not fit the requested terminal width.");
        }
        foreach (int width in new[] { 0, 1, 2, 3 })
        {
            if (ConsoleWorkflowOutput.FitProgressLine(line, width) != new string('.', width))
            {
                throw new Exception("Very narrow progress line exceeds its available width.");
            }
        }
        foreach (int width in new[] { 200, 64, 7, 40, 200 })
        {
            string resized = ConsoleWorkflowOutput.FitProgressLine(line, width);
            if (resized.IndexOfAny(['\r', '\n', '\t']) >= 0 || resized.Length > width)
            {
                throw new Exception("Resizing introduced a line break or exceeded the available progress width.");
            }
        }
        Console.WriteLine("PASS: narrow progress lines preserve their edges without wrapping and expand back to full text");
    }

    /// <summary>
    /// 驗證等待與一般進度皆以分秒顯示，並正確處理分鐘及小時邊界。
    /// </summary>
    private static void VerifyElapsedFormatting()
    {
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var reporter = new ConsoleWorkflowProgressReporter { EnableNotifications = false };
            var cases = new (double Seconds, string Expected)[]
            {
                (0, "累計0分0秒"), (59.9, "累計0分59秒"), (60, "累計1分0秒"),
                (65, "累計1分5秒"), (3601, "累計60分1秒")
            };
            foreach (var item in cases)
            {
                output.GetStringBuilder().Clear();
                var progress = new AppServerWorkflowProgress(DateTimeOffset.Now, AppServerWorkflowStatus.Running,
                    "Spec", "demo", "等待 AI 執行結果。", TimeSpan.FromSeconds(item.Seconds));
                reporter.Report(progress);
                reporter.Report(progress with { Status = AppServerWorkflowStatus.StepCompleted, Message = "步驟完成。" });
                if (output.ToString().Split(item.Expected, StringSplitOptions.None).Length != 3)
                {
                    throw new Exception($"Expected elapsed format {item.Expected} in both waiting and completion output.");
                }
            }
            foreach (var status in new[] { AppServerWorkflowStatus.StepCompleted, AppServerWorkflowStatus.StepFailed,
                AppServerWorkflowStatus.NeedsRevision, AppServerWorkflowStatus.Validating })
            {
                output.GetStringBuilder().Clear();
                reporter.Report(new AppServerWorkflowProgress(DateTimeOffset.Now, status,
                    "Acceptance", "demo", "本輪步驟結束。", TimeSpan.FromSeconds(185))
                {
                    StepDuration = TimeSpan.FromSeconds(65)
                });
                if (!output.ToString().Contains("本次步驟耗時1分5秒，累計3分5秒"))
                {
                    throw new Exception($"Missing separate step and workflow duration for {status}.");
                }
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
        Console.WriteLine("PASS: elapsed time uses total minutes and remaining seconds in waiting and completion output");
    }

    /// <summary>
    /// 模擬等待、同一行刷新與問答輸出，供顯示檢查使用。
    /// </summary>
    internal static async Task DemoAsync(bool fast)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var reporter = new ConsoleWorkflowProgressReporter();
        var running = new AppServerWorkflowProgress(DateTimeOffset.Now, AppServerWorkflowStatus.Running,
            "Spec", "demo", "等待 AI 執行結果。", TimeSpan.Zero);
        reporter.Report(running);
        for (int seconds = 1; seconds <= 3; seconds++)
        {
            await Task.Delay(fast ? TimeSpan.Zero : TimeSpan.FromSeconds(1));
            reporter.Report(running with
            {
                Timestamp = running.Timestamp.AddSeconds(seconds), Elapsed = TimeSpan.FromSeconds(seconds), IsHeartbeat = true
            });
        }

        var waitingForInput = running with
        {
            Timestamp = running.Timestamp.AddSeconds(3), Elapsed = TimeSpan.FromSeconds(3),
            Status = AppServerWorkflowStatus.WaitingForInput, Message = "等待使用者回答，共 1 個問題。"
        };
        reporter.Report(waitingForInput);
        TextReader originalInput = Console.In;
        try
        {
            Console.SetIn(new StringReader("demo\n"));
            var answers = await new ConsoleWorkflowInputHandler().AnswerAsync(new("Spec", "demo", "turn",
                "這是進度刷新示範，會自動填入 demo，不呼叫真實 AI。", ["請確認等待行與問題已分開顯示。"]));
            Console.WriteLine(answers[0]);
        }
        finally
        {
            Console.SetIn(originalInput);
        }
        var resumed = waitingForInput with { Status = AppServerWorkflowStatus.Running, Message = "已收到回答，繼續等待 AI 執行結果。" };
        reporter.Report(resumed);
        await Task.Delay(fast ? TimeSpan.Zero : TimeSpan.FromSeconds(1));
        reporter.Report(resumed with { Timestamp = running.Timestamp.AddSeconds(4), Elapsed = TimeSpan.FromSeconds(4), IsHeartbeat = true });
        reporter.Report(resumed with { Status = AppServerWorkflowStatus.StepCompleted, Message = "步驟完成。", Elapsed = TimeSpan.FromSeconds(4) });
        reporter.Report(resumed with { Status = AppServerWorkflowStatus.Completed, StepName = null, Message = "Workflow 已完成。", Elapsed = TimeSpan.FromSeconds(4) });
    }
}
