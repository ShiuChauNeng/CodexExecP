# App Server Workflow 執行規則

## 內部分工

| 元件 | 責任 |
|---|---|
| AppServerWorkflow | 步驟設定驗證、執行順序、驗收返回與停止流程 |
| WorkflowStepExecutor | 執行單一步驟、thread 選取、執行額度、驗證與診斷保存 |
| WorkflowPromptBuilder | 組合步驟指令、已確認決策、驗收回饋與必要項目 |
| WorkflowStepInput | 單一步驟的問答次數、輸入逾時、需求決策確認與紀錄 |
| WorkflowAcceptanceTracker | 本次 Run 各驗收步驟的累計次數與已知項目 ID |
| WorkflowProgressScope | 步驟結果轉為進度、定期回報與輸出錯誤隔離 |

公開的 Workflow、步驟設定與結果型別保留於 `CodexClient.AppServer.Workflows`。
執行器、問答協調、Prompt 組合、驗收追蹤與時間管理位於 `Internal/`，namespace 為 `CodexClient.AppServer.Workflows.Internal`。
Console 顯示、輸入與通知位於 `ConsoleUI/`，namespace 為 `CodexClient.AppServer.Workflows.ConsoleUI`，不決定驗收是否通過。
執行狀態仍在每次 Run 或每次步驟執行時建立。

## 狀態層級與流程影響

判定由外往內進行：App Server turn → AI 任務回覆 → 驗收資料 → C# 驗收決策 → Workflow 結果。
外層失敗會先停止，不再採用內層結果；外層正常完成，也不代表產品驗收通過。
`OutputSchema` 定義任務回覆的外層欄位；驗收步驟會將 `AcceptanceResultSchema` 組進其 `data`，最後以一份 Schema 傳給 AI。
一般步驟未指定 `DataSchema` 時，`data` 為 null；也可以指定自己的結構化產出格式。

| 層級 | 欄位／結果 | 代表意義 | 最後對流程的影響 |
|---|---|---|---|
| ① App Server 執行層 | `turn.Status == "completed"` | 這次模型回合正常結束 | 繼續解析 AI 回覆，不代表任務完成或驗收通過 |
| ① App Server 執行層 | 其他 turn 狀態 | 回合未正常完成 | 視為執行錯誤，停止流程 |
| ② AI 任務層 | `state = "needs_input"` | 需要使用者提供資訊或決策 | 等待回答，在同一個 thread 繼續；此時 `data=null` |
| ② AI 任務層 | `state = "failed"` | AI 表示無法完成任務 | 停止流程，不進入驗收修正重試 |
| ② AI 任務層 | `state = "completed"` | AI 表示這項工作已完成 | 檢查回覆格式；驗收步驟再分析 `data`，一般步驟則檢查接受條件 |
| ② AI 任務層 | `issues` 非空 | AI 回報尚未解決的問題，不是狀態值 | `StopOnIssues=true` 就停止；預設 false，保留問題並繼續判定 |
| ③ 單項驗收層 | `data.checks[].status = "Passed"` | 該項目符合驗收標準 | 該項目不阻擋通過；仍需看其他項目與 Findings |
| ③ 單項驗收層 | `data.checks[].status = "Failed"` | 該項目已確認不符合標準 | 整體判為 `NeedsRevision`，包括非必要項目，不受 Finding 嚴重程度門檻影響 |
| ③ 單項驗收層 | `data.checks[].status = "NotEvaluated"` | 該項目尚未檢查完成或無法判斷 | 若為必要項目，且沒有已知失敗條件，整體判為 `Inconclusive`；非必要項目不阻擋通過 |
| ③ 驗收缺陷層 | `data.findings[].severity` | AI 回報缺陷的嚴重程度 | 達到 `FailureSeverity` 就判為 `NeedsRevision`；預設只有 Error 觸發 |
| ④ C# 整體驗收決策 | `AcceptanceDecision.Passed` | 必要項目均通過，且沒有觸發失敗條件 | 驗收接受，繼續下一步 |
| ④ C# 整體驗收決策 | `AcceptanceDecision.NeedsRevision` | 已確認有需要修正的問題 | `FailureCount + 1`，返回 `RetryStep`；達 `MaxFailures` 就停止，預設第 3 次失敗停止 |
| ④ C# 整體驗收決策 | `AcceptanceDecision.Inconclusive` | 沒有已確認的失敗條件，但不足以判定通過 | `InconclusiveCount + 1`，重跑目前驗收步驟補查；預設最多額外補查 2 次 |
| ⑤ Workflow 結果層 | `Succeeded = true` | 流程正常走到結尾，且各已執行驗收步驟的最後一次結果均通過 | 回傳流程成功，保留所有執行歷程 |
| ⑤ Workflow 結果層 | `Succeeded = false` | 某步驟錯誤、接受條件未過，或達驗收上限 | 停止後續步驟，回傳已執行結果 |

