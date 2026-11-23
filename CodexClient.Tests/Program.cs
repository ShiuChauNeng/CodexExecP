using System.Text.Json;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;

// No external test packages or real AI calls are required.
if (args.FirstOrDefault() == "exec")
{
    await Console.In.ReadToEndAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { type = "thread.started", thread_id = "thread" }));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        type = "item.completed",
        item = new { type = "agent_message", text = JsonSerializer.Serialize(args) }
    }));
    Console.WriteLine(JsonSerializer.Serialize(new { type = "turn.completed" }));
    return;
}

if (args.Contains("app-server"))
{
    await ServeAsync();
    return;
}

if (args.Contains("--demo-progress"))
{
    await ConsoleProgressChecks.DemoAsync(args.Contains("--fast"));
    return;
}

if (args.Contains("--progress-only"))
{
    int progressCount = await ConsoleProgressChecks.RunAsync();
    Console.WriteLine($"Passed {progressCount} console progress check.");
    return;
}

if (args.Contains("--demo-acceptance"))
{
    await AcceptanceStepDemo.RunAsync(args.Contains("--always-fail"));
    return;
}

if (args.Contains("--acceptance-only"))
{
    string acceptanceWorkspace = Directory.GetCurrentDirectory();
    int acceptanceCount = await AcceptanceChecks.RunAsync(acceptanceWorkspace);
    acceptanceCount += await WorkflowRoutingChecks.RunAsync(acceptanceWorkspace);
    acceptanceCount += await AcceptanceStepChecks.RunAsync(acceptanceWorkspace);
    acceptanceCount += await WorkflowDecisionChecks.RunAsync(acceptanceWorkspace);
    Console.WriteLine($"Passed {acceptanceCount} acceptance, routing and decision checks.");
    return;
}

