# Workflow 回歸檢查

互動觀察驗收 Step 的執行順序，不需 Skill、API Key 或 Codex 服務，也不修改檔案：

```powershell
dotnet run --project CodexClient.Tests/CodexClient.Tests.csproj --no-restore -- --demo-acceptance
```

預設前兩次驗收失敗、第三次只有 Warning，會繼續執行 Style。
觀察第三次仍失敗而停止的情境：

```powershell
dotnet run --project CodexClient.Tests/CodexClient.Tests.csproj --no-restore -- --demo-acceptance --always-fail
```

模擬的 reportPath 僅配合 Schema，不會產生實際報表檔案。

執行：

```powershell
dotnet run --project CodexClient.Tests/CodexClient.Tests.csproj
```

只執行驗收契約、分流與驗收 Step（不包含程序啟動及毫秒級逾時檢查）：

```powershell
dotnet run --project CodexClient.Tests/CodexClient.Tests.csproj -- --acceptance-only
```

此專案使用可執行的斷言，不依賴額外測試套件，也不會呼叫真實 AI。
任何檢查失敗都會以非零代碼結束。

測試包含流程接受條件、驗證失敗、跨 turn 的診斷保存、執行額度暫停與累計、
輸入及整體逾時、外部取消、問答輪次限制，以及模擬 App Server 程序的
工具失敗後恢復、stderr 截斷與取消後的診斷保存。

網路權限檢查使用模擬程序驗證 Exec 的參數、Session Resume 的選項傳遞、
App Server 共用 thread 的逐步權限，以及不支援模式的驗證；不會實際連網。

驗收契約檢查包含 Error/Warning/Info、缺少必要項目、未完成驗收、資料格式驗證，
以及「驗收步驟正常完成，但產品需要修正」的 Workflow 模擬整合。

分流檢查涵蓋向前或向後跳轉、原地重跑、最新結果、固定 Context 快照、
長流程不受總次數限制、錯誤目標、失敗及驗證的執行順序、Thread 重用與更新，以及外部取消。

驗收 Step 檢查涵蓋自動 Schema、失敗等級門檻、返回修正時的證據傳遞、各關卡獨立的失敗次數、
驗收不明、格式及 callback 錯誤，以及原有步驟選項的保留。
