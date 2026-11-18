namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 使用者明確確認為需求補充或變更的原始問答；僅在本次 Workflow 執行內自動傳遞。
/// </summary>
/// <param name="Group">決策群組，區分大小寫。</param>
/// <param name="StepName">提出問題的步驟名稱。</param>
/// <param name="ThreadId">來源 thread 識別碼。</param>
/// <param name="TurnId">來源 turn 識別碼。</param>
/// <param name="Message">提出問題時的原始說明。</param>
/// <param name="Question">原始問題。</param>
/// <param name="Answer">使用者原始回答。</param>
/// <param name="ConfirmedAt">確認時間。</param>
public sealed record AppServerWorkflowDecision(string Group, string StepName, string ThreadId,
    string TurnId, string Message, string Question, string Answer, DateTimeOffset ConfirmedAt);
