namespace CodexClient.AppServer.Workflows.ConsoleUI;

public sealed class ConsoleWorkflowProgressReporter : IProgress<AppServerWorkflowProgress>
{
    private readonly object _sync = new();
    private AppServerWorkflowProgress? _waiting;
    private object? _waitingLineKey;
    private bool _terminalNotified;

    /// <summary>流程完成、停止、失敗或取消時通知一次；等待刷新與一般步驟完成不通知。</summary>
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
        string line = $"[{progress.Timestamp:HH:mm:ss}] [{name}] {progress.Message}（累計 {progress.Elapsed.TotalSeconds:F0} 秒）";
        if (!progress.IsHeartbeat && progress.Status is AppServerWorkflowStatus.NeedsRevision or AppServerWorkflowStatus.Cancelled)
        {
            ConsoleWorkflowOutput.WriteOrangeLine(line);
            return;
        }
        ConsoleColor? color = progress.IsHeartbeat ? ConsoleColor.DarkGray : progress.Status switch
        {
            AppServerWorkflowStatus.WaitingForInput => ConsoleColor.Magenta,
            AppServerWorkflowStatus.StepCompleted or AppServerWorkflowStatus.Completed => ConsoleColor.Green,
            AppServerWorkflowStatus.StepFailed or AppServerWorkflowStatus.Stopped or AppServerWorkflowStatus.Faulted => ConsoleColor.Red,
            _ => null
        };
        ConsoleWorkflowOutput.WriteLine(line, color);
    }

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
        string line = $"[{timestamp:HH:mm:ss}] [{name}] {progress.Message}（累計 {progress.Elapsed.TotalSeconds:F0} 秒）";
        ConsoleWorkflowOutput.WriteProgressLine(_waitingLineKey!, line, ConsoleColor.DarkGray);
    }
}
