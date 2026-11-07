namespace CodexClient.Exec;


public sealed record ExecutionEvent(
    string Type,
    string? ItemId = null,
    string? ItemType = null,
    string? Command = null,
    int? ExitCode = null,
    string? Text = null,
    string? ThreadId = null,
    string? ItemStatus = null,
    string? ErrorMessage = null,
    string? ToolName = null);