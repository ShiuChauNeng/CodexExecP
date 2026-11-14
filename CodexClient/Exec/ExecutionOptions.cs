namespace CodexClient.Exec;

/// <summary>
/// 定義 Codex 執行時使用的選項。
/// </summary>
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
    /// WorkspaceWrite 的網路權限；null 沿用 Codex 設定，true/false 明確允許或禁止。
    /// </summary>
    public bool? NetworkAccess { get; init; }

    /// <summary>
    /// 輸出結構描述檔案的路徑。
    /// </summary>
    public string? OutputSchemaPath { get; init; }

    /// <summary>
    /// 是否略過 Git 儲存庫檢查。
    /// </summary>
    public bool SkipGitRepoCheck { get; init; } = true;

    /// <summary>
    /// 逾時時間。
    /// </summary>
    public TimeSpan? Timeout { get; init; }


    // Methods
    /// <summary>
    /// 驗證執行選項是否有效且彼此相容。
    /// </summary>
    /// <exception cref="ArgumentException">選項包含空白或無效文字時擲出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">列舉值或逾時設定超出有效範圍時擲出。</exception>
    /// <exception cref="FileNotFoundException">指定的輸出結構描述檔案不存在時擲出。</exception>
    /// <exception cref="NotSupportedException">指定了目前不支援的選項組合時擲出。</exception>
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

        if (NetworkAccess is not null && Sandbox != CodexSandboxMode.WorkspaceWrite)
        {
            throw new NotSupportedException("NetworkAccess can only be configured with WorkspaceWrite.");
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

        if (Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must be positive and supported by CancellationTokenSource.");
        }
    }
}

/// <summary>
/// Codex 模型可使用的推理強度。
/// </summary>
public enum CodexReasoningEffort
{
    /// <summary>
    /// 最小推理強度。
    /// </summary>
    Minimal,

    /// <summary>
    /// 低推理強度。
    /// </summary>
    Low,

    /// <summary>
    /// 中等推理強度。
    /// </summary>
    Medium,

    /// <summary>
    /// 高推理強度。
    /// </summary>
    High,

    /// <summary>
    /// 最高推理強度。
    /// </summary>
    XHigh
}

/// <summary>
/// Codex 可使用的沙箱權限模式。
/// </summary>
public enum CodexSandboxMode
{
    /// <summary>
    /// 僅允許讀取工作區。
    /// </summary>
    ReadOnly,

    /// <summary>
    /// 允許寫入工作區。
    /// </summary>
    WorkspaceWrite,
}
