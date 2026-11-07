using CodexClient.Exec;
using CodexClient.Sessions;
using CodexController.Workflow;
using Microsoft.Extensions.Logging;
using System.Diagnostics;


Stopwatch stopwatch = new Stopwatch();
stopwatch.Start();
try
{
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Error);
        builder.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
        });
    });
    ILogger logger = loggerFactory.CreateLogger("CodexController");


    var runner = new CodexRunner(logger);
    //var workflow = new CodexWorkflow(runner);

    string workspaceRoot = @"C:\Users\User\Desktop\TestProj";

    var store = new SessionStore();
    string sessionName = "order-service-analysis";
    SessionInfo? session = await store.LoadAsync(sessionName);

    // Session 固定對應原本的 Workspace。
    // 避免同名 Session 意外在另一個專案中使用。
    if (session is not null && !Path.GetFullPath(session.WorkspaceRoot).Equals(Path.GetFullPath(workspaceRoot), StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Session workspace does not match.");
    }

    var options = new ExecutionOptions
    {
        SessionId = session?.SessionId,
        Sandbox = CodexSandboxMode.ReadOnly
    };

    string prompt = session is null
        ? "請分析 STAR 分析的主要邏輯。"
        : "請繼續剛才的分析，找出可能存在的問題。";

    ExecutionResult result = await runner.ExecuteAsync(workspaceRoot, prompt, options);

    if (!result.Completed || string.IsNullOrWhiteSpace(result.SessionId))
    {
        throw new InvalidOperationException("Codex execution did not complete successfully.");
    }

    await store.SaveAsync(new SessionInfo(sessionName, result.SessionId, Path.GetFullPath(workspaceRoot), DateTimeOffset.UtcNow));

    Console.WriteLine($"Session: {result.SessionId}");
    Console.WriteLine(result.FinalResponse);
    Console.WriteLine($"Outcome: {result.Outcome}");
    Console.WriteLine($"Completed: {result.Completed}");
    Console.WriteLine($"HasIssues: {result.HasIssues}");




    //var firstResult = await runner.ExecuteAsync(workspaceRoot, "請記住測試代號是 CODEX-9527，並確認已理解。");

    //if (!firstResult.Completed || string.IsNullOrWhiteSpace(firstResult.SessionId))
    //{
    //    throw new InvalidOperationException("Failed to create Codex session.");
    //}

    //Console.WriteLine($"SessionId: {firstResult.SessionId}");
    //Console.WriteLine(firstResult.FinalResponse);

    //var options = new CodexExecutionOptions
    //{
    //    SessionId = firstResult.SessionId
    //};

    //var secondResult = await runner.ExecuteAsync(workspaceRoot, "請告訴我剛才的測試代號。", options);

    //if (!secondResult.Completed)
    //{
    //    throw new InvalidOperationException("Failed to resume Codex session.");
    //}

    //Console.WriteLine(secondResult.FinalResponse);



    //WorkflowResult result = await workflow.ExecuteAsync(workspaceRoot, "VerificationTool");

    //Console.WriteLine("===== Analysis =====");
    //Console.WriteLine(result.Analysis.Summary);

    //Console.WriteLine();
    //Console.WriteLine("===== Review =====");
    //Console.WriteLine(result.Review.Summary);

    //Console.WriteLine();
    //Console.WriteLine("Workflow completed.");

}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
}
stopwatch.Stop();
Console.WriteLine("Spend time : " + stopwatch.ElapsedMilliseconds);
Console.Read();



