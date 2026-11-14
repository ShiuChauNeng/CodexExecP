using System.Text.Json;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;
using CodexClient.Exec;
using CodexClient.Workflows;
using Microsoft.Extensions.Logging.Abstractions;

internal static class NetworkAccessChecks
{
    internal static async Task<int> RunAsync(string workspace)
    {
        int passed = 0;
        var runner = new CodexExecRunner(NullLogger.Instance, Environment.ProcessPath!);
        foreach (bool? access in new bool?[] { true, false, null })
        {
            var options = new ExecutionOptions
            {
                Sandbox = CodexSandboxMode.WorkspaceWrite,
                NetworkAccess = access,
                Timeout = TimeSpan.FromSeconds(5)
            };
            var result = await runner.ExecuteAsync(workspace, "inspect arguments", options);
            Assert(result.Succeeded);
            VerifyArguments(result.FinalResponse!, access);
            passed++;
            Console.WriteLine($"PASS: Exec transmits network access {access?.ToString() ?? "unset"}");
        }

        var workflow = new CodexWorkflow(runner, NullLogger<CodexWorkflow>.Instance, workspace);
        workflow.Add(new CodexWorkflowStep("first", "inspect arguments")
        {
            Session = Session.Create("shared"),
            Options = new ExecutionOptions { Sandbox = CodexSandboxMode.WorkspaceWrite, NetworkAccess = true }
        });
        workflow.Add(new CodexWorkflowStep("resume", "inspect arguments")
        {
            Session = Session.Use("shared"),
            Options = new ExecutionOptions { Sandbox = CodexSandboxMode.WorkspaceWrite, NetworkAccess = false }
        });
        var workflowResult = await workflow.RunAsync();
        Assert(workflowResult.Succeeded);
        VerifyArguments(workflowResult.Steps[0].Execution.FinalResponse!, true);
        VerifyArguments(workflowResult.Steps[1].Execution.FinalResponse!, false);
        Assert(JsonSerializer.Deserialize<string[]>(workflowResult.Steps[1].Execution.FinalResponse!)!.Contains("resume"));
        passed++;
        Console.WriteLine("PASS: Exec Workflow preserves network settings across session resume");

        await using var client = new CodexAppServerClient(Environment.ProcessPath!);
        await client.StartAsync("network-tests");
        var appWorkflow = new AppServerWorkflow(client, workspace,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        int index = 0;
        foreach (bool? access in new bool?[] { true, false, null })
        {
            appWorkflow.Add(new AppServerWorkflowStep($"network-{index}", "network-check")
            {
                Thread = index == 0 ? AppServerThread.Create("shared") : AppServerThread.Use("shared"),
                Options = new AppServerOptions { Sandbox = AppServerSandboxMode.WorkspaceWrite, NetworkAccess = access },
                ExecutionTimeout = TimeSpan.FromSeconds(5)
            });
            index++;
        }
        var appResult = await appWorkflow.RunAsync();
        Assert(appResult.Succeeded);
        for (int i = 0; i < appResult.Steps.Count; i++)
        {
            using var document = JsonDocument.Parse(appResult.Steps[i].Execution!.Message);
            var policy = document.RootElement.GetProperty("sandboxPolicy");
            Assert(policy.GetProperty("type").GetString() == "workspaceWrite");
            Assert(policy.GetProperty("networkAccess").GetBoolean() == (i == 0));
            Assert(policy.GetProperty("writableRoots")[0].GetString() == Path.GetFullPath(workspace));
        }
        passed++;
        Console.WriteLine("PASS: App Server Workflow sends per-step network settings on a shared thread");

        foreach (bool access in new[] { true, false })
        {
            try
            {
                new ExecutionOptions { NetworkAccess = access }.Validate();
                throw new Exception("Expected unsupported read-only network override.");
            }
            catch (NotSupportedException) { }
            try
            {
                await client.RunTurnAsync("thread", "unused", options: new AppServerOptions { NetworkAccess = access });
                throw new Exception("Expected unsupported read-only network override.");
            }
            catch (NotSupportedException) { }
        }
        passed++;
        Console.WriteLine("PASS: Both clients reject explicit network overrides outside WorkspaceWrite");
        return passed;
    }

    private static void VerifyArguments(string response, bool? access)
    {
        string[] arguments = JsonSerializer.Deserialize<string[]>(response)!;
        int index = Array.FindIndex(arguments, value => value.StartsWith("sandbox_workspace_write.network_access="));
        if (access is { } enabled)
        {
            Assert(index > 0 && arguments[index - 1] == "-c");
            Assert(arguments[index] == $"sandbox_workspace_write.network_access={(enabled ? "true" : "false")}");
        }
        else
        {
            Assert(index == -1);
        }
    }

    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Network access assertion failed.");
        }
    }
}
