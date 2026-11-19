using System.Diagnostics;
using System.Text;
using CodexClient.AppServer.Workflows;
using CodexClient.AppServer.Workflows.ConsoleUI;

internal static class ConsoleProgressChecks
{
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
                !lines.Any(line => line.Contains("累計 3 秒")) ||
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
        return 1;
    }

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
