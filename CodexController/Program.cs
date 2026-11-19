using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.AppServer.Workflows.ConsoleUI;
using CodexClient.Exec;
using CodexClient.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;


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
    // var runner = new CodexExecRunner(logger);

    // Session 取得
    //var store = new SessionStore(applicationId);
    //string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
    //string sessionName = "order-service-analysis";sd
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

    //string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
    //await using var client = new CodexAppServerClient(logger: loggerFactory.CreateLogger<CodexAppServerClient>());
    //await client.StartAsync(applicationId);

    //var handler = new ConsoleWorkflowInputHandler();
    //var reporter = new ConsoleWorkflowProgressReporter();
    //AppServerWorkflow workflow = new AppServerWorkflow(client, workspaceRoot, handler.AnswerAsync, reporter);

    //var analysisStep = new AppServerWorkflowStep("Analysis", "分析 VerificationTool.cs 的邏輯")
    //{
    //    Thread = AppServerThread.Create("Analysis"),
    //    Options = new AppServerOptions
    //    {
    //        Sandbox = CodexSandboxMode.WorkspaceWrite,
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


    string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
    await using var client = new CodexAppServerClient(logger: loggerFactory.CreateLogger<CodexAppServerClient>());
    await client.StartAsync(applicationId);

    // 需求決策測試：原始 Spec 不變，由使用者確認的決策補充驗收依據。
    var handler = new ConsoleWorkflowInputHandler();
    var reporter = new ConsoleWorkflowProgressReporter();
    var writeOptions = new AppServerOptions
    {
        Sandbox = CodexSandboxMode.WorkspaceWrite,
        Model = CodexModels.Gpt61Sol
    };
    var workflow = new AppServerWorkflow(client, workspaceRoot, handler.AnswerAsync, reporter);
   

    Console.WriteLine($"測試工作區：{workspaceRoot}");
    Console.WriteLine("本次將建立或覆寫 DecisionDemo.Spec.md、DecisionDemo.Result.txt、DecisionDemo.Acceptance.md。");
    Console.WriteLine("原始 Spec 要求 ERROR。建議在實作提問時回答 EMPTY，再選 2 確認為需求變更。");
    Console.WriteLine("也可以重跑時選 1 不記錄，觀察驗收是否指出與原始 Spec 不一致。");

    workflow
        .Add(new AppServerWorkflowStep(
            "Spec",
            """
            這是獨立的需求決策測試。只建立或覆寫 DecisionDemo.Spec.md，內容如下：
            # 需求決策測試 Spec
            - NOT_FOUND_RESULT：找不到資料時，DecisionDemo.Result.txt 必須只有一行 ERROR。

            不修改其他檔案，不詢問使用者，不以舊的測試結果變更以上規格。
            """)
        {
            Options = writeOptions
        })
        .Add(new AppServerWorkflowStep(
            "Implement",
            """
            讀取 DecisionDemo.Spec.md。這是示範實作期間需求問答的測試，不需要產生程式碼。
            首次執行必須先用 state=needs_input 提問：
            「原始 Spec 要求找不到資料時輸出 ERROR。你希望保留 ERROR，還是改為 EMPTY？請回答 ERROR 或 EMPTY。」
            不要在使用者回答前寫入結果，也不要自行推測答案或呼叫 request_user_input。

            取得回答後，只將使用者選擇的 ERROR 或 EMPTY 寫入 DecisionDemo.Result.txt，一行一個值。
            若回答無法判斷，繼續詢問。不修改原始 Spec，也不自行將回答寫成已核准的需求文件。
            使用者是否確認為需求決策由 Workflow 處理，不需要你另外詢問。

            若後續收到驗收失敗回饋，依該次驗收的規格與已確認決策修正結果，不重新執行首次提問。
            只處理本範例的結果檔案，不修改其他檔案。
            """)
        {
            Options = writeOptions,
            RecordDecisionsTo = "DecisionDemo",
            ReadDecisionsFrom = ["DecisionDemo"]
        })
        .Add(new AppServerAcceptanceStep(
            "Acceptance",
            """
            讀取 DecisionDemo.Spec.md 與 DecisionDemo.Result.txt，驗收 NOT_FOUND_RESULT。
            驗收依據為原始 Spec，加上本輪 Prompt 附加的使用者已確認需求決策。
            已確認決策若將 ERROR 改為 EMPTY，就依 EMPTY 驗收；沒有該決策時仍依原始 Spec 的 ERROR 驗收。
            不可從結果檔案或實作者的說明反推規格，也不可自行修改 Spec 或結果檔案。

            不符合有效需求時，該 Check 回報 Failed，問題列為 Error；符合則回報 Passed。
            證據須包含檔案實際內容。建立或覆寫 DecisionDemo.Acceptance.md，列出原規格、
            收到的已確認決策、實際結果與判定，並將該路徑放入驗收結果的 reportPath。
            """)
        {
            Options = writeOptions,
            FailureSeverity = FindingSeverity.Error,
            MaxInconclusiveRetries = 2,
            MaxFailures = 3,
            RetryStep = "Implement",
            RequiredCriterionIds = ["NOT_FOUND_RESULT"],
            ReadDecisionsFrom = ["DecisionDemo"]
        })
        .Add(new AppServerWorkflowStep(
            "Style",
            "只整理 DecisionDemo.Result.txt 的文字格式：移除多餘空白行，保留單行原始值與一個行尾換行。不得變更 ERROR／EMPTY 值，不修改 Spec 或驗收報表。")
        {
            Options = writeOptions
        });

    var result = await workflow.RunAsync();



    var resultReporter = new ConsoleWorkflowResultReporter();
    resultReporter.Report(result);

    Console.WriteLine($"已確認需求決策：{result.Decisions.Count} 筆");
    foreach (var decision in result.Decisions)
    {
        Console.WriteLine($"[{decision.Group}] 來源：{decision.StepName}，確認時間：{decision.ConfirmedAt.ToLocalTime():HH:mm:ss}");
        Console.WriteLine($"問題：{decision.Question}");
        Console.WriteLine($"回答：{decision.Answer}");
    }
    //Console.WriteLine($"原始規格：{Path.Combine(workspaceRoot, "DecisionDemo.Spec.md")}");
    //Console.WriteLine($"實作結果：{Path.Combine(workspaceRoot, "DecisionDemo.Result.txt")}");
    //Console.WriteLine($"驗收報表：{Path.Combine(workspaceRoot, "DecisionDemo.Acceptance.md")}");

}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
}
stopwatch.Stop();
Console.WriteLine("Spend time : " + stopwatch.ElapsedMilliseconds);
Console.Read();



