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
    /// 要討論的已完成 Workflow 結果；指定時使用其整體判定與歷程，未指定則使用目前流程的先前步驟。
    /// </summary>
    public AppServerWorkflowResult? SourceResult { get; init; }

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
        var jsonOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = true
        };
        if (SourceResult is { } source)
        {
            prompt += "\n\n要討論的 Workflow 整體結果：\n" + JsonSerializer.Serialize(new
            {
                source.Succeeded,
                source.FailedStep,
                source.StopReason,
                Error = source.Error?.Message,
                source.Decisions
            }, jsonOptions);
        }
        var history = (SourceResult?.Steps ?? context.Steps).Select((step, index) => new
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
            "\n\n要討論的 Workflow 已執行紀錄：\n" + JsonSerializer.Serialize(history, jsonOptions);
    }
}
