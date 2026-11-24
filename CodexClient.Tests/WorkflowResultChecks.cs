using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.AppServer.Workflows.ConsoleUI;

internal static class WorkflowResultChecks
{
    /// <summary>
    /// 驗證最終報告顯示停止原因與最新問題，並保留步驟與需求決策輸出。
    /// </summary>
    internal static async Task<int> RunAsync(string workspace)
    {
        var stopped = await new AppServerWorkflow(new Client(), workspace, (_, _) => throw new Exception("Unexpected question."))
            .Add(new AppServerAcceptanceStep("Acceptance", "review") { RetryStep = "Acceptance" })
            .RunAsync();
        Assert(!stopped.Succeeded && stopped.Error is null && stopped.StopReason is not null);
        string output = Render(stopped);
        Assert(output.Contains("停止步驟：Acceptance") && output.Contains($"停止原因：{stopped.StopReason}"));
        Assert(output.Contains("[無法判定]") && output.Contains("累計無法判定 3 次") && output.Contains("已確認需求決策：0 筆"));
        Assert(!output.Contains('\u001b'));
        Console.WriteLine("PASS: final report retains a stop reason even without an exception");

        var successful = new AppServerWorkflowResult(true, null, [], new Dictionary<string, string>());
        output = Render(successful);
        Assert(output.Contains("執行結果：成功") && !output.Contains("停止步驟：") && !output.Contains("停止原因："));
        Assert(!output.Contains("未解決問題："));
        Console.WriteLine("PASS: successful reports omit stopping details");

        output = Render(stopped with { StopReason = null, Error = new IOException("test execution error") });
        Assert(output.Contains("停止原因：test execution error"));
        output = Render(stopped with { StopReason = null });
        Assert(output.Contains("停止原因：未提供停止原因。"));
        Console.WriteLine("PASS: manually constructed results fall back to the exception or an explicit missing-reason message");

        output = Render(successful with
        {
            Steps = [Step("Implement", "old issue"), Step("Acceptance", "acceptance issue"),
                Step("Implement", "current issue", "another issue"), Step("implement", "separate step issue")]
        });
        Assert(output.Contains("執行結果：成功") && output.Contains("未解決問題："));
        Assert(!output.Contains("old issue") && output.Contains("[Implement] current issue") && output.Contains("[Implement] another issue"));
        Assert(output.Contains("[Acceptance] acceptance issue") && output.Contains("[implement] separate step issue"));
        Assert(output.IndexOf("未解決問題：", StringComparison.Ordinal) < output.IndexOf("已確認需求決策：", StringComparison.Ordinal));
        Assert(!output.Contains('\u001b'));
        Console.WriteLine("PASS: successful reports list only the latest issues per case-sensitive step name");

        output = Render(successful with { Steps = [Step("Implement", "resolved issue"), Step("Implement")] });
        Assert(!output.Contains("resolved issue") && !output.Contains("未解決問題："));
        Console.WriteLine("PASS: a later issue-free execution removes earlier issues from the final summary");

        var validationFailure = Step("Implement", "retained issue") with { Error = new IOException("validation failed") };
        output = Render(stopped with { Steps = [validationFailure] });
        Assert(output.Contains("[Implement] retained issue"));
        output = Render(stopped with
        {
            Steps = [Step("Implement", "outdated issue"), new("Implement", "thread", null, new IOException("turn failed"))]
        });
        Assert(!output.Contains("outdated issue") && !output.Contains("未解決問題："));
        Console.WriteLine("PASS: failed reports retain available latest issues without reviving an earlier execution's issues");

        var revision = await new AppServerWorkflow(new RevisionClient(), workspace, (_, _) => throw new Exception("Unexpected question."))
            .Add(new AppServerWorkflowStep("Implement", "implement"))
            .Add(new AppServerAcceptanceStep("Acceptance", "review") { RetryStep = "Implement" })
            .RunAsync();
        output = Render(revision);
        Assert(revision.Succeeded && revision.Steps.Count == 6 && revision.Steps.All(step => !step.HasIssues));
        Assert(output.Contains("需修正問題：[Error] First revision finding"));
        Assert(output.Contains("需修正問題：[SPEC-002] 驗收未通過；Second revision evidence"));
        Assert(!output.Contains("Duplicate check evidence") && !output.Contains("未解決問題："));
        Assert(output.IndexOf("需修正問題：[Error]", StringComparison.Ordinal) < output.IndexOf("03  Implement", StringComparison.Ordinal));
        Assert(output.IndexOf("需修正問題：[SPEC-002]", StringComparison.Ordinal) < output.IndexOf("05  Implement", StringComparison.Ordinal));
        Assert(!output.Contains('\u001b'));
        Console.WriteLine("PASS: revision findings and failed checks remain under their original assessment after final acceptance passes");
        double[] seconds = [0, 65, 59, 3601, 120, 5];
        output = Render(revision with
        {
            Steps = revision.Steps.Select((step, index) => step with { Duration = TimeSpan.FromSeconds(seconds[index]) }).ToArray()
        });
        var stepLines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 1 && char.IsDigit(line[0]) && char.IsDigit(line[1])).ToArray();
        Assert(stepLines.Length == 6);
        Assert(stepLines[0].Contains("[通過]") && stepLines[0].Contains("耗時0分0秒"));
        Assert(stepLines[1].Contains("[需修正]") && stepLines[1].Contains("耗時1分5秒"));
        Assert(stepLines[2].Contains("耗時0分59秒") && stepLines[3].Contains("耗時60分1秒"));
        Assert(stepLines[4].Contains("耗時2分0秒") && stepLines[5].Contains("耗時0分5秒"));
        output = Render(stopped with
        {
            Steps = [new AppServerWorkflowStepResult("Failure", "thread", null, new IOException("failed"))
                { Duration = TimeSpan.FromSeconds(75) }]
        });
        Assert(output.Contains("[執行失敗] | 耗時1分15秒"));
        Console.WriteLine("PASS: final timeline shows each execution's duration for successful, failed and repeated steps");
        return 8;
    }

    /// <summary>
    /// 建立附帶指定 issues 的步驟結果，供報告歷程彙整測試使用。
    /// </summary>
    private static AppServerWorkflowStepResult Step(string name, params string[] issues)
        => new(name, "thread", new TaskRunResult("thread", "turn", "Done") { Issues = issues }, null);

    /// <summary>
    /// 擷取結果 Reporter 的純文字輸出，結束後還原原本的輸出來源。
    /// </summary>
    private static string Render(AppServerWorkflowResult result)
    {
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            new ConsoleWorkflowResultReporter().Report(result);
            return output.ToString();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    /// <summary>
    /// 確認報告內容符合預期，不符時使案例失敗。
    /// </summary>
    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Workflow result report assertion failed.");
        }
    }

    private sealed class Client : IAppServerClient
    {
        /// <summary>
        /// 回傳模擬 thread，不啟動實際 App Server。
        /// </summary>
        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult("report-thread");

        /// <summary>
        /// 回傳無法完成必要項目檢查的驗收資料，用於驗證補查上限與最終報告。
        /// </summary>
        public Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
            AppServerOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new TurnResult(threadId, "turn", "completed", JsonSerializer.Serialize(new
            {
                state = "completed", message = "Unable to evaluate", questions = Array.Empty<string>(), issues = Array.Empty<string>(),
                data = new
                {
                    checks = new[] { new { criterionId = "SPEC-001", status = "NotEvaluated", evidence = "Test environment unavailable" } },
                    findings = Array.Empty<object>(), reportPath = "report.md"
                }
            })));
    }

    private sealed class RevisionClient : IAppServerClient
    {
        private int _reviews;

        /// <summary>
        /// 建立模擬 thread，供驗收歷程的報告測試使用。
        /// </summary>
        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult("revision-thread");

        /// <summary>
        /// 模擬前兩輪需修正、第三輪通過；問題僅放在驗收資料，外層 issues 保持空白。
        /// </summary>
        public Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
            AppServerOptions? options = null, CancellationToken cancellationToken = default)
        {
            object? data = null;
            if (outputSchema!.Value.GetProperty("properties").GetProperty("data").TryGetProperty("anyOf", out _))
            {
                _reviews++;
                data = _reviews switch
                {
                    1 => new AcceptanceResult([new("SPEC-001", AcceptanceCheckStatus.Failed, "Duplicate check evidence")],
                        [new("F-001", FindingSeverity.Error, "SPEC-001", "First revision finding", "First revision evidence", null)], "report.md"),
                    2 => new AcceptanceResult([new("SPEC-001", AcceptanceCheckStatus.Passed, "Passed evidence"),
                        new("SPEC-002", AcceptanceCheckStatus.Failed, "Second revision evidence")], [], "report.md"),
                    _ => new AcceptanceResult([new("SPEC-001", AcceptanceCheckStatus.Passed, "Passed evidence"),
                        new("SPEC-002", AcceptanceCheckStatus.Passed, "Passed evidence")], [], "report.md")
                };
            }
            return Task.FromResult(new TurnResult(threadId, "turn", "completed", JsonSerializer.Serialize(new
            {
                state = "completed", message = "Done", questions = Array.Empty<string>(), issues = Array.Empty<string>(), data
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
    }
}
