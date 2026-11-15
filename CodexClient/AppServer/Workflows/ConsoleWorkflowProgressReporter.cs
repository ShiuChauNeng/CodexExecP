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
        Console.WriteLine($"[{progress.Timestamp:HH:mm:ss}] [{name}] {message}（累計 {progress.Elapsed.TotalSeconds:F0} 秒）");
    }
}
