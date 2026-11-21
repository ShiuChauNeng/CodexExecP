using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.Models;

internal static class AcceptanceStepChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 驗證驗收步驟的修正、補查、次數上限與異常停止行為。
    /// </summary>
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
            Assert(result.Steps[0].ThreadId == result.Steps[2].ThreadId);
            Assert(result.Steps[1].ThreadId == result.Steps[3].ThreadId);
            Assert(result.Steps.Select(step => step.ThreadId).Distinct().Count() == 3);
            Assert(client.Prompts[2].Contains("Spec and code evidence") && client.Prompts[2].Contains("\"acceptanceStep\":\"Review\""));
            Assert(!client.Prompts[4].Contains("\"acceptanceStep\":\"Review\""));
        });
        await Check("final acceptance guard rejects a forward retry that bypasses reassessment", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result(FindingSeverity.Error) };
            var progress = new RecordingProgress();
            var workflow = new AppServerWorkflow(client, workspace, (_, _) => Task.FromResult<IReadOnlyList<string>>([]), progress);
            var result = await workflow.Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]") { RetryStep = "Style" })
                .Add(new("Style", "style")).RunAsync();
            Assert(!result.Succeeded && result.FailedStep == "Review" && result.Error is null);
            Assert(result.StopReason!.Contains("Review（NeedsRevision）") && result.StopReason.Contains("流程結束前檢查"));
            Assert(result.Steps.Select(step => step.Name).SequenceEqual(["Implement", "Review", "Style"]));
            Assert(result.Steps[1].FailureCount == 1 && result.Steps.Last().Accepted);
            Assert(progress.Items.Last().Status == AppServerWorkflowStatus.Stopped);
            Assert(progress.Items.All(item => item.Status != AppServerWorkflowStatus.Completed));
        });
        await Check("final acceptance guard rejects routing around acceptance after revision", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result(FindingSeverity.Error) };
            var result = await Workflow(client).Add(new("Implement", "implement")
                { NextStep = context => context.Steps.Any(step => step.Name == "Review") ? "Style" : null })
                .Add(Review()).Add(new("Style", "style")).RunAsync();
            Assert(!result.Succeeded && result.FailedStep == "Review" && result.Error is null);
            Assert(result.StopReason!.Contains("Review（NeedsRevision）"));
            Assert(result.Steps.Select(step => step.Name).SequenceEqual(["Implement", "Review", "Implement", "Style"]));
        });
        await Check("final acceptance guard reports every pending acceptance independently", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result(FindingSeverity.Error) };
            var result = await Workflow(client)
                .Add(new AppServerAcceptanceStep("ReviewA", "[review:A]") { RetryStep = "Continue" })
                .Add(new("Continue", "continue"))
                .Add(new AppServerAcceptanceStep("ReviewB", "[review:B]") { RetryStep = "Style" })
                .Add(new("Style", "style")).RunAsync();
            Assert(!result.Succeeded && result.FailedStep == "ReviewA" && result.Error is null);
            Assert(result.StopReason!.Contains("ReviewA（NeedsRevision）") && result.StopReason.Contains("ReviewB（NeedsRevision）"));
            Assert(result.Steps.Count == 4 && result.Steps[0].FailureCount == 1 && result.Steps[2].FailureCount == 1);
        });
        await Check("final acceptance guard permits an intentionally skipped acceptance", async () =>
        {
            var result = await Workflow(new())
                .Add(new("Implement", "implement") { NextStep = _ => "Style" })
                .Add(Review()).Add(new("Style", "style")).RunAsync();
            Assert(result.Succeeded && result.FailedStep is null && result.StopReason is null);
            Assert(result.Steps.Select(step => step.Name).SequenceEqual(["Implement", "Style"]));
        });
        await Check("GetOrCreate shares A and B across C acceptance retries and resets between runs", async () =>
        {
            var client = new Client { Assessment = (_, count) => Result(count % 2 == 1 ? FindingSeverity.Error : null) };
            var workflow = Workflow(client)
                .Add(new("A", "[initial:A] implement") { Thread = AppServerThread.GetOrCreate("shared") })
                .Add(new("B", "[initial:B] continue implementation") { Thread = AppServerThread.Use("shared") })
                .Add(new AppServerAcceptanceStep("C", "[review:A]") { RetryStep = "A" });
            var first = await workflow.RunAsync();
            var second = await workflow.RunAsync();
            foreach (var result in new[] { first, second })
            {
                Assert(result.Succeeded && result.Steps.Select(step => step.Name).SequenceEqual(["A", "B", "C", "A", "B", "C"]));
                string shared = result.Threads["shared"];
                Assert(result.Steps.Where(step => step.Name != "C").All(step => step.ThreadId == shared));
                Assert(result.Steps[2].ThreadId == result.Steps[5].ThreadId && result.Steps[2].ThreadId != shared);
            }
            Assert(first.Threads["shared"] != second.Threads["shared"]);
            Assert(client.Prompts[3].Contains("\"acceptanceStep\":\"C\""));
            Assert(client.Prompts[0].Contains("[initial:A]") && !client.Prompts[3].Contains("[initial:A]"));
            Assert(client.Prompts[4].Contains("[initial:B]") && client.Prompts[5].Contains("[review:A]"));
            Assert(client.Prompts[6].Contains("[initial:A]") && !client.Prompts[9].Contains("[initial:A]"));
        });
        await Check("acceptance retries only regenerate the initial prompt when the target lacks its original thread", async () =>
        {
            AppServerThread[] modes = [AppServerThread.PerStep, AppServerThread.GetOrCreate("shared"),
                AppServerThread.Use("seed"), AppServerThread.New, AppServerThread.Create("shared")];
            foreach (var mode in modes)
            {
                var client = new Client { Assessment = (_, count) => Result(count == 1 ? FindingSeverity.Error : null) };
                int factoryCalls = 0;
                var workflow = Workflow(client);
                bool useSeed = mode.Mode == AppServerThreadMode.Use;
                if (useSeed)
                {
                    workflow.Add(new("Seed", "seed") { Thread = AppServerThread.Create("seed") });
                }
                workflow.Add(new("Implement", _ => { factoryCalls++; return "[initial:Implement]"; }) { Thread = mode })
                    .Add(Review());
                var result = await workflow.RunAsync();
                bool fresh = mode.Mode is AppServerThreadMode.New or AppServerThreadMode.Create;
                int offset = useSeed ? 1 : 0;
                Assert(result.Succeeded && factoryCalls == (fresh ? 2 : 1));
                Assert(client.Prompts[offset].Contains("[initial:Implement]"));
                Assert(client.Prompts[offset + 2].Contains("[initial:Implement]") == fresh);
                Assert(client.Prompts[offset + 2].Contains("Spec and code evidence"));
                Assert((result.Steps[offset].ThreadId == result.Steps[offset + 2].ThreadId) == !fresh);
            }
        });
        await Check("a retry target not previously executed retains its prompt on an existing shared thread", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result(FindingSeverity.Error) };
            var result = await Workflow(client)
                .Add(new("Seed", "seed") { Thread = AppServerThread.Create("shared") })
                .Add(Review())
                .Add(new("Implement", "[initial:Implement]") { Thread = AppServerThread.Use("shared") }).RunAsync();
            Assert(result.Steps.Count == 3 && result.Steps.Last().Accepted);
            Assert(client.Prompts[2].Contains("[initial:Implement]") && client.Prompts[2].Contains("Spec and code evidence"));
        });
        await Check("third failed acceptance stops without a fourth implementation", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result(FindingSeverity.Error) };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(Review()).Add(new("Style", "style")).RunAsync();
            Assert(!result.Succeeded && result.FailedStep == "Review" && result.Error!.Message.Contains("Failures: 3"));
            Assert(result.StopReason == "驗收不通過，已達失敗上限（3 次）。");
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
        await Check("inconclusive and empty assessments stop after bounded rechecks without a product failure", async () =>
        {
            foreach (bool empty in new[] { false, true })
            {
                var client = new Client { Assessment = (_, _) => Result() with
                    { Checks = empty ? [] : [new("SPEC-001", AcceptanceCheckStatus.NotEvaluated, "test environment unavailable")] } };
                var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
                Assert(!result.Succeeded && result.Steps.Count == 4 && result.Error is null);
                Assert(result.FailedStep == "Review" && result.StopReason == "必要驗收項目仍無法判定，已達補查上限（2 次）。");
                Assert(result.Steps[1].AcceptanceDecision == AcceptanceDecision.Inconclusive && result.Steps[1].FailureCount == 0);
                Assert(result.Steps.Last().InconclusiveCount == 3 && result.Steps.Last().FailureCount == 0);
                Assert(result.Steps.Skip(1).All(step => step.Name == "Review" && !step.Accepted));
            }
        });
        await Check("recheck can complete acceptance on the same thread without rerunning implementation", async () =>
        {
            var client = new Client { TaskIssues = true, Assessment = (_, count) => count == 1
                ? Result() with { Checks = [new("SPEC-001", AcceptanceCheckStatus.NotEvaluated, "Contract evidence missing")] }
                : Result() };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review())
                .Add(new("Style", "style")).RunAsync();
            Assert(result.Succeeded && result.Steps.Select(s => s.Name).SequenceEqual(["Implement", "Review", "Review", "Style"]));
            Assert(result.Steps[1].ThreadId == result.Steps[2].ThreadId);
            Assert(result.Steps[2].FailureCount == 0 && result.Steps[2].InconclusiveCount == 1 && result.Steps[2].Accepted);
            Assert(client.Prompts[2].Contains("Contract evidence missing") && client.Prompts[2].Contains("[review:A]"));
            Assert(client.Prompts[2].Contains("不修改實作") && client.Prompts[2].Contains("GOGO.txt"));
            Assert(!client.Prompts[3].Contains("\"acceptanceStep\""));
        });
        await Check("known defects are revised before unresolved necessary checks are rechecked", async () =>
        {
            var client = new Client { Assessment = (_, count) => count < 3
                ? Result(count == 1 ? FindingSeverity.Error : null) with
                    { Checks = [new("SPEC-001", AcceptanceCheckStatus.NotEvaluated, "Missing contract")] }
                : Result() };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(result.Succeeded && result.Steps.Select(s => s.Name).SequenceEqual(["Implement", "Review", "Implement", "Review", "Review"]));
            Assert(result.Steps[1].AcceptanceDecision == AcceptanceDecision.NeedsRevision);
            Assert(result.Steps[3].AcceptanceDecision == AcceptanceDecision.Inconclusive);
            Assert(result.Steps.Last().FailureCount == 1 && result.Steps.Last().InconclusiveCount == 1);
        });
        await Check("explicit required criteria allow optional unevaluated checks without rechecking", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result() with { Checks = [.. Result().Checks,
                new("OPTIONAL-001", AcceptanceCheckStatus.NotEvaluated, "Optional limitation")] } };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]")
                    { RetryStep = "Implement", RequiredCriterionIds = ["SPEC-001"] }).RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 2 && result.Steps.Last().InconclusiveCount == 0);
            Assert(result.Steps.Last().GetData<AcceptanceResult>().Checks.Last().Status == AcceptanceCheckStatus.NotEvaluated);
        });
        await Check("unspecified required criteria keep all reported checks necessary", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result() with { Checks = [.. Result().Checks,
                new("EXTRA-001", AcceptanceCheckStatus.NotEvaluated, "Missing evidence")] } };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(!result.Succeeded && result.Steps.Last().InconclusiveCount == 3);
        });
        await Check("rechecks cannot drop previously reported unevaluated criteria", async () =>
        {
            var client = new Client { Assessment = (_, count) => count == 1
                ? Result() with { Checks = [.. Result().Checks,
                    new("SPEC-002", AcceptanceCheckStatus.NotEvaluated, "Missing evidence")] }
                : Result() };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(!result.Succeeded && result.Steps.Count == 4 && result.Steps.Last().InconclusiveCount == 3);
            Assert(result.Steps.Skip(1).All(step => step.AcceptanceDecision == AcceptanceDecision.Inconclusive && step.FailureCount == 0));
            Assert(client.Prompts[3].Contains("必要驗收項目 ID：") && client.Prompts[3].Contains("SPEC-002"));
        });
        await Check("product revisions retain both passed and failed criterion IDs without reusing their statuses", async () =>
        {
            foreach (string omittedId in new[] { "SPEC-001", "SPEC-002" })
            {
                var client = new Client { Assessment = (_, count) => Result() with
                {
                    Checks = count == 1
                        ? [new("SPEC-001", AcceptanceCheckStatus.Passed, "Passed evidence"),
                            new("SPEC-002", AcceptanceCheckStatus.Failed, "Defect evidence")]
                        : count == 2
                            ? [new(omittedId == "SPEC-001" ? "SPEC-002" : "SPEC-001", AcceptanceCheckStatus.Passed, "Current evidence")]
                            : [new("SPEC-001", AcceptanceCheckStatus.Passed, "Current evidence"),
                                new("SPEC-002", AcceptanceCheckStatus.Passed, "Current evidence")]
                } };
                var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
                Assert(result.Succeeded && result.Steps.Select(step => step.Name).SequenceEqual(["Implement", "Review", "Implement", "Review", "Review"]));
                Assert(result.Steps[3].AcceptanceDecision == AcceptanceDecision.Inconclusive);
                Assert(result.Steps.Last().FailureCount == 1 && result.Steps.Last().InconclusiveCount == 1);
            }
        });
        await Check("new criteria discovered during rechecks remain necessary on subsequent rechecks", async () =>
        {
            var client = new Client { Assessment = (_, count) => Result() with
            {
                Checks = count switch
                {
                    1 => [new("SPEC-001", AcceptanceCheckStatus.NotEvaluated, "Missing evidence")],
                    2 => [.. Result().Checks, new("SPEC-002", AcceptanceCheckStatus.NotEvaluated, "New check")],
                    3 => Result().Checks,
                    _ => [.. Result().Checks, new("SPEC-002", AcceptanceCheckStatus.Passed, "Current evidence")]
                }
            } };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]") { RetryStep = "Implement", MaxInconclusiveRetries = 3 }).RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 5 && result.Steps.Last().InconclusiveCount == 3);
            Assert(result.Steps[3].AcceptanceDecision == AcceptanceDecision.Inconclusive && client.Prompts[4].Contains("SPEC-002"));
        });
        await Check("explicit required lists do not promote optional criteria observed before a recheck", async () =>
        {
            var client = new Client { Assessment = (_, count) => count == 1
                ? Result() with { Checks = [new("SPEC-001", AcceptanceCheckStatus.NotEvaluated, "Missing evidence"),
                    new("OPTIONAL-001", AcceptanceCheckStatus.NotEvaluated, "Optional limitation")] }
                : Result() };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]") { RetryStep = "Implement", RequiredCriterionIds = ["SPEC-001"] }).RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 3 && result.Steps.Last().InconclusiveCount == 1);
        });
        await Check("inferred criterion lists are independent per step and reset between runs", async () =>
        {
            var client = new Client { Assessment = (name, count) => Result() with
                { Checks = [new($"{name}-{count}", AcceptanceCheckStatus.Passed, "Current evidence")] } };
            var workflow = Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("ReviewA", "[review:A]") { RetryStep = "Implement" })
                .Add(new AppServerAcceptanceStep("ReviewB", "[review:B]") { RetryStep = "Implement" });
            var first = await workflow.RunAsync();
            var second = await workflow.RunAsync();
            Assert(first.Succeeded && second.Succeeded && first.Steps.Count == 3 && second.Steps.Count == 3);
        });
        await Check("inferred criteria survive a passed assessment when a later assessment returns to implementation", async () =>
        {
            var client = new Client { Assessment = (name, count) => name == "B"
                ? Result(count == 1 ? FindingSeverity.Error : null)
                : count == 2 ? Result() : Result() with
                    { Checks = [.. Result().Checks, new("SPEC-002", AcceptanceCheckStatus.Passed, "Current evidence")] } };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("ReviewA", "[review:A]") { RetryStep = "Implement" })
                .Add(new AppServerAcceptanceStep("ReviewB", "[review:B]") { RetryStep = "Implement" }).RunAsync();
            Assert(result.Succeeded && result.Steps.Select(step => step.Name)
                .SequenceEqual(["Implement", "ReviewA", "ReviewB", "Implement", "ReviewA", "ReviewA", "ReviewB"]));
            Assert(result.Steps[4].AcceptanceDecision == AcceptanceDecision.Inconclusive);
        });
        await Check("zero rechecks restores immediate stopping", async () =>
        {
            var client = new Client { Assessment = (_, _) => Result() with { Checks = [] } };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]")
                    { RetryStep = "Implement", MaxInconclusiveRetries = 0 }).RunAsync();
            Assert(!result.Succeeded && result.Steps.Count == 2 && result.Steps.Last().InconclusiveCount == 1);
        });
        await Check("recheck counts are independent per acceptance step and reset between runs", async () =>
        {
            var client = new Client { Assessment = (_, count) => count % 2 == 1
                ? Result() with { Checks = [] } : Result() };
            var workflow = Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("ReviewA", "[review:A]")
                    { RetryStep = "Implement", MaxInconclusiveRetries = 1 })
                .Add(new AppServerAcceptanceStep("ReviewB", "[review:B]")
                    { RetryStep = "Implement", MaxInconclusiveRetries = 1 });
            foreach (var result in new[] { await workflow.RunAsync(), await workflow.RunAsync() })
            {
                Assert(result.Succeeded && result.Steps.Count == 5);
                Assert(result.Steps.Skip(1).All(s => s.InconclusiveCount == 1 && s.FailureCount == 0));
            }
        });
        await Check("rechecks remain bounded across intervening product revisions", async () =>
        {
            var client = new Client { Assessment = (_, count) => count == 2
                ? Result(FindingSeverity.Error) : Result() with { Checks = [] } };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]")
                    { RetryStep = "Implement", MaxInconclusiveRetries = 1 }).RunAsync();
            Assert(!result.Succeeded && result.Steps.Count == 5);
            Assert(result.Steps.Last().FailureCount == 1 && result.Steps.Last().InconclusiveCount == 2);
        });
        await Check("rechecking on fresh threads preserves the original prompt and evidence", async () =>
        {
            var client = new Client { Assessment = (_, count) => count == 1
                ? Result() with { Checks = [new("SPEC-001", AcceptanceCheckStatus.NotEvaluated, "Missing contract")] }
                : Result() };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]")
                    { RetryStep = "Implement", Thread = AppServerThread.New }).RunAsync();
            Assert(result.Succeeded && result.Steps[1].ThreadId != result.Steps[2].ThreadId);
            Assert(client.Prompts[2].Contains("[review:A]") && client.Prompts[2].Contains("Missing contract"));
        });
        await Check("required criterion IDs are sent on rechecks and missing checks eventually stop", async () =>
        {
            var client = new Client();
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]")
                {
                    RetryStep = "Implement", RequiredCriterionIds = ["SPEC-001", "SPEC-002"]
                }).RunAsync();
            Assert(!result.Succeeded && result.Steps.Last().AcceptanceDecision == AcceptanceDecision.Inconclusive);
            Assert(result.Steps.Count == 4 && result.Steps.Last().InconclusiveCount == 3);
            Assert(client.Prompts.Skip(1).All(prompt => prompt.Contains("SPEC-002")));
        });
        await Check("malformed data is an execution error rather than an acceptance failure", async () =>
        {
            var client = new Client { Malformed = true };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(!result.Succeeded && result.Error is JsonException && result.Steps.Last().FailureCount == 0);
            Assert(!result.Steps.Last().Completed && client.Prompts.Count == 2);
        });
        await Check("duplicate product issues do not block automatic revision or successful acceptance", async () =>
        {
            var client = new Client { TaskIssues = true, Assessment = (_, count) => Result(count == 1 ? FindingSeverity.Error : null) };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 4 && result.Steps.Last().FailureCount == 1);
            Assert(result.Steps[1].HasIssues && result.Steps[1].Completed && !result.Steps[1].Accepted);
            Assert(result.Steps.Last().HasIssues && result.Steps.Last().Accepted && result.Steps.Last().Succeeded);
            Assert(result.Steps[1].Execution!.Issues[0] == "GOGO.txt 第 1 行算術錯誤，尚未修正。");
        });
        await Check("explicit StopOnIssues still adds a stopping condition", async () =>
        {
            var client = new Client { TaskIssues = true, Assessment = (_, _) => Result(FindingSeverity.Error) };
            var result = await Workflow(client).Add(new("Implement", "implement"))
                .Add(new AppServerAcceptanceStep("Review", "[review:A]") { RetryStep = "Implement", StopOnIssues = true }).RunAsync();
            Assert(!result.Succeeded && result.Steps.Count == 2 && result.Steps.Last().FailureCount == 0);
        });
        await Check("failed task state remains an execution failure without retry", async () =>
        {
            var client = new Client { FailedTask = true };
            var result = await Workflow(client).Add(new("Implement", "implement")).Add(Review()).RunAsync();
            Assert(!result.Succeeded && result.Steps.Count == 2 && result.Error is InvalidOperationException);
            Assert(!result.Steps.Last().Completed && result.Steps.Last().FailureCount == 0);
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
                    Options = new() { Sandbox = CodexSandboxMode.WorkspaceWrite, NetworkAccess = true }
                }).RunAsync();
            Assert(result.Succeeded && client.Options.Last().NetworkAccess == true && client.Prompts.Last().Contains("finished"));
        });
        await Check("acceptance instructions cannot hide an empty dynamic prompt", async () =>
        {
            foreach (string? prompt in new string?[] { null, " " })
            {
                var client = new Client();
                var result = await Workflow(client).Add(new("Implement", "implement"))
                    .Add(new AppServerAcceptanceStep("Review", _ => prompt!) { RetryStep = "Implement" }).RunAsync();
                Assert(!result.Succeeded && result.FailedStep == "Review" && result.Error is ArgumentException);
                Assert(client.Prompts.Count == 1);
            }
        });
        await Check("invalid acceptance configuration fails before any model calls", async () =>
        {
            AppServerAcceptanceStep[] invalid =
            [
                new("Review", "[review:A]") { RetryStep = "Missing" },
                new("Review", "[review:A]") { RetryStep = "" },
                new("Review", "[review:A]") { RetryStep = "Implement", MaxFailures = 0 },
                new("Review", "[review:A]") { RetryStep = "Implement", MaxInconclusiveRetries = -1 },
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

        /// <summary>
        /// 以測試用戶端、測試工作區與回答處理建立工作流程。
        /// </summary>
        AppServerWorkflow Workflow(Client client) => new(client, workspace, (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        /// <summary>
        /// 執行測試案例，成功後累計通過數並輸出案例名稱。
        /// </summary>
        async Task Check(string name, Func<Task> action)
        {
            await action();
            passed++;
            Console.WriteLine($"PASS: acceptance step {name}");
        }
    }

    /// <summary>
    /// 建立返回 Implement 的測試用驗收步驟。
    /// </summary>
    private static AppServerAcceptanceStep Review() => new("Review", "[review:A]") { RetryStep = "Implement" };

    /// <summary>
    /// 建立包含驗收項目、缺陷與報表路徑的測試資料。
    /// </summary>
    private static AcceptanceResult Result(FindingSeverity? severity = null)
        => new([new("SPEC-001", AcceptanceCheckStatus.Passed, "Spec and code evidence")],
            severity is { } value ? [new("AC-001", value, "SPEC-001", "finding", "Spec and code evidence", "suggested fix")] : [],
            "reports/review.md");

    /// <summary>
    /// 檢查測試條件，不符合時拋出例外使案例失敗。
    /// </summary>
    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Acceptance step assertion failed.");
        }
    }

    private sealed class RecordingProgress : IProgress<AppServerWorkflowProgress>
    {
        internal List<AppServerWorkflowProgress> Items { get; } = [];

        /// <summary>
        /// 同步保存進度，驗證結尾檢查失敗時不會回報流程完成。
        /// </summary>
        public void Report(AppServerWorkflowProgress value)
        {
            Items.Add(value);
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
        public bool FailedTask { get; init; }

        /// <summary>
        /// 建立測試用 thread 識別碼，不啟動實際 App Server。
        /// </summary>
        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult($"thread-{++_thread}");

        /// <summary>
        /// 依測試情境產生模擬 turn 結果，不呼叫實際 AI。
        /// </summary>
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
            bool failed = review && FailedTask;
            return Task.FromResult(new TurnResult(threadId, Prompts.Count.ToString(), "completed", JsonSerializer.Serialize(new
            {
                state = failed ? "failed" : "completed", message = failed ? "Unable to perform acceptance." : "finished", questions = Array.Empty<string>(),
                issues = review && TaskIssues ? new[] { "GOGO.txt 第 1 行算術錯誤，尚未修正。" } : [],
                data = failed ? null : data
            }, JsonOptions)));
        }
    }
}
