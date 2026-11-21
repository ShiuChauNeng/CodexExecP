using CodexClient.Acceptance;

namespace CodexClient.AppServer.Workflows.Internal;

/// <summary>
/// 保存本次 Run 各驗收步驟的必要項目、缺陷次數與補查次數。
/// </summary>
internal sealed class WorkflowAcceptanceTracker
{
    // Fields
    private readonly Dictionary<string, AcceptanceState> _states;


    // Constructors
    /// <summary>
    /// 為各驗收步驟建立本次 Run 專用的項目與次數紀錄。
    /// </summary>
    internal WorkflowAcceptanceTracker(IEnumerable<AppServerWorkflowStep> steps)
    {
        _states = steps.OfType<AppServerAcceptanceStep>()
            .ToDictionary(step => step.Name, _ => new AcceptanceState(), StringComparer.Ordinal);
    }


    // Methods
    /// <summary>
    /// 取得指定驗收步驟累積的項目 ID；一般步驟回傳 null。
    /// </summary>
    internal HashSet<string>? GetKnownCriterionIds(string stepName)
        => _states.GetValueOrDefault(stepName)?.KnownCriterionIds;

    /// <summary>
    /// 依任務接受條件累計驗收次數，並更新結果的 Accepted 與累計次數。
    /// </summary>
    internal AppServerWorkflowStepResult Record(AppServerWorkflowStepResult result, bool taskAccepted)
    {
        var state = _states.GetValueOrDefault(result.Name);
        // 只有正常完成並符合步驟接受條件時，才累計產品缺陷或無法判定次數。
        if (taskAccepted && state is not null)
        {
            if (result.AcceptanceDecision == AcceptanceDecision.NeedsRevision)
            {
                state.FailureCount++;
            }
            if (result.AcceptanceDecision == AcceptanceDecision.Inconclusive)
            {
                state.InconclusiveCount++;
            }
        }

        return result with
        {
            Accepted = taskAccepted && (result.AcceptanceDecision is null or AcceptanceDecision.Passed),
            FailureCount = state?.FailureCount ?? 0,
            InconclusiveCount = state?.InconclusiveCount ?? 0
        };
    }

    private sealed class AcceptanceState
    {
        internal HashSet<string> KnownCriterionIds { get; } = new(StringComparer.Ordinal);
        internal int FailureCount { get; set; }
        internal int InconclusiveCount { get; set; }
    }
}
