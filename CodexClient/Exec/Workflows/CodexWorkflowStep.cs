using CodexClient.Exec;

namespace CodexClient.Workflows;

public sealed class CodexWorkflowStep
{
    // Properties
    /// <summary>
    /// 取得步驟名稱。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 取得傳遞給 Codex 的提示內容。
    /// </summary>
    public string Prompt { get; }

    /// <summary>
    /// 取得或設定此步驟使用的工作階段設定。
    /// </summary>
    public Session Session { get; init; } = Session.None;

    /// <summary>
    /// 取得或設定步驟發生問題時是否停止工作流程。
    /// </summary>
    public bool StopOnIssues { get; init; }

    /// <summary>
    /// 取得或設定此步驟使用的 Codex 執行選項。
    /// </summary>
    public ExecutionOptions? Options { get; init; }


    // Constructors
    /// <summary>
    /// 以名稱與固定提示文字建立 exec 工作流程步驟。
    /// </summary>
    public CodexWorkflowStep(string name, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        Name = name;
        Prompt = prompt;
    }
}