string workspace = Directory.GetCurrentDirectory();
int passed = 0;
await CheckAsync("issues can be accepted without claiming issue-free success", async () =>
{
    var result = await Workflow(new FakeClient((_, _) => Task.FromResult(Reply(issues: ["known issue"]))))
        .Add(new("step", "task")).RunAsync();
    Assert(result.Succeeded && result.Steps[0].Accepted && !result.Steps[0].Succeeded);
    Assert(result.StopReason is null);
});
await CheckAsync("StopOnIssues rejects the step and skips subsequent steps", async () =>
{
    var result = await Workflow(new FakeClient((_, _) => Task.FromResult(Reply(issues: ["known issue"]))))
        .Add(new("step", "task") { StopOnIssues = true }).Add(new("never", "task")).RunAsync();
    Assert(!result.Succeeded && !result.Steps[0].Accepted && result.Steps.Count == 1);
    Assert(result.Error is null && result.StopReason!.Contains("known issue"));
});
await CheckAsync("validation failure retains AI result and diagnostics", async () =>
{
    var diagnostic = Diagnostic("1");
    var result = await Workflow(new FakeClient((_, _) => Task.FromResult(Reply() with { Diagnostics = diagnostic })))
        .Add(new("step", "task") { ValidateCompletionAsync = (_, _) => throw new InvalidOperationException("build failed") })
        .RunAsync();
    Assert(!result.Succeeded && !result.Steps[0].Completed && !result.Steps[0].Accepted);
    Assert(result.Steps[0].Execution is not null && result.Steps[0].Diagnostics.Count == 1);
    Assert(result.StopReason!.Contains("build failed"));
});
await CheckAsync("waiting for answers pauses the execution budget", async () =>
{
    var result = await Workflow(new FakeClient((n, _) => Task.FromResult(Reply(n == 1 ? "needs_input" : "completed"))),
        async (_, ct) => { await Task.Delay(500, ct); return new[] { "answer" }; })
        .Add(new("step", "task") { ExecutionTimeout = TimeSpan.FromMilliseconds(300), InputTimeout = TimeSpan.FromSeconds(2) })
        .RunAsync();
    Assert(result.Succeeded);
    Assert(result.Steps[0].Duration >= TimeSpan.FromMilliseconds(450));
});
await CheckAsync("execution budget accumulates across turns", async () =>
{
    var result = await Workflow(new FakeClient(async (n, ct) =>
        { await Task.Delay(300, ct); return Reply(n == 1 ? "needs_input" : "completed"); }))
        .Add(new("step", "task") { ExecutionTimeout = TimeSpan.FromMilliseconds(500) }).RunAsync();
    Assert(result.Steps[0].Error is TimeoutException error && error.Message.Contains("execution"));
});
await CheckAsync("input timeout also bounds a handler that ignores cancellation", async () =>
{
    var never = new TaskCompletionSource<IReadOnlyList<string>>();
    var result = await Workflow(new FakeClient((_, _) => Task.FromResult(Reply("needs_input"))), (_, _) => never.Task)
        .Add(new("step", "task") { InputTimeout = TimeSpan.FromMilliseconds(100) }).RunAsync();
    Assert(result.Steps[0].Error is TimeoutException error && error.Message.Contains("input"));
});
await CheckAsync("legacy overall timeout still includes user input", async () =>
{
    var result = await Workflow(new FakeClient((_, _) => Task.FromResult(Reply("needs_input"))),
        async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Array.Empty<string>(); })
        .Add(new("step", "task") { Timeout = TimeSpan.FromMilliseconds(100) }).RunAsync();
    Assert(result.Steps[0].Error is TimeoutException error && error.Message.Contains("overall"));
});
await CheckAsync("maximum input rounds stops repeated questions", async () =>
{
    int answers = 0;
    var result = await Workflow(new FakeClient((_, _) => Task.FromResult(Reply("needs_input"))),
        (_, _) => { answers++; return Task.FromResult<IReadOnlyList<string>>(["answer"]); })
        .Add(new("step", "task") { MaxInputRounds = 1 }).RunAsync();
    Assert(answers == 1 && result.Steps[0].Error is InvalidOperationException && !result.Steps[0].Accepted);
});
await CheckAsync("external cancellation remains cancellation while waiting for input", async () =>
{
    using var cancel = new CancellationTokenSource(100);
    try
    {
        await Workflow(new FakeClient((_, _) => Task.FromResult(Reply("needs_input"))),
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Array.Empty<string>(); })
            .Add(new("step", "task") { ExecutionTimeout = TimeSpan.FromSeconds(5) }).RunAsync(cancel.Token);
        throw new Exception("Expected cancellation.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
});
await CheckAsync("validation time consumes the execution budget", async () =>
{
    var result = await Workflow(new FakeClient((_, _) => Task.FromResult(Reply())))
        .Add(new("step", "task")
        {
            ExecutionTimeout = TimeSpan.FromMilliseconds(100),
            ValidateCompletionAsync = (_, ct) => Task.Delay(Timeout.Infinite, ct)
        }).RunAsync();
    Assert(result.Steps[0].Error is TimeoutException && result.Steps[0].Execution is not null);
});
await CheckAsync("diagnostics survive a malformed final response", async () =>
{
    var result = await Workflow(new FakeClient((_, _) => Task.FromResult(Reply() with
        { Response = "invalid JSON", Diagnostics = Diagnostic("1") }))).Add(new("step", "task")).RunAsync();
    Assert(result.Steps[0].Error is JsonException && result.Steps[0].Diagnostics.Count == 1);
});
await CheckAsync("diagnostics accumulate over question and completion turns", async () =>
{
    var result = await Workflow(new FakeClient((n, _) => Task.FromResult(
        Reply(n == 1 ? "needs_input" : "completed") with { Diagnostics = Diagnostic(n.ToString()) })))
        .Add(new("step", "task")).RunAsync();
    Assert(result.Steps[0].Diagnostics.Count == 2 && result.Steps[0].Execution!.Diagnostics.Count == 2);
});
await CheckAsync("invalid limits fail before starting a thread", async () =>
{
    var client = new FakeClient((_, _) => Task.FromResult(Reply()));
    try
    {
        await Workflow(client).Add(new("step", "task") { MaxInputRounds = -1 }).RunAsync();
        throw new Exception("Expected invalid limit.");
    }
    catch (ArgumentOutOfRangeException) { Assert(client.Threads == 0); }
});
await CheckAsync("transport keeps recovered tool failures and bounded stderr without rejecting success", async () =>
{
    await using var client = new CodexAppServerClient(Environment.ProcessPath!);
    await client.StartAsync("test-client");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!client.StandardError.Contains("stderr-tail")) await Task.Delay(10, deadline.Token);
    var result = await Workflow(client).Add(new("step", "transport task")).RunAsync(deadline.Token);
    var diagnostics = result.Steps[0].Diagnostics;
    Assert(result.Succeeded && result.Steps[0].Accepted && !result.Steps[0].HasIssues);
    Assert(diagnostics.Count == 1 && diagnostics[0].CompletedItems.Count == 3 && diagnostics[0].Errors.Count == 1);
    Assert(diagnostics[0].CompletedItems[0].GetProperty("status").GetString() == "failed");
    Assert(diagnostics[0].StandardError.StartsWith("[stderr truncated") && diagnostics[0].StandardError.Length < 16500);
});
await CheckAsync("transport cancellation closes the client and preserves partial diagnostics", async () =>
{
    await using var client = new CodexAppServerClient(Environment.ProcessPath!);
    await client.StartAsync("test-client");
    var result = await Workflow(client).Add(new("step", "hang") { ExecutionTimeout = TimeSpan.FromMilliseconds(300) })
        .RunAsync();
    // WaitAsync may return before the concrete client's asynchronous cancellation cleanup completes.
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (client.LastTurnDiagnostics is null) await Task.Delay(10, deadline.Token);
    Assert(result.Steps[0].Error is TimeoutException);
    Assert(result.Steps[0].Diagnostics.Count == 1 && result.Steps[0].Diagnostics[0].CompletedItems.Count == 1);
    Assert(client.LastTurnDiagnostics.CompletedItems.Count == 1);
    try { await client.StartThreadAsync(workspace); throw new Exception("Expected disposed client."); }
    catch (ObjectDisposedException) { }
});
passed += await NetworkAccessChecks.RunAsync(workspace);
passed += await ReasoningEffortChecks.RunAsync(workspace);
passed += await ServiceTierChecks.RunAsync(workspace);
passed += await RequestLoggingChecks.RunAsync(workspace);
passed += await AcceptanceChecks.RunAsync(workspace);
passed += await WorkflowRoutingChecks.RunAsync(workspace);
passed += await AcceptanceStepChecks.RunAsync(workspace);
passed += await WorkflowDecisionChecks.RunAsync(workspace);
passed += await WorkflowResultChecks.RunAsync(workspace);
passed += WorkflowPlanChecks.Run(workspace);
passed += await ConsoleProgressChecks.RunAsync();
Console.WriteLine($"Passed {passed} checks.");

