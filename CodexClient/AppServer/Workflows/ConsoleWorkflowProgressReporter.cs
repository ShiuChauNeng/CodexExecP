namespace CodexClient.AppServer.Workflows;

public sealed class ConsoleWorkflowProgressReporter : IProgress<AppServerWorkflowProgress>
{
    /// <summary>
    /// 將 Workflow 進度輸出至主控台。
    /// </summary>
    /// <param name="progress">要輸出的 Workflow 進度。</param>
    public void Report(AppServerWorkflowProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string name = progress.StepName ?? "Workflow";
        string message = progress.IsHeartbeat ? $"仍在等待：{progress.Message}" : progress.Message;
        string line = $"[{progress.Timestamp:HH:mm:ss}] [{name}] {message}（累計 {progress.Elapsed.TotalSeconds:F0} 秒）";
        if (!progress.IsHeartbeat && progress.Status is AppServerWorkflowStatus.NeedsRevision or AppServerWorkflowStatus.Cancelled)
        {
            ConsoleWorkflowOutput.WriteOrangeLine(line);
            return;
        }
        ConsoleColor? color = progress.IsHeartbeat ? ConsoleColor.DarkGray : progress.Status switch
        {
            AppServerWorkflowStatus.Running => ConsoleColor.DarkGray,
            AppServerWorkflowStatus.WaitingForInput => ConsoleColor.Magenta,
            AppServerWorkflowStatus.StepCompleted or AppServerWorkflowStatus.Completed => ConsoleColor.Green,
            AppServerWorkflowStatus.StepFailed or AppServerWorkflowStatus.Stopped or AppServerWorkflowStatus.Faulted => ConsoleColor.Red,
            _ => null
        };
        ConsoleWorkflowOutput.WriteLine(line, color);
    }
}
