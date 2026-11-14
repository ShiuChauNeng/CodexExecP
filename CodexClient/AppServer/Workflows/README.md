# App Server Workflow 執行規則

## 驗收 Step

驗收使用 `AppServerAcceptanceStep`，直接加入原本的 Workflow：

```csharp
var acceptance = new AppServerAcceptanceStep("Acceptance", "使用驗收 Skill 檢查 Spec 與實作")
{
    FailureSeverity = FindingSeverity.Error,
    MaxFailures = 3,
    RetryStep = "Implement"
};

workflow.Add(implementStep).Add(acceptance).Add(styleStep);
```

只需要設定失敗標準、失敗次數上限與返回步驟，不需要自行設定 DataSchema 或 NextStep。
此型別繼承 AppServerWorkflowStep，可使用相同的 Options、Thread、逾時、PromptFactory 與驗證 callback。
需要寫入報表時，和其他步驟一樣在 Options 明確設定 WorkspaceWrite。

- `FailureSeverity` 預設 Error；嚴重程度由高至低為 Error、Warning、Info。設為 Warning 時，Error 與 Warning 都算失敗。
- `MaxFailures` 預設 3，第三次失敗即停止，不再返回修正；第三次驗收若通過，仍可繼續。
- `RetryStep` 必須指定已加入 Workflow 的步驟名稱，區分大小寫。
- 各驗收 Step 在同一次 Run 中獨立累計失敗次數，通過不清零；每次 RunAsync 重新計算。
- `RequiredCriterionIds` 是選填的必要項目清單，用來發現漏查。清單會自動加入驗收 Prompt。
  未提供時只能依 Skill 回報的 Checks 判斷，無法知道它是否遺漏 Spec 中的項目。

驗收 Step 自動使用 AcceptanceResultSchema，依驗收資料判斷：

| 結果 | 行為 |
|---|---|
| 所有必要項目完成，沒有達到失敗等級的問題 | 執行加入順序中的下一步 |
| 有達到失敗等級的問題，或 Check 為 Failed | 累計一次失敗，未達上限時返回 RetryStep |
| 達到 MaxFailures | 停止 Workflow，保留最後結果與次數 |
| NotEvaluated、缺少必要項目或空白的 Checks 清單 | 以 Inconclusive 停止，不增加失敗次數 |
| 格式錯誤、任務 issues 或驗證 callback 失敗 | 依一般執行失敗規則停止，不當成產品缺陷反覆修正 |

Failed 的驗收項目代表必要條件不符合，即使 Findings 沒有 Error，也會判定失敗。
缺少證據、重複 ID 等無效資料會停止；C# 不會重新判讀規格與程式碼是否一致。
驗收正常執行完成時，Skill 的外層 state 仍應為 completed，產品缺陷放在 data.findings。
預設 StopOnIssues=true，外層 task issues 與產品缺陷分開處理。

返回 RetryStep 時，Workflow 自動將完整驗收資料（包含問題、證據、報表路徑）附加到目標步驟 Prompt。
報表內容與產出位置仍由 Skill 指令決定；Workflow 不會自動儲存 JSON、檢查報表檔案或重新執行測試。
可使用 ValidateCompletionAsync 加上這些專案檢查。DataSchema 必須保持預設的驗收 Schema，
驗收 Step 也不能同時設定 NextStep，避免與自動分流衝突。

`result.Steps` 保留每次執行；透過各結果的 AcceptanceDecision、FailureCount 查閱驗收狀態。
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

## 網路權限

每個步驟可透過 `Options.NetworkAccess` 設定工作區沙箱的網路權限：

```csharp
Options = new AppServerOptions
{
    Sandbox = AppServerSandboxMode.WorkspaceWrite,
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
- `HasIssues`：AI 回報尚未解決的問題；不是所有工具失敗的集合。
- Step 的 `Succeeded`：任務完成、沒有 task issues，而且驗收 Step 必須通過驗收。
- `Accepted`：通過這個步驟的流程接受條件；驗收 Step 不通過時為 false。一般步驟 StopOnIssues=false 時允許帶 issues 完成。
- Workflow 的 `Succeeded`：流程正常走到結尾；歷程可以包含重跑前未通過的驗收結果。

重要產出應提供 `ValidateCompletionAsync`，實際檢查檔案、建置或業務條件。
未設定驗證時，`Completed` 仍只是接受 AI 回報的完成狀態。

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
