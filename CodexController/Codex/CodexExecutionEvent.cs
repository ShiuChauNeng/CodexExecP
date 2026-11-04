using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexController.Codex;

public sealed record CodexExecutionEvent(string Type, string? ItemId, string? ItemType, string? Command, int? ExitCode, string? Text, string? ThreadId);