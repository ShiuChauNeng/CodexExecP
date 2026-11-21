
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexClient.AppServer;

public sealed class CodexAppServerClient : IAsyncDisposable, IAppServerClient
{
    // Fields
    private readonly AppServerConnection _connection;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private AppServerTurnDiagnostics? _lastTurnDiagnostics;

    /// <summary>
    /// 最近一次 turn 的診斷快照，失敗或取消後仍可讀取。
    /// </summary>
    public AppServerTurnDiagnostics? LastTurnDiagnostics
    {
        get => Volatile.Read(ref _lastTurnDiagnostics);
        private set => Volatile.Write(ref _lastTurnDiagnostics, value);
    }

    /// <summary>
    /// 連線層級 stderr 尾端，最多保留 16384 個字元及截斷提示。
    /// </summary>
    public string StandardError => _connection.StandardError;

    private bool _initialized;
    private int _disposed;


    // Constructors
    /// <summary>
    /// 建立 App Server 用戶端及其底層連線。
    /// </summary>
    public CodexAppServerClient(string? executablePath = null, ILogger<CodexAppServerClient>? logger = null)
    {
        _logger = logger ?? NullLogger<CodexAppServerClient>.Instance;
        _connection = new AppServerConnection(executablePath, _logger);
    }


    // Methods
    /// <summary>
    /// 啟動 App Server 並完成初始化交握。
    /// </summary>
    /// <param name="applicationName">要提供給 App Server 的應用程式名稱。</param>
    /// <param name="cancellationToken">用於取消啟動作業的權杖。</param>
    /// <returns>表示啟動作業的非同步工作。</returns>
    public async Task StartAsync(string applicationName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        if (_initialized)
        {
            throw new InvalidOperationException("App Server has already been initialized.");
        }

        try
        {
            _logger.LogInformation("Codex App Server starting. ApplicationName: {ApplicationName}", applicationName);
            await _connection.StartAsync(cancellationToken);
            _logger.LogInformation("Codex App Server initializing");
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
            _logger.LogInformation("Codex App Server started");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Codex App Server startup cancelled");
            await DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Codex App Server startup failed");
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// 在指定工作區建立新的 App Server thread。
    /// </summary>
    /// <param name="workspaceRoot">thread 使用的工作區根目錄。</param>
    /// <param name="cancellationToken">用於取消建立作業的權杖。</param>
    /// <returns>新建立的 thread 識別碼。</returns>
    public async Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException(workspaceRoot);
        }

        _logger.LogInformation("Codex thread starting. WorkspaceRoot: {WorkspaceRoot}", workspaceRoot);
        JsonElement result = await _connection.SendRequestAsync("thread/start",
            new
            {
                cwd = Path.GetFullPath(workspaceRoot),
                approvalPolicy = "never",
                sandbox = "read-only"
            },
            cancellationToken);

        string threadId = result.GetProperty("thread").GetProperty("id").GetString() ?? throw new JsonException("Thread ID is missing.");
        _logger.LogInformation("Codex thread started: {SessionId}", threadId);
        return threadId;
    }

    /// <summary>
    /// 在指定 thread 執行一個 turn。
    /// </summary>
    /// <param name="threadId">目標 thread 識別碼。</param>
    /// <param name="prompt">要傳送給模型的提示文字。</param>
    /// <param name="outputSchema">選用的結構化輸出描述。</param>
    /// <param name="options">App Server 執行選項。</param>
    /// <param name="cancellationToken">用於取消 turn 的權杖。</param>
    /// <returns>非同步回傳 turn 執行結果。</returns>
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
        using var logScope = _logger.BeginScope("ThreadId: {ThreadId}", threadId);
        var items = new List<JsonElement>();
        var errors = new List<JsonElement>();
        var diagnosticsSync = new object();
        string? currentTurnId = null;
        LastTurnDiagnostics = null;
        /// <summary>
        /// 在同步鎖內擷取目前 turn 的項目、錯誤與標準錯誤快照。
        /// </summary>
        AppServerTurnDiagnostics Snapshot()
        {
            lock (diagnosticsSync)
                return new(threadId, currentTurnId,
                    Array.AsReadOnly(items.ToArray()), Array.AsReadOnly(errors.ToArray()), StandardError);
        }
        try
        {
            _logger.LogInformation("Codex turn starting");
            var turnCompletion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new StringBuilder();
            string? finalResponse = null;
            /// <summary>
            /// 處理目前 thread 的通知，累積回覆、診斷與 turn 完成結果。
            /// </summary>
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
                    _logger.LogWarning("Codex reported an error event. Details: {Details}", parameters);
                    lock (diagnosticsSync)
                    {
                        errors.Add(parameters.Clone());
                        LastTurnDiagnostics = Snapshot();
                    }
                }

                if (notification.Method is "item/started" or "item/completed" &&
                    parameters.TryGetProperty("item", out var commandItem) &&
                    commandItem.TryGetProperty("type", out var commandType) &&
                    commandType.GetString() == "commandExecution")
                {
                    string? itemId = commandItem.TryGetProperty("id", out var id) ? id.GetString() : null;
                    if (notification.Method == "item/started")
                    {
                        _logger.LogInformation("Command started: {ItemId}, {Command}", itemId,
                            commandItem.TryGetProperty("command", out var command) ? command.GetString() : null);
                    }
                    else
                    {
                        _logger.LogInformation("Command completed: {ItemId}, ExitCode: {ExitCode}", itemId,
                            commandItem.TryGetProperty("exitCode", out var exitCode) ? exitCode.ToString() : null);
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
                _logger.LogInformation("Codex turn started. TurnId: {TurnId}", turnId);

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
                if (status == "failed")
                {
                    _logger.LogError("Codex turn failed. TurnId: {TurnId}, Error: {Error}", turnId,
                        completed.TryGetProperty("error", out var turnError) ? turnError.ToString() : null);
                }
                else
                {
                    _logger.LogInformation("Codex turn finished. TurnId: {TurnId}, Status: {Status}", turnId, status);
                }
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
            _logger.LogWarning("Codex execution interrupted. Reason: {Reason}, TurnId: {TurnId}", "Cancelled", currentTurnId);
            // 第一版不支援中斷後繼續重用相同連線。
            // 直接關閉 Process，避免背景 Turn 繼續執行。
            await DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Codex execution failed. TurnId: {TurnId}", currentTurnId);
            throw;
        }
        finally
        {
            LastTurnDiagnostics = Snapshot();
            _turnLock.Release();
        }
    }

    /// <summary>
    /// 確認 Client 已完成 App Server 初始化。
    /// </summary>
    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("App Server has not been initialized.");
        }
    }

    /// <summary>
    /// 非同步釋放 App Server Client 使用的資源。
    /// </summary>
    /// <returns>表示釋放作業的非同步值工作。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _connection.DisposeAsync();
    }


}
