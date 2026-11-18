using CodexClient.Models;
using System.Text.Json;

namespace CodexClient.AppServer;

public sealed record AppServerOptions
{
    // Properties
    /// <summary>
    /// 要使用的模型 ID。未指定時沿用 Codex 設定。
    /// <para>GPT-5.6 系列：</para>
    /// <list type="bullet">
    /// <item><description>gpt-5.6-luna：低成本、大量簡單任務。</description></item>
    /// <item><description>gpt-5.6-terra：平衡能力與成本。</description></item>
    /// <item><description>gpt-5.6-sol：複雜專業工作與程式開發。</description></item>
    /// </list>
    /// <para>GPT-6 系列：</para>
    /// <list type="bullet">
    /// <item><description>gpt-6-luna：高效率、明確且重複的任務。</description></item>
    /// <item><description>gpt-6-sol：程式開發與多步驟工作流程。</description></item>
    /// <item><description>gpt-6-astra：複雜推理、程式開發與審查。</description></item>
    /// </list>
    /// <para>GPT-6.1 系列：</para>
    /// <list type="bullet">
    /// <item><description>gpt-6.1-sol：一般開發及持續的多步驟工作。</description></item>
    /// <item><description>gpt-6.1-terra：快速文字處理、視覺理解與電腦操作。</description></item>
    /// </list>
    /// <para>
    /// </para>
    /// </summary>
    /// <seealso cref="CodexModels"/>
    public string? Model { get; init; }

    /// <summary>
    /// 要使用的推理強度；未指定時沿用 thread 設定。實際支援程度依模型而定。
    /// </summary>
    public CodexReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    /// App Server 使用的沙箱模式。
    /// </summary>
    public CodexSandboxMode Sandbox { get; init; } = CodexSandboxMode.ReadOnly;

    /// <summary>
    /// WorkspaceWrite 的網路權限；null 維持原有的禁止連網行為。
    /// </summary>
    public bool? NetworkAccess { get; init; }

    /// <summary>
    /// 寫入範圍；Workflow 會自動帶入自己的工作區。
    /// </summary>
    public string? WorkspaceRoot { get; init; }


    /// <summary>
    /// 驗證 App Server 執行選項是否有效。
    /// </summary>
    /// <exception cref="ArgumentException">選項包含空白或無效文字時擲出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">沙箱模式或推理強度無效時擲出。</exception>
    /// <exception cref="DirectoryNotFoundException">可寫入沙箱未指定有效工作區時擲出。</exception>
    /// <exception cref="NotSupportedException">指定不支援的網路權限組合時擲出。</exception>
    internal void Validate()
    {
        if (Model is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        }

        if (ReasoningEffort is { } effort && !Enum.IsDefined(effort))
        {
            throw new ArgumentOutOfRangeException(nameof(ReasoningEffort));
        }

        if (!Enum.IsDefined(Sandbox))
        {
            throw new ArgumentOutOfRangeException(nameof(Sandbox));
        }

        if (NetworkAccess is not null && Sandbox != CodexSandboxMode.WorkspaceWrite)
        {
            throw new NotSupportedException("NetworkAccess can only be configured with WorkspaceWrite.");
        }

        if (Sandbox == CodexSandboxMode.WorkspaceWrite)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceRoot);
            if (!Directory.Exists(WorkspaceRoot))
            {
                throw new DirectoryNotFoundException(WorkspaceRoot);
            }
        }
    }

    /// <summary>
    /// 建立 turn/start 要使用的 App Server 參數。
    /// </summary>
    /// <param name="threadId">目標工作階段識別碼。</param>
    /// <param name="prompt">要傳送給模型的提示文字。</param>
    /// <param name="outputSchema">選用的結構化輸出描述。</param>
    /// <returns>可傳送給 App Server 的參數字典。</returns>
    internal Dictionary<string, object?> CreateParameters(string threadId, string prompt, JsonElement? outputSchema)
    {
        Validate();

        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["input"] = new[] { new { type = "text", text = prompt } },
            ["sandboxPolicy"] = Sandbox == CodexSandboxMode.ReadOnly
                ? (object)new { type = "readOnly" }
                : new { type = "workspaceWrite", writableRoots = new[] { Path.GetFullPath(WorkspaceRoot!) }, networkAccess = NetworkAccess ?? false }
        };

        if (outputSchema is not null)
        {
            parameters["outputSchema"] = outputSchema;
        }

        if (Model is not null)
        {
            parameters["model"] = Model;
        }

        if (ReasoningEffort is { } effort)
        {
            parameters["effort"] = effort switch
            {
                CodexReasoningEffort.Minimal => "minimal",
                CodexReasoningEffort.Low => "low",
                CodexReasoningEffort.Medium => "medium",
                CodexReasoningEffort.High => "high",
                CodexReasoningEffort.XHigh => "xhigh",
                _ => throw new ArgumentOutOfRangeException(nameof(ReasoningEffort))
            };
        }
        return parameters;
    }
}
