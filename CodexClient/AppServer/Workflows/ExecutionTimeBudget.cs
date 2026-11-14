using System.Diagnostics;

namespace CodexClient.AppServer.Workflows;

/// <summary>累計執行額度；暫停期間仍遵守外部取消與整體逾時。</summary>
internal sealed class ExecutionTimeBudget : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TimeSpan? _limit;
    private readonly Stopwatch _elapsed = new();
    internal CancellationToken Token => _cancellation.Token;

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

    public void Dispose() => _cancellation.Dispose();
}
