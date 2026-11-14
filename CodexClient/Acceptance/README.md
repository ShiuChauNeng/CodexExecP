# 驗收結果契約

Skill 負責理解規格、檢查實作、判定問題等級並提供證據。
C# 只驗證資料完整性與決定流程方向，不重新判讀規格是否正確實作。

## 問題與項目狀態

| 問題等級 | 定義 | 處理方式 |
|---|---|---|
| `Error` | 必須修正，包含違反必要規格或必要測試失敗 | 退回修正 |
| `Warning` | 未違反必要條件的風險或品質疑慮 | 允許通過，保留報表 |
| `Info` | 改善建議或補充資訊 | 記錄即可 |

每個驗收項目也要回報 `Passed`、`Failed` 或 `NotEvaluated`，並附上證據。
證據應包含規格條文、程式位置或實際測試結果；無法驗證時，填寫具體原因。
違反必要條件不可降為 Warning；無法判定不可當作 Passed。

必要項目 ID 由呼叫端依已確認的 Spec 提供，必須同時傳給 Skill 與 `Decide()`。
這個清單不可直接從當輪 Skill 回覆的 `Checks` 推導，否則無法發現漏查。
若 Spec 由另一個 Skill 產生，應保存其已確認的驗收清單，供每輪沿用。

`AcceptanceResult.Decide(requiredCriterionIds, failureSeverity)` 的結果（failureSeverity 預設 Error）：

1. 必要項目漏查，或任何回報項目是 `NotEvaluated`：`Inconclusive`，停止並處理無法完成驗收的原因。
2. 所有必要項目均已檢查，但有 `Failed` 或達到失敗等級的問題：`NeedsRevision`，交回實作步驟修正。
3. 所有必要項目均已檢查，且沒有 `Failed` 或達到失敗等級的問題：`Passed`，允許後續步驟。

若同時有 Error 與未完成項目，優先回傳 `Inconclusive`；已發現的問題仍保留在 Findings。
資料格式錯誤（例如空白證據、重複 ID、未知等級）會拋出例外，與正常的驗收不合格分開。

## 與 AppServerWorkflow 搭配

一般使用情境建議直接使用 [AppServerAcceptanceStep](../AppServer/Workflows/README.md)：
設定 FailureSeverity、MaxFailures、RetryStep 即可自動判斷並返回修正。
以下是以一般 Step 手動處理契約的進階用法。

將 `AcceptanceResultSchema.Value` 指定給驗收步驟的 `DataSchema`。
Skill 的驗收結果放在 TaskRunner 外層回覆的 `data`，而不是改變外層回覆格式。
正常執行驗收後，即使發現不合格，外層 `state` 仍為 `completed`，產品缺陷放在 `data.findings`。
外層 `issues` 保留給任務本身的未解決問題，不要重複放入產品缺陷。
若整個驗收無法執行，仍使用 TaskRunner 的 `failed` 或任務 issues。

```csharp
using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.AppServer;
using CodexClient.AppServer.Workflows;

string[] requiredCriteria = ["SPEC-001", "SPEC-002"];

var acceptanceStep = new AppServerWorkflowStep(
    "Acceptance",
    "使用驗收 Skill 檢查 Spec 與實作。必要項目：SPEC-001、SPEC-002。產生報表並回傳驗收資料。")
{
    DataSchema = AcceptanceResultSchema.Value,
    Options = new AppServerOptions
    {
        Sandbox = AppServerSandboxMode.WorkspaceWrite
    },
    StopOnIssues = true,
    ValidateCompletionAsync = (execution, token) =>
    {
        token.ThrowIfCancellationRequested();
        var data = execution.Data!.Value.Deserialize<AcceptanceResult>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        data.Validate();

        // 另外確認報表存在。Validate/Decide 本身不檢查檔案或執行測試。
        if (!File.Exists(Path.Combine(workspaceRoot, data.ReportPath)))
        {
            throw new InvalidOperationException("驗收報表不存在。");
        }
        return Task.CompletedTask;
    }
};
```

只有當 Workflow 的步驟正常完成並被接受後，外層控制器才讀取驗收結果：

```csharp
var stepResult = workflowResult.Steps.Single(step => step.Name == "Acceptance");
if (!stepResult.Accepted)
{
    throw new InvalidOperationException("驗收工作未能正常完成。", stepResult.Error);
}

AcceptanceResult acceptance = stepResult.GetData<AcceptanceResult>();
AcceptanceDecision decision = acceptance.Decide(requiredCriteria);
```

一般 Step 的 `NeedsRevision` 只是資料；使用 AppServerAcceptanceStep 才會自動處理。
也可手動使用一般 AppServerWorkflowStep 的 NextStep 依 decision 返回實作；
流程順序及返回目標由呼叫端決定，不應把 Coding Style 無條件接在驗收步驟後面。
修正時應傳遞 Failed 項目與 Findings 的證據，不能只要求「再試一次」。
