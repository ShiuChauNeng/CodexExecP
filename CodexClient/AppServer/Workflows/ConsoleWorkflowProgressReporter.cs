namespace CodexClient.AppServer.Workflows;

/// <summary>同步輸出 Workflow 狀態，可重複使用於 Console 應用程式。</summary>
public sealed class ConsoleWorkflowProgressReporter : IProgress<AppServerWorkflowProgress>
{
    public void Report(AppServerWorkflowProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string name = progress.StepName ?? "Workflow";
        string message = progress.IsHeartbeat
            ? $"仍在等待：{progress.Message}"
            : progress.Message;
        Console.WriteLine($"[{progress.Timestamp:HH:mm:ss}] [{name}] {message}（累計 {progress.Elapsed.TotalSeconds:F0} 秒）");
    }
}
