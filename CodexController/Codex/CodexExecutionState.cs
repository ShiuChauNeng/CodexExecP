using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexController.Codex;

public enum CodexExecutionState
{
    Created,
    Running,
    Completed,
    Failed,
    Cancelled,
    Incomplete
}