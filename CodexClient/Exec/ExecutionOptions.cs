namespace CodexClient.Exec;

public sealed class ExecutionOptions
{
    // Properties
    /// <summary>
    /// 要繼續執行的 Codex 工作階段識別碼；未指定時建立新的工作階段。
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// 要使用的 Codex 模型名稱。
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// 模型推理強度；未指定時使用 Codex 預設值。
    /// </summary>
    public CodexReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    /// Codex 可使用的沙箱模式。
    /// </summary>
    public CodexSandboxMode Sandbox { get; init; } = CodexSandboxMode.ReadOnly;

    /// <summary>
    /// 輸出結構描述檔案的路徑。
    /// </summary>
    public string? OutputSchemaPath { get; init; }

    /// <summary>
    /// 是否略過 Git 儲存庫檢查。
    /// </summary>
    public bool SkipGitRepoCheck { get; init; } = true;


    // Methods
    public void Validate()
    {
        if (SessionId is not null && string.IsNullOrWhiteSpace(SessionId))
        {
            throw new ArgumentException("SessionId cannot be empty.", nameof(SessionId));
        }

        if (Model is not null && string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("Model cannot be empty.", nameof(Model));
        }

        if (!Enum.IsDefined(Sandbox))
        {
            throw new ArgumentOutOfRangeException(nameof(Sandbox));
        }

        if (ReasoningEffort is { } effort && !Enum.IsDefined(effort))
        {
            throw new ArgumentOutOfRangeException(nameof(ReasoningEffort));
        }

        if (OutputSchemaPath is not null)
        {
            if (string.IsNullOrWhiteSpace(OutputSchemaPath))
            {
                throw new ArgumentException("OutputSchemaPath cannot be empty.", nameof(OutputSchemaPath));
            }

            string fullPath = Path.GetFullPath(OutputSchemaPath);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Codex output schema not found.",
                    fullPath);
            }
        }

        if (SessionId is not null && OutputSchemaPath is not null)
        {
            throw new NotSupportedException("The current Runner does not support OutputSchemaPath with Session Resume.");
        }
    }
}

public enum CodexReasoningEffort
{
    Minimal,
    Low,
    Medium,
    High,
    XHigh
}

public enum CodexSandboxMode
{
    ReadOnly,
    WorkspaceWrite,
    DangerFullAccess
}
