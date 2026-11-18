namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 表示 Workflow 步驟需要使用者回答的輸入要求。
/// </summary>
/// <param name="StepName">提出要求的步驟名稱。</param>
/// <param name="ThreadId">提出要求的 thread 識別碼。</param>
/// <param name="TurnId">提出要求的 turn 識別碼。</param>
/// <param name="Message">模型提供的問題說明。</param>
/// <param name="Questions">需要使用者回答的問題。</param>
public sealed record AppServerWorkflowInputRequest(
    string StepName,
    string ThreadId,
    string TurnId,
    string Message,
    IReadOnlyList<string> Questions)
{
    /// <summary>
    /// 是否為需求決策確認；為 true 時只有一個問題，回答 "1" 不記錄、"2" 明確確認記錄。
    /// </summary>
    public bool IsDecisionConfirmation { get; init; }

    /// <summary>確認時要記錄的決策群組；一般問答為 null。</summary>
    public string? DecisionGroup { get; init; }
}
