using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.AppServer.Workflows.ConsoleUI;
using CodexClient.AppServer.Workflows.Internal;

internal static class WorkflowColorChecks
{
    /// <summary>
    /// 驗證顯示分類在原有狀態、心跳與最終結果之間維持一致。
    /// </summary>
    internal static async Task<int> RunAsync()
    {
        var recorder = new Recorder();
        await using (var scope = new WorkflowProgressScope(recorder, TimeSpan.FromMilliseconds(10)))
        {
            scope.Update(AppServerWorkflowStatus.Validating, "warning", messageKind: AppServerWorkflowMessageKind.Warning);
            await recorder.WarningHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(3));
            scope.Update(AppServerWorkflowStatus.Validating, "running");
            await recorder.RunningHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var warnings = recorder.Items.Where(item => item.Message == "warning").ToArray();
        var running = recorder.Items.Where(item => item.Message == "running").ToArray();
        Assert(warnings.Length >= 2 && running.Length >= 2);
        Assert(warnings.All(item => item.Status == AppServerWorkflowStatus.Validating && item.SupportsHeartbeat && item.StepDuration is null));
        Assert(warnings.All(item => ConsoleWorkflowProgressReporter.GetMessageKind(item) == AppServerWorkflowMessageKind.Warning));
        Assert(running.All(item => ConsoleWorkflowProgressReporter.GetMessageKind(item) == AppServerWorkflowMessageKind.Waiting));
        Assert(ConsoleWorkflowOutput.GetMessageStyle(AppServerWorkflowMessageKind.Warning) == (ConsoleColor.DarkYellow, "\u001b[38;2;255;165;0m"));
        Assert(ConsoleWorkflowOutput.GetMessageStyle(AppServerWorkflowMessageKind.Waiting) == (ConsoleColor.DarkGray, null));
        Console.WriteLine("PASS: actual warning heartbeats retain orange while a later execution returns to gray without changing Status");

        var issueStep = new AppServerWorkflowStepResult("Implement", "thread", new TaskRunResult("thread", "turn", "Done")
        {
            Issues = ["Unresolved issue"]
        }, null) { Accepted = true };
        var inconclusive = issueStep with { Name = "Acceptance", Accepted = false, AcceptanceDecision = AcceptanceDecision.Inconclusive };
        var stepRecorder = new Recorder();
        await using (var scope = new WorkflowProgressScope(stepRecorder, TimeSpan.FromHours(1)))
        {
            scope.ReportStepResult(issueStep);
            scope.ReportStepResult(inconclusive);
        }
        Assert(stepRecorder.Items[0].Status == AppServerWorkflowStatus.StepCompleted && issueStep.Accepted && !issueStep.Succeeded);
        Assert(stepRecorder.Items[1].Status == AppServerWorkflowStatus.Validating);
        Assert(stepRecorder.Items.All(item => item.MessageKind == AppServerWorkflowMessageKind.Warning));
        Assert(ConsoleWorkflowResultReporter.GetStatus(issueStep) == ("[通過]", AppServerWorkflowMessageKind.Warning));
        Assert(ConsoleWorkflowResultReporter.GetStatus(inconclusive) == ("[無法判定]", AppServerWorkflowMessageKind.Warning));
        Console.WriteLine("PASS: accepted issues and inconclusive results share warning colors across progress and final reports");

        var failed = issueStep with { Accepted = false, Error = new IOException("execution failed") };
        var failedRecorder = new Recorder();
        await using (var scope = new WorkflowProgressScope(failedRecorder, TimeSpan.FromHours(1)))
        {
            scope.ReportStepResult(failed);
        }
        Assert(failedRecorder.Items.Single().Status == AppServerWorkflowStatus.StepFailed);
        Assert(failedRecorder.Items.Single().MessageKind == AppServerWorkflowMessageKind.Error);
        Assert(ConsoleWorkflowResultReporter.GetStatus(failed) == ("[執行失敗]", AppServerWorkflowMessageKind.Error));
        Assert(ConsoleWorkflowOutput.GetMessageStyle(AppServerWorkflowMessageKind.Error).Color == ConsoleColor.Red);
        Assert(ConsoleWorkflowOutput.GetMessageStyle(AppServerWorkflowMessageKind.Success).Color == ConsoleColor.Green);
        var legacy = new AppServerWorkflowProgress(DateTimeOffset.Now, AppServerWorkflowStatus.WaitingForInput,
            "Step", "thread", "question", TimeSpan.Zero);
        Assert(ConsoleWorkflowProgressReporter.GetMessageKind(legacy) == AppServerWorkflowMessageKind.Question);
        Assert(ConsoleWorkflowProgressReporter.GetMessageKind(legacy with { IsHeartbeat = true }) == AppServerWorkflowMessageKind.Question);
        Assert(ConsoleWorkflowOutput.GetMessageStyle(AppServerWorkflowMessageKind.Question).AnsiColor == "\u001b[38;2;225;145;215m");
        Console.WriteLine("PASS: execution errors remain red and legacy status-based colors stay stable across heartbeat flags");

        var legacyValidation = legacy with
        {
            Status = AppServerWorkflowStatus.Validating,
            StepDuration = TimeSpan.FromSeconds(5)
        };
        foreach (bool heartbeat in new[] { false, true })
        {
            var progress = legacyValidation with { IsHeartbeat = heartbeat };
            Assert(ConsoleWorkflowProgressReporter.GetMessageKind(progress) == AppServerWorkflowMessageKind.Warning);
            Assert(ConsoleWorkflowProgressReporter.GetMessageKind(progress with { StepDuration = TimeSpan.Zero }) == AppServerWorkflowMessageKind.Warning);
            Assert(ConsoleWorkflowProgressReporter.GetMessageKind(progress with { StepDuration = null }) == AppServerWorkflowMessageKind.Waiting);
            Assert(ConsoleWorkflowProgressReporter.GetMessageKind(progress with { MessageKind = AppServerWorkflowMessageKind.Waiting }) == AppServerWorkflowMessageKind.Waiting);
            Assert(ConsoleWorkflowProgressReporter.GetMessageKind(progress with { StepDuration = null, MessageKind = AppServerWorkflowMessageKind.Warning }) == AppServerWorkflowMessageKind.Warning);
        }
        Console.WriteLine("PASS: legacy completed validation remains orange while explicit message kinds retain priority");
        return 4;
    }

    /// <summary>
    /// 驗證條件，不符合時使測試失敗。
    /// </summary>
    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Workflow color assertion failed.");
        }
    }

    private sealed class Recorder : IProgress<AppServerWorkflowProgress>
    {
        internal List<AppServerWorkflowProgress> Items { get; } = [];
        internal TaskCompletionSource WarningHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RunningHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 保存進度並在收到實際心跳時通知測試，讀取清單前由 scope 結束所有回報。
        /// </summary>
        public void Report(AppServerWorkflowProgress value)
        {
            Items.Add(value);
            if (value.IsHeartbeat && value.Message == "warning")
            {
                WarningHeartbeat.TrySetResult();
            }
            if (value.IsHeartbeat && value.Message == "running")
            {
                RunningHeartbeat.TrySetResult();
            }
        }
    }
}
