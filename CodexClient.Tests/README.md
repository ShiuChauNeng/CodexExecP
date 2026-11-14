# Workflow 回歸檢查

執行：

```powershell
dotnet run --project CodexClient.Tests/CodexClient.Tests.csproj
```

此專案使用可執行的斷言，不依賴額外測試套件，也不會呼叫真實 AI。
任何檢查失敗都會以非零代碼結束。

測試包含流程接受條件、驗證失敗、跨 turn 的診斷保存、執行額度暫停與累計、
輸入及整體逾時、外部取消、問答輪次限制，以及模擬 App Server 程序的
工具失敗後恢復、stderr 截斷與取消後的診斷保存。
