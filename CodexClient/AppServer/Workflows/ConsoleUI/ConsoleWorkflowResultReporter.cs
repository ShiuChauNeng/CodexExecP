using CodexClient.Acceptance;
using System.Text;

namespace CodexClient.AppServer.Workflows.ConsoleUI;

/// <summary>以緊湊時間線輸出 Workflow 執行結果，並列出已確認需求決策。</summary>
public sealed class ConsoleWorkflowResultReporter
{
    /// <summary>輸出流程結果、各次步驟紀錄與已確認需求決策；只有驗收結果顯示累計次數。</summary>
    /// <param name="result">要輸出的 Workflow 結果。</param>
    public void Report(AppServerWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ConsoleWorkflowOutput.WriteLine("=================================", ConsoleColor.DarkGray);
        ConsoleWorkflowOutput.Write("執行結果：");
        ConsoleWorkflowOutput.WriteLine(result.Succeeded ? "成功" : "失敗",
            result.Succeeded ? ConsoleColor.Green : ConsoleColor.Red);

        var statuses = result.Steps.Select(GetStatus).ToArray();
        int nameWidth = result.Steps.Select(step => GetDisplayWidth(step.Name)).DefaultIfEmpty(0).Max();
        int statusWidth = statuses.Select(status => GetDisplayWidth(status.Text)).DefaultIfEmpty(0).Max();
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

            if (step.FailureCount > 0 || step.InconclusiveCount > 0)
            {
                ConsoleWorkflowOutput.Write(new string(' ', statusWidth - GetDisplayWidth(status.Text)));
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
        }
        ConsoleWorkflowOutput.WriteLine("");
        ReportDecisions(result.Decisions);
    }

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

    /// <summary>依常見中英文等寬終端機的欄寬計算，避免中文字元使欄位錯位。</summary>
    private static int GetDisplayWidth(string text)
        => text.EnumerateRunes().Sum(character => character.Value is
            >= 0x2E80 and <= 0xA4CF or >= 0xF900 and <= 0xFAFF or
            >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or
            >= 0x20000 and <= 0x3FFFD ? 2 : 1);
}
