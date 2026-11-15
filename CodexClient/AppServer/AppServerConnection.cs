using System.Diagnostics;
using System.Text.Json;
using System.Text;
using CodexClient.Internal;

namespace CodexClient.AppServer;

internal sealed class AppServerConnection : IAsyncDisposable
{
    // Fields
    private readonly Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly object _sync = new();
    private readonly StringBuilder _standardError = new();
    private bool _standardErrorTruncated;

    /// <summary>連線層級 stderr 尾端，可能包含先前 turn 的內容。</summary>
    internal string StandardError
    {
        get
        {
            lock (_standardError)
                return (_standardErrorTruncated ? "[stderr truncated; showing last 16384 characters]\n" : "") + _standardError;
        }
    }

    private Task? _readTask;
    private Task? _errorTask;
    private long _requestId;
    private bool _started;
    private int _disposed;
    private Exception? _failure;

    /// <summary>
    /// 收到 App Server notification 時觸發的事件。
    /// </summary>
    public event Action<AppServerEvent>? NotificationReceived;

    /// <summary>
    /// 取得讀取迴圈的完成工作；連線尚未啟動時會擲出例外。
    /// </summary>
    public Task Completion => _readTask ?? throw new InvalidOperationException("Connection has not started.");


    // Constructors
    public AppServerConnection(string? executablePath = null)
    {
        string codexPath = CodexExecutableResolver.Resolve(executablePath);
        var startInfo = CodexProcessStartInfoFactory.Create(codexPath);
        startInfo.ArgumentList.Add("app-server");
        _process = new Process
        {
            StartInfo = startInfo
        };
    }


    // Methods
    /// <summary>
    /// 啟動 App Server 程序與讀取迴圈。
    /// </summary>
    /// <param name="cancellationToken">用於取消啟動作業的權杖。</param>
    /// <returns>表示啟動作業的非同步工作。</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (_started)
        {
            throw new InvalidOperationException("Connection has already started.");
        }

        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start App Server.");
        }

        _started = true;

        _readTask = ReadLoopAsync();

        // 持續排空 stderr，保留有限長度的尾端供診斷。
        _errorTask = ReadStandardErrorAsync();

        return Task.CompletedTask;
    }


    /// <summary>
    /// 持續讀取並保留 App Server 標準錯誤輸出的尾端內容。
    /// </summary>
    /// <returns>表示讀取作業的非同步工作。</returns>
    private async Task ReadStandardErrorAsync()
    {
        var buffer = new char[2048];
        int count;
        while ((count = await _process.StandardError.ReadAsync(buffer.AsMemory())) > 0)
        {
            lock (_standardError)
            {
                _standardError.Append(buffer, 0, count);
                if (_standardError.Length > 16384)
                {
                    _standardError.Remove(0, _standardError.Length - 16384);
                    _standardErrorTruncated = true;
                }
            }
        }
    }

    /// <summary>
    /// 傳送 JSON-RPC request 並等待對應的回應。
    /// </summary>
    /// <param name="method">JSON-RPC 方法名稱。</param>
    /// <param name="parameters">要傳送的方法參數。</param>
    /// <param name="cancellationToken">用於取消要求的權杖。</param>
    /// <returns>非同步回傳 App Server 的 JSON 回應。</returns>
    public async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        long id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            // 與讀取迴圈的失敗清理使用同一把鎖，避免失敗後才新增永遠等不到回覆的 Request。
            EnsureRunning();
            _pending.Add(id, completion);
        }

        try
        {
            await SendAsync(new
            {
                id,
                method,
                @params = parameters
            }, cancellationToken);

            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                _pending.Remove(id);
            }
        }
    }

    /// <summary>
    /// 傳送不需要回應的 JSON-RPC notification。
    /// </summary>
    /// <param name="method">JSON-RPC 方法名稱。</param>
    /// <param name="cancellationToken">用於取消傳送作業的權杖。</param>
    /// <returns>表示傳送作業的非同步工作。</returns>
    public Task SendNotificationAsync(string method, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        EnsureRunning();

        return SendAsync(new
        {
            method,
            @params = new { }
        }, cancellationToken);
    }

    /// <summary>
    /// 將 JSON-RPC 訊息寫入 App Server 標準輸入。
    /// </summary>
    /// <param name="message">要序列化並傳送的訊息。</param>
    /// <param name="cancellationToken">用於取消傳送作業的權杖。</param>
    /// <returns>表示傳送作業的非同步工作。</returns>
    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRunning();
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 持續讀取 App Server 回應並分派通知或完成中的要求。
    /// </summary>
    /// <returns>表示讀取迴圈的非同步工作。</returns>
    private async Task ReadLoopAsync()
    {
        Exception? failure = null;

        try
        {
            while (true)
            {
                string? line = await _process.StandardOutput.ReadLineAsync();

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("method", out var methodElement))
                {
                    string method = methodElement.GetString() ?? throw new JsonException("Method is null.");

                    if (root.TryGetProperty("id", out var serverRequestId))
                    {
                        await SendAsync(new
                        {
                            id = serverRequestId.Clone(),
                            error = new
                            {
                                code = -32601,
                                message = "Server request is not supported by this client."
                            }
                        }, CancellationToken.None);
                        continue;
                    }

                    JsonElement parameters = root.TryGetProperty("params", out var value) ? value.Clone() : default;

                    NotificationReceived?.Invoke(new AppServerEvent(method, parameters));

                    continue;
                }

                if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt64(out long id))
                {
                    continue;
                }

                TaskCompletionSource<JsonElement>? completion;

                lock (_sync)
                {
                    _pending.TryGetValue(id, out completion);
                }

                if (completion is null)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(new InvalidOperationException($"App Server request failed: {error}"));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetException(new JsonException("Response has no result."));
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            failure ??= new EndOfStreamException("App Server connection closed.");
            lock (_sync)
            {
                _failure = failure;
                foreach (var completion in _pending.Values)
                {
                    completion.TrySetException(failure);
                }

                _pending.Clear();
            }
        }

        throw failure;
    }

    /// <summary>
    /// 確認連線已啟動且尚未失敗或釋放。
    /// </summary>
    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        lock (_sync)
        {
            if (_failure is not null)
            {
                throw new IOException("App Server connection has stopped reading messages.", _failure);
            }

            if (!_started || _process.HasExited)
            {
                throw new InvalidOperationException("App Server is not running.");
            }
        }
    }

    /// <summary>
    /// 非同步停止 App Server 程序並釋放連線資源。
    /// </summary>
    /// <returns>表示釋放作業的非同步值工作。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_started && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            if (_started)
            {
                await _process.WaitForExitAsync();
            }

            if (_readTask is not null)
            {
                try
                {
                    await _readTask;
                }
                catch (Exception)
                {
                    // 關閉連線時可能發生預期的 EOF 或 Pipe 結束。
                }
            }

            if (_errorTask is not null)
            {
                await _errorTask;
            }
        }
        finally
        {
            _process.Dispose();
            _writeLock.Dispose();
        }
    }
}