例如，下列各層結果可以同時成立：

```text
turn.Status = completed                 回合正常結束
  state = completed                     驗收工作已完成
    checks[0].status = Failed            發現產品缺陷
      AcceptanceDecision = NeedsRevision
        Workflow 返回 RetryStep（未達失敗上限時）
```

### 分析結論與限制

- AI 負責回報任務 state、issues、單項驗收狀態與缺陷嚴重程度；C# 驗證資料並計算整體驗收決策與流程方向，不會自行判讀規格與實作是否一致。
- `NotEvaluated` 是單一項目狀態，`Inconclusive` 是整體驗收決策，兩者不是一對一。必要項目漏報也會造成 Inconclusive；非必要項目 NotEvaluated 可以不阻擋通過。
- 有可用必要項目清單時，已知缺陷優先於無法判定，會先返回修正。若沒有明確清單且尚未累積任何 Check ID，`Evaluate()` 會先回傳 Inconclusive，即使 Findings 有缺陷也不會進入 `Decide()`。
- 未指定 `RequiredCriterionIds` 時，只能追蹤本次 Run 已回報的項目，無法發現從未回報的規格項目。每輪都需重新提供必要項目的狀態與證據。
- JSON 格式不合法、驗收資料不完整、自訂驗證失敗、逾時或超過問答次數，會直接停止，不當作 NeedsRevision 返回修正。呼叫端取消則拋出取消例外；設定驗證等 Workflow 層級錯誤也會向呼叫端拋出。
- `StopOnIssues=true` 的停止條件優先於驗收重試，即使已有驗收決策，也不會進入修正／補查分支或增加對應次數。issues 由 AI 填寫，不會從所有工具錯誤自動產生。
- 失敗次數與無法判定次數分開，各驗收步驟在同一次 Run 中獨立累計，途中通過不清零；下次 Run 重新計算。
- 一般步驟設定 `StopOnIssues=false` 時，可能同時出現 `Accepted=true`、Step `Succeeded=false`；流程依接受條件繼續，不依 Step 的 Succeeded 控制。
- Workflow 回傳成功前會檢查各已執行驗收步驟的最後一次結果。若 `RetryStep` 或 `NextStep` 繞過重新驗收，仍有未通過的驗收紀錄，會停止並回傳 Succeeded=false，於 StopReason 列出未通過的驗收步驟。
- Schema 與資料驗證不保證證據真實或報表存在；重要產出仍需透過 `ValidateCompletionAsync` 實際檢查。

### 對應程式位置

| 判定內容 | 程式位置 |
|---|---|
| 外層 OutputSchema、任務狀態指令、turn 狀態與 AI 回覆解析 | [CodexTaskRunner.cs](../CodexTaskRunner.cs)：`OutputSchema`、`Instructions`、`RunCoreAsync()` |
| 將驗收或自訂資料 Schema 組進外層 data | [CodexTaskRunner.cs](../CodexTaskRunner.cs)：`CreateOutputSchema()` |
| 驗收資料 Schema 與狀態描述 | [AcceptanceResultSchema.cs](../../Acceptance/AcceptanceResultSchema.cs)：`Value` |
| Checks、Findings 資料完整性與整體驗收決策 | [AcceptanceResult.cs](../../Acceptance/AcceptanceResult.cs)：`Validate()`、`Decide()` |
| 自動指定驗收 Schema、必要項目來源與無項目時的判定 | [AppServerAcceptanceStep.cs](AppServerAcceptanceStep.cs)：建構式、`Evaluate()` |
| 執行錯誤、逾時與自訂完成驗證 | [WorkflowStepExecutor.cs](Internal/WorkflowStepExecutor.cs)：`ExecuteAsync()` |
| 問答次數與輸入逾時 | [WorkflowStepInput.cs](Internal/WorkflowStepInput.cs)：`AnswerAsync()` |
| 驗收次數與 Accepted | [WorkflowAcceptanceTracker.cs](Internal/WorkflowAcceptanceTracker.cs)：`Record()` |
| 接受條件、補查、返回修正、結尾驗收檢查與 Workflow 成功／停止 | [AppServerWorkflow.cs](AppServerWorkflow.cs)：`RunAsync()`、`RunStepsAsync()` |
| Completed、HasIssues 與 Step Succeeded 定義 | [AppServerWorkflowStepResult.cs](AppServerWorkflowStepResult.cs) |

## 執行前流程摘要

完成 Add／AddRange 後、呼叫 RunAsync 前，可輸出目前設定：

