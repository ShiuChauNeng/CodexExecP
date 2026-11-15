using System.Diagnostics;

namespace CodexClient.AppServer.Workflows;

internal sealed class WorkflowProgressScope : IAsyncDisposable
{
    // Fields
    private IProgress<AppServerWorkflowProgress>? _reporter;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly Task _heartbeat;
    private AppServerWorkflowProgress? _current;
    internal Exception? ReportingError { get; private set; }


    // Constructors
    internal WorkflowProgressScope(IProgress<AppServerWorkflowProgress>? reporter, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _reporter = reporter;
        _heartbeat = reporter is null ? Task.CompletedTask : HeartbeatAsync(interval);
    }


    // Methods
    /// <summary>
    /// 更新目前 Workflow 狀態並立即回報。
    /// </summary>
    /// <param name="status">新的 Workflow 狀態。</param>
    /// <param name="message">狀態訊息。</param>
    /// <param name="stepName">相關步驟名稱。</param>
    /// <param name="threadId">相關 thread 識別碼。</param>
    internal void Update(AppServerWorkflowStatus status, string message, string? stepName = null, string? threadId = null)
    {
        lock (_sync)
        {
            _current = new(DateTimeOffset.Now, status, stepName, threadId, message, _elapsed.Elapsed);
            Report(_current);
        }
    }

    /// <summary>
    /// 以指定間隔產生 Workflow 心跳通知。
    /// </summary>
    /// <param name="interval">心跳通知間隔。</param>
    /// <returns>表示心跳工作的非同步工作。</returns>
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
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 嘗試回報 Workflow 進度並保留回報錯誤。
    /// </summary>
    /// <param name="progress">要回報的 Workflow 進度。</param>
    private void Report(AppServerWorkflowProgress progress)
    {
        try
        {
            _reporter?.Report(progress);
        }
        catch (Exception ex)
        {
            // 顯示錯誤不能取代原本的任務結果；停用後續輸出並保留原因供呼叫端查詢。
            ReportingError = ex;
            _reporter = null;
        }
    }

    /// <summary>
    /// 停止心跳並釋放進度範圍資源。
    /// </summary>
    /// <returns>表示釋放作業的非同步值工作。</returns>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _heartbeat;
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
