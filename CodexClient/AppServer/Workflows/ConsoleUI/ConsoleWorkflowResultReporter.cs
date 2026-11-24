using CodexClient.Acceptance;
using System.Text;

namespace CodexClient.AppServer.Workflows.ConsoleUI;

/// <summary>
/// 以緊湊時間線輸出 Workflow 執行結果，並列出未解決問題與已確認需求決策。
/// </summary>
public sealed class ConsoleWorkflowResultReporter
{
    /// <summary>
    /// 輸出流程結果、停止原因、各次步驟紀錄及耗時、未解決問題與已確認需求決策；只有驗收結果顯示累計次數。
    /// </summary>
    /// <param name="result">要輸出的 Workflow 結果。</param>
    public void Report(AppServerWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ConsoleWorkflowOutput.WriteLine("=================================", ConsoleColor.DarkGray);
        ConsoleWorkflowOutput.Write("執行結果：");
        ConsoleWorkflowOutput.WriteLine(result.Succeeded ? "成功" : "失敗",
            result.Succeeded ? ConsoleColor.Green : ConsoleColor.Red);
        if (!result.Succeeded)
        {
            ReportStopReason(result);
        }

        var statuses = result.Steps.Select(GetStatus).ToArray();
        var durations = result.Steps.Select(step => FormatDuration(step.Duration)).ToArray();
        int nameWidth = result.Steps.Select(step => GetDisplayWidth(step.Name)).DefaultIfEmpty(0).Max();
        int statusWidth = statuses.Select(status => GetDisplayWidth(status.Text)).DefaultIfEmpty(0).Max();
        int durationWidth = durations.Select(GetDisplayWidth).DefaultIfEmpty(0).Max();
        int numberWidth = Math.Max(2, result.Steps.Count.ToString().Length);
        for (int index = 0; index < result.Steps.Count; index++)
        {
            var step = result.Steps[index];
            var status = statuses[index];
            ConsoleWorkflowOutput.Write((index + 1).ToString($"D{numberWidth}") + "  ", ConsoleColor.DarkGray);
            ConsoleWorkflowOutput.Write(step.Name + new string(' ', nameWidth - GetDisplayWidth(step.Name) + 2));
            if (status.Color == ConsoleColor.DarkYellow)
            {
                ConsoleWorkflowOutput.WriteOrange(status.Text);
            }
            else
            {
                ConsoleWorkflowOutput.Write(status.Text, status.Color);
            }

            ConsoleWorkflowOutput.Write(new string(' ', statusWidth - GetDisplayWidth(status.Text)));
            ConsoleWorkflowOutput.Write($" | 耗時{durations[index]}", ConsoleColor.DarkGray);
            if (step.FailureCount > 0 || step.InconclusiveCount > 0)
            {
                ConsoleWorkflowOutput.Write(new string(' ', durationWidth - GetDisplayWidth(durations[index])));
            }

            if (step.FailureCount > 0)
            {
                ConsoleWorkflowOutput.Write($" | 累計失敗 {step.FailureCount} 次", ConsoleColor.DarkGray);
            }
            if (step.InconclusiveCount > 0)
            {
                ConsoleWorkflowOutput.Write($" | 累計無法判定 {step.InconclusiveCount} 次", ConsoleColor.DarkGray);
            }
            ConsoleWorkflowOutput.WriteLine("");
            ReportAcceptanceIssues(step, numberWidth + 2);
        }
        ConsoleWorkflowOutput.WriteLine("");
        ReportIssues(result.Steps);
        ReportDecisions(result.Decisions);
    }

    /// <summary>
    /// 以總分鐘與剩餘秒數顯示單次步驟耗時，超過一小時仍持續累計分鐘。
    /// </summary>
    private static string FormatDuration(TimeSpan duration)
        => $"{(long)duration.TotalMinutes}分{duration.Seconds}秒";