```csharp
new ConsoleWorkflowPlanReporter().Report(workflow);
var result = await workflow.RunAsync();
new ConsoleWorkflowResultReporter().Report(result);
```

摘要依宣告順序列出步驟名稱、Model、Sandbox、推理強度、沙箱網路選項與 StopOnIssues。
有設定時另外顯示共用 Thread、決策記錄／讀取群組、動態分流，以及每個驗收步驟的失敗門檻、失敗上限、額外補查次數、返回目標與明確必要項目。
未指定 Model／推理強度會標示沿用設定，不推測實際模型；WorkspaceWrite 未指定 NetworkAccess 時會標示禁止（預設）。

`workflow.WorkspaceRoot` 提供絕對工作區路徑，`workflow.Steps` 每次取得目前加入順序的唯讀清單快照；後續 Add 不改變先前快照。
摘要不會執行 PromptFactory、NextStep、AI 任務或設定驗證；實際路徑仍可能因驗收重試與動態分流而改變。
設定驗證維持在 RunAsync 中；後續若調整了步驟，需重新呼叫 Reporter 才能顯示最新摘要。
Program 的測試範例已在執行前呼叫此 Reporter；其他呼叫端可以自行選擇是否顯示。

## 驗收 Step

驗收使用 `AppServerAcceptanceStep`，直接加入原本的 Workflow：

```csharp
var acceptance = new AppServerAcceptanceStep("Acceptance", "使用驗收 Skill 檢查 Spec 與實作")
{
    FailureSeverity = FindingSeverity.Error,
    MaxFailures = 3,
    MaxInconclusiveRetries = 2,
    RetryStep = "Implement"
};

workflow.Add(implementStep).Add(acceptance).Add(styleStep);
```

只需要設定失敗標準、失敗次數上限與返回步驟，不需要自行設定 DataSchema 或 NextStep。
此型別繼承 AppServerWorkflowStep，可使用相同的 Options、Thread、逾時、PromptFactory 與驗證 callback。
需要寫入報表時，和其他步驟一樣在 Options 明確設定 WorkspaceWrite。

- `FailureSeverity` 預設 Error；嚴重程度由高至低為 Error、Warning、Info。設為 Warning 時，Error 與 Warning 都算失敗。
- `MaxFailures` 預設 3，第三次失敗即停止，不再返回修正；第三次驗收若通過，仍可繼續。
- `MaxInconclusiveRetries` 預設 2；必要項目無法判定時，只重跑驗收步驟補查，最多額外兩次。設為 0 則立即停止。
  補查使用該步驟原有的 Thread 設定，保留原驗收指令，附上前輪完整結果、問題及證據；不返回實作步驟。
  補查額度與產品缺陷次數分開，各驗收步驟在同一次 Run 中獨立累計，不因中途修正或通過而重設。
- `RetryStep` 必須指定已加入 Workflow 的步驟名稱，區分大小寫。
- 各驗收 Step 在同一次 Run 中獨立累計失敗次數，通過不清零；每次 RunAsync 重新計算。
- `RequiredCriterionIds` 是選填的必要項目清單，用來發現漏查。清單會自動加入驗收 Prompt。
  清單外的 NotEvaluated 保留為限制，不阻擋流程；已證實的 Failed 或達到失敗等級的問題仍會返回修正。
  未提供時，在同次 Run 內累積各輪回報的 Check ID 作為必要清單，補查或修正後不能漏報；各驗收步驟獨立保存，下次 Run 重設。
  每輪 Prompt 會附上已知的必要清單，並要求重新提供當輪狀態與證據，不沿用之前的 Passed。
  仍無法發現從第一輪起就未回報的 Spec 項目；需完整性保證時請明確提供 RequiredCriterionIds。

驗收 Step 自動使用 AcceptanceResultSchema，依驗收資料判斷：

| 結果 | 行為 |
|---|---|
| 所有必要項目完成，沒有達到失敗等級的問題 | 執行加入順序中的下一步 |
| 有達到失敗等級的問題，或 Check 為 Failed | 優先累計一次失敗，未達上限時返回 RetryStep；保留未判定項目 |
| 達到 MaxFailures | 停止 Workflow，保留最後結果與次數 |
| 無已知缺陷，但必要項目 NotEvaluated、缺少必要項目或空白的 Checks 清單 | 以 Inconclusive 補查驗收，超過補查上限才停止，不增加產品失敗次數 |
| 僅非必要項目 NotEvaluated，必要項目通過且無已知缺陷 | 保留未判定項目，繼續下一步 |
| 任務回報 failed、格式錯誤或驗證 callback 失敗 | 停止，不當成產品缺陷反覆修正 |

