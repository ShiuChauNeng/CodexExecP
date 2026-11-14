using System.Text.Json;

namespace CodexClient.AppServer.Workflows;

public sealed record AppServerWorkflowStepResult(
    string Name,
    string? ThreadId,
    TaskRunResult? Execution,
    Exception? Error)
{
    /// <summary>是否通過本次 Workflow 的接受條件，由 Workflow 設定。</summary>
    public bool Accepted { get; internal init; }
    public IReadOnlyList<AppServerTurnDiagnostics> Diagnostics { get; init; } = [];

    public bool Completed => Execution is not null && Error is null;
    public bool HasIssues => Execution?.Issues.Count > 0;
    public bool Succeeded => Completed && !HasIssues;

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
