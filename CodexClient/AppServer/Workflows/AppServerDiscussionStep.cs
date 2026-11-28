using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using CodexClient.Models;

namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 說明前面步驟的結果並持續接受追問；使用者輸入空白問題後才結束步驟。
/// </summary>
public sealed class AppServerDiscussionStep : AppServerWorkflowStep
{
    /// <summary>
    /// 以固定討論指令、沙箱模式與模型建立互動步驟；預設唯讀。
    /// </summary>
    public AppServerDiscussionStep(string name, string prompt, CodexSandboxMode sandbox = CodexSandboxMode.ReadOnly, string? model = null)
        : base(name, prompt, sandbox, model)
    {
    }

    /// <summary>
    /// 以提示產生器、沙箱模式與模型建立互動步驟；預設唯讀。
    /// </summary>
    public AppServerDiscussionStep(string name, Func<AppServerWorkflowContext, string> promptFactory,
        CodexSandboxMode sandbox = CodexSandboxMode.ReadOnly, string? model = null)
        : base(name, promptFactory, sandbox, model)
    {
    }

    /// <summary>
    /// 附加已執行步驟與已確認決策，要求完整回答放在 message，討論結束由使用者控制。
    /// </summary>
    internal override string CreatePrompt(AppServerWorkflowContext context)
    {
        string prompt = base.CreatePrompt(context);
        var history = context.Steps.Select((step, index) => new
        {
            RunOrder = index + 1,
            step.Name,
            step.Accepted,
            AcceptanceDecision = step.AcceptanceDecision?.ToString(),
            Error = step.Error?.Message,
            Message = step.Execution?.Message,
            Issues = step.Execution?.Issues,
            Data = step.Execution?.Data,
            step.Decisions
        });
        return prompt + "\n\n先依上述指令說明目前結果；後續回答使用者的追問，完整回答放在 message。" +
            "每次回答完成使用 state=completed；需要釐清問題時才使用 needs_input。是否繼續討論由使用者決定。" +
            "\n\n本次 Workflow 已執行紀錄：\n" + JsonSerializer.Serialize(history, new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                WriteIndented = true
            });
    }
}
