using System;
using System.Collections.Generic;
using System.Text;

namespace CodexClient.Exec;

public enum ExecutionOutcome
{
    Completed,
    CompletedWithIssues,
    TurnFailed,
    StreamFailed,
    ProcessFailed,
    Incomplete
}
