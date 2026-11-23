using System.Text.Json;
using CodexClient.AppServer;
using CodexClient.Models;
using Microsoft.Extensions.Logging;

internal static class RequestLoggingChecks
{
    /// <summary>
    /// 驗證完整請求僅寫入啟用的專用 Debug 記錄器，並涵蓋任務指令與使用者回答。
    /// </summary>
    internal static async Task<int> RunAsync(string workspace)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ordinaryLogger = new RecordingLogger<CodexAppServerClient>();
        await using (var defaultClient = new CodexAppServerClient(Environment.ProcessPath!, ordinaryLogger))
        {
            await defaultClient.StartAsync("default-request-logging", deadline.Token);
            var threadId = await defaultClient.StartThreadAsync(workspace, deadline.Token);
            await defaultClient.RunTurnAsync(threadId, "private prompt", cancellationToken: deadline.Token);
        }
        Assert(ordinaryLogger.Entries.All(entry => entry.Request is null && entry.Prompt is null));
        Console.WriteLine("PASS: request logging stays off even when the ordinary logger enables Debug");

        var logger = new RecordingLogger<CodexAppServerClient>();
        await using var client = new CodexAppServerClient(Environment.ProcessPath!, requestLogger: logger);
        await client.StartAsync("request-logging", deadline.Token);
        string thread = await client.StartThreadAsync(workspace, deadline.Token);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "string" } } });
        const string prompt = "[request-debug-check] 完整中文提示\n第二行，字面值 \\u539F";
        var loggedTurn = await client.RunTurnAsync(thread, prompt, schema, new AppServerOptions
        {
            Model = "test-model", Sandbox = CodexSandboxMode.WorkspaceWrite, WorkspaceRoot = workspace,
            ReasoningEffort = CodexReasoningEffort.High, ServiceTier = CodexServiceTier.Fast, NetworkAccess = true
        }, deadline.Token);
        var entry = logger.Entries.Single();
        Assert(entry.Level == LogLevel.Debug && entry.Prompt == prompt && entry.Message.Contains(prompt));
        Assert(entry.Message.Contains("Prompt:" + Environment.NewLine) && entry.Message.Contains("Parameters (excluding input):"));
        using var reply = JsonDocument.Parse(loggedTurn.Response);
        using var sent = JsonDocument.Parse(reply.RootElement.GetProperty("message").GetString()!);
        Assert(sent.RootElement.GetProperty("input")[0].GetProperty("text").GetString() == prompt);
        using (var document = JsonDocument.Parse(entry.Request!))
        {
            Assert(document.RootElement.GetProperty("method").GetString() == "turn/start");
            var parameters = document.RootElement.GetProperty("params");
            Assert(parameters.GetProperty("threadId").GetString() == thread);
            Assert(!parameters.TryGetProperty("input", out _));
            Assert(JsonElement.DeepEquals(parameters.GetProperty("outputSchema"), schema));
            Assert(parameters.GetProperty("model").GetString() == "test-model" && parameters.GetProperty("effort").GetString() == "high");
            Assert(parameters.GetProperty("serviceTier").GetString() == "fast");
            Assert(parameters.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean());
        }
        Console.WriteLine("PASS: request Debug log preserves the final prompt, output schema and execution settings");

        logger.Enabled = false;
        await client.RunTurnAsync(thread, "not logged", cancellationToken: deadline.Token);
        Assert(logger.Entries.Count == 1);
        logger.Enabled = true;
        var taskResult = await new CodexTaskRunner(client).RunAsync(thread, "[request-log-question] 請先詢問", (_, _) =>
            Task.FromResult<IReadOnlyList<string>>(["我的回答"]), cancellationToken: deadline.Token);
        Assert(logger.Entries.Count == 3 && taskResult.ThreadId == thread);
        string initialPrompt = logger.Entries[1].Prompt!;
        string resumedPrompt = logger.Entries[2].Prompt!;
        Assert(initialPrompt.Contains("state=completed") && initialPrompt.Contains("[request-log-question]"));
        Assert(resumedPrompt.Contains("使用者回答如下") && resumedPrompt.Contains("state=completed"));
        Assert(resumedPrompt.Contains("我的回答") && !resumedPrompt.Contains("\\u6211"));
        const string answerPrefix = "使用者回答如下，請繼續原任務：\n";
        using var answers = JsonDocument.Parse(resumedPrompt[(resumedPrompt.IndexOf(answerPrefix, StringComparison.Ordinal) + answerPrefix.Length)..]);
        Assert(answers.RootElement[0].GetProperty("answer").GetString() == "我的回答");
        Console.WriteLine("PASS: disabled request logging is silent and each question/resume turn logs its actual assembled input");
        return 3;
    }

    /// <summary>
    /// 驗證條件，不符時使案例失敗。
    /// </summary>
    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Request logging assertion failed.");
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal bool Enabled { get; set; } = true;
        internal List<(LogLevel Level, string Message, string? Prompt, string? Request)> Entries { get; } = [];

        /// <summary>
        /// 測試記錄器不額外建立 scope。
        /// </summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>
        /// 依開關控制 Debug 與更高等級的輸出。
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => Enabled && logLevel >= LogLevel.Debug;

        /// <summary>
        /// 保存格式化訊息、原始 Prompt 與參數 JSON，供顯示及實際傳送內容比對。
        /// </summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var values = state as IEnumerable<KeyValuePair<string, object?>>;
            Entries.Add((logLevel, formatter(state, exception),
                values?.FirstOrDefault(pair => pair.Key == "Prompt").Value as string,
                values?.FirstOrDefault(pair => pair.Key == "Request").Value as string));
        }
    }
}
