namespace CodexClient.AppServer.Workflows;

public sealed record AppServerWorkflowInputRequest(
    string StepName,
    string ThreadId,
    string TurnId,
    string Message,
    IReadOnlyList<string> Questions);
