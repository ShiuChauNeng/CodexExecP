using System.Text.Json;

namespace CodexClient.AppServer;

public enum AppServerSandboxMode { ReadOnly, WorkspaceWrite }

/// <summary>App Server 執行選項。模型與推理強度未指定時沿用 thread 設定；沙箱預設唯讀。</summary>
public sealed record AppServerOptions
{
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public AppServerSandboxMode Sandbox { get; init; } = AppServerSandboxMode.ReadOnly;
    /// <summary>WorkspaceWrite 的網路權限；null 維持原有的禁止連網行為。</summary>
    public bool? NetworkAccess { get; init; }
    /// <summary>寫入範圍；Workflow 會自動帶入自己的工作區。</summary>
    public string? WorkspaceRoot { get; init; }


    // Methods
    internal void Validate()
    {
        if (Model is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        }

        if (ReasoningEffort is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ReasoningEffort);
        }

        if (!Enum.IsDefined(Sandbox))
        {
            throw new ArgumentOutOfRangeException(nameof(Sandbox));
        }

        if (NetworkAccess is not null && Sandbox != AppServerSandboxMode.WorkspaceWrite)
        {
            throw new NotSupportedException("NetworkAccess can only be configured with WorkspaceWrite.");
        }

        if (Sandbox == AppServerSandboxMode.WorkspaceWrite)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceRoot);
            if (!Directory.Exists(WorkspaceRoot))
            {
                throw new DirectoryNotFoundException(WorkspaceRoot);
            }
        }
    }

    internal Dictionary<string, object?> CreateParameters(string threadId, string prompt, JsonElement? outputSchema)
    {
        Validate();

        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["input"] = new[] { new { type = "text", text = prompt } },
            ["sandboxPolicy"] = Sandbox == AppServerSandboxMode.ReadOnly
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

        if (ReasoningEffort is not null)
        {
            parameters["effort"] = ReasoningEffort;
        }
        return parameters;
    }
}
