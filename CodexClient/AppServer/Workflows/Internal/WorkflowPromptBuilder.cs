using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using CodexClient.Acceptance;

namespace CodexClient.AppServer.Workflows.Internal;

/// <summary>
/// 組合步驟指令、已確認需求決策、前次驗收回饋與必要驗收清單。
/// </summary>
internal static class WorkflowPromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>
    /// 組合步驟指令、已確認需求決策、驗收回饋與必要項目清單。
    /// </summary>
    internal static string Create(AppServerWorkflowStep step, AppServerWorkflowContext context, string? threadId,
        HashSet<string>? knownCriterionIds, AppServerWorkflowStepResult? acceptanceFeedback)
    {
        // 只有目標步驟已在同一 thread 執行過，才能省略原始指令。
        bool resumeForRevision = acceptanceFeedback?.AcceptanceDecision == AcceptanceDecision.NeedsRevision && threadId is not null &&
            context.Steps.Any(result => result.Name == step.Name && result.ThreadId == threadId);
        string prompt = resumeForRevision ? "" : step.CreatePrompt(context);
        var relevantDecisions = context.Steps.SelectMany(result => result.Decisions)
            .Where(decision => step.ReadDecisionsFrom.Contains(decision.Group, StringComparer.Ordinal)).ToArray();
        if (relevantDecisions.Length > 0)
        {
            prompt += "\n\n以下是使用者已確認的需求補充或變更，僅修訂其對應事項，其餘仍以原始規格為準。" +
                "若決策彼此衝突或含義不明，請提問確認，不可自行擴大變更範圍：\n" + JsonSerializer.Serialize(relevantDecisions, JsonOptions);
        }
        if (acceptanceFeedback is not null)
        {
            if (!string.IsNullOrEmpty(prompt))
            {
                prompt += "\n\n";
            }
            bool recheck = acceptanceFeedback.AcceptanceDecision == AcceptanceDecision.Inconclusive;
            prompt += (recheck
                ? "前次驗收尚有必要項目無法判定，請依下列結果與原因補查缺漏或 NotEvaluated 的必要項目，保留完整驗收清單、已知缺陷與證據並更新報表。遵守原驗收範圍，不修改實作或放寬必要條件；仍無法判定時保留具體原因，不可當作通過：\n"
                : "前次驗收不通過，請依下列問題、證據與建議修正，完成後將再次驗收：\n") +
                JsonSerializer.Serialize(new
                {
                    acceptanceStep = acceptanceFeedback.Name,
                    failureCount = acceptanceFeedback.FailureCount,
                    inconclusiveCount = acceptanceFeedback.InconclusiveCount,
                    issues = acceptanceFeedback.Execution!.Issues,
                    acceptance = acceptanceFeedback.Execution!.Data
                }, JsonOptions);
        }
        if (step is AppServerAcceptanceStep acceptanceForPrompt)
        {
            prompt = acceptanceForPrompt.AppendRequiredCriteria(prompt, knownCriterionIds!);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return prompt;
    }
}
