using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;

internal static class AcceptanceStepDemo
{
    internal static async Task RunAsync(bool alwaysFail)
    {
        Console.WriteLine("驗收 Step 示範：全程使用記憶體模擬，不呼叫 AI、不修改檔案。");
        Console.WriteLine(alwaysFail ? "情境：驗收持續失敗，第三次失敗時停止。" : "情境：前兩次失敗，第三次只剩 Warning，允許通過。");
        Console.WriteLine();

        var client = new DemoClient(alwaysFail);
        var workflow = new AppServerWorkflow(client, Directory.GetCurrentDirectory(),
            (_, _) => throw new InvalidOperationException("此範例不需要使用者輸入。"));

        workflow
            .Add(new AppServerWorkflowStep("Implement", "[demo:implement] 實作 3 + 5，正確答案應為 8。"))
            .Add(new AppServerAcceptanceStep("Acceptance", "[demo:acceptance] 驗收 3 + 5 是否回傳 8。")
            {
                FailureSeverity = FindingSeverity.Error,
                MaxFailures = 3,
                RetryStep = "Implement"
            })
            .Add(new AppServerWorkflowStep("Style", "[demo:style] 調整程式風格。"));

        var result = await workflow.RunAsync();

        Console.WriteLine();
        Console.WriteLine("── 執行歷程 ──");
        foreach (var step in result.Steps)
        {
            Console.WriteLine($"{step.Name,-12} 通過={step.Accepted,-5} 驗收={step.AcceptanceDecision?.ToString() ?? "—",-13} 累計失敗={step.FailureCount}");
        }
        Console.WriteLine($"流程成功：{result.Succeeded}");
        if (result.Error is not null)
        {
            Console.WriteLine($"停止原因：{result.Error.Message}");
        }
    }

    private sealed class DemoClient(bool alwaysFail) : IAppServerClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private int _thread;
        private int _implementation;
        private int _reviews;
        private int _sum;

        public Task<string> StartThreadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult($"demo-thread-{++_thread}");

        public Task<TurnResult> RunTurnAsync(string threadId, string prompt, JsonElement? outputSchema = null,
            AppServerOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? data = null;
            string message;
            if (prompt.Contains("[demo:implement]"))
            {
                _implementation++;
                _sum = alwaysFail ? 7 : _implementation switch { 1 => 7, 2 => 9, _ => 8 };
                Console.WriteLine($"實作第 {_implementation} 輪：模擬程式回傳 3 + 5 = {_sum}");
                if (prompt.Contains("前次驗收不通過"))
                {
                    Console.WriteLine("  已自動收到前次驗收的問題與證據。");
                }
                message = "模擬實作完成。";
            }
            else if (prompt.Contains("[demo:acceptance]"))
            {
                _reviews++;
                bool correct = _sum == 8;
                string evidence = $"預期 3 + 5 = 8，實際回傳 {_sum}。";
                data = new AcceptanceResult(
                    [new("SUM-001", correct ? AcceptanceCheckStatus.Passed : AcceptanceCheckStatus.Failed, evidence)],
                    [new("F-001", correct ? FindingSeverity.Warning : FindingSeverity.Error, "SUM-001",
                        correct ? "計算正確，仍有示範用的格式提醒。" : "加法結果不符合規格。", evidence,
                        correct ? "可於風格調整步驟處理。" : "修正計算，應回傳 8。")],
                    $"reports/demo-acceptance-{_reviews:D2}.md");
                Console.WriteLine($"驗收第 {_reviews} 次：{(correct ? "Warning：計算正確，只有格式提醒 → 通過" : "Error：計算錯誤 → 不通過")}");
                message = "模擬驗收完成；reportPath 僅為 Schema 示意，不會寫入報表檔。";
            }
            else
            {
                Console.WriteLine("執行 Style：模擬調整風格。");
                message = "模擬風格調整完成。";
            }

            return Task.FromResult(new TurnResult(threadId, $"demo-turn-{_implementation}-{_reviews}", "completed",
                JsonSerializer.Serialize(new
                {
                    state = "completed", message, questions = Array.Empty<string>(),
                    issues = Array.Empty<string>(), data
                }, JsonOptions)));
        }
    }
}
