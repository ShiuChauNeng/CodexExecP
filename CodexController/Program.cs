using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.Exec;
using CodexClient.Sessions;
using CodexClient.Workflows;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;


Stopwatch stopwatch = new Stopwatch();
stopwatch.Start();
try
{
    var applicationId = "CodexController";
    // 設定 Log
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Error);
        builder.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
        });
    });
    ILogger logger = loggerFactory.CreateLogger(applicationId);

    // 啟動 Runner
    var runner = new CodexExecRunner(logger);

    // Session 取得
    //var store = new SessionStore(applicationId);
    //string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
    //string sessionName = "order-service-analysis";
    //var lookupResult = await store.FindAsync(sessionName, workspaceRoot);
    //SessionInfo? session = null;
    ////if (lookupResult.Status == SessionLookupStatus.Matched)
    ////{
    ////    session = lookupResult.Session;
    ////}

    //// 設定 Option
    //var options = new ExecutionOptions
    //{
    //    SessionId = session?.SessionId,
    //    Sandbox = CodexSandboxMode.WorkspaceWrite
    //};

    //string prompt = session is null ? "理解VerificationTool.cs 後告訴我這個工具能做什麼?" : "我最喜歡的程式語言是什麼?";
    //ExecutionResult result = await runner.ExecuteAsync(workspaceRoot, prompt, options);
    //if (!result.Completed || string.IsNullOrWhiteSpace(result.SessionId))
    //{
    //    throw new InvalidOperationException("Codex execution did not complete successfully.");
    //}

    //await store.SaveAsync(new SessionInfo(sessionName, result.SessionId, Path.GetFullPath(workspaceRoot), DateTimeOffset.UtcNow));

    //Console.WriteLine($"Session: {result.SessionId}");
    //Console.WriteLine(result.FinalResponse);
    //Console.WriteLine($"Outcome: {result.Outcome}");
    //Console.WriteLine($"Completed: {result.Completed}");
    //Console.WriteLine($"HasIssues: {result.HasIssues}");



    // Workflow
    //var workflow = new CodexWorkflow(runner);
    //string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
    //WorkflowResult result = await workflow.ExecuteAsync(workspaceRoot, "VerificationTool");

    //Console.WriteLine("===== Analysis =====");
    //Console.WriteLine(result.Analysis.Summary);

    //Console.WriteLine();
    //Console.WriteLine("===== Review =====");
    //Console.WriteLine(result.Review.Summary);

    //Console.WriteLine();
    //Console.WriteLine("Workflow completed.");

    // Workflows
    //string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
    //var workflow = new CodexWorkflow(runner, loggerFactory.CreateLogger<CodexWorkflow>(), workspaceRoot);


    //var analysisStep = new CodexWorkflowStep("Analysis", "分析 VerificationTool.cs 的邏輯")
    //{
    //    Session = Session.Create("analysis"),
    //};
    //var TestStep = new CodexWorkflowStep("Test", "9*9=??");
    //var verifyStep = new CodexWorkflowStep("Verify", "告訴我分析的結果")
    //{
    //    Session = Session.Use("analysis"),
    //};

    //workflow.Add(analysisStep)
    //        .Add(TestStep)
    //        .Add(verifyStep);

    //WorkflowResult result = await workflow.RunAsync();

    //Console.WriteLine($"Workflow Succeeded: {result.Succeeded}");

    //foreach (WorkflowStepResult step in result.Steps)
    //{
    //    Console.WriteLine($"Step: {step.Name}");
    //    Console.WriteLine($"Outcome: {step.Execution.Outcome}");
    //    Console.WriteLine($"SessionId: {step.Execution.SessionId}");
    //    Console.WriteLine($"Response: {step.Execution.FinalResponse}");
    //}

    string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
    await using var client = new CodexAppServerClient();
    await client.StartAsync(workspaceRoot);

    //var handler = new ConsoleWorkflowInputHandler();
    //var reporter = new ConsoleWorkflowProgressReporter();
    //AppServerWorkflow workflow = new AppServerWorkflow(client, workspaceRoot, handler.AnswerAsync, reporter);

    //var analysisStep = new AppServerWorkflowStep("Analysis", "分析 VerificationTool.cs 的邏輯")
    //{
    //    Thread = AppServerThread.Create("Analysis"),
    //    Options = new AppServerOptions
    //    {
    //        Sandbox = AppServerSandboxMode.WorkspaceWrite,
    //        WorkspaceRoot = workspaceRoot
    //    }
    //};
    //var TestStep = new AppServerWorkflowStep("Test", "9*9=??");
    //var verifyStep = new AppServerWorkflowStep("Verify", "告訴我分析的結果")
    //{
    //    Thread = AppServerThread.Use("Analysis")
    //};

    //workflow.Add(analysisStep).Add(TestStep).Add(verifyStep);

    //workflow.Add(new AppServerAcceptanceStep("Acceptance", "使用驗收 Skill")
    //{
    //    FailureSeverity = FindingSeverity.Error,
    //    MaxFailures = 3,
    //    RetryStep = "Implement"
    //});

    //var result = await workflow.RunAsync();
    //Console.WriteLine($"Result : {result}");
    //foreach (var step in result.Steps)
    //{
    //    Console.WriteLine($"Step: {step.Name}");
    //    Console.WriteLine($"Error: {step.Error}");
    //}

    // Test
    var handler = new ConsoleWorkflowInputHandler();
    var reporter = new ConsoleWorkflowProgressReporter();

    var writeOptions = new AppServerOptions
    {
        Sandbox = AppServerSandboxMode.WorkspaceWrite
    };

    var workflow = new AppServerWorkflow(
        client,
        workspaceRoot,
        handler.AnswerAsync,
        reporter);

    workflow
        .Add(new AppServerWorkflowStep(
            "Implement",
            "首次執行時，於 GOGO.txt 寫入 1+1=3，作為驗收測試資料。" +
"若收到前次驗收失敗的問題與證據，請修正 GOGO.txt 中的算術錯誤。")
        {
            Options = writeOptions
        })
        .Add(new AppServerAcceptanceStep(
            "Acceptance",
            "讀取 GOGO.txt，檢查其中算術是否正確。" +
"算術錯誤列為 Error；正確則回報 Passed。" +
"每個檢查項目都要提供檔案內容作為證據。" +
"驗收正常完成時 state=completed，驗收資料放在 data，" +
"並依指示產生驗收報表。")
        {
            Options = writeOptions,
            FailureSeverity = FindingSeverity.Error,
            MaxFailures = 3,
            RetryStep = "Implement"
        })
        .Add(new AppServerWorkflowStep(
            "Style",
            "將 GOGO.txt 內容複製兩次")
        {
            Options = writeOptions
        });

    var result = await workflow.RunAsync();

    Console.WriteLine($"流程成功：{result.Succeeded}");

    foreach (var step in result.Steps)
    {
        Console.WriteLine(
            $"{step.Name}：" +
            $"通過={step.Accepted}，" +
            $"驗收結果={step.AcceptanceDecision}，" +
            $"累計失敗={step.FailureCount}");
    }


}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
}
stopwatch.Stop();
Console.WriteLine("Spend time : " + stopwatch.ElapsedMilliseconds);
Console.Read();



