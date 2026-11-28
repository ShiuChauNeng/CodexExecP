using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.AppServer.Workflows.ConsoleUI;
using CodexClient.Models;

internal static class DiscussionStepChecks
{
    /// <summary>
    /// 驗證多輪討論、使用者結束、thread、輸入限制與既有停止規則。
    /// </summary>
    internal static async Task<int> RunAsync(string workspace)
    {
        int passed = 0;
        await Check("multiple completed answers share one thread until the user ends discussion", async () =>
        {
            var client = new Client { IssueOnTurn = 1 };
            var questions = new Queue<string>(["First question", "Second question", ""]);
            int validations = 0;
            var result = await Workflow(client, (request, _) =>
            {
                Assert(request.IsDiscussion && !request.IsDecisionConfirmation && request.Questions.Count == 1);
                Assert(request.Message.StartsWith("Answer"));
                return Answer(questions.Dequeue());
            }).Add(new("Before", "Implementation evidence"))
                .Add(new AppServerDiscussionStep("Discuss", _ => "Review instructions", model: CodexModels.Gpt61Sol)
                {
                    RecordDecisionsTo = "Group",
                    ValidateCompletionAsync = (_, _) => { validations++; return Task.CompletedTask; }
                })
                .Add(new("After", "Next task")).RunAsync();
            Assert(result.Succeeded && result.Steps.Count == 3 && questions.Count == 0 && validations == 1);
            Assert(result.Steps[1].Execution!.Message == "Answer 4" && result.Decisions.Count == 0);
            Assert(client.Calls.Count == 5 && client.Calls.Skip(1).Take(3).Select(call => call.Thread).Distinct().Count() == 1);
            Assert(client.Calls[1].Prompt.Contains("Answer 1") && client.Calls[1].Prompt.Contains("Before"));
            Assert(client.Calls[1].Prompt.Contains("Unresolved issue"));
            Assert(client.Calls[2].Prompt.Contains("First question") && !client.Calls[2].Prompt.Contains("Review instructions"));
            Assert(client.Calls[3].Prompt.Contains("Second question"));
            Assert(client.Calls.Skip(1).Take(3).All(call => call.Options.Sandbox == CodexSandboxMode.ReadOnly &&
                call.Options.Model == CodexModels.Gpt61Sol));
        });
        await Check("a separate discussion receives the final success or stopped result without changing it", async () =>
        {
            foreach (bool stopOnIssues in new[] { false, true })
            {
                var client = new Client { IssueOnTurn = 1 };
                var source = await Workflow(client, (_, _) => Answer(""))
                    .Add(new("Implementation", "Run implementation") { StopOnIssues = stopOnIssues }).RunAsync();
                Assert(source.Succeeded == !stopOnIssues);
                var discussion = await Workflow(client, (_, _) => Answer(""))
                    .Add(new AppServerDiscussionStep("Discuss", "Explain the final result") { SourceResult = source }).RunAsync();
                string prompt = client.Calls.Last().Prompt;
                Assert(discussion.Succeeded && discussion.Steps.Count == 1 && source.Steps.Count == 1);
                Assert(prompt.Contains(stopOnIssues ? "\"Succeeded\": false" : "\"Succeeded\": true"));
                Assert(prompt.Contains("Implementation") && prompt.Contains("Unresolved issue"));
                Assert(!stopOnIssues || prompt.Contains(source.StopReason!));
                Assert(source.Succeeded == !stopOnIssues && client.Calls.Count == 2);
                Assert(client.Calls[0].Thread != client.Calls[1].Thread);
            }
        });
        await Check("a separate discussion includes the original execution error and stop reason", async () =>
        {
            var client = new Client { FailOnTurn = 1 };
            var source = await Workflow(client, (_, _) => Answer(""))
                .Add(new("Implementation", "Run implementation")).RunAsync();
            Assert(!source.Succeeded && source.Error is not null);
            var discussion = await Workflow(client, (_, _) => Answer(""))
                .Add(new AppServerDiscussionStep("Discuss", "Explain failure") { SourceResult = source }).RunAsync();
            string prompt = client.Calls.Last().Prompt;
            var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
            Assert(discussion.Succeeded && prompt.Contains(JsonSerializer.Serialize(source.Error!.Message, jsonOptions)) &&
                prompt.Contains(JsonSerializer.Serialize(source.StopReason, jsonOptions)));
            Assert(source.FailedStep == "Implementation" && source.Steps.Count == 1);
        });
        await Check("a blank question ends normally without another AI turn", async () =>
        {
            var client = new Client();
            var result = await Workflow(client, (_, _) => Answer("   "))
                .Add(new AppServerDiscussionStep("Discuss", "Explain results")).RunAsync();
            Assert(result.Succeeded && client.Calls.Count == 1);
        });
        await Check("AI clarification stays distinct from the user's next question", async () =>
        {
            var client = new Client { ClarifyOnTurn = 2 };
            var inputs = new Queue<string>(["Explain more", "The missing detail", ""]);
            var kinds = new List<bool>();
            var result = await Workflow(client, (request, _) =>
            {
                kinds.Add(request.IsDiscussion);
                return Answer(inputs.Dequeue());
            }).Add(new AppServerDiscussionStep("Discuss", "Explain results")).RunAsync();
            Assert(result.Succeeded && kinds.SequenceEqual([true, false, true]) && client.Calls.Count == 3);
            Assert(client.Calls.Select(call => call.Thread).Distinct().Count() == 1);
            Assert(client.Calls.Last().Prompt.Contains("The missing detail"));
        });
        await Check("waiting for the next question pauses the execution budget", async () =>
        {
            var client = new Client();
            var result = await Workflow(client, async (_, token) =>
            {
                await Task.Delay(600, token);
                return new[] { "" };
            }).Add(new AppServerDiscussionStep("Discuss", "Explain results")
            {
                ExecutionTimeout = TimeSpan.FromMilliseconds(400)
            }).RunAsync();
            Assert(result.Succeeded && result.Steps[0].Duration >= TimeSpan.FromMilliseconds(550));
        });
        await Check("input timeout and input round limits still stop discussion", async () =>
        {
            var never = new TaskCompletionSource<IReadOnlyList<string>>();
            var timedOut = await Workflow(new Client(), (_, _) => never.Task)
                .Add(new AppServerDiscussionStep("Discuss", "Explain results")
                {
                    InputTimeout = TimeSpan.FromMilliseconds(50)
                }).RunAsync();
            Assert(!timedOut.Succeeded && timedOut.Error is TimeoutException);
            int inputs = 0;
            var limited = await Workflow(new Client(), (_, _) => { inputs++; return Answer("Continue"); })
                .Add(new AppServerDiscussionStep("Discuss", "Explain results") { MaxInputRounds = 1 }).RunAsync();
            Assert(!limited.Succeeded && limited.Error is InvalidOperationException && inputs == 1);
        });
        await Check("external cancellation during discussion remains cancellation", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var workflow = Workflow(new Client(), (_, _) =>
            {
                cancellation.Cancel();
                return Answer("");
            }).Add(new AppServerDiscussionStep("Discuss", "Explain results"));
            try
            {
                await workflow.RunAsync(cancellation.Token);
                throw new Exception("Expected cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        });
        await Check("StopOnIssues and failed follow-up turns retain existing stopping behavior", async () =>
        {
            int inputs = 0;
            var stopped = await Workflow(new Client { IssueOnTurn = 1 }, (_, _) => { inputs++; return Answer(""); })
                .Add(new AppServerDiscussionStep("Discuss", "Explain results") { StopOnIssues = true })
                .Add(new("After", "Must not run")).RunAsync();
            Assert(!stopped.Succeeded && stopped.Steps.Count == 1 && inputs == 0 && stopped.Steps[0].HasIssues);
            var failed = await Workflow(new Client { FailOnTurn = 2 }, (_, _) => Answer("Next question"))
                .Add(new AppServerDiscussionStep("Discuss", "Explain results")).RunAsync();
            Assert(!failed.Succeeded && failed.Error is InvalidOperationException);
        });
        await Check("console discussion accepts Enter and EOF while normal questions still reject blanks", async () =>
        {
            TextReader originalInput = Console.In;
            TextWriter originalOutput = Console.Out;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                var handler = new ConsoleWorkflowInputHandler { EnableNotifications = false };
                var request = new AppServerWorkflowInputRequest("Discuss", "thread", "turn", "Full answer", ["Next question"])
                {
                    IsDiscussion = true
                };
                Console.SetIn(new StringReader("\n"));
                Assert((await handler.AnswerAsync(request))[0] == "");
                Console.SetIn(new StringReader(""));
                Assert((await handler.AnswerAsync(request))[0] == "");
                Console.SetIn(new StringReader("\nActual answer\n"));
                Assert((await handler.AnswerAsync(request with { IsDiscussion = false }))[0] == "Actual answer");
                Assert(output.ToString().Contains("Full answer"));
            }
            finally
            {
                Console.SetIn(originalInput);
                Console.SetOut(originalOutput);
            }
        });
        return passed;

        /// <summary>
        /// 建立不呼叫實際 AI 的測試流程。
        /// </summary>
        AppServerWorkflow Workflow(Client client, Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> handler)
            => new(client, workspace, handler);

        /// <summary>
        /// 執行並統計測試案例。
        /// </summary>
        async Task Check(string name, Func<Task> run)
        {
            await run();
            passed++;
            Console.WriteLine($"PASS: discussion {name}");
        }
    }

    /// <summary>
    /// 建立輸入回呼的單筆答案。
    /// </summary>
    private static Task<IReadOnlyList<string>> Answer(string answer)
        => Task.FromResult<IReadOnlyList<string>>([answer]);

    /// <summary>
    /// 條件不成立時中止測試。
    /// </summary>
    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Discussion assertion failed.");
        }
    }

    private sealed class Client : IAppServerClient
    {
        private int _threads;
        public int ClarifyOnTurn { get; init; }
        public int IssueOnTurn { get; init; }
        public int FailOnTurn { get; init; }
        public List<(string Thread, string Prompt, AppServerOptions Options)> Calls { get; } = [];

        /// <summary>
        /// 建立測試 thread。
        /// </summary>
        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult($"thread-{++_threads}");

        /// <summary>
        /// 記錄指令與選項，回傳可控制的模擬結果。
        /// </summary>
        public Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
            AppServerOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((threadId, prompt, options!));
            int turn = Calls.Count;
            bool clarify = turn == ClarifyOnTurn;
            return Task.FromResult(new TurnResult(threadId, turn.ToString(), turn == FailOnTurn ? "failed" : "completed",
                JsonSerializer.Serialize(new
                {
                    state = clarify ? "needs_input" : "completed",
                    message = $"Answer {turn}",
                    questions = clarify ? new[] { "Clarify detail" } : [],
                    issues = turn == IssueOnTurn ? new[] { "Unresolved issue" } : [],
                    data = (object?)null
                })));
        }
    }
}
