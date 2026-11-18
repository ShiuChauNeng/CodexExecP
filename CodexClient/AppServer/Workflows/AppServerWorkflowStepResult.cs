using System.Text.Json;
using CodexClient.Acceptance;

namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 表示 Workflow 單一步驟的執行結果。
/// </summary>
/// <param name="Name">步驟名稱。</param>
/// <param name="ThreadId">步驟使用的 thread 識別碼。</param>
/// <param name="Execution">任務執行結果。</param>
/// <param name="Error">步驟執行期間發生的錯誤。</param>
public sealed record AppServerWorkflowStepResult(string Name, string? ThreadId, TaskRunResult? Execution, Exception? Error)
{
    // Properties
    /// <summary>
    /// 是否通過本次 Workflow 的接受條件，由 Workflow 設定。
    /// </summary>
    public bool Accepted { get; internal init; }

    /// <summary>
    /// 取得此步驟各次 turn 的診斷資訊。
    /// </summary>
    public IReadOnlyList<AppServerTurnDiagnostics> Diagnostics { get; init; } = [];

    /// <summary>
    /// 取得步驟是否已完成且沒有執行錯誤。
    /// </summary>
    public bool Completed => Execution is not null && Error is null;

    /// <summary>
    /// 取得步驟執行期間是否記錄到問題。
    /// </summary>
    public bool HasIssues => Execution?.Issues.Count > 0;

    /// <summary>
    /// 取得驗收步驟的驗收決策；一般步驟不會設定此值。
    /// </summary>
    public AcceptanceDecision? AcceptanceDecision { get; internal init; }

    /// <summary>
    /// 此驗收步驟在本次 Run 的累計失敗次數；一般步驟為 0。
    /// </summary>
    public int FailureCount { get; internal init; }

    /// <summary>
    /// 此驗收步驟在本次 Run 累計回傳 Inconclusive 的次數，與產品缺陷次數分開。
    /// </summary>
    public int InconclusiveCount { get; internal init; }

    /// <summary>
    /// 取得步驟是否成功完成並通過接受條件。
    /// </summary>
    public bool Succeeded => Completed && (AcceptanceDecision is null ? !HasIssues : Accepted);


    // Methods
    /// <summary>
    /// 取得步驟產出的結構化資料。
    /// </summary>
    /// <typeparam name="T">資料的目標型別。</typeparam>
    /// <returns>反序列化後的步驟資料。</returns>
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
