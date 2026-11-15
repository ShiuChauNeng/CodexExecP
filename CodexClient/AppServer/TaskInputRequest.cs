namespace CodexClient.AppServer;

/// <summary>
/// 表示任務需要使用者回答的問題集合。
/// </summary>
/// <param name="ThreadId">目前使用的 thread 識別碼。</param>
/// <param name="TurnId">提出問題的 turn 識別碼。</param>
/// <param name="Message">模型提供的問題說明。</param>
/// <param name="Questions">需要使用者回答的問題。</param>
public sealed record TaskInputRequest(
    string ThreadId,
    string TurnId,
    string Message,
    IReadOnlyList<string> Questions);
