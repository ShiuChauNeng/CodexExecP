using CodexController.Codex;
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

    
    var runner = new CodexRunner(@"C:\Users\User\AppData\Local\OpenAI\Codex\bin\b6c7fc9497179c98\codex.exe", logger);
    var workflow = new CodexWorkflow(runner);

    string workspaceRoot = @"C:\Users\User\Desktop\TestProj";
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

