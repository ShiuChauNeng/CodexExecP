using System.Text.Json;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.Models;

internal static class ReasoningEffortChecks
{
    internal static async Task<int> RunAsync(string workspace)
    {
        await using var client = new CodexAppServerClient(Environment.ProcessPath!);
        await client.StartAsync("reasoning-tests");
        var workflow = new AppServerWorkflow(client, workspace,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        var cases = new (CodexReasoningEffort? Effort, string? Expected)[]
        {
            (CodexReasoningEffort.Minimal, "minimal"),
            (CodexReasoningEffort.Low, "low"),
            (CodexReasoningEffort.Medium, "medium"),
            (CodexReasoningEffort.High, "high"),
            (CodexReasoningEffort.XHigh, "xhigh"),
            (null, null)
        };
        for (int index = 0; index < cases.Length; index++)
        {
            workflow.Add(new AppServerWorkflowStep($"reasoning-{index}", "reasoning-check")
            {
                Thread = index == 0 ? AppServerThread.Create("shared") : AppServerThread.Use("shared"),
                Options = new AppServerOptions { ReasoningEffort = cases[index].Effort },
                ExecutionTimeout = TimeSpan.FromSeconds(10)
            });
        }

        var result = await workflow.RunAsync();
        if (!result.Succeeded)
        {
            throw new Exception("Reasoning effort workflow failed.", result.Error);
        }
        for (int index = 0; index < cases.Length; index++)
        {
            using var document = JsonDocument.Parse(result.Steps[index].Execution!.Message);
            bool hasEffort = document.RootElement.TryGetProperty("effort", out var value);
            if (cases[index].Expected is { } expected)
            {
                if (!hasEffort || value.ValueKind != JsonValueKind.String || value.GetString() != expected)
                {
                    throw new Exception($"Unexpected serialized effort for {cases[index].Effort}.");
                }
            }
            else if (hasEffort)
            {
                throw new Exception("Unset reasoning effort must be omitted to preserve thread settings.");
            }
        }
        Console.WriteLine("PASS: App Server serializes every reasoning enum and omits unset effort on a shared thread");

        try
        {
            await client.RunTurnAsync("thread", "unused",
                options: new AppServerOptions { ReasoningEffort = (CodexReasoningEffort)999 });
            throw new Exception("Expected undefined reasoning effort to be rejected.");
        }
        catch (ArgumentOutOfRangeException error) when (error.ParamName == nameof(AppServerOptions.ReasoningEffort)) { }
        Console.WriteLine("PASS: App Server rejects undefined reasoning enum values before sending a turn");
        return 2;
    }
}