/// <summary>
/// 以測試用戶端、測試工作區與回答處理建立工作流程。
/// </summary>
AppServerWorkflow Workflow(IAppServerClient client,
    Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>>? answer = null)
    => new(client, workspace, answer ?? ((_, _) => Task.FromResult<IReadOnlyList<string>>(["answer"])));

/// <summary>
/// 執行非同步測試案例，成功後累計通過數並輸出案例名稱。
/// </summary>
async Task CheckAsync(string name, Func<Task> check)
{
    await check();
    passed++;
    Console.WriteLine($"PASS: {name}");
}
/// <summary>
/// 檢查測試條件，不符合時拋出例外使案例失敗。
/// </summary>
static void Assert(bool condition)
{
    if (!condition)
    {
        throw new Exception("Assertion failed.");
    }
}
/// <summary>
/// 建立包含任務狀態、問題與 issues 的模擬 turn 回覆。
/// </summary>
static TurnResult Reply(string state = "completed", string[]? issues = null) => new("thread", "1", "completed",
    JsonSerializer.Serialize(new { state, message = "summary", questions = state == "needs_input" ? new[] { "question" } : [], issues = issues ?? [], data = (object?)null }));
/// <summary>
/// 建立指定 turn 的測試用診斷快照。
/// </summary>
static AppServerTurnDiagnostics Diagnostic(string turnId) => new("thread", turnId, [], [], "stderr");

/// <summary>
/// 透過標準輸入與輸出模擬 App Server 請求、通知及異常情境。
/// </summary>
static async Task ServeAsync()
{
    Console.InputEncoding = System.Text.Encoding.UTF8;
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    /// <summary>
    /// 將模擬伺服器訊息序列化為 JSON，寫入標準輸出。
    /// </summary>
    void Send(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    /// <summary>
    /// 輸出包含方法與參數的模擬 App Server 通知。
    /// </summary>
    void Notify(string method, object parameters) => Send(new { method, @params = parameters });
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id))
        {
            continue;
        }
        switch (root.GetProperty("method").GetString())
        {
            case "initialize":
                Console.Error.Write(new string('x', 20000) + "stderr-tail");
                Console.Error.Flush();
                Send(new { id, result = new { } });
                break;
            case "thread/start":
                Send(new { id, result = new { thread = new { id = "thread" } } });
                break;
            case "turn/start":
                var turn = new { id = "1", status = "completed" };
                Send(new { id, result = new { turn } });
                Notify("item/completed", new { threadId = "thread", turnId = "1", item = new { type = "commandExecution", status = "failed", exitCode = 1 } });
                if (root.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()!.Contains("hang"))
                {
                    await Task.Delay(Timeout.Infinite);
                    return;
                }
                Notify("error", new { threadId = "thread", turnId = "1", message = "recovered error", willRetry = true });
                Notify("item/completed", new { threadId = "thread", turnId = "1", item = new { type = "commandExecution", status = "completed", exitCode = 0 } });
                string response = Reply().Response;
                string input = root.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()!;
                if (input.Contains("[request-log-question]"))
                {
                    response = Reply("needs_input").Response;
                }
                if (input.Contains("network-check") || input.Contains("reasoning-check") || input.Contains("service-tier-check") || input.Contains("request-debug-check"))
                {
                    response = JsonSerializer.Serialize(new
                    {
                        state = "completed",
                        message = root.GetProperty("params").GetRawText(),
                        questions = Array.Empty<string>(),
                        issues = Array.Empty<string>(),
                        data = (object?)null
                    });
                }
                Notify("item/completed", new { threadId = "thread", turnId = "1", item = new { type = "agentMessage", text = response } });
                Notify("turn/completed", new { threadId = "thread", turn });
                break;
        }
    }
}

sealed class FakeClient(Func<int, CancellationToken, Task<TurnResult>> run) : IAppServerClient
{
    private int _turn;
    public int Threads { get; private set; }
    public AppServerTurnDiagnostics? LastTurnDiagnostics { get; private set; }
    /// <summary>
    /// 建立測試用 thread 識別碼，不啟動實際 App Server。
    /// </summary>
    public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    { Threads++; return Task.FromResult("thread"); }
    /// <summary>
    /// 依測試情境產生模擬 turn 結果，不呼叫實際 AI。
    /// </summary>
    public async Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
        AppServerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await run(++_turn, cancellationToken);
        LastTurnDiagnostics = result.Diagnostics;
        return result;
    }
}
