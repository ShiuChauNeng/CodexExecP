
using System.Text;
using System.Text.Json;

namespace CodexClient.AppServer;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    // Fields
    private readonly AppServerConnection _connection;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private bool _initialized;
    private int _disposed;


    // Properties
    public Func<UserInputRequest, CancellationToken, Task<IReadOnlyDictionary<string, string[]>>>? UserInputRequested { get; set; }


    // Constructors
    public CodexAppServerClient(string? executablePath = null)
    {
        _connection = new AppServerConnection(executablePath);
        _connection.UserInputHandler = HandleUserInputAsync;
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
                    },
                    capabilities = new
                    {
                        experimentalApi = true
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

    public async Task<TurnResult> RunTurnAsync(string threadId, string prompt, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _turnLock.WaitAsync(cancellationToken);

        try
        {
            var turnCompletion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

            var response = new StringBuilder();
            string? finalResponse = null;

            void OnNotification(AppServerEvent notification)
            {
                JsonElement parameters = notification.Params;

                if (parameters.ValueKind != JsonValueKind.Object ||
                    !parameters.TryGetProperty("threadId", out var threadValue) ||
                    threadValue.GetString() != threadId)
                {
                    return;
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
                JsonElement result = await _connection.SendRequestAsync("turn/start",
                    new
                    {
                        threadId,
                        input = new[]
                        {
                            new
                            {
                                type = "text",
                                text = prompt
                            }
                        }
                    },
                    cancellationToken);

                string turnId = result.GetProperty("turn").GetProperty("id").GetString() ?? throw new JsonException("Turn ID is missing.");

                // 同時等待 Turn 完成或底層連線終止。
                Task finished = await Task.WhenAny(turnCompletion.Task, _connection.Completion);
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
                    finalResponse ?? response.ToString());
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


    private async Task<object> HandleUserInputAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (UserInputRequested is null)
        {
            throw new NotSupportedException("User input handler has not been configured.");
        }

        var request = JsonSerializer.Deserialize<UserInputRequest>(
            parameters,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Invalid user input request.");

        IReadOnlyDictionary<string, string[]> answers = await UserInputRequested(request, cancellationToken);
        ArgumentNullException.ThrowIfNull(answers);
        var response = new Dictionary<string, object>();
        foreach (var question in request.Questions)
        {
            if (!answers.TryGetValue(question.Id, out var values) ||
                values is null ||
                values.Length == 0)
            {
                throw new InvalidOperationException($"Answer is missing: {question.Id}");
            }

            response.Add(question.Id, new
            {
                answers = values
            });
        }

        return new
        {
            answers = response
        };
    }


    public async Task<JsonElement> GetExperimentalFeaturesAsync(
    CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        return await _connection.SendRequestAsync(
            "experimentalFeature/list",
            new
            {
                cursor = (string?)null,
                limit = (int?)null,
                threadId = (string?)null
            },
            cancellationToken);
    }
}

