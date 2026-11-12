using CodexClient.AppServer;
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

    client.UserInputRequested = (request, cancellationToken) =>
    {
        var answers = new Dictionary<string, string[]>();

        foreach (var question in request.Questions)
        {
            Console.WriteLine();
            Console.WriteLine(question.Question);

            if (question.Options is not null)
            {
                for (int i = 0; i < question.Options.Count; i++)
                {
                    Console.WriteLine($"{i + 1}. {question.Options[i].Label}");
                }
            }

            Console.Write("請輸入答案：");

            string answer = Console.ReadLine() ?? throw new OperationCanceledException("使用者未提供答案。");
            answers.Add(question.Id, [answer]);
        }

        return Task.FromResult<IReadOnlyDictionary<string, string[]>>(answers);
    };

    await client.StartAsync("MiniCodingAgent");

    string threadId = await client.StartThreadAsync(workspaceRoot);

    TurnResult result = await client.RunTurnAsync(
        threadId,
        """
    我需要你實作一個訂單取消功能。

    但有一項尚未決定的業務規則：
    訂單取消後，是否允許重新送出？

    這項決策必須由使用者決定。
    在得到答案前，不要自行選擇業務規則，
    也不要修改任何程式碼。

    如果 request_user_input 工具可用，
    請使用該工具向使用者詢問。
    """);

    JsonElement features =    await client.GetExperimentalFeaturesAsync();
    Console.WriteLine(features.GetRawText());

    Console.WriteLine($"Status: {result.Status}");
    Console.WriteLine(result.Response);












}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
}
stopwatch.Stop();
Console.WriteLine("Spend time : " + stopwatch.ElapsedMilliseconds);
Console.Read();



