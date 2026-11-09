using CodexClient.Exec;
using CodexClient.Sessions;
using CodexController.Workflow;
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
    var runner = new CodexRunner(logger);

    //// Session 取得
    //var store = new SessionStore(applicationId);
    string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
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




    var workflow = new CodexWorkflow(runner);

    WorkflowResult result = await workflow.ExecuteAsync(workspaceRoot, "VerificationTool");

    Console.WriteLine("===== Analysis =====");
    Console.WriteLine(result.Analysis.Summary);

    Console.WriteLine();
    Console.WriteLine("===== Review =====");
    Console.WriteLine(result.Review.Summary);

    Console.WriteLine();
    Console.WriteLine("Workflow completed.");



}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
}
stopwatch.Stop();
Console.WriteLine("Spend time : " + stopwatch.ElapsedMilliseconds);
Console.Read();



