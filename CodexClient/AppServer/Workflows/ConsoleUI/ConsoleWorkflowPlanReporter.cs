using CodexClient.Models;

namespace CodexClient.AppServer.Workflows.ConsoleUI;

/// <summary>
/// 顯示執行前的流程設定摘要，不執行任務、提示產生器或分流 callback。
/// </summary>
public sealed class ConsoleWorkflowPlanReporter
{
    /// <summary>
    /// 依步驟宣告順序輸出模型、權限、決策群組與驗收設定。
    /// 摘要不取代 RunAsync 的設定驗證，也不保證動態分流後的實際路徑。
    /// </summary>
    /// <param name="workflow">要顯示設定的工作流程。</param>
    public void Report(AppServerWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var steps = workflow.Steps;
        ConsoleWorkflowOutput.WriteLine("=================================", ConsoleColor.DarkGray);
        ConsoleWorkflowOutput.WriteLine($"流程摘要：{steps.Count} 個步驟（宣告順序）");
        ConsoleWorkflowOutput.WriteLine($"工作區：{workflow.WorkspaceRoot}");

        int numberWidth = Math.Max(2, steps.Count.ToString().Length);
        string indent = new(' ', numberWidth + 2);
        for (int index = 0; index < steps.Count; index++)
        {
            if (index >= 0)
            {
                ConsoleWorkflowOutput.WriteLine("---------------------------------", ConsoleColor.DarkGray);
            }
            var step = steps[index];
            ConsoleWorkflowOutput.Write((index + 1).ToString($"D{numberWidth}") + "  ", ConsoleColor.DarkGray);
            ConsoleWorkflowOutput.WriteLine($"{step.Name} | Model：{step.Options.Model ?? "沿用 Codex 設定"} | 權限：{step.Options.Sandbox}");
            ConsoleWorkflowOutput.WriteLine($"{indent}推理：{step.Options.ReasoningEffort?.ToString() ?? "沿用 thread 設定"} | 網路：{GetNetworkAccess(step.Options)} | issues：{(step.StopOnIssues ? "停止" : "允許繼續")}");
            if (step.Thread.Mode != AppServerThreadMode.PerStep)
            {
                ConsoleWorkflowOutput.WriteLine($"{indent}Thread：{step.Thread.Mode}{(step.Thread.Key is null ? "" : $"（{step.Thread.Key}）")}");
            }
            if (step.RecordDecisionsTo is not null || step.ReadDecisionsFrom.Count > 0)
            {
                string readers = step.ReadDecisionsFrom.Count == 0 ? "無" : string.Join("、", step.ReadDecisionsFrom);
                ConsoleWorkflowOutput.WriteLine($"{indent}決策：記錄至 {step.RecordDecisionsTo ?? "無"} | 讀取 {readers}");
            }
            if (step is AppServerAcceptanceStep acceptance)
            {
                ConsoleWorkflowOutput.WriteLine($"{indent}驗收：{acceptance.FailureSeverity} 以上視為失敗 | 失敗上限 {acceptance.MaxFailures} 次 | 額外補查 {acceptance.MaxInconclusiveRetries} 次 | 返回 {acceptance.RetryStep}");
                if (acceptance.RequiredCriterionIds.Count > 0)
                {
                    ConsoleWorkflowOutput.WriteLine($"{indent}必要項目：{string.Join("、", acceptance.RequiredCriterionIds)}");
                }
            }
            else if (step.NextStep is not null)
            {
                ConsoleWorkflowOutput.WriteLine($"{indent}分流：執行後動態決定");
            }
        }
        ConsoleWorkflowOutput.WriteLine("=================================", ConsoleColor.DarkGray);
        ConsoleWorkflowOutput.WriteLine("");
    }

    /// <summary>
    /// 顯示沙箱命令的網路選項；WorkspaceWrite 未設定時實際禁止連網。
    /// </summary>
    private static string GetNetworkAccess(AppServerOptions options)
        => options.NetworkAccess switch
        {
            true => "允許",
            false => "禁止",
            null when options.Sandbox == CodexSandboxMode.WorkspaceWrite => "禁止（預設）",
            _ => "未設定"
        };
}
