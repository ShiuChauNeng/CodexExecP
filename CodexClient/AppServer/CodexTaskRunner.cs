using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexClient.AppServer;

/// <summary>
/// 在既有 thread 執行單一任務，提問後等待回答並繼續。
/// 逾時與完成驗證由 Workflow 管理；單獨使用時由呼叫端提供取消權杖與驗證結果。
/// </summary>
public sealed class CodexTaskRunner(IAppServerClient client)
{
    private readonly IAppServerClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement OutputSchema = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "state": { "type": "string", "enum": ["completed", "needs_input", "failed"] },
            "message": { "type": "string" },
            "questions": { "type": "array", "items": { "type": "string" } },
            "issues": { "type": "array", "items": { "type": "string" } },
            "data": { "type": "null" }
          },
          "required": ["state", "message", "questions", "issues", "data"],
          "additionalProperties": false
        }
        """);

    private const string Instructions =
        """
        執行使用者的任務，最後依指定 Schema 回傳結果。
        state=completed：任務已完成，questions 為空陣列。
        state=needs_input：缺少使用者決策或資訊，questions 必須列出具體問題；等待下一個 turn 回答。
        state=failed：無法完成任務，在 message 說明原因，questions 為空陣列。
        message 必須提供摘要。不得自行假設尚未決定的業務規則，也不得將尚待回答的任務標為 completed。
        所有問題都透過最終結果的 questions 提出，不要呼叫 request_user_input。
        回答後繼續原任務；仍缺資訊時可以再次回傳 needs_input。
        issues 列出已知未解決問題，沒有時為空陣列。
        data 在 completed 時依 Schema 提供產出；其他狀態為 null。未定義資料結構時 data 為 null。
        """;

    public async Task<TaskRunResult> RunAsync(
        string threadId,
        string task,
        Func<TaskInputRequest, CancellationToken, Task<IReadOnlyList<string>>> answerQuestionsAsync,
        AppServerOptions? options = null,
        JsonElement? dataSchema = null,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(threadId, task, answerQuestionsAsync, options, dataSchema, cancellationToken, null);

    internal async Task<TaskRunResult> RunCoreAsync(
        string threadId, string task,
        Func<TaskInputRequest, CancellationToken, Task<IReadOnlyList<string>>> answerQuestionsAsync,
        AppServerOptions? options, JsonElement? dataSchema, CancellationToken cancellationToken,
        Action<AppServerTurnDiagnostics>? reportDiagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentNullException.ThrowIfNull(answerQuestionsAsync);
        options ??= new();
        options.Validate();
        JsonElement schema = CreateOutputSchema(dataSchema);

        // 診斷 Log，當出問題時，reportDiagnostics 會被呼叫，並且在 TaskRunResult 中提供完整的診斷資訊。
        var diagnostics = new List<AppServerTurnDiagnostics>();
        string input = task;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TurnResult turn = await _client.RunTurnAsync(
                threadId, Instructions + "\n\n" + input, schema, options, cancellationToken).WaitAsync(cancellationToken);
            if (turn.Diagnostics is { } diagnostic)
            {
                diagnostics.Add(diagnostic);
                reportDiagnostics?.Invoke(diagnostic);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (turn.Status != "completed")
            {
                throw new InvalidOperationException($"Turn {turn.TurnId} ended with status {turn.Status}: {turn.Error ?? turn.Response}");
            }

            Reply reply = JsonSerializer.Deserialize<Reply>(turn.Response, JsonOptions) ?? throw new JsonException("Task response is missing.");

            if (string.IsNullOrWhiteSpace(reply.Message) || reply.Questions is null || reply.Issues is null ||
                reply.Questions.Any(string.IsNullOrWhiteSpace) || reply.Issues.Any(string.IsNullOrWhiteSpace) ||
                reply.Data.ValueKind == JsonValueKind.Undefined)
            {
                throw new JsonException("Task response requires message, questions, issues and data.");
            }
            if ((reply.State != "completed" || dataSchema is null) && reply.Data.ValueKind != JsonValueKind.Null)
            {
                throw new JsonException("Task data must be null unless a completed task requested object data.");
            }

            switch (reply.State)
            {
                case "needs_input":
                    if (reply.Questions.Length == 0)
                    {
                        throw new JsonException("needs_input requires at least one question.");
                    }
                    var request = new TaskInputRequest(threadId, turn.TurnId, reply.Message, Array.AsReadOnly(reply.Questions));
                    IReadOnlyList<string> answers = await answerQuestionsAsync(request, cancellationToken).WaitAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (answers is null || answers.Count != reply.Questions.Length || answers.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new ArgumentException("Provide one nonempty answer for each question.", nameof(answerQuestionsAsync));
                    }

                    input = "使用者回答如下，請繼續原任務：\n" + JsonSerializer.Serialize(reply.Questions.Select((question, index) => new { question, answer = answers[index] }));
                    continue;

                case "completed":
                    if (reply.Questions.Length != 0)
                    {
                        throw new JsonException("completed must not contain pending questions.");
                    }

                    if (dataSchema is not null && reply.Data.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("Completed task requires object data.");
                    }

                    return new TaskRunResult(threadId, turn.TurnId, reply.Message)
                    {
                        Data = reply.Data.ValueKind == JsonValueKind.Null ? null : reply.Data.Clone(),
                        Issues = Array.AsReadOnly(reply.Issues),
                        Diagnostics = Array.AsReadOnly(diagnostics.ToArray())
                    };

                case "failed":
                    if (reply.Questions.Length != 0)
                    {
                        throw new JsonException("failed must not contain pending questions.");
                    }
                    throw new InvalidOperationException($"Task failed in turn {turn.TurnId}: {reply.Message}");

                default:
                    throw new JsonException($"Unknown task state: {reply.State}");
            }
        }
    }

    internal static void ValidateDataSchema(JsonElement? dataSchema)
    {
        if (dataSchema is { } schema && (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object"))
        {
            throw new ArgumentException("DataSchema must describe an object.", nameof(dataSchema));
        }
    }

    private static JsonElement CreateOutputSchema(JsonElement? dataSchema)
    {
        ValidateDataSchema(dataSchema);
        if (dataSchema is null)
        {
            return OutputSchema;
        }
        var schema = JsonNode.Parse(OutputSchema.GetRawText())!;
        schema["properties"]!["data"] = new JsonObject
        {
            ["anyOf"] = new JsonArray(JsonNode.Parse(dataSchema.Value.GetRawText()),
                JsonNode.Parse("""{"type":"null"}"""))
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    private sealed record Reply(string? State, string? Message, string[]? Questions, string[]? Issues, JsonElement Data);
}
