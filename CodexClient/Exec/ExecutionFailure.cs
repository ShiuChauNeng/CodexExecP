using System;
using System.Collections.Generic;
using System.Text;

namespace CodexClient.Exec;

public enum ExecutionFailureKind
{
    Process,
    Tool,
    AgentItem,
    Stream,
    Turn
}

public sealed record ExecutionFailure(
    ExecutionFailureKind Kind,
    string Message,
    string? ItemId = null,
    string? ItemType = null,
    string? ItemStatus = null,
    string? Command = null,
    string? ToolName = null,
    int? ExitCode = null);