Failed 的驗收項目代表必要條件不符合，即使 Findings 沒有 Error，也會判定失敗。
缺少證據、重複 ID 等無效資料會停止；C# 不會重新判讀規格與程式碼是否一致。
驗收正常執行完成時，Skill 的外層 state 仍應為 completed，產品缺陷放在 data.findings。
驗收 Step 預設 StopOnIssues=false，外層 issues 保留為問題資訊，不再重複決定是否退回或停止。
任務正常完成且資料有效時，流程方向由 Checks、Findings 與 FailureSeverity 決定；
即使 AI 將產品缺陷重複列在 issues，也會正常累計驗收失敗並退回修正。
若需要「issues 非空就停止」的額外條件，可以明確設定 StopOnIssues=true。
真正無法完成任務應使用 state=failed，無法完成驗收項目則使用 NotEvaluated；
重要產出（例如報表或測試結果）仍應透過 ValidateCompletionAsync 實際檢查。

返回 RetryStep 時，Workflow 自動將完整驗收資料（包含問題、證據、報表路徑）附加到目標步驟 Prompt。
報表內容與產出位置仍由 Skill 指令決定；Workflow 不會自動儲存 JSON、檢查報表檔案或重新執行測試。
可使用 ValidateCompletionAsync 加上這些專案檢查。DataSchema 必須保持預設的驗收 Schema，
驗收 Step 也不能同時設定 NextStep，避免與自動分流衝突。

`result.Steps` 保留每次執行；透過各結果的 AcceptanceDecision、FailureCount、InconclusiveCount 查閱驗收狀態。
InconclusiveCount 為累計回傳無法判定的次數；例如補查上限為 2，第三次仍無法判定時停止。
驗收不通過但已正常執行時，Completed=true、Accepted=false、Succeeded=false、Error=null。
重跑後通過可使整個 Workflow 成功，先前失敗的驗收紀錄仍保留。

## 一般步驟分流

一般 AppServerWorkflowStep 仍可設定 NextStep：回傳名稱就跳轉，回傳 null 就依加入順序繼續。
最後一個步驟回傳 null 便正常結束。NextStep 只在步驟通過驗證及接受條件後執行。
一般步驟不負責驗收失敗計數；需要這個功能時使用 AppServerAcceptanceStep。

Context 包含已執行的完整歷程；GetStep(name) 取得最近一次結果。
PromptFactory 與 NextStep 收到固定快照，後續執行不會改變已保留的 Context。
指定不存在的跳轉目標或 callback 拋出例外時，Workflow 停止並在 Error 保留原因。
整體時間上限可透過會逾時取消的 CancellationToken 控制；各步驟逾時則每次重跑重新計算。

返回 Thread.Create(key) 步驟會建立新 thread 並更新對應；後續 Use(key) 使用最新 thread。
返回 Use(key) 則繼續同一個 thread。Create 必須在宣告順序中先於 Use，
實際執行路徑也必須先執行 Create；跳過必要的 Create 時，Use 會回報錯誤。

## 服務速度（Fast 模式）

App Server 與 Codex exec 共用 `CodexClient.Models.CodexServiceTier`，可逐步設定，與 Model／ReasoningEffort 分開：

```csharp
Options = new AppServerOptions
{
    Model = CodexModels.Gpt61Sol,
    Sandbox = CodexSandboxMode.WorkspaceWrite,
    ReasoningEffort = CodexReasoningEffort.High,
    ServiceTier = CodexServiceTier.Fast
};
```

- `Fast`：請求加速模式；App Server 傳送 `serviceTier="fast"`，exec 傳送 `-c service_tier="fast"`。
- `Standard`：明確使用一般速度，傳送值為 `default`，可將共用 thread 從 Fast 切回一般速度。
- `null`（預設）：不傳送覆寫；App Server 沿用 thread 的服務速度，exec 沿用 Codex 設定。null 不代表關閉 Fast。
- App Server 的 `serviceTier` 也會影響同一 thread 後續 turns；若下一步需要一般速度，請明確設定 Standard。
- `ExecutionOptions.ServiceTier` 使用相同列舉，exec Workflow 複製選項與 session resume 時也會保留。
- 執行前摘要會顯示每步的速度設定。未指定時只標示沿用設定，不宣稱實際速度。

