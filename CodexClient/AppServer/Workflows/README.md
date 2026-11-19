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

- `Completed`：取得任務產出，而且選填的 `ValidateCompletionAsync` 已通過。
- `HasIssues`：AI 回報尚未解決的問題；不是所有工具失敗的集合，也不直接代表驗收是否通過。
- Step 的 `Succeeded`：一般步驟為 Completed 且沒有 issues；驗收 Step 為 Completed 且 Accepted。
- `Accepted`：通過這個步驟的流程接受條件；驗收 Step 不通過時為 false。一般步驟 StopOnIssues=false 時允許帶 issues 完成。
- Workflow 的 `Succeeded`：流程正常走到結尾；歷程可以包含重跑前未通過的驗收結果。

重要產出應提供 `ValidateCompletionAsync`，實際檢查檔案、建置或業務條件。
未設定驗證時，`Completed` 仍只是接受 AI 回報的完成狀態。

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

- `RecordDecisionsTo` 未設定時維持原有問答，不額外詢問。設定後，每個回答都會再確認一次：1 不記錄，2 明確確認為需求補充或變更。
- C# 保留原始說明、問題、回答、來源步驟、thread、turn 與確認時間，不由 AI 摘要或自行判定為變更，也不直接修改 Spec。
- `ReadDecisionsFrom` 支援多個群組，區分大小寫；執行前附加該群組本次 Run 的已確認紀錄，重跑與驗收補查也取得最新紀錄。
- 紀錄只修訂其對應事項；相互衝突或語意不明時，模型應提問確認，不自動採用「最後一筆覆蓋全部規格」。
- `result.Decisions` 與 `stepResult.Decisions` 提供已確認紀錄快照，失敗步驟中已確認的紀錄也會保留。下一次 Run 重設，不自動寫檔或跨次恢復。
- 確認仍使用既有的回答委派，`request.IsDecisionConfirmation` 為 true 時，`DecisionGroup` 指出群組，必須回傳單一答案 `"1"` 或 `"2"`。自訂介面應向使用者確認，不能自動沿用原回答。Console handler 已提供選擇與無效輸入重問。
- 確認包含在該輪 `InputTimeout`／整體 `Timeout` 內；`ExecutionTimeout` 仍暫停，`MaxInputRounds` 只計算 AI 提問輪次，不增加確認輪次。
- 群組設定只控制自動附加的內容，不會抹除共用 thread 的對話歷史；若需要對話隔離，應使用不同 thread。

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

`ConsoleWorkflowProgressReporter` 會在同一行更新等待狀態的累計秒數，保留等待開始時的時間與訊息。
更新頻率由 `ProgressInterval` 決定，例如設為 `TimeSpan.FromSeconds(1)` 每秒刷新。
其他 Workflow Console 輸出會先結束等待行，避免覆蓋問題或結果；偵測到游標已移動時也不回頭覆寫。
輸出導向檔案、終端機不支援游標位置，或文字因視窗太窄而換行時，退回逐行輸出。

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
