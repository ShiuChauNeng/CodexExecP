using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;

internal static class AcceptanceStepChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string workspace)
    {
        int passed = 0;
        await Check("simple acceptance step automatically supplies schema and continues on success", async () =>
        {
            var client = new Client();
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(Review()).Add(new("Style", "style")).RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 3);
            Assert(result.Steps[1].AcceptanceDecision == AcceptanceDecision.Passed && result.Steps[1].FailureCount == 0);
            Assert(client.Prompts[1].Contains("state=completed"));
        });
        await Check("failed acceptance automatically returns with evidence and preserves normal completion", async () =>
        {
            var client = new Client { Assessment = (_, count) => Result(count == 1 ? FindingSeverity.Error : null) };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(Review()).Add(new("Style", "style")).RunAsync();
            Assert(result.Succeeded && result.Steps.Select(s => s.Name).SequenceEqual(["Implement", "Review", "Implement", "Review", "Style"]));
            var failed = result.Steps[1];
            Assert(failed.Completed && !failed.Accepted && !failed.Succeeded && failed.Error is null);
            Assert(failed.AcceptanceDecision == AcceptanceDecision.NeedsRevision && failed.FailureCount == 1);
            Assert(result.Steps[3].Accepted && result.Steps[3].FailureCount == 1);
            Assert(client.Prompts[2].Contains("Spec and code evidence") && client.Prompts[2].Contains("\"acceptanceStep\":\"Review\""));
            Assert(!client.Prompts[4].Contains("\"acceptanceStep\":\"Review\""));
        });
        await Check("third failed acceptance stops without a fourth implementation", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result(FindingSeverity.Error) };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(Review()).Add(new("Style", "style")).RunAsync();
            Assert(!result.Succeeded && result.FailedStep == "Review" && result.Error!.Message.Contains("Failures: 3"));
            Assert(result.Steps.Count == 6 && result.Steps.Last().FailureCount == 3 && result.Steps.Last().Completed);
        });
        await Check("passing on the third assessment does not consume a third failure", async () =>
        {
            var client = new Client { Assessment = (_, count) => Result(count < 3 ? FindingSeverity.Error : null) };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(result.Succeeded && result.Steps.Last().FailureCount == 2 && result.Steps.Count == 6);
        });
        await Check("severity threshold follows Error then Warning then Info", async () =>
        {
            foreach (var threshold in Enum.GetValues<FindingSeverity>())
            {
                foreach (var severity in Enum.GetValues<FindingSeverity>())
                {
                    var client = new Client { Assessment = (_, _) => Result(severity) };
                    var result = await Workflow(client).Add(new("Implement", "implement"))
                        .Add(new AppServerAcceptanceStep("Review", "[review:A]")
                        {
                            RetryStep = "Implement", MaxFailures = 1, FailureSeverity = threshold
                        }).RunAsync();
                    bool shouldFail = severity == FindingSeverity.Error ||
                        threshold == FindingSeverity.Info || threshold == FindingSeverity.Warning && severity == FindingSeverity.Warning;
                    Assert(result.Succeeded != shouldFail && result.Steps.Last().FailureCount == (shouldFail ? 1 : 0));
                }
            }
        });
        await Check("failed required checks remain failures even without findings", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result() with { Checks = [new("SPEC-001", AcceptanceCheckStatus.Failed, "test failed")] } };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]") { RetryStep = "Implement", MaxFailures = 1 }).RunAsync();
            Assert(!result.Succeeded && result.Steps.Last().AcceptanceDecision == AcceptanceDecision.NeedsRevision);
        });
        await Check("multiple acceptance steps have independent failure counters", async () =>
        {
            var client = new Client { Assessment = (name, count) => Result(count < (name == "A" ? 2 : 3) ? FindingSeverity.Error : null) };
            var result = await Workflow(client).Add(new("ImplementA", "implement A"))
                .Add(new AppServerAcceptanceStep("ReviewA", "[review:A]") { RetryStep = "ImplementA", MaxFailures = 2 })
                .Add(new("ImplementB", "implement B"))
                .Add(new AppServerAcceptanceStep("ReviewB", "[review:B]") { RetryStep = "ImplementB", MaxFailures = 3 }).RunAsync();
            Assert(result.Succeeded && result.Steps.Last(s => s.Name == "ReviewA").FailureCount == 1);
            Assert(result.Steps.Last(s => s.Name == "ReviewB").FailureCount == 2);
        });
        await Check("failure counters reset between runs", async () =>
        {
            var client = new Client { Assessment = (_, count) => Result(count % 2 == 1 ? FindingSeverity.Error : null) };
            var workflow = Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]") { RetryStep = "Implement", MaxFailures = 2 });
            var first = await workflow.RunAsync();
            var second = await workflow.RunAsync();
            Assert(first.Succeeded && second.Succeeded && first.Steps.Last().FailureCount == 1 && second.Steps.Last().FailureCount == 1);
        });
        await Check("inconclusive and empty assessments stop without counting a product failure", async () =>
        {
            foreach (bool empty in new[] { false, true })
            {
                var client = new Client { Assessment = (_, _) => Result() with
                    { Checks = empty ? [] : [new("SPEC-001", AcceptanceCheckStatus.NotEvaluated, "test environment unavailable")] } };
                var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
                Assert(!result.Succeeded && result.Steps.Count == 2 && result.Error is null);
                Assert(result.Steps[1].AcceptanceDecision == AcceptanceDecision.Inconclusive && result.Steps[1].FailureCount == 0);
            }
        });
        await Check("required criterion IDs are sent to the skill and missing checks stop", async () =>
        {
            var client = new Client();
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]")
                {
                    RetryStep = "Implement", RequiredCriterionIds = ["SPEC-001", "SPEC-002"]
                }).RunAsync();
            Assert(!result.Succeeded && result.Steps.Last().AcceptanceDecision == AcceptanceDecision.Inconclusive);
            Assert(client.Prompts[1].Contains("SPEC-002"));
        });
        await Check("malformed data is an execution error rather than an acceptance failure", async () =>
        {
            var client = new Client { Malformed = true };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(!result.Succeeded && result.Error is JsonException && result.Steps.Last().FailureCount == 0);
            Assert(!result.Steps.Last().Completed && client.Prompts.Count == 2);
        });
        await Check("task issues stop before automatic retry", async () =>
        {
            var client = new Client { TaskIssues = true, Assessment = (_, _) => Result(FindingSeverity.Error) };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(!result.Succeeded && result.Steps.Count == 2 && result.Steps.Last().FailureCount == 0);
        });
        await Check("custom completion validation is preserved and failure does not retry", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result(FindingSeverity.Error) };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]")
                {
                    RetryStep = "Implement",
                    ValidateCompletionAsync = (_, _) => throw new IOException("report missing")
                }).RunAsync();
            Assert(!result.Succeeded && result.Error is IOException && result.Steps.Last().FailureCount == 0);
        });
        await Check("acceptance supports prompt factories and inherited execution options", async () =>
        {
            var client = new Client();
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", context => "[review:A] " + context.GetStep("Implement").Execution!.Message)
                {
                    RetryStep = "Implement",
                    Options = new() { Sandbox = AppServerSandboxMode.WorkspaceWrite, NetworkAccess = true }
                }).RunAsync();
            Assert(result.Succeeded && client.Options.Last().NetworkAccess == true && client.Prompts.Last().Contains("finished"));
        });
        await Check("invalid acceptance configuration fails before any model calls", async () =>
        {
            AppServerAcceptanceStep[] invalid =
            [
                new("Review", "[review:A]") { RetryStep = "Missing" },
                new("Review", "[review:A]") { RetryStep = "" },
                new("Review", "[review:A]") { RetryStep = "Implement", MaxFailures = 0 },
                new("Review", "[review:A]") { RetryStep = "Implement", FailureSeverity = (FindingSeverity)99 },
                new("Review", "[review:A]") { RetryStep = "Implement", NextStep = _ => "Implement" },
                new("Review", "[review:A]") { RetryStep = "Implement", DataSchema = null },
                new("Review", "[review:A]") { RetryStep = "Implement", RequiredCriterionIds = [" "] }
            ];
            foreach (var step in invalid)
            {
                var client = new Client();
                try
                {
                    await Workflow(client).Add(new("Implement", "implement")).Add(step).RunAsync();
                    throw new Exception("Expected invalid configuration.");
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                {
                    Assert(client.Prompts.Count == 0);
                }
            }
        });
        return passed;

        AppServerWorkflow Workflow(Client client) => new(client, workspace, (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        async Task Check(string name, Func<Task> action)
        {
            await action();
            passed++;
            Console.WriteLine($"PASS: acceptance step {name}");
        }
    }

    private static AppServerAcceptanceStep Review() => new("Review", "[review:A]") { RetryStep = "Implement" };

    private static AcceptanceResult Result(FindingSeverity? severity = null)
        => new([new("SPEC-001", AcceptanceCheckStatus.Passed, "Spec and code evidence")],
            severity is { } value ? [new("AC-001", value, "SPEC-001", "finding", "Spec and code evidence", "suggested fix")] : [],
            "reports/review.md");

    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Acceptance step assertion failed.");
        }
    }

    private sealed class Client : IAppServerClient
    {
        private readonly Dictionary<string, int> _counts = [];
        private int _thread;
        public List<string> Prompts { get; } = [];
        public List<AppServerOptions> Options { get; } = [];
        public Func<string, int, AcceptanceResult> Assessment { get; init; } = (_, _) => Result();
        public bool Malformed { get; init; }
        public bool TaskIssues { get; init; }

        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult($"thread-{++_thread}");

        public Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
            AppServerOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            Options.Add(options!);
            object? data = null;
            bool review = prompt.Contains("[review:");
            if (review)
            {
                string name = prompt.Contains("[review:A]") ? "A" : "B";
                _counts[name] = _counts.GetValueOrDefault(name) + 1;
                Assert(JsonElement.DeepEquals(outputSchema!.Value.GetProperty("properties").GetProperty("data").GetProperty("anyOf")[0], AcceptanceResultSchema.Value));
                data = Malformed ? new { invalid = "output" } : Assessment(name, _counts[name]);
            }
            return Task.FromResult(new TurnResult(threadId, Prompts.Count.ToString(), "completed", JsonSerializer.Serialize(new
            {
                state = "completed", message = "finished", questions = Array.Empty<string>(),
                issues = review && TaskIssues ? new[] { "unable to finish task" } : [], data
            }, JsonOptions)));
        }
    }
}
