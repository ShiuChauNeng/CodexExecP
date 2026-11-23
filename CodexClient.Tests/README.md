# Workflow 回歸檢查

觀察等待行刷新與切換到問答的版面，不呼叫真實 AI，回答會自動填入：

```powershell
dotnet run --project CodexClient.Tests/CodexClient.Tests.csproj --no-restore -- --demo-progress
```

單獨檢查導向輸出仍保留逐行紀錄，且不包含游標控制碼：

```powershell
dotnet run --project CodexClient.Tests/CodexClient.Tests.csproj --no-restore -- --progress-only
```

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

推理強度檢查透過模擬 App Server 驗證各 enum 的字串傳輸、未指定時省略 effort，
以及無效 enum 值會在送出前被拒絕。

服務速度檢查涵蓋 Fast、Standard 與未指定時的參數，App Server 共用 thread、exec Workflow 的 session resume，
以及無效列舉值拒絕；使用模擬程序，不執行真實 AI，也不消耗加速額度。

請求紀錄檢查驗證一般 Log 開到 Debug 時仍預設關閉完整請求、專用記錄器的 Debug 開關、
最終 Prompt／Schema／權限參數的 JSON，以及問答前後每個 turn 的實際指令與回答；不呼叫真實 AI。
也驗證 Prompt 以原文換行顯示、參數 JSON 不重複包含 input，及使用者字面值跳脫文字不被擅自解碼。

驗收契約檢查包含 Error/Warning/Info、缺少必要項目、未完成驗收、資料格式驗證，
以及「驗收步驟正常完成，但產品需要修正」的 Workflow 模擬整合。

分流檢查涵蓋向前或向後跳轉、原地重跑、最新結果、固定 Context 快照、
長流程不受總次數限制、錯誤目標、失敗及驗證的執行順序、Thread 重用與更新，以及外部取消。
預設每個步驟在同次 Run 內重用自己的 thread（包含驗收重試），不同 Run 重新建立；明確指定 New 則每次建立新 thread，Create/Use 的共用行為不變。GetOrCreate(key) 會重用指定鍵值的 thread，不存在時才建立，可搭配 Use(key) 跨步驟延續對話。

驗收 Step 檢查涵蓋自動 Schema、失敗等級門檻、返回修正時的證據傳遞、各關卡獨立的失敗次數、
驗收不明、格式及 callback 錯誤，以及原有步驟選項的保留。
也檢查結尾時不可將尚未通過的驗收判為成功，包含向後續步驟返回、修正後分流繞過驗收、
多個驗收仍未通過的停止原因與進度狀態；從未執行而被分流跳過的驗收不阻擋結尾。
也包含 AI 將產品缺陷重複放入 issues 時仍可退回修正，以及 state=failed 仍必須停止的情境。

決策群組檢查涵蓋逐題明確確認、保留原始問答、指定群組讀取、一般回答不傳遞、重跑更新、
跨 Run 重設、取消與輸入逾時、無效確認不能視為同意，以及 Console 確認選擇。
上述檢查也包含在 `--acceptance-only` 中，不使用真實 AI。
