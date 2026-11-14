namespace CodexClient.AppServer.Workflows;

public class AppServerWorkflowStep
{
    // Properties
    public string Name { get; }
    public string? Prompt { get; }
    public Func<AppServerWorkflowContext, string>? PromptFactory { get; }
    /// <summary>步驟被接受後決定下一步名稱；null 沿用 Add 順序，可返回先前步驟。</summary>
    public Func<AppServerWorkflowContext, string?>? NextStep { get; init; }
    public AppServerThread Thread { get; init; } = AppServerThread.None;
    public AppServerOptions Options { get; init; } = new();
    /// <summary>整個步驟的時間上限，包含建立 thread、等待回答與驗證；可與其他逾時同時使用。</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>建立 thread、AI 執行與驗證的累計上限；等待回答時暫停計時。在 turn 中取消會關閉實際 client。</summary>
    public TimeSpan? ExecutionTimeout { get; init; }
    /// <summary>每輪等待使用者回答的上限；未指定則不設上限。</summary>
    public TimeSpan? InputTimeout { get; init; }
    /// <summary>最多允許幾輪問答；0 表示不允許提問，null 表示不限。</summary>
    public int? MaxInputRounds { get; init; }
    /// <summary>選填的結構化產出格式。透過檔案交接的 Skill 可以省略。</summary>
    public System.Text.Json.JsonElement? DataSchema { get; init; }
    /// <summary>AI 回報 issues 時是否停止。執行失敗、格式錯誤與驗證失敗一律停止。</summary>
    public bool StopOnIssues { get; init; }
    public Func<TaskRunResult, CancellationToken, Task>? ValidateCompletionAsync { get; init; }


    // Constructors
    public AppServerWorkflowStep(string name, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        (Name, Prompt) = (name, prompt);
    }


    // Methods
    /// <summary>用前面步驟的結果產生提示，可跨獨立 thread 傳遞資料。</summary>
    public AppServerWorkflowStep(string name, Func<AppServerWorkflowContext, string> promptFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(promptFactory);
        (Name, PromptFactory) = (name, promptFactory);
    }

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

    private static void ValidateTimeout(TimeSpan? value, string name)
    {
        if (value is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
