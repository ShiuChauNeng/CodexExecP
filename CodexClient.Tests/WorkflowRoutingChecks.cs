using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;

internal static class WorkflowRoutingChecks
{
    private static readonly JsonElement ReviewSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object", properties = new { approved = new { type = "boolean" } },
        required = new[] { "approved" }, additionalProperties = false
    });

    internal static async Task<int> RunAsync(string workspace)
    {
        int passed = 0;
        await Check("unconfigured steps retain sequential behavior", async () =>
        {
            var client = new ScriptedClient();
            var result = await Workflow(client).Add(new("A", "A")).Add(new("B", "B")).RunAsync();
            Assert(result.Succeeded && result.Error is null && result.Steps.Select(s => s.Name).SequenceEqual(["A", "B"]));
        });
        await Check("backward routing uses latest results and retains immutable context snapshots", async () =>
        {
            var client = new ScriptedClient { Data = call => new { approved = call >= 4 } };
            var snapshots = new List<AppServerWorkflowContext>();
            var result = await Workflow(client)
                .Add(new("Implement", context => context.Steps.Any(s => s.Name == "Review")
                    ? "Implement fixes: " + context.GetStep("Review").Execution!.Data!.Value.GetRawText()
                    : "Implement first") { DataSchema = ReviewSchema })
                .Add(new("Review", "Review")
                {
                    DataSchema = ReviewSchema,
                    NextStep = context =>
                    {
                        snapshots.Add(context);
                        return context.GetStep("Review").Execution!.Data!.Value.GetProperty("approved").GetBoolean()
                            ? null : "Implement";
                    }
                })
                .Add(new("Finish", "Finish") { DataSchema = ReviewSchema }).RunAsync();
            Assert(result.Succeeded && result.Steps.Select(s => s.Name).SequenceEqual(["Implement", "Review", "Implement", "Review", "Finish"]));
            Assert(client.Prompts[2].Contains("Implement fixes: {\"approved\":false}"));
            Assert(snapshots[0].Steps.Count == 2 && snapshots[1].Steps.Count == 4);
            Assert(!snapshots[0].GetStep("Review").Execution!.Data!.Value.GetProperty("approved").GetBoolean());
        });
        await Check("forward routing skips steps", async () =>
        {
            var result = await Workflow(new()).Add(new("A", "A") { NextStep = _ => "C" })
                .Add(new("B", "B")).Add(new("C", "C")).RunAsync();
            Assert(result.Succeeded && result.Steps.Select(s => s.Name).SequenceEqual(["A", "C"]));
        });
        await Check("acceptance contract can route revision without a specialized controller", async () =>
        {
            var client = new ScriptedClient
            {
                Data = call => call == 2 || call == 4 ? new AcceptanceResult(
                    [new("SPEC-001", call == 2 ? AcceptanceCheckStatus.Failed : AcceptanceCheckStatus.Passed, "Spec and code evidence")],
                    [], $"reports/acceptance-{call / 2}.md") : null
            };
            var result = await Workflow(client).Add(new("Implement", "Implement"))
                .Add(new("Acceptance", "Acceptance")
                {
                    DataSchema = AcceptanceResultSchema.Value,
                    NextStep = context => context.GetStep("Acceptance").GetData<AcceptanceResult>().Decide(["SPEC-001"])
                        == AcceptanceDecision.NeedsRevision ? "Implement" : null
                }).Add(new("Style", "Style")).RunAsync();
            Assert(result.Succeeded && result.Steps.Select(s => s.Name)
                .SequenceEqual(["Implement", "Acceptance", "Implement", "Acceptance", "Style"]));
        });
        await Check("a step may repeat itself and then fall through", async () =>
        {
            var result = await Workflow(new()).Add(new("A", "A")
                { NextStep = context => context.Steps.Count < 3 ? "A" : null }).RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 3);
        });
        await Check("long sequential workflows have no global execution cap", async () =>
        {
            var workflow = Workflow(new()).AddRange(Enumerable.Range(0, 120)
                .Select(index => new AppServerWorkflowStep($"Step-{index}", "task")));
            var result = await workflow.RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 120);
        });
        await Check("unknown, empty and differently cased targets stop with control errors", async () =>
        {
            foreach (string target in new[] { "unknown", "", " ", "b" })
            {
                var result = await Workflow(new()).Add(new("A", "A") { NextStep = _ => target })
                    .Add(new("B", "B")).RunAsync();
                Assert(!result.Succeeded && result.Error is InvalidOperationException && result.FailedStep == "A");
                Assert(result.Steps.Count == 1 && result.Steps[0].Accepted && result.Steps[0].Execution is not null);
            }
        });
        await Check("routing exceptions do not discard completed work", async () =>
        {
            var error = new InvalidOperationException("manual review required");
            var result = await Workflow(new()).Add(new("A", "A") { NextStep = _ => throw error }).RunAsync();
            Assert(!result.Succeeded && ReferenceEquals(result.Error, error) && result.Steps[0].Completed);
        });
        await Check("failed execution never invokes routing", async () =>
        {
            bool routed = false;
            var result = await Workflow(new ScriptedClient { Fail = true })
                .Add(new("A", "A") { NextStep = _ => { routed = true; return "A"; } }).RunAsync();
            Assert(!result.Succeeded && !routed && result.Error is IOException && result.Steps.Count == 1);
        });
        await Check("StopOnIssues is evaluated before routing", async () =>
        {
            bool routed = false;
            var result = await Workflow(new ScriptedClient { Issues = ["unresolved"] })
                .Add(new("A", "A") { StopOnIssues = true, NextStep = _ => { routed = true; return "A"; } }).RunAsync();
            Assert(!result.Succeeded && !routed && result.Steps[0].HasIssues);
        });
        await Check("accepted issues may still select a next step", async () =>
        {
            var result = await Workflow(new ScriptedClient { Issues = ["warning"] })
                .Add(new("A", "A") { NextStep = _ => "C" }).Add(new("B", "B")).Add(new("C", "C")).RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 2 && result.Steps[0].HasIssues);
        });
        await Check("validation completes before routing and failure prevents routing", async () =>
        {
            bool validated = false;
            var result = await Workflow(new()).Add(new("A", "A")
            {
                ValidateCompletionAsync = async (_, _) => { await Task.Yield(); validated = true; },
                NextStep = _ => { Assert(validated); return null; }
            }).RunAsync();
            Assert(result.Succeeded);
            bool routed = false;
            result = await Workflow(new()).Add(new("A", "A")
            {
                ValidateCompletionAsync = (_, _) => throw new InvalidOperationException("invalid output"),
                NextStep = _ => { routed = true; return null; }
            }).RunAsync();
            Assert(!result.Succeeded && !routed);
        });
        await Check("revisiting Create renews the thread mapping and Use reads the latest", async () =>
        {
            var client = new ScriptedClient();
            var snapshots = new List<AppServerWorkflowContext>();
            var result = await Workflow(client)
                .Add(new("Create", "Create") { Thread = AppServerThread.Create("shared") })
                .Add(new("Use", "Use")
                {
                    Thread = AppServerThread.Use("shared"),
                    NextStep = context =>
                    {
                        snapshots.Add(context);
                        return context.Steps.Count < 4 ? "Create" : null;
                    }
                }).RunAsync();
            Assert(result.Succeeded && client.ThreadCount == 2);
            Assert(client.ThreadIds.SequenceEqual(["thread-1", "thread-1", "thread-2", "thread-2"]));
            Assert(result.Threads["shared"] == "thread-2" && snapshots[0].Threads["shared"] == "thread-1");
        });
        await Check("revisiting Use retains the same thread", async () =>
        {
            var client = new ScriptedClient();
            var result = await Workflow(client)
                .Add(new("Create", "Create") { Thread = AppServerThread.Create("shared") })
                .Add(new("Use", "Use")
                {
                    Thread = AppServerThread.Use("shared"),
                    NextStep = context => context.Steps.Count < 4 ? "Use" : null
                }).RunAsync();
            Assert(result.Succeeded && client.ThreadCount == 1 && client.ThreadIds.Distinct().Count() == 1);
        });
        await Check("skipping a required Create fails before sending a turn on Use", async () =>
        {
            var client = new ScriptedClient();
            var result = await Workflow(client).Add(new("Route", "Route") { NextStep = _ => "Use" })
                .Add(new("Create", "Create") { Thread = AppServerThread.Create("shared") })
                .Add(new("Use", "Use") { Thread = AppServerThread.Use("shared") }).RunAsync();
            Assert(!result.Succeeded && result.Error is KeyNotFoundException && result.FailedStep == "Use");
            Assert(client.Prompts.Count == 1);
        });
        await Check("cancellation from a routing callback remains cancellation", async () =>
        {
            using var cancel = new CancellationTokenSource();
            var client = new ScriptedClient();
            try
            {
                await Workflow(client).Add(new("A", "A") { NextStep = _ => { cancel.Cancel(); return "A"; } }).RunAsync(cancel.Token);
                throw new Exception("Expected cancellation.");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            Assert(client.Prompts.Count == 1);
        });
        return passed;

        AppServerWorkflow Workflow(ScriptedClient client) => new(client, workspace, Answers);
        async Task Check(string name, Func<Task> check)
        {
            await check();
            passed++;
            Console.WriteLine($"PASS: workflow routing {name}");
        }
    }

    private static Task<IReadOnlyList<string>> Answers(AppServerWorkflowInputRequest _, CancellationToken token)
        => Task.FromResult<IReadOnlyList<string>>([]);

    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Workflow routing assertion failed.");
        }
    }

    private sealed class ScriptedClient : IAppServerClient
    {
        public List<string> Prompts { get; } = [];
        public List<string> ThreadIds { get; } = [];
        public int ThreadCount { get; private set; }
        public Func<int, object?>? Data { get; init; }
        public string[] Issues { get; init; } = [];
        public bool Fail { get; init; }

        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult($"thread-{++ThreadCount}");

        public Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
            AppServerOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            ThreadIds.Add(threadId);
            if (Fail)
            {
                throw new IOException("turn failed");
            }
            return Task.FromResult(new TurnResult(threadId, Prompts.Count.ToString(), "completed", JsonSerializer.Serialize(new
            {
                state = "completed", message = "finished", questions = Array.Empty<string>(), issues = Issues,
                data = Data?.Invoke(Prompts.Count)
            })));
        }
    }
}
