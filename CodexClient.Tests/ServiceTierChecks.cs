using System.Text.Json;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.Exec;
using CodexClient.Models;
using CodexClient.Workflows;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ServiceTierChecks
{
    /// <summary>
    /// 以模擬程序驗證速度設定在 App Server、exec 與 session resume 間的參數傳遞。
    /// </summary>
    internal static async Task<int> RunAsync(string workspace)
    {
        var cases = new (CodexServiceTier? Tier, string? Value)[]
        {
            (CodexServiceTier.Fast, "fast"), (CodexServiceTier.Standard, "default"), (null, null)
        };
        var runner = new CodexExecRunner(NullLogger.Instance, Environment.ProcessPath!);
        int passed = 0;
        foreach (var item in cases)
        {
            var result = await runner.ExecuteAsync(workspace, "inspect arguments", new ExecutionOptions
            {
                ServiceTier = item.Tier, Timeout = TimeSpan.FromSeconds(10)
            });
            Assert(result.Succeeded);
            VerifyArguments(result.FinalResponse!, item.Value);
            passed++;
            Console.WriteLine($"PASS: exec service tier {item.Tier?.ToString() ?? "unset"}");
        }

        var workflow = new CodexWorkflow(runner, NullLogger<CodexWorkflow>.Instance, workspace)
            .Add(new CodexWorkflowStep("Fast", "inspect arguments")
            {
                Session = Session.Create("shared"), Options = new ExecutionOptions { ServiceTier = CodexServiceTier.Fast }
            })
            .Add(new CodexWorkflowStep("Standard", "inspect arguments")
            {
                Session = Session.Use("shared"), Options = new ExecutionOptions { ServiceTier = CodexServiceTier.Standard }
            });
        var resumed = await workflow.RunAsync();
        Assert(resumed.Succeeded);
        VerifyArguments(resumed.Steps[0].Execution.FinalResponse!, "fast");
        VerifyArguments(resumed.Steps[1].Execution.FinalResponse!, "default");
        Assert(JsonSerializer.Deserialize<string[]>(resumed.Steps[1].Execution.FinalResponse!)!.Contains("resume"));
        passed++;
        Console.WriteLine("PASS: exec workflow preserves service tier when resuming a session");

        await using var client = new CodexAppServerClient(Environment.ProcessPath!);
        await client.StartAsync("service-tier-tests");
        var appWorkflow = new AppServerWorkflow(client, workspace, (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        for (int index = 0; index < cases.Length; index++)
        {
            appWorkflow.Add(new AppServerWorkflowStep($"tier-{index}", "service-tier-check")
            {
                Thread = index == 0 ? AppServerThread.Create("shared") : AppServerThread.Use("shared"),
                Options = new AppServerOptions { ServiceTier = cases[index].Tier, ReasoningEffort = CodexReasoningEffort.High },
                ExecutionTimeout = TimeSpan.FromSeconds(10)
            });
        }
        var appResult = await appWorkflow.RunAsync();
        Assert(appResult.Succeeded);
        for (int index = 0; index < cases.Length; index++)
        {
            using var document = JsonDocument.Parse(appResult.Steps[index].Execution!.Message);
            bool supplied = document.RootElement.TryGetProperty("serviceTier", out var tier);
            Assert(cases[index].Value is { } expected ? supplied && tier.GetString() == expected : !supplied);
            Assert(document.RootElement.GetProperty("effort").GetString() == "high");
        }
        passed++;
        Console.WriteLine("PASS: app server sends Fast and Standard explicitly, omits unset tier and retains reasoning effort");

        try
        {
            new ExecutionOptions { ServiceTier = (CodexServiceTier)999 }.Validate();
            throw new Exception("Expected invalid exec service tier rejection.");
        }
        catch (ArgumentOutOfRangeException error) when (error.ParamName == nameof(ExecutionOptions.ServiceTier)) { }
        try
        {
            await client.RunTurnAsync("thread", "unused", options: new AppServerOptions { ServiceTier = (CodexServiceTier)999 });
            throw new Exception("Expected invalid app server service tier rejection.");
        }
        catch (ArgumentOutOfRangeException error) when (error.ParamName == nameof(AppServerOptions.ServiceTier)) { }
        passed++;
        Console.WriteLine("PASS: both clients reject undefined service tier values");
        return passed;
    }

    /// <summary>
    /// 檢查 exec 實際命令列參數；未指定時不得新增 service_tier 覆寫。
    /// </summary>
    private static void VerifyArguments(string response, string? expected)
    {
        string[] arguments = JsonSerializer.Deserialize<string[]>(response)!;
        int index = Array.FindIndex(arguments, value => value.StartsWith("service_tier=", StringComparison.Ordinal));
        Assert(expected is null ? index == -1 : index > 0 && arguments[index - 1] == "-c" &&
            arguments[index] == $"service_tier=\"{expected}\"");
    }

    /// <summary>
    /// 驗證測試條件，不符合時使案例失敗。
    /// </summary>
    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Service tier assertion failed.");
        }
    }
}
