
using System.Text;
using System.Text.Json;

namespace CodexClient.AppServer;

public sealed class CodexAppServerClient : IAsyncDisposable, IAppServerClient
{
    // Fields
    private readonly AppServerConnection _connection;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private AppServerTurnDiagnostics? _lastTurnDiagnostics;

    /// <summary>最近一次 turn 的診斷快照，失敗或取消後仍可讀取。</summary>
    public AppServerTurnDiagnostics? LastTurnDiagnostics
    {
        get => Volatile.Read(ref _lastTurnDiagnostics);
        private set => Volatile.Write(ref _lastTurnDiagnostics, value);
    }

    /// <summary>連線層級 stderr 尾端，最多保留 16384 個字元及截斷提示。</summary>
    public string StandardError => _connection.StandardError;

    private bool _initialized;
    private int _disposed;


    // Constructors
    public CodexAppServerClient(string? executablePath = null)
    {
        _connection = new AppServerConnection(executablePath);
    }


    // Methods
    public async Task StartAsync(string applicationName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        if (_initialized)
        {
            throw new InvalidOperationException("App Server has already been initialized.");
        }

        try
        {
            await _connection.StartAsync(cancellationToken);
            await _connection.SendRequestAsync("initialize",
                new
                {
                    clientInfo = new
                    {
                        name = applicationName,
                        title = applicationName,
                        version = "1.0.0"
                    }
                },
                cancellationToken);

            await _connection.SendNotificationAsync("initialized", cancellationToken);
            _initialized = true;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException(workspaceRoot);
        }

        JsonElement result = await _connection.SendRequestAsync("thread/start",
            new
            {
                cwd = Path.GetFullPath(workspaceRoot),
                approvalPolicy = "never",
                sandbox = "read-only"
            },
            cancellationToken);

        return result.GetProperty("thread").GetProperty("id").GetString() ?? throw new JsonException("Thread ID is missing.");
    }

    public async Task<TurnResult> RunTurnAsync(
        string threadId,
        string prompt,
        JsonElement? outputSchema = null,
        AppServerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        options ??= new();
        Dictionary<string, object?> parameters = options.CreateParameters(threadId, prompt, outputSchema);

        await _turnLock.WaitAsync(cancellationToken);
        var items = new List<JsonElement>();
        var errors = new List<JsonElement>();
        var diagnosticsSync = new object();
        string? currentTurnId = null;
        LastTurnDiagnostics = null;
        AppServerTurnDiagnostics Snapshot()
        {
            lock (diagnosticsSync)
                return new(threadId, currentTurnId,
                    Array.AsReadOnly(items.ToArray()), Array.AsReadOnly(errors.ToArray()), StandardError);
        }
        try
        {
            var turnCompletion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new StringBuilder();
            string? finalResponse = null;
            void OnNotification(AppServerEvent notification)
            {
                JsonElement parameters = notification.Params;
                if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("threadId", out var threadValue) || threadValue.GetString() != threadId)
                {
                    return;
                }

                if (notification.Method == "item/completed" && parameters.TryGetProperty("item", out var completedItem))
                {
                    lock (diagnosticsSync)
                    {
                        items.Add(completedItem.Clone());
                        LastTurnDiagnostics = Snapshot();
                    }
                }
                if (notification.Method == "error")
                {
                    lock (diagnosticsSync)
                    {
                        errors.Add(parameters.Clone());
                        LastTurnDiagnostics = Snapshot();
                    }
                }

                if (notification.Method == "item/agentMessage/delta" && parameters.TryGetProperty("delta", out var delta))
                {
                    response.Append(delta.GetString());
                }

                if (notification.Method == "item/completed" &&
                    parameters.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("type", out var itemType) &&
                    itemType.GetString() == "agentMessage" &&
                    item.TryGetProperty("text", out var text))
                {
                    finalResponse = text.GetString();
                }

                if (notification.Method == "turn/completed")
                {
                    turnCompletion.TrySetResult(parameters.GetProperty("turn").Clone());
                }
            }

            // 必須在送出 turn/start 前訂閱，避免漏接快速完成的 Turn。
            _connection.NotificationReceived += OnNotification;

            try
            {
                JsonElement result = await _connection.SendRequestAsync("turn/start", parameters, cancellationToken);
                string turnId = result.GetProperty("turn").GetProperty("id").GetString() ?? throw new JsonException("Turn ID is missing.");

                lock (diagnosticsSync)
                {
                    currentTurnId = turnId;
                    LastTurnDiagnostics = Snapshot();
                }
                // 同時等待 Turn 完成或底層連線終止。
                Task finished = await Task.WhenAny(turnCompletion.Task, _connection.Completion).WaitAsync(cancellationToken);
                if (finished == _connection.Completion)
                {
                    await _connection.Completion;
                    throw new EndOfStreamException("Connection closed before Turn completed.");
                }

                JsonElement completed = await turnCompletion.Task.WaitAsync(cancellationToken);
                string completedTurnId = completed.GetProperty("id").GetString() ?? throw new JsonException("Completed Turn ID is missing.");
                if (completedTurnId != turnId)
                {
                    throw new InvalidOperationException("Completed Turn ID does not match the started Turn.");
                }

                string status = completed.GetProperty("status").GetString() ?? throw new JsonException("Turn status is missing.");
                return new TurnResult(
                    threadId,
                    turnId,
                    status,
                    finalResponse ?? response.ToString())
                {
                    Diagnostics = Snapshot(),
                    Error = completed.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
                        ? error.ToString()
                        : null
                };
            }
            finally
            {
                _connection.NotificationReceived -= OnNotification;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 第一版不支援中斷後繼續重用相同連線。
            // 直接關閉 Process，避免背景 Turn 繼續執行。
            await DisposeAsync();
            throw;
        }
        finally
        {
            LastTurnDiagnostics = Snapshot();
            _turnLock.Release();
        }
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("App Server has not been initialized.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _connection.DisposeAsync();
    }


}
