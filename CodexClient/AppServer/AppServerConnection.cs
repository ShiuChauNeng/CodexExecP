
using System.Diagnostics;
using System.Text.Json;
using CodexClient.Internal;

namespace CodexClient.AppServer;

internal sealed class AppServerConnection : IAsyncDisposable
{
    // Fields
    private readonly Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly object _sync = new();

    private Task? _readTask;
    private Task? _errorTask;
    private long _requestId;
    private bool _started;
    private int _disposed;

    public event Action<AppServerEvent>? NotificationReceived;
    public Task Completion => _readTask ?? throw new InvalidOperationException("Connection has not started.");


    // Properties
    internal Func<JsonElement, CancellationToken, Task<object>>? UserInputHandler { get; set; }


    // Constructors
    public AppServerConnection(string? executablePath = null)
    {
        string codexPath = CodexExecutableResolver.Resolve(executablePath);
        var startInfo = CodexProcessStartInfoFactory.Create(codexPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("features.default_mode_request_user_input=true");

        startInfo.ArgumentList.Add("app-server");
        _process = new Process
        {
            StartInfo = startInfo
        };
    }


    // Methods
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

        // 排空 stderr，避免 Pipe Buffer 填滿。
        _errorTask = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);

        return Task.CompletedTask;
    }

    public async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        EnsureRunning();

        long id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
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

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

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

                    // Server 主動要求 Client 回覆的 Request。
                    //if (root.TryGetProperty("id", out var serverRequestId))
                    //{
                    //    await SendAsync(new
                    //    {
                    //        id = serverRequestId.Clone(),
                    //        error = new
                    //        {
                    //            code = -32601,
                    //            message = "Server request is not supported."
                    //        }
                    //    }, CancellationToken.None);

                    //    continue;
                    //}

                    if (root.TryGetProperty("id", out _))
                    {
                        // 不可以在 ReadLoop 直接 await 使用者輸入。
                        _ = HandleServerRequestAsync(root.Clone());
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
                foreach (var completion in _pending.Values)
                {
                    completion.TrySetException(failure);
                }

                _pending.Clear();
            }
        }

        throw failure;
    }

    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (!_started || _process.HasExited)
        {
            throw new InvalidOperationException("App Server is not running.");
        }
    }

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

    private async Task HandleServerRequestAsync(JsonElement request)
    {
        JsonElement requestId = request.GetProperty("id");
        string method = request.GetProperty("method").GetString()!;

        try
        {
            if (method != "item/tool/requestUserInput" || UserInputHandler is null)
            {
                throw new NotSupportedException($"Unsupported server request: {method}");
            }
            JsonElement parameters = request.GetProperty("params");
            object result = await UserInputHandler(parameters, CancellationToken.None);

            Console.WriteLine($"[UserInput] Request received: {parameters.GetRawText()}");

            await SendResponseAsync(requestId, result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            try
            {
                await SendAsync(new
                {
                    id = requestId,
                    error = new
                    {
                        code = -32603,
                        message = ex.Message
                    }
                }, CancellationToken.None);
            }
            catch
            {
                // Connection 可能已經關閉。
                // 不讓背景 Request Task 產生未觀察的例外。
            }
        }
    }

    private Task SendResponseAsync(JsonElement requestId, object result, CancellationToken cancellationToken)
    {
        return SendAsync(new
        {
            id = requestId,
            result
        }, cancellationToken);
    }
}
