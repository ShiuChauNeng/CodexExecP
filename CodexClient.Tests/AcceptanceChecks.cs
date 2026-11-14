using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;

internal static class AcceptanceChecks
{
    private static readonly string[] RequiredCriteria = ["SPEC-001", "SPEC-002"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string workspace)
    {
        int passed = 0;
        Check("all required criteria passed", () =>
            Assert(Result().Decide(RequiredCriteria) == AcceptanceDecision.Passed));
        Check("warnings and information allow acceptance", () =>
            Assert(Result(findings: [Finding(FindingSeverity.Warning), Finding(FindingSeverity.Info, "AC-002")])
                .Decide(RequiredCriteria) == AcceptanceDecision.Passed));
        Check("error finding requests revision", () =>
            Assert(Result(findings: [Finding(FindingSeverity.Error)]).Decide(RequiredCriteria) == AcceptanceDecision.NeedsRevision));
        Check("failed criterion requests revision even without error findings", () =>
            Assert(Result(AcceptanceCheckStatus.Failed).Decide(RequiredCriteria) == AcceptanceDecision.NeedsRevision));
        Check("missing required criterion cannot pass", () =>
            Assert((Result() with { Checks = [new("SPEC-001", AcceptanceCheckStatus.Passed, "test evidence")] })
                .Decide(RequiredCriteria) == AcceptanceDecision.Inconclusive));
        Check("empty check list cannot pass", () =>
            Assert((Result() with { Checks = [] }).Decide(RequiredCriteria) == AcceptanceDecision.Inconclusive));
        Check("not evaluated is inconclusive rather than a product failure", () =>
            Assert(Result(AcceptanceCheckStatus.NotEvaluated).Decide(RequiredCriteria) == AcceptanceDecision.Inconclusive));
        Check("incomplete acceptance takes priority over revision", () =>
            Assert(Result(AcceptanceCheckStatus.NotEvaluated, [Finding(FindingSeverity.Error)])
                .Decide(RequiredCriteria) == AcceptanceDecision.Inconclusive));
        Check("duplicate criteria are invalid output", () =>
            Throws<ArgumentException>(() => (Result() with
                { Checks = [new("SPEC-001", AcceptanceCheckStatus.Passed, "a"), new("SPEC-001", AcceptanceCheckStatus.Failed, "b")] }).Validate()));
        Check("duplicate findings are invalid output", () =>
            Throws<ArgumentException>(() => Result(findings: [Finding(FindingSeverity.Error), Finding(FindingSeverity.Warning)]).Validate()));
        Check("findings require evidence and valid criterion references", () =>
        {
            Throws<ArgumentException>(() => Result(findings: [Finding(FindingSeverity.Error) with { Evidence = " " }]).Validate());
            Throws<ArgumentException>(() => Result(findings: [Finding(FindingSeverity.Error) with { CriterionId = "unknown" }]).Validate());
        });
        Check("missing report and unknown enum cannot pass", () =>
        {
            Throws<ArgumentException>(() => (Result() with { ReportPath = " " }).Validate());
            Throws<ArgumentOutOfRangeException>(() => Result(findings: [Finding((FindingSeverity)99)]).Validate());
        });
        Check("caller must supply required criteria", () =>
            Throws<ArgumentException>(() => Result().Decide([])));
        Check("required JSON properties cannot default to passed or error", () =>
        {
            Throws<JsonException>(() => JsonSerializer.Deserialize<AcceptanceResult>(
                """{"checks":[{"criterionId":"SPEC-001","evidence":"test"}],"findings":[],"reportPath":"report.md"}""", JsonOptions));
            Throws<JsonException>(() => JsonSerializer.Deserialize<AcceptanceResult>(
                """{"checks":[],"findings":[{"id":"AC-001","severity":"Unknown","criterionId":null,"message":"issue","evidence":"code","suggestedFix":null}],"reportPath":"report.md"}""", JsonOptions));
        });

        var client = new AcceptanceClient(Result(AcceptanceCheckStatus.Failed));
        var workflow = new AppServerWorkflow(client, workspace,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        workflow.Add(new AppServerWorkflowStep("Acceptance", "Run acceptance skill against SPEC-001 and SPEC-002")
        {
            DataSchema = AcceptanceResultSchema.Value,
            StopOnIssues = true,
            ValidateCompletionAsync = (task, _) =>
            {
                task.Data!.Value.Deserialize<AcceptanceResult>(JsonOptions)!.Validate();
                return Task.CompletedTask;
            }
        });
        var result = await workflow.RunAsync();
        Assert(result.Succeeded && result.Steps[0].Accepted && result.Steps[0].Completed);
        Assert(result.Steps[0].GetData<AcceptanceResult>().Decide(RequiredCriteria) == AcceptanceDecision.NeedsRevision);
        passed++;
        Console.WriteLine("PASS: Skill acceptance failure remains a successful workflow step with a revision decision");
        return passed;

        void Check(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine($"PASS: acceptance {name}");
        }
    }

    private static AcceptanceResult Result(AcceptanceCheckStatus status = AcceptanceCheckStatus.Passed,
        IReadOnlyList<AcceptanceFinding>? findings = null)
        => new([new("SPEC-001", status, "Spec section and test evidence"),
                new("SPEC-002", AcceptanceCheckStatus.Passed, "Spec section and code evidence")],
            findings ?? [], "reports/acceptance-01.md");

    private static AcceptanceFinding Finding(FindingSeverity severity, string id = "AC-001")
        => new(id, severity, "SPEC-001", "Finding description", "Spec section and code location", null);

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Acceptance assertion failed.");
        }
    }

    private sealed class AcceptanceClient(AcceptanceResult result) : IAppServerClient
    {
        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult("acceptance-thread");

        public Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
            AppServerOptions? options = null, CancellationToken cancellationToken = default)
        {
            var dataSchema = outputSchema!.Value.GetProperty("properties").GetProperty("data").GetProperty("anyOf")[0];
            Assert(JsonElement.DeepEquals(dataSchema, AcceptanceResultSchema.Value));
            return Task.FromResult(new TurnResult(threadId, "acceptance-turn", "completed", JsonSerializer.Serialize(new
            {
                state = "completed",
                message = "Acceptance completed; implementation needs revision.",
                questions = Array.Empty<string>(),
                issues = Array.Empty<string>(),
                data = result
            }, JsonOptions)));
        }
    }
}
