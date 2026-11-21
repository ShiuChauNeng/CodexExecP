using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.AppServer.Workflows.ConsoleUI;

internal static class WorkflowDecisionChecks
{
    /// <summary>
    /// 驗證需求決策確認、群組傳遞、結果保存與問答限制。
    /// </summary>
    internal static async Task<int> RunAsync(string workspace)
    {
        int passed = 0;
        await Check("unconfigured steps preserve the existing question callback", async () =>
        {
            var client = new Client();
            int calls = 0;
            var result = await Workflow(client, (request, _) =>
            {
                calls++;
                Assert(!request.IsDecisionConfirmation && request.DecisionGroup is null);
                return Answer("RAW-ANSWER");
            }).Add(new("Writer", "[ask:A]")).RunAsync();
            Assert(result.Succeeded && calls == 1 && result.Decisions.Count == 0);
        });
        await Check("only explicitly selected original question answers reach configured readers", async () =>
        {
            var client = new Client { QuestionCount = 2 };
            int confirmations = 0;
            var result = await Workflow(client, (request, _) =>
            {
                if (!request.IsDecisionConfirmation)
                {
                    return Answer("  RAW-APPROVED  ", "RAW-PRIVATE");
                }
                Assert(request.DecisionGroup == "FeatureA" && request.Questions.Count == 1);
                return Answer(++confirmations == 1 ? "2" : "1");
            }).Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA", MaxInputRounds = 1 })
                .Add(new AppServerAcceptanceStep("Reader", "[review]") { ReadDecisionsFrom = ["FeatureA"], RetryStep = "Writer" })
                .Add(new("Style", "[style]"))
                .Add(new("Other", "[other]") { ReadDecisionsFrom = ["featurea"] }).RunAsync();
            Assert(result.Succeeded && confirmations == 2 && result.Decisions.Count == 1);
            var decision = result.Decisions[0];
            Assert(decision.Group == "FeatureA" && decision.StepName == "Writer" && decision.TurnId == "1");
            Assert(decision.Message == "Needs a decision" && decision.Question == "Question 1" &&
                decision.Answer == "  RAW-APPROVED  " && decision.ConfirmedAt != default);
            Assert(decision.ThreadId == result.Steps[0].ThreadId && result.Steps[0].Decisions.Count == 1);
            string reader = client.Prompts.Single(prompt => prompt.Contains("[review]"));
            Assert(reader.Contains("RAW-APPROVED") && !reader.Contains("RAW-PRIVATE"));
            Assert(!client.Prompts.Single(prompt => prompt.Contains("[style]")).Contains("RAW-APPROVED"));
            Assert(!client.Prompts.Single(prompt => prompt.Contains("[other]")).Contains("RAW-APPROVED"));
            Assert(!client.Prompts.Any(prompt => prompt.Contains("確認記錄")));
        });
        await Check("declining to record does not publish ordinary answers", async () =>
        {
            var client = new Client();
            var result = await Workflow(client, (request, _) => Answer(request.IsDecisionConfirmation ? "1" : "RAW-SKIPPED"))
                .Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA" })
                .Add(new("Reader", "[read]") { ReadDecisionsFrom = ["FeatureA"] }).RunAsync();
            Assert(result.Succeeded && result.Decisions.Count == 0);
            Assert(!client.Prompts.Last().Contains("RAW-SKIPPED"));
        });
        await Check("decision groups remain separate and readers may request multiple groups", async () =>
        {
            var client = new Client();
            var result = await Workflow(client, (request, _) => Answer(request.IsDecisionConfirmation ? "2" : "RAW-" + request.StepName))
                .Add(new("A", "[ask:A]") { RecordDecisionsTo = "FeatureA" })
                .Add(new("B", "[ask:B]") { RecordDecisionsTo = "FeatureB" })
                .Add(new("ReaderA", "[read:A]") { ReadDecisionsFrom = ["FeatureA"] })
                .Add(new("ReaderBoth", "[read:both]") { ReadDecisionsFrom = ["FeatureA", "FeatureB"] }).RunAsync();
            Assert(result.Succeeded && result.Decisions.Count == 2);
            string firstReader = client.Prompts.Single(prompt => prompt.Contains("[read:A]"));
            Assert(firstReader.Contains("RAW-A") && !firstReader.Contains("RAW-B"));
            Assert(client.Prompts.Last().Contains("RAW-A") && client.Prompts.Last().Contains("RAW-B"));
        });
        await Check("returning to a step includes all latest confirmed decisions", async () =>
        {
            var client = new Client();
            int rounds = 0;
            var result = await Workflow(client, (request, _) => Answer(request.IsDecisionConfirmation ? "2" : $"RAW-{++rounds}"))
                .Add(new("Writer", "[ask:A]")
                {
                    RecordDecisionsTo = "FeatureA",
                    ReadDecisionsFrom = ["FeatureA"],
                    NextStep = context => context.Steps.Count < 2 ? "Writer" : null
                })
                .Add(new("Reader", "[read]") { ReadDecisionsFrom = ["FeatureA"] }).RunAsync();
            Assert(result.Succeeded && result.Decisions.Count == 2 && result.Steps[0].Decisions.Count == 1);
            string secondWriter = client.Prompts.Where(prompt => prompt.Contains("[ask:A]")).Last();
            Assert(secondWriter.Contains("RAW-1"));
            Assert(client.Prompts.Last().Contains("RAW-1") && client.Prompts.Last().Contains("RAW-2"));
        });
        await Check("acceptance retries receive decisions confirmed during intervening implementation", async () =>
        {
            var client = new Client { ReviewFailures = 1 };
            int rounds = 0;
            var result = await Workflow(client, (request, _) => Answer(request.IsDecisionConfirmation ? "2" : $"RAW-{++rounds}"))
                .Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA", Thread = AppServerThread.New })
                .Add(new AppServerAcceptanceStep("Review", "[review]")
                    { ReadDecisionsFrom = ["FeatureA"], RetryStep = "Writer", Thread = AppServerThread.New }).RunAsync();
            Assert(result.Succeeded && result.Decisions.Count == 2);
            var reviews = client.Prompts.Where(prompt => prompt.Contains("[review]")).ToArray();
            Assert(reviews.Length == 2 && reviews[0].Contains("RAW-1") && !reviews[0].Contains("RAW-2"));
            Assert(reviews[1].Contains("RAW-1") && reviews[1].Contains("RAW-2"));
        });
        await Check("decisions reset on each run and previous result snapshots remain intact", async () =>
        {
            var client = new Client();
            bool skipWriter = false;
            var workflow = Workflow(client, (request, _) => Answer(request.IsDecisionConfirmation ? "2" : "RAW-FIRST-RUN"))
                .Add(new("Route", "route") { NextStep = _ => skipWriter ? "Reader" : "Writer" })
                .Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA" })
                .Add(new("Reader", "[read]") { ReadDecisionsFrom = ["FeatureA"] });
            var first = await workflow.RunAsync();
            skipWriter = true;
            var second = await workflow.RunAsync();
            Assert(first.Succeeded && second.Succeeded && first.Decisions.Count == 1 && second.Decisions.Count == 0);
            Assert(!client.Prompts.Last().Contains("RAW-FIRST-RUN"));
        });
        await Check("invalid original answers are rejected before asking for confirmation", async () =>
        {
            var client = new Client();
            int confirmations = 0;
            var result = await Workflow(client, (request, _) =>
            {
                if (request.IsDecisionConfirmation)
                {
                    confirmations++;
                }
                return Answer("one", "extra");
            }).Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA" }).RunAsync();
            Assert(!result.Succeeded && result.Error is ArgumentException && confirmations == 0 && result.Decisions.Count == 0);
        });
        await Check("invalid confirmation responses never count as consent", async () =>
        {
            foreach (var choice in new[] { Array.Empty<string>(), new[] { "yes" }, new[] { "2", "2" } })
            {
                var client = new Client();
                var result = await Workflow(client, (request, _) => request.IsDecisionConfirmation ? Answer(choice) : Answer("RAW"))
                    .Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA" }).RunAsync();
                Assert(!result.Succeeded && result.Error is ArgumentException && result.Decisions.Count == 0 && client.Prompts.Count == 1);
            }
        });
        await Check("confirmed records remain available if the implementation subsequently fails", async () =>
        {
            var client = new Client { FailAfterAnswer = true };
            var result = await Workflow(client, (request, _) => Answer(request.IsDecisionConfirmation ? "2" : "RAW"))
                .Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA" }).RunAsync();
            Assert(!result.Succeeded && result.Error is IOException && result.Decisions.Count == 1 && result.Steps[0].Decisions.Count == 1);
        });
        await Check("cancelling confirmation never resumes the AI", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var client = new Client();
            var workflow = Workflow(client, (request, _) =>
            {
                if (request.IsDecisionConfirmation)
                {
                    cancellation.Cancel();
                    return Answer("2");
                }
                return Answer("RAW");
            }).Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA" });
            try
            {
                await workflow.RunAsync(cancellation.Token);
                throw new Exception("Cancellation was not preserved.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Assert(client.Prompts.Count == 1);
            }
        });
        await Check("confirmation is bounded by the existing input timeout", async () =>
        {
            var lateChoice = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new Client();
            var result = await Workflow(client, (request, _) => request.IsDecisionConfirmation ? lateChoice.Task : Answer("RAW"))
                .Add(new("Writer", "[ask:A]") { RecordDecisionsTo = "FeatureA", InputTimeout = TimeSpan.FromSeconds(1) }).RunAsync();
            lateChoice.SetResult(["2"]);
            Assert(!result.Succeeded && result.Error is TimeoutException && result.Decisions.Count == 0 && client.Prompts.Count == 1);
        });
        await Check("invalid group settings fail before calling the AI", async () =>
        {
            AppServerWorkflowStep[] invalid =
            [
                new("Writer", "task") { RecordDecisionsTo = " " },
                new("Writer", "task") { ReadDecisionsFrom = null! },
                new("Writer", "task") { ReadDecisionsFrom = [""] },
                new("Writer", "task") { ReadDecisionsFrom = ["FeatureA", "FeatureA"] }
            ];
            foreach (var step in invalid)
            {
                var client = new Client();
                try
                {
                    await Workflow(client, (_, _) => Answer("RAW")).Add(step).RunAsync();
                    throw new Exception("Invalid groups were accepted.");
                }
                catch (ArgumentException)
                {
                    Assert(client.Prompts.Count == 0);
                }
            }
        });
        await Check("console confirmation requires an explicit choice and preserves ordinary answer text", async () =>
        {
            TextReader originalInput = Console.In;
            TextWriter originalOutput = Console.Out;
            using var output = new StringWriter();
            try
            {
                Console.SetIn(new StringReader("  RAW  \ninvalid\n 2 \n"));
                Console.SetOut(output);
                var handler = new ConsoleWorkflowInputHandler { EnableNotifications = false };
                var request = new AppServerWorkflowInputRequest("Writer", "thread", "turn", "message", ["question"]);
                var ordinary = await handler.AnswerAsync(request);
                var confirmed = await handler.AnswerAsync(request with { IsDecisionConfirmation = true, DecisionGroup = "FeatureA" });
                Assert(ordinary[0] == "  RAW  " && confirmed[0] == "2" && output.ToString().Contains("請輸入 1"));
            }
            finally
            {
                Console.SetIn(originalInput);
                Console.SetOut(originalOutput);
            }
        });
        return passed;

        /// <summary>
        /// 以測試用戶端、測試工作區與回答處理建立工作流程。
        /// </summary>
        AppServerWorkflow Workflow(Client client,
            Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> handler)
            => new(client, workspace, handler);

        /// <summary>
        /// 執行測試案例，成功後累計通過數並輸出案例名稱。
        /// </summary>
        async Task Check(string name, Func<Task> run)
        {
            await run();
            passed++;
            Console.WriteLine($"PASS: workflow decisions {name}");
        }
    }

