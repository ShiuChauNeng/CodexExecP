# App Server Workflow 執行規則

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
- Step 的 `Succeeded`：`Completed && !HasIssues`，保留原有定義。
- `Accepted`：通過這個步驟的流程接受條件。`StopOnIssues=false` 時允許帶 issues 完成。
- Workflow 的 `Succeeded`：每個步驟都被接受。

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
