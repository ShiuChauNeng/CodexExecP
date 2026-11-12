namespace CodexClient.AppServer;

public sealed record UserInputOption(
    string Label,
    string Description);

public sealed record UserInputQuestion(
    string Id,
    string Header,
    string Question,
    IReadOnlyList<UserInputOption>? Options,
    bool IsOther = false,
    bool IsSecret = false);

public sealed record UserInputRequest(
    string ThreadId,
    string TurnId,
    string ItemId,
    IReadOnlyList<UserInputQuestion> Questions);