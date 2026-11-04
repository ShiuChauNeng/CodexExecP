using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexController.Codex;

public class CodexExecutionOptions
{
    public string? Model { get; init; }

    public string? ReasoningEffort { get; init; }

    public string Sandbox { get; init; } = "read-only";

    public string? OutputSchemaPath { get; init; }

    public bool SkipGitRepoCheck { get; init; } = true;
}