    /// <summary>
    /// 在需修正的驗收紀錄下列出當輪 Findings，沒有對應 Finding 的失敗項目則以證據補充。
    /// 這是當輪歷程，不代表後續驗收通過後仍未修復。
    /// </summary>
    private static void ReportAcceptanceIssues(AppServerWorkflowStepResult step, int indentation)
    {
        if (!step.Completed || step.AcceptanceDecision != AcceptanceDecision.NeedsRevision)
        {
            return;
        }

        var acceptance = step.GetData<AcceptanceResult>();
        string prefix = new(' ', indentation);
        foreach (var finding in acceptance.Findings)
        {
            ConsoleWorkflowOutput.WriteOrangeLine($"{prefix}需修正問題：[{finding.Severity}] {finding.Message}");
        }

        var describedCriteria = acceptance.Findings.Where(finding => finding.CriterionId is not null)
            .Select(finding => finding.CriterionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var check in acceptance.Checks)
        {
            if (check.Status == AcceptanceCheckStatus.Failed && !describedCriteria.Contains(check.CriterionId))
            {
                ConsoleWorkflowOutput.WriteOrangeLine($"{prefix}需修正問題：[{check.CriterionId}] 驗收未通過；{check.Evidence}");
            }
        }
    }

    /// <summary>
    /// 只列出各步驟最後一次執行回報的 issues，避免顯示重跑前的舊問題。
    /// </summary>
    private static void ReportIssues(IReadOnlyList<AppServerWorkflowStepResult> steps)
    {
        var latestStepsWithIssues = steps.GroupBy(step => step.Name, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Where(step => step.HasIssues)
            .ToArray();
        if (latestStepsWithIssues.Length == 0)
        {
            return;
        }

        ConsoleWorkflowOutput.WriteOrangeLine("未解決問題：");
        foreach (var step in latestStepsWithIssues)
        {
            foreach (string issue in step.Execution!.Issues)
            {
                ConsoleWorkflowOutput.WriteOrangeLine($"[{step.Name}] {issue}");
            }
        }
        ConsoleWorkflowOutput.WriteLine("");
    }

    /// <summary>
    /// 顯示停止步驟與原因；手動建立的結果未提供原因時，退回錯誤訊息或明確提示。
    /// </summary>
    private static void ReportStopReason(AppServerWorkflowResult result)
    {
        ConsoleWorkflowOutput.WriteLine($"停止步驟：{result.FailedStep ?? "未提供"}");
        string reason = $"停止原因：{result.StopReason ?? result.Error?.Message ?? "未提供停止原因。"}";
        if (result.Error is not null)
        {
            ConsoleWorkflowOutput.WriteLine(reason, ConsoleColor.Red);
        }
        else
        {
            ConsoleWorkflowOutput.WriteOrangeLine(reason);
        }
    }

    /// <summary>
    /// 列出已確認需求決策的筆數、來源、確認時間、問題與回答。
    /// </summary>
    private static void ReportDecisions(IReadOnlyList<AppServerWorkflowDecision> decisions)
    {
        ConsoleWorkflowOutput.WriteLine($"已確認需求決策：{decisions.Count} 筆");
        foreach (var decision in decisions)
        {
            ConsoleWorkflowOutput.WriteLine($"[{decision.Group}] 來源：{decision.StepName}，確認時間：{decision.ConfirmedAt.ToLocalTime():HH:mm:ss}", ConsoleColor.DarkGray);
            ConsoleWorkflowOutput.WriteQuestionLine($"問題：{decision.Question}");
            ConsoleWorkflowOutput.WriteLine($"回答：{decision.Answer}");
        }
    }

    /// <summary>
    /// 依步驟完成、接受與驗收結果，取得顯示標籤及顏色。
    /// </summary>
    private static (string Text, ConsoleColor Color) GetStatus(AppServerWorkflowStepResult step)
    {
        if (!step.Completed)
        {
            return ("[執行失敗]", ConsoleColor.Red);
        }
        if (step.Accepted)
        {
            return ("[通過]", ConsoleColor.Green);
        }
        return step.AcceptanceDecision switch
        {
            AcceptanceDecision.NeedsRevision => ("[需修正]", ConsoleColor.DarkYellow),
            AcceptanceDecision.Inconclusive => ("[無法判定]", ConsoleColor.Yellow),
            _ => ("[未通過]", ConsoleColor.Red)
        };
    }

    /// <summary>
    /// 依常見中英文等寬終端機的欄寬計算，避免中文字元使欄位錯位。
    /// </summary>
    private static int GetDisplayWidth(string text)
        => text.EnumerateRunes().Sum(character => character.Value is
            >= 0x2E80 and <= 0xA4CF or >= 0xF900 and <= 0xFAFF or
            >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or
            >= 0x20000 and <= 0x3FFFD ? 2 : 1);
}