Fast 不會自行降低推理強度，但可能增加額度或點數用量；實際可用性與速度依模型、帳號及服務狀態而定。
本工具只傳送速度偏好，不保證服務端一定採用或達到固定加速倍率。
實作已核對本機 Codex CLI 0.167.1 產生的 App Server Schema；使用其他版本時需確認支援 `turn/start.serviceTier`。
參考：[官方速度說明](https://learn.chatgpt.com/docs/agent-configuration/speed)、[Codex 設定參考](https://developers.openai.com/codex/config-reference)。

## 推理強度

App Server 與 Exec 共用 `CodexClient.CodexReasoningEffort` 和 `CodexClient.CodexSandboxMode`，
呼叫端可透過 `using CodexClient;` 引用。推理強度設定方式：

```csharp
Options = new AppServerOptions
{
    ReasoningEffort = CodexReasoningEffort.High
}
```

可設定 Minimal、Low、Medium、High、XHigh；實際支援程度依模型而定。
未指定（null）時不傳送 effort，沿用 thread 設定；無效的 enum 值會在送出前被拒絕。

## 網路權限

每個步驟可透過 `Options.NetworkAccess` 設定工作區沙箱的網路權限：

```csharp
Options = new AppServerOptions
{
    Sandbox = CodexSandboxMode.WorkspaceWrite,
    NetworkAccess = true
}
```

`true` 允許連網，`false` 禁止連網。App Server 未指定（`null`）時維持禁止連網。
Exec 的 `ExecutionOptions.NetworkAccess` 使用相同的選項名稱，但未指定時沿用 Codex 設定，維持原有行為。
兩者只有在 `WorkspaceWrite` 模式下支援明確指定；其他模式指定 `true` 或 `false` 都會拋出 `NotSupportedException`。
Workflow 會逐步傳遞選項；App Server 即使共用 thread，下一步仍會重新指定自己的權限。
這個設定控制沙箱中的命令連網，不是 Web 搜尋或所有外部工具的總開關；實際權限仍受執行環境限制。

## 接受條件

最終步驟時間線會在狀態後顯示每次執行的 `Duration`，例如 `[通過] | 耗時2分15秒`。
重跑紀錄分開列出，執行失敗也顯示該次已花費的時間；這不是整個 Workflow 的累計時間。

最終 `ConsoleWorkflowResultReporter` 會在流程未成功時，於步驟歷程前顯示停止步驟與原因：

```text
執行結果：失敗
停止步驟：Acceptance
停止原因：必要驗收項目仍無法判定，已達補查上限（2 次）。
```

`result.StopReason` 保留執行／驗證失敗、未解決 issues、驗收失敗達上限、補查達上限、分流失敗或結尾驗收檢查未通過的原因。
成功時為 null；停止原因獨立於 `result.Error`，因此補查達上限或 StopOnIssues 停止即使 Error 為 null，仍有可讀說明。
Reporter 不自行推算重試上限；手動建立結果未提供 StopReason 時，退回 Error.Message，兩者皆無則顯示「未提供停止原因」。
外部取消與流程設定錯誤仍依原有方式拋出例外，不會因此改為回傳結果。

結果 Reporter 會在步驟歷程後、需求決策前，以橘色列出未解決問題並標示來源：

```text
未解決問題：
[Implement] 測試資料庫無法連線，尚未驗證寫入功能。
```

只取各步驟最後一次執行的外層 `Execution.Issues`，名稱以區分大小寫方式比對；成功與停止的流程都會顯示。
若最後一次執行沒有 issues，便不列出該步驟的舊問題；最後一次執行未取得任務結果時，也不回頭採用舊資料。
有任務結果但後續驗證失敗時，仍列出該次回報的 issues。沒有問題時不顯示此區塊。
這是 AI 最後回報的問題快照，不代表程式重新驗證過問題是否修復；不會合併驗收的 `data.findings`，也不改變流程判定。

驗收退回修正的具體問題，則顯示在每次 `[需修正]` 紀錄下方，即使最後驗收通過也保留：

```text
02  Acceptance  [需修正] | 累計失敗 1 次
    需修正問題：[Error] 結果與規格要求不符。
03  Implement   [通過]
04  Acceptance  [通過]   | 累計失敗 1 次
```

此處顯示該輪 `data.findings` 的嚴重程度與說明；Failed Check 若沒有對應 Finding，會以項目 ID 與證據補充。
這些是當輪驗收歷程，不會因後續通過而移除，也不會列入最後的「未解決問題」區塊。

- `Completed`：取得任務產出，而且選填的 `ValidateCompletionAsync` 已通過。
- `HasIssues`：AI 回報尚未解決的問題；不是所有工具失敗的集合，也不直接代表驗收是否通過。
- Step 的 `Succeeded`：一般步驟為 Completed 且沒有 issues；驗收 Step 為 Completed 且 Accepted。
- `Accepted`：通過這個步驟的流程接受條件；驗收 Step 不通過時為 false。一般步驟 StopOnIssues=false 時允許帶 issues 完成。
- Workflow 的 `Succeeded`：流程正常走到結尾，且各已執行驗收步驟的最後一次結果均通過；歷程可以包含重跑前未通過的驗收結果。

結尾驗收檢查依步驟名稱分組（區分大小寫），只看每個已執行 `AppServerAcceptanceStep` 的最後一次結果。
先前失敗、後來通過可以正常成功；若仍未通過，就回傳 `Succeeded=false`、`Error=null`，
`FailedStep` 指向第一個尚未通過的驗收，`StopReason` 列出所有尚未通過的驗收名稱與決策，並回報 Stopped 而非 Completed。
這項檢查不自動重跑、不增加失敗或補查次數，也不要求因分流而從未執行的驗收必須通過。
它只確認既有驗收結果，不會重新執行測試，亦不會自動判定後續步驟是否使先前的 Passed 失效；需要再次驗證時仍須安排驗收步驟。

重要產出應提供 `ValidateCompletionAsync`，實際檢查檔案、建置或業務條件。
未設定驗證時，`Completed` 仍只是接受 AI 回報的完成狀態。

## 流程名稱

`CodexClient.Models.WorkflowNames` 提供 Spec、Plan、Design、Implement、Build、Test、Acceptance、CodingStyle、CodeReview、Refactor、Documentation 與 Report 常數。
名稱只供識別，不會自動執行 Skill 或改變判定規則；驗收仍需使用 `AppServerAcceptanceStep`。
多組步驟可以使用 `Create(scope, name)` 產生固定名稱；不自動編號，也不保證不同輸入組合必定唯一。

```csharp
string implementName = WorkflowNames.Create("FeatureA", WorkflowNames.Implement);
string acceptanceName = WorkflowNames.Create("FeatureA", WorkflowNames.Acceptance);

var implement = new AppServerWorkflowStep(implementName, "使用實作 Skill")
{
    RecordDecisionsTo = implementName,
    ReadDecisionsFrom = [implementName]
};
var acceptance = new AppServerAcceptanceStep(acceptanceName, "依規格與已確認決策驗收")
{
    RetryStep = implementName,
    ReadDecisionsFrom = [implementName]
};
```

Step 名稱與 `RecordDecisionsTo` 可以相同，兩者用途獨立，不會互相衝突。
同一個 Workflow 的步驟名稱必須唯一；決策群組可由多個步驟共用。
即使群組名稱與步驟名稱相同，也不會自動讀取決策，仍需明確設定 `ReadDecisionsFrom`。
步驟名稱與群組名稱均區分大小寫。

## 問答中的需求決策

```csharp
var implement = new AppServerWorkflowStep("Implement", "使用實作 Skill")
{
    RecordDecisionsTo = "FeatureA",
    ReadDecisionsFrom = ["FeatureA"]
};
var acceptance = new AppServerAcceptanceStep("Acceptance", "依原始 Spec 與已確認決策驗收")
{
    ReadDecisionsFrom = ["FeatureA"],
    RetryStep = "Implement"
};
```

- `RecordDecisionsTo` 未設定時維持原有問答，不額外詢問。設定後，每個回答都會再確認一次：1 明確確認為需求補充或變更，2 不記錄。
- C# 保留原始說明、問題、回答、來源步驟、thread、turn 與確認時間，不由 AI 摘要或自行判定為變更，也不直接修改 Spec。
- `ReadDecisionsFrom` 支援多個群組，區分大小寫；執行前附加該群組本次 Run 的已確認紀錄，重跑與驗收補查也取得最新紀錄。
- 紀錄只修訂其對應事項；相互衝突或語意不明時，模型應提問確認，不自動採用「最後一筆覆蓋全部規格」。
- `result.Decisions` 與 `stepResult.Decisions` 提供已確認紀錄快照，失敗步驟中已確認的紀錄也會保留。下一次 Run 重設，不自動寫檔或跨次恢復。
- 確認仍使用既有的回答委派，`request.IsDecisionConfirmation` 為 true 時，`DecisionGroup` 指出群組，必須回傳單一答案 `"1"` 或 `"2"`。自訂介面應向使用者確認，不能自動沿用原回答。Console handler 已提供選擇與無效輸入重問。
- 確認包含在該輪 `InputTimeout`／整體 `Timeout` 內；`ExecutionTimeout` 仍暫停，`MaxInputRounds` 只計算 AI 提問輪次，不增加確認輪次。
- 群組設定只控制自動附加的內容，不會抹除共用 thread 的對話歷史；若需要對話隔離，應使用不同 thread。

## Console 訊息配色

進度的 `MessageKind` 與結果的 `StopMessageKind` 只提供顯示資訊，原有 Status、心跳條件、Accepted、Succeeded、Error 與重試規則維持原有行為。
首次輸出、同一行刷新與最終報告共用 `ConsoleWorkflowOutput.GetMessageStyle()` 的配色：

| 顯示分類 | 顏色 | 用途 |
|---|---|---|
| Waiting | 深灰色 | 執行中、等待 AI、真正執行驗證 |
| Question | 粉紫色 | 等待使用者回答 |
| Success | 粉綠色 | 沒有 issues 的完成訊息與通過狀態 |
| Warning | 橘色 | 保留 issues、需修正、無法判定、補查、取消，以及因驗收限制而停止的原因 |
| Error | 紅色 | 執行、驗證或分流錯誤 |
| Information | 主控台原色 | 一般資訊 |

驗收無法判定與補查仍沿用 Validating 狀態與原有心跳，但會明確帶 Warning，刷新時也保留橘色。
真正執行驗證時未指定 Warning，維持深灰色。已提供 MessageKind 的訊息不依 StepDuration 或訊息文字猜測顏色。
步驟 Accepted=true 但有 issues 時仍允許流程繼續，原有「通過」標籤維持，顏色則為橘色提醒。
即使達驗收失敗上限時 Error 仍保留原有例外，進度與最後停止原因都以 Warning 顯示；執行或分流錯誤仍用紅色。
最上方的「執行結果：成功／失敗」仍分別以綠色／紅色表示 Succeeded，與停止原因的顯示分類分開。
未提供 MessageKind 的舊進度呼叫端仍依 Status 選色；其中 Validating 且 StepDuration 有值時相容原有橘色顯示，首次與心跳一致。
此耗時判斷只用於未提供分類的舊呼叫方式，明確的 MessageKind 一律優先。
未提供 StopMessageKind 的手動結果仍依 Error 選色。

## Console 事件通知

使用 Console 元件時請加入：

```csharp
using CodexClient.AppServer.Workflows.ConsoleUI;
```

Console 問答介面預設在每批問題與每次需求決策確認時提醒一次，無效輸入重問不再次提醒。
Console 進度介面只在 Workflow 完成、停止、失敗或取消時通知一次；一般步驟完成、驗收重試與等待刷新不通知。

- 傳統 Windows Console 不在前景時，使用 FlashWindowEx 閃爍工作列；回到前景後停止，不搶焦點。
- 不發出音效或 BEL。Windows Terminal 的 pseudoconsole 無法以目前方式取得可見視窗，因此不通知；其他不支援的終端機也靜默略過。
- 不自動修改終端機設定；輸出導向檔案或非互動環境時不通知，通知失敗也不影響執行結果。
- 可分別設定 `new ConsoleWorkflowProgressReporter { EnableNotifications = false }` 與 `new ConsoleWorkflowInputHandler { EnableNotifications = false }` 關閉通知。

## 檢查實際送出的 Prompt 與參數

完整請求使用獨立的 `CodexClient.AppServer.Requests` Log 分類，以 Debug 等級輸出。
Program 的 `logRequests` 預設 false，需要查看時改為 true；一般啟動、執行與釋放 Log 不受此開關影響。
其他呼叫端可依下列方式設定：

```csharp
bool logRequests = false;
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddFilter(CodexAppServerClient.RequestLogCategory, logRequests ? LogLevel.Debug : LogLevel.None);
    builder.AddSimpleConsole();
});

await using var client = new CodexAppServerClient(
    logger: loggerFactory.CreateLogger<CodexAppServerClient>(),
    requestLogger: loggerFactory.CreateLogger(CodexAppServerClient.RequestLogCategory));
```

未提供 requestLogger 時不會輸出完整請求；僅將一般 Log 調成 Debug／Trace 也不會開啟它。
每次送出 `turn/start` 前，`CodexAppServerClient.LogTurnRequest()` 會將內容分成兩部分顯示：

- `Prompt:`：直接輸出最終 input 文字，保留中文與原本換行，不再包進外層 JSON 字串，也不截斷內容。
- `Parameters (excluding input):`：以縮排 JSON 顯示 method 與不含 input 的 params，包括 threadId、outputSchema、Model、推理強度、服務速度與 sandboxPolicy（以當輪實際存在的欄位為準）。

問答資料序列化時保留中文並縮排；需求決策與驗收回饋的 JSON 也保留中文。
JSON 字串本身必要的跳脫仍會保留，不會將使用者原文中的字面值 `\uXXXX` 或 `\n` 自行解碼。
只有 Debug 啟用時才建立顯示用參數 JSON；它排除 input 以避免重複顯示，不會修改實際送出的參數。

這個位置涵蓋 TaskRunner 自動附加的指令、需求決策、驗收回饋與 Schema；問答後續 turn、修正與補查皆各自記錄。
Log 記錄的是送出前的請求內容，不代表服務端已成功接收；不包含傳輸層後續產生的 JSON-RPC id，
也不包含 App Server 自行載入的 AGENTS／Skill、既有 thread 歷史或服務端額外指令。

## 診斷紀錄

`stepResult.Diagnostics` 保留每個 turn 的原始 `item/completed` 與 `error` 事件，
包含已修復的工具失敗；不會因為曾經有工具失敗就自動拒絕任務。
格式錯誤、AI 回報失敗或驗證失敗時，已收到的紀錄仍保留在步驟結果。
執行中的 turn 逾時時，也會保留最近收到的診斷快照。

`taskResult.Diagnostics` 是任務成功回傳時的各輪紀錄。
實際 client 的 `LastTurnDiagnostics` 在失敗或取消後仍可查閱。
stderr 是整個連線最近 16384 個字元的快照，截斷時會加上提示，
可能包含先前 turn 的內容；它不保證與 stdout 事件同步。

## 時間與問答限制

`ConsoleWorkflowProgressReporter` 會在同一行更新等待狀態的累計時間，保留等待開始時的時間與訊息。
等待與其他進度皆使用「累計X分X秒」，例如「累計2分15秒」；不足一分鐘顯示「累計0分15秒」，超過一小時則持續累計分鐘。
每次步驟結束時另外顯示「本次耗時X分X秒」，例如「步驟完成。（本次耗時1分5秒，累計3分5秒）」。
本次耗時包含建立 thread、AI 執行、等待使用者回答與完成驗證；每次重跑或驗收補查獨立計時，結果保存在 `stepResult.Duration`。
成功、需修正、無法判定與執行失敗的步驟結果都會顯示；外部取消仍拋出取消例外，不產生該步驟結果。
更新頻率由 `ProgressInterval` 決定，例如設為 `TimeSpan.FromSeconds(1)` 每秒刷新。
其他 Workflow Console 輸出會先結束等待行，避免覆蓋問題或結果；游標或尺寸改變後，必須重新核對畫面上的等待文字才能覆寫。
視窗太窄時只縮短等待行的中段，以 `...` 標示並盡量保留步驟資訊與末尾耗時；保留一欄避免自動換行。
傳統 Windows 主控台中，使用者捲動到目前輸出游標以外的歷史內容時，同一等待訊息不重畫、不拉回視窗；背景任務與時間計算持續進行。
等待行仍在畫面內時，刷新會保留原本的可見範圍，避免放大視窗回答後再縮小，覆寫游標又將畫面捲至底部；刷新途中若尺寸改變，不套用舊的視窗位置。
捲回目前輸出位置時恢復最新累計時間；切換步驟／階段、需要回答或輸出完成結果時，會將視窗帶回最新訊息。
傳統 Windows 主控台縮放後，由 `WindowsConsoleProgress` 回讀實際緩衝區文字，核對上次輸出的內容；核對成功才以原生清除介面清除那一段並原地刷新。
重新換行產生的填補空白不參與內容比對；不依字寬推算起始列，也不使用 ANSI 清行指令。即使文字剛好填滿行寬，仍可持續刷新。
每次刷新都核對實際游標與內容，因此兩次刷新之間縮小再放回原尺寸，也可恢復原地更新，沒有永久暫停分支。
如果畫面已有其他輸出、無法核對內容，或不支援 Windows 主控台讀取介面，則保留既有文字，在目前位置繼續輸出；不清除未確認的紀錄，也不停止後續刷新。
這只調整等待行顯示，不截短最終結果或輸出導向檔案的內容；終端機不支援游標位置時仍退回逐行輸出。

```csharp
var step = new AppServerWorkflowStep("Implement", "實作需求")
{
    ExecutionTimeout = TimeSpan.FromMinutes(10),
    InputTimeout = TimeSpan.FromMinutes(30),
    MaxInputRounds = 3,
    StopOnIssues = true,
    ValidateCompletionAsync = ValidateOutputAsync
};
```

- `ExecutionTimeout`：建立 thread、AI 執行與驗證的累計額度。等待回答時暫停，回答後沿用剩餘額度。
- `InputTimeout`：每輪等待整批回答的上限，每輪重新計時。
- `MaxInputRounds`：最多允許的問答輪次；0 表示不允許提問。
- 原有的 `Timeout`：整個步驟的總時間上限，包含等待回答，維持原本行為。
- 未指定的限制不設上限；同時設定多種限制時，最先到達的限制會停止步驟。
- 外部 `CancellationToken`：取消整個 Workflow，拋出取消例外，不轉成步驟逾時。

在 AI turn 中逾時或取消，實際 client 會關閉 App Server 程序；後續執行需建立新的 client。
輸入或驗證 callback 應遵守取消權杖。Workflow 可以停止等待，但無法強制終止忽略取消的 callback。
