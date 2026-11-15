using System.Diagnostics;

namespace CodexClient.AppServer.Workflows;

internal sealed class ExecutionTimeBudget : IDisposable
{
    // Fields
    private readonly CancellationTokenSource _cancellation;
    private readonly TimeSpan? _limit;
    private readonly Stopwatch _elapsed = new();
    internal CancellationToken Token => _cancellation.Token;


    // Constructors
    internal ExecutionTimeBudget(TimeSpan? limit, CancellationToken cancellationToken)
    {
        _limit = limit;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _elapsed.Start();
        if (limit is { } duration)
        {
            _cancellation.CancelAfter(duration);
        }
    }


    // Methods
    /// <summary>
    /// 暫停累計執行時間計算。
    /// </summary>
    internal void Pause()
    {
        _cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
        _elapsed.Stop();
        // 避免額度剛用完、計時器尚未排程時被暫停。
        if (_limit is { } limit && _elapsed.Elapsed >= limit)
        {
            _cancellation.Cancel();
        }
        Token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 恢復累計執行時間計算。
    /// </summary>
    internal void Resume()
    {
        Token.ThrowIfCancellationRequested();
        if (_limit is { } limit)
        {
            TimeSpan remaining = limit - _elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                _cancellation.Cancel();
                Token.ThrowIfCancellationRequested();
            }
            _cancellation.CancelAfter(remaining);
        }
        _elapsed.Start();
    }

    /// <summary>
    /// 釋放執行額度使用的取消資源。
    /// </summary>
    public void Dispose() => _cancellation.Dispose();
}
