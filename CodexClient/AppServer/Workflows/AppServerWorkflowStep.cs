using CodexClient.Models;

namespace CodexClient.AppServer.Workflows;

public class AppServerWorkflowStep
{
    // Properties
    /// <summary>
    /// 取得步驟名稱。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 取得步驟使用的固定提示文字；使用提示產生器時為 <see langword="null"/>。
    /// </summary>
    public string? Prompt { get; }

    /// <summary>
    /// 取得依工作流程內容產生提示文字的委派；使用固定提示文字時為 <see langword="null"/>。
    /// </summary>
    public Func<AppServerWorkflowContext, string>? PromptFactory { get; }

    /// <summary>
    /// 步驟被接受後決定下一步名稱；null 沿用 Add 順序，可返回先前步驟。
    /// </summary>
    public Func<AppServerWorkflowContext, string?>? NextStep { get; init; }

    /// <summary>
    /// 取得或設定此步驟使用 App Server thread 的方式。
    /// </summary>
    public AppServerThread Thread { get; init; } = AppServerThread.None;

    /// <summary>
    /// 取得或設定此步驟使用的 App Server 執行選項；指定時會取代建構式建立的選項（包含沙箱模式）。
    /// </summary>
    public AppServerOptions Options { get; init; } = new();

    /// <summary>
    /// 整個步驟的時間上限，包含建立 thread、等待回答與驗證；可與其他逾時同時使用。
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// 建立 thread、AI 執行與驗證的累計上限；等待回答時暫停計時。在 turn 中取消會關閉實際 client。
    /// </summary>
    public TimeSpan? ExecutionTimeout { get; init; }

    /// <summary>
    /// 每輪等待使用者回答的上限；未指定則不設上限。
    /// </summary>
    public TimeSpan? InputTimeout { get; init; }

    /// <summary>
    /// 最多允許幾輪問答；0 表示不允許提問，null 表示不限。
    /// </summary>
    public int? MaxInputRounds { get; init; }

    /// <summary>
    /// 選填的結構化產出格式。透過檔案交接的 Skill 可以省略。
    /// </summary>
    public System.Text.Json.JsonElement? DataSchema { get; init; }

    /// <summary>
    /// AI 回報 issues 時是否停止。執行失敗、格式錯誤與驗證失敗一律停止。
    /// </summary>
    public bool StopOnIssues { get; init; }
    /// <summary>
    /// 取得或設定步驟完成後的自訂非同步驗證委派。
    /// </summary>
    public Func<TaskRunResult, CancellationToken, Task>? ValidateCompletionAsync { get; init; }


    // Constructors
    /// <summary>
    /// 以固定提示文字與沙箱模式建立步驟。
    /// </summary>
    /// <param name="name">步驟名稱。</param>
    /// <param name="prompt">要執行的提示文字。</param>
    /// <param name="sandbox">沙箱模式；預設為唯讀。</param>
    public AppServerWorkflowStep(string name, string prompt, CodexSandboxMode sandbox = CodexSandboxMode.ReadOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        (Name, Prompt) = (name, prompt);
        Options = new AppServerOptions { Sandbox = sandbox };
    }

    /// <summary>
    /// 以提示產生器與沙箱模式建立步驟。
    /// </summary>
    /// <param name="name">步驟名稱。</param>
    /// <param name="promptFactory">產生提示文字的委派。</param>
    /// <param name="sandbox">沙箱模式；預設為唯讀。</param>
    public AppServerWorkflowStep(string name, Func<AppServerWorkflowContext, string> promptFactory, CodexSandboxMode sandbox = CodexSandboxMode.ReadOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(promptFactory);

        (Name, PromptFactory) = (name, promptFactory);
        Options = new AppServerOptions { Sandbox = sandbox };
    }


    // Methods
    /// <summary>
    /// 根據目前工作流程內容產生提示文字。
    /// </summary>
    /// <param name="context">目前的工作流程內容。</param>
    /// <returns>固定提示文字或提示產生器的結果。</returns>
    internal virtual string CreatePrompt(AppServerWorkflowContext context)
    {
        string prompt = PromptFactory is null ? Prompt! : PromptFactory(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return prompt;
    }

    /// <summary>
    /// 驗證步驟設定與工作區選項。
    /// </summary>
    /// <param name="workspaceRoot">Workflow 使用的工作區根目錄。</param>
    internal virtual void Validate(string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(Thread);
        ArgumentNullException.ThrowIfNull(Options);

        (Options with { WorkspaceRoot = workspaceRoot }).Validate();

        ValidateTimeout(Timeout, nameof(Timeout));
        ValidateTimeout(ExecutionTimeout, nameof(ExecutionTimeout));
        ValidateTimeout(InputTimeout, nameof(InputTimeout));
        if (MaxInputRounds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInputRounds));
        }

        CodexTaskRunner.ValidateDataSchema(DataSchema);
    }

    /// <summary>
    /// 驗證逾時值是否為正值且可由取消權杖支援。
    /// </summary>
    /// <param name="value">要驗證的逾時值。</param>
    /// <param name="name">參數名稱。</param>
    private static void ValidateTimeout(TimeSpan? value, string name)
    {
        if (value is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