    /// <summary>
    /// 將指定回答包裝為非同步結果，供需求決策測試使用。
    /// </summary>
    private static Task<IReadOnlyList<string>> Answer(params string[] answers)
        => Task.FromResult<IReadOnlyList<string>>(answers);

    /// <summary>
    /// 檢查測試條件，不符合時拋出例外使案例失敗。
    /// </summary>
    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Workflow decision assertion failed.");
        }
    }

    private sealed class Client : IAppServerClient
    {
        private int _thread;
        private int _turn;
        private int _reviews;
        public int QuestionCount { get; init; } = 1;
        public int ReviewFailures { get; init; }
        public bool FailAfterAnswer { get; init; }
        public List<string> Prompts { get; } = [];

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
            if (FailAfterAnswer && prompt.Contains("使用者回答如下"))
            {
                throw new IOException("Implementation failed after confirmation.");
            }
            bool asks = prompt.Contains("[ask:");
            object? data = null;
            if (prompt.Contains("[review]"))
            {
                data = new AcceptanceResult([new("SPEC-001", ++_reviews <= ReviewFailures
                    ? AcceptanceCheckStatus.Failed : AcceptanceCheckStatus.Passed, "Current evidence")], [], "review.md");
            }
            return Task.FromResult(new TurnResult(threadId, (++_turn).ToString(), "completed", JsonSerializer.Serialize(new
            {
                state = asks ? "needs_input" : "completed", message = asks ? "Needs a decision" : "Done",
                questions = asks ? Enumerable.Range(1, QuestionCount).Select(index => $"Question {index}").ToArray() : [],
                issues = Array.Empty<string>(), data
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
    }
}
