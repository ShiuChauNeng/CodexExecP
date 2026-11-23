using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.AppServer.Workflows.ConsoleUI;
using CodexClient.Models;

internal static class WorkflowPlanChecks
{
    /// <summary>
    /// 驗證執行前摘要的設定內容、快照與無副作用行為。
    /// </summary>
    internal static int Run(string workspace)
    {
        var workflow = new AppServerWorkflow(new Client(), workspace, (_, _) => throw new Exception("Unexpected question."));
        var snapshot = workflow.Steps;
        string output = Render(workflow);
        Assert(output.Contains("流程摘要：0 個步驟") && output.Contains($"工作區：{Path.GetFullPath(workspace)}"));
        Console.WriteLine("PASS: empty plan reports its workspace without starting a task");

        workflow
            .Add(new AppServerWorkflowStep("Spec", _ => throw new Exception("Prompt factory must not run.")))
            .Add(new AppServerWorkflowStep("Implement", "implement")
            {
                Options = new()
                {
                    Model = "test-model", Sandbox = CodexSandboxMode.WorkspaceWrite,
                    ReasoningEffort = CodexReasoningEffort.High, NetworkAccess = true, ServiceTier = CodexServiceTier.Fast
                },
                Thread = AppServerThread.GetOrCreate("shared"),
                RecordDecisionsTo = "FeatureA", ReadDecisionsFrom = ["FeatureA", "FeatureB"],
                NextStep = _ => throw new Exception("Routing callback must not run."),
                StopOnIssues = true
            })
            .Add(new AppServerAcceptanceStep("Acceptance", "review")
            {
                Options = new() { Sandbox = CodexSandboxMode.WorkspaceWrite },
                FailureSeverity = FindingSeverity.Warning, MaxFailures = 4, MaxInconclusiveRetries = 1,
                RetryStep = "Implement", RequiredCriterionIds = ["SPEC-001", "SPEC-002"],
                ReadDecisionsFrom = ["FeatureA"], Thread = AppServerThread.Use("shared")
            });
        output = Render(workflow);
        Assert(snapshot.Count == 0 && workflow.Steps.Count == 3);
        Assert(output.Contains("01  Spec | Model：沿用 Codex 設定 | 權限：ReadOnly"));
        Assert(output.Contains("02  Implement | Model：test-model | 權限：WorkspaceWrite"));
        Assert(output.Contains("推理：High | 網路：允許 | issues：停止"));
        Assert(output.Contains("速度：Fast") && output.Contains("速度：沿用 thread 設定"));
        Assert(output.Contains("推理：沿用 thread 設定") && output.Contains("網路：禁止（預設）"));
        Assert(output.Contains("決策：記錄至 FeatureA | 讀取 FeatureA、FeatureB"));
        Assert(output.Contains("Thread：GetOrCreate（shared）") && output.Contains("Thread：Use（shared）"));
        Assert(output.Contains("分流：執行後動態決定") && !output.Contains('\u001b'));
        Console.WriteLine("PASS: plan shows settings and dynamic routing without invoking callbacks or mutating snapshots");

        Assert(output.Contains("驗收：Warning 以上視為失敗 | 失敗上限 4 次 | 額外補查 1 次 | 返回 Implement"));
        Assert(output.Contains("必要項目：SPEC-001、SPEC-002"));
        workflow.Add(new AppServerWorkflowStep("CodingStyle", "style")
        {
            Options = new() { Sandbox = CodexSandboxMode.WorkspaceWrite, NetworkAccess = false },
            Thread = AppServerThread.New
        });
        output = Render(workflow);
        Assert(output.Contains("04  CodingStyle") && output.Contains("網路：禁止 | issues：允許繼續"));
        Assert(output.Contains("Thread：New"));
        Console.WriteLine("PASS: plan retains per-acceptance limits and distinguishes explicit network denial");
        return 3;
    }

    /// <summary>
    /// 擷取摘要的純文字輸出，結束後還原主控台輸出來源。
    /// </summary>
    private static string Render(AppServerWorkflow workflow)
    {
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            new ConsoleWorkflowPlanReporter().Report(workflow);
            return output.ToString();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    /// <summary>
    /// 確認摘要內容符合預期，不符時使案例失敗。
    /// </summary>
    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Workflow plan assertion failed.");
        }
    }

    private sealed class Client : IAppServerClient
    {
        /// <summary>
        /// 摘要不應建立 thread；若被呼叫就使測試失敗。
        /// </summary>
        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => throw new Exception("Plan reporter must not create a thread.");

        /// <summary>
        /// 摘要不應執行 AI；若被呼叫就使測試失敗。
        /// </summary>
        public Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
            AppServerOptions? options = null, CancellationToken cancellationToken = default)
            => throw new Exception("Plan reporter must not execute a turn.");
    }
}
