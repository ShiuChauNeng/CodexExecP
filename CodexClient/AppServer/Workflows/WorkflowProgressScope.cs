using System.Diagnostics;

namespace CodexClient.AppServer.Workflows;

internal sealed class WorkflowProgressScope : IAsyncDisposable
{
    private IProgress<AppServerWorkflowProgress>? _reporter;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly Task _heartbeat;
    private AppServerWorkflowProgress? _current;
    internal Exception? ReportingError { get; private set; }

    internal WorkflowProgressScope(IProgress<AppServerWorkflowProgress>? reporter, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
        _reporter = reporter;
        _heartbeat = reporter is null ? Task.CompletedTask : HeartbeatAsync(interval);
    }

    internal void Update(AppServerWorkflowStatus status, string message, string? stepName = null, string? threadId = null)
    {
        lock (_sync)
        {
            _current = new(DateTimeOffset.Now, status, stepName, threadId, message, _elapsed.Elapsed);
            Report(_current);
        }
    }

    private async Task HeartbeatAsync(TimeSpan interval)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                lock (_sync)
                {
                    // 輸入期間保持安靜，避免打斷使用者回答；完成或停止後也不再輸出。
                    if (_current?.Status is AppServerWorkflowStatus.Starting or AppServerWorkflowStatus.CreatingThread
                        or AppServerWorkflowStatus.Running or AppServerWorkflowStatus.Validating)
                    {
                        Report(_current with
                        {
                            Timestamp = DateTimeOffset.Now,
                            Elapsed = _elapsed.Elapsed,
                            IsHeartbeat = true
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Report(AppServerWorkflowProgress progress)
    {
        try { _reporter?.Report(progress); }
        catch (Exception ex)
        {
            // 顯示錯誤不能取代原本的任務結果；停用後續輸出並保留原因供呼叫端查詢。
            ReportingError = ex;
            _reporter = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _heartbeat; }
        finally { _stop.Dispose(); }
    }
}
