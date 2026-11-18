using CodexClient.Acceptance;

namespace CodexClient.AppServer.Workflows;

/// <summary>以單行步驟的緊湊時間線輸出 Workflow 執行結果。</summary>
public sealed class ConsoleWorkflowResultReporter
{
    /// <summary>輸出流程結果與各次步驟紀錄；只有驗收結果顯示累計次數。</summary>
    /// <param name="result">要輸出的 Workflow 結果。</param>
    public void Report(AppServerWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ConsoleWorkflowOutput.WriteLine("=================================", ConsoleColor.DarkGray);
        ConsoleWorkflowOutput.Write("執行結果：");
        ConsoleWorkflowOutput.WriteLine(result.Succeeded ? "成功" : "失敗",
            result.Succeeded ? ConsoleColor.Green : ConsoleColor.Red);

        for (int index = 0; index < result.Steps.Count; index++)
        {
            var step = result.Steps[index];
            ConsoleWorkflowOutput.Write($"{index + 1:00}  ", ConsoleColor.DarkGray);
            ConsoleWorkflowOutput.Write($"{step.Name}  ");
            if (!step.Completed)
            {
                ConsoleWorkflowOutput.Write("[執行失敗]", ConsoleColor.Red);
            }
            else if (step.Accepted)
            {
                ConsoleWorkflowOutput.Write("[通過]", ConsoleColor.Green);
            }
            else if (step.AcceptanceDecision == AcceptanceDecision.NeedsRevision)
            {
                ConsoleWorkflowOutput.WriteOrange("[需修正]");
            }
            else if (step.AcceptanceDecision == AcceptanceDecision.Inconclusive)
            {
                ConsoleWorkflowOutput.Write("[無法判定]", ConsoleColor.Yellow);
            }
            else
            {
                ConsoleWorkflowOutput.Write("[未通過]", ConsoleColor.Red);
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
    }
}
