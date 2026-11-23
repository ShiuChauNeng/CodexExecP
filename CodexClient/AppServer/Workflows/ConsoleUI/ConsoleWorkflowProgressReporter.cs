namespace CodexClient.AppServer.Workflows.ConsoleUI;

public sealed class ConsoleWorkflowProgressReporter : IProgress<AppServerWorkflowProgress>
{
    private readonly object _sync = new();
    private AppServerWorkflowProgress? _waiting;
    private object? _waitingLineKey;
    private bool _terminalNotified;

    /// <summary>
    /// 流程完成、停止、失敗或取消時通知一次；等待刷新與一般步驟完成不通知。
    /// </summary>
    public bool EnableNotifications { get; init; } = true;

    /// <summary>
    /// 將 Workflow 進度輸出至主控台。
    /// </summary>
    /// <param name="progress">要輸出的 Workflow 進度。</param>
    public void Report(AppServerWorkflowProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_sync)
        {
            ReportCore(progress);
            NotifyIfFinished(progress);
        }
    }

    /// <summary>
    /// 在流程完成、停止、失敗或取消時通知一次，忽略心跳與重複通知。
    /// </summary>
    private void NotifyIfFinished(AppServerWorkflowProgress progress)
    {
        if (progress.IsHeartbeat)
        {
            return;
        }
        if (progress.Status == AppServerWorkflowStatus.Starting)
        {
            _terminalNotified = false;
        }

        bool finished = progress.Status is AppServerWorkflowStatus.Completed or AppServerWorkflowStatus.Stopped
            or AppServerWorkflowStatus.Faulted or AppServerWorkflowStatus.Cancelled;
        if (!EnableNotifications || _terminalNotified || !finished)
        {
            return;
        }
        _terminalNotified = true;
        ConsoleWorkflowNotifier.Notify();
    }

    /// <summary>
    /// 依進度狀態選擇輸出文字與顏色，等待期間改用同一行更新。
    /// </summary>
    private void ReportCore(AppServerWorkflowProgress progress)
    {
        if (progress.SupportsHeartbeat)
        {
            WriteWaitingProgress(progress);
            return;
        }

        _waiting = null;
        _waitingLineKey = null;
        string name = progress.StepName ?? "Workflow";
        string line = $"[{progress.Timestamp:HH:mm:ss}] [{name}] {progress.Message}（{FormatTiming(progress)}）";
        if (!progress.IsHeartbeat && progress.Status is AppServerWorkflowStatus.NeedsRevision or AppServerWorkflowStatus.Cancelled)
        {
            ConsoleWorkflowOutput.WriteOrangeLine(line);
            return;
        }
        if (!progress.IsHeartbeat && progress.Status == AppServerWorkflowStatus.WaitingForInput)
        {
            ConsoleWorkflowOutput.WriteQuestionLine(line);
            return;
        }
        ConsoleColor? color = progress.IsHeartbeat ? ConsoleColor.DarkGray : progress.Status switch
        {
            AppServerWorkflowStatus.StepCompleted or AppServerWorkflowStatus.Completed => ConsoleColor.Green,
            AppServerWorkflowStatus.StepFailed or AppServerWorkflowStatus.Stopped or AppServerWorkflowStatus.Faulted => ConsoleColor.Red,
            _ => null
        };
        ConsoleWorkflowOutput.WriteLine(line, color);
    }

    /// <summary>
    /// 保留等待開始時間並更新等待行的累計分秒。
    /// </summary>
    private void WriteWaitingProgress(AppServerWorkflowProgress progress)
    {
        // 心跳延續原訊息與時間；切換步驟、thread 或階段時另起一行。
        bool continuesWaiting = progress.IsHeartbeat && _waiting is not null && _waiting.Status == progress.Status
            && _waiting.StepName == progress.StepName && _waiting.ThreadId == progress.ThreadId && _waiting.Message == progress.Message;
        if (!continuesWaiting)
        {
            _waiting = progress;
            _waitingLineKey = new object();
        }

        DateTimeOffset timestamp = Console.IsOutputRedirected ? progress.Timestamp : _waiting!.Timestamp;
        string name = progress.StepName ?? "Workflow";
        string line = $"[{timestamp:HH:mm:ss}] [{name}] {progress.Message}（{FormatTiming(progress)}）";
        ConsoleWorkflowOutput.WriteProgressLine(_waitingLineKey!, line, ConsoleColor.DarkGray);
    }

    /// <summary>
    /// 顯示流程累計時間；步驟結束時另外列出該次執行的總耗時。
    /// </summary>
    private static string FormatTiming(AppServerWorkflowProgress progress)
        => progress.StepDuration is { } duration
            ? $"本次步驟耗時{FormatElapsed(duration)}，累計{FormatElapsed(progress.Elapsed)}"
            : $"累計{FormatElapsed(progress.Elapsed)}";

    /// <summary>
    /// 以總分鐘與剩餘秒數顯示時間；超過一小時仍持續累計分鐘。
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed)
        => $"{(long)elapsed.TotalMinutes}分{elapsed.Seconds}秒";
}
