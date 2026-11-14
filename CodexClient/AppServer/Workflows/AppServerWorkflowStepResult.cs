using System.Text.Json;
using CodexClient.Acceptance;

namespace CodexClient.AppServer.Workflows;

public sealed record AppServerWorkflowStepResult(
    string Name,
    string? ThreadId,
    TaskRunResult? Execution,
    Exception? Error)
{
    /// <summary>是否通過本次 Workflow 的接受條件，由 Workflow 設定。</summary>
    public bool Accepted { get; internal init; }
    public AcceptanceDecision? AcceptanceDecision { get; internal init; }
    /// <summary>此驗收步驟在本次 Run 的累計失敗次數；一般步驟為 0。</summary>
    public int FailureCount { get; internal init; }
    public IReadOnlyList<AppServerTurnDiagnostics> Diagnostics { get; init; } = [];

    public bool Completed => Execution is not null && Error is null;
    public bool HasIssues => Execution?.Issues.Count > 0;
    public bool Succeeded => Completed && !HasIssues &&
        (AcceptanceDecision is null or CodexClient.Acceptance.AcceptanceDecision.Passed);

    public T GetData<T>()
    {
        if (Execution?.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            throw new InvalidOperationException($"Step '{Name}' has no object data.");
        }
        return data.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException($"Step '{Name}' returned null data.");
    }
}
