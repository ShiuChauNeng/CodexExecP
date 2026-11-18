using System.Text.Json;
using CodexClient.Acceptance;
using CodexClient.Models;

namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 依結構化驗收結果自動判斷通過、返回修正或停止；可直接加入 AppServerWorkflow。
/// 預設保留外層 issues 作為問題資訊，不據此停止；可明確設定 StopOnIssues=true 增加停止條件。
/// </summary>
public sealed class AppServerAcceptanceStep : AppServerWorkflowStep
{
    /// <summary>
    /// 此等級以上視為失敗；Error 最嚴重，Info 最輕微。
    /// </summary>
    public FindingSeverity FailureSeverity { get; init; } = FindingSeverity.Error;

    /// <summary>
    /// 每次 Run 內獨立累計；達到此失敗次數便停止，不再返回修正。
    /// </summary>
    public int MaxFailures { get; init; } = 3;

    /// <summary>
    /// 同一次 Run 內，無法判定時最多額外補查的次數；預設 2，設為 0 則立即停止。
    /// 補查只重跑驗收步驟，不計入產品缺陷次數，各驗收步驟獨立累計。
    /// </summary>
    public int MaxInconclusiveRetries { get; init; } = 2;

    /// <summary>
    /// 驗收不通過時要返回的步驟名稱。
    /// </summary>
    public string RetryStep { get; init; } = "";

    /// <summary>
    /// 選填的必要項目清單，用來發現 Skill 漏查；清單外的 NotEvaluated 不阻擋流程。
    /// 未指定時累積本次 Run 歷次回報的項目作為必要清單，無法發現從未回報的項目。
    /// </summary>
    public IReadOnlyList<string> RequiredCriterionIds { get; init; } = [];

    /// <summary>
    /// 以固定提示文字建立驗收步驟。
    /// </summary>
    /// <param name="name">步驟名稱。</param>
    /// <param name="prompt">要執行的提示文字。</param>
    /// <param name="sandbox">沙箱模式；預設為唯讀。</param>
    /// <param name="model">模型 ID；未指定時沿用 Codex 設定。</param>
    public AppServerAcceptanceStep(string name, string prompt, CodexSandboxMode sandbox = CodexSandboxMode.ReadOnly, string? model = null)
        : base(name, prompt, sandbox, model)
    {
        DataSchema = AcceptanceResultSchema.Value;
        StopOnIssues = false;
    }

    /// <summary>
    /// 以依工作流程內容產生提示的委派建立驗收步驟。
    /// </summary>
    /// <param name="name">步驟名稱。</param>
    /// <param name="promptFactory">產生提示文字的委派。</param>
    /// <param name="sandbox">沙箱模式；預設為唯讀。</param>
    /// <param name="model">模型 ID；未指定時沿用 Codex 設定。</param>
    public AppServerAcceptanceStep(string name, Func<AppServerWorkflowContext, string> promptFactory, CodexSandboxMode sandbox = CodexSandboxMode.ReadOnly, string? model = null)
        : base(name, promptFactory, sandbox, model)
    {
        DataSchema = AcceptanceResultSchema.Value;
        StopOnIssues = false;
    }


    // Methods
    /// <summary>
    /// 產生驗收提示並附加驗收回覆規則與必要項目。
    /// </summary>
    /// <param name="context">目前的工作流程內容。</param>
    /// <returns>包含驗收指令的提示文字。</returns>
    internal override string CreatePrompt(AppServerWorkflowContext context)
    {
        string prompt = base.CreatePrompt(context);
        prompt += "\n\n驗收正常執行完成時 state=completed；產品缺陷放在 data.findings。" +
            "所有驗收項目均須回報狀態及證據，無法判斷時使用 NotEvaluated，不可自行當成通過。";

        return prompt;
    }

    /// <summary>
    /// 附加明確指定或本次 Run 累積的必要項目，要求當輪重新回報狀態與證據。
    /// </summary>
    internal string AppendRequiredCriteria(string prompt, IReadOnlyCollection<string> knownCriterionIds)
    {
        IReadOnlyCollection<string> criteria = RequiredCriterionIds.Count > 0 ? RequiredCriterionIds : knownCriterionIds;
        if (criteria.Count > 0)
        {
            prompt += "\n必要驗收項目 ID：" + JsonSerializer.Serialize(criteria) +
                "\n請逐項回報本輪狀態與證據，不可省略，也不可沿用前輪的通過狀態。";
        }
        return prompt;
    }

    /// <summary>
    /// 根據任務產出的結構化資料評估驗收結果。
    /// </summary>
    /// <param name="execution">要評估的任務結果。</param>
    /// <returns>驗收決策。</returns>
    internal AcceptanceDecision Evaluate(TaskRunResult execution, HashSet<string> knownCriterionIds)
    {
        var result = execution.Data!.Value.Deserialize<AcceptanceResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Acceptance data is missing.");

        result.Validate();

        if (RequiredCriterionIds.Count == 0)
        {
            knownCriterionIds.UnionWith(result.Checks.Select(check => check.CriterionId));
        }
        IReadOnlyCollection<string> criteria = RequiredCriterionIds.Count > 0 ? RequiredCriterionIds : knownCriterionIds;

        if (criteria.Count == 0)
        {
            return AcceptanceDecision.Inconclusive;
        }

        return result.Decide(criteria, FailureSeverity);
    }

    /// <summary>
    /// 驗證驗收步驟的設定。
    /// </summary>
    /// <param name="workspaceRoot">工作流程使用的工作區根目錄。</param>
    internal override void Validate(string workspaceRoot)
    {
        base.Validate(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(RetryStep);
        ArgumentNullException.ThrowIfNull(RequiredCriterionIds);
        if (MaxFailures <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFailures));
        }
        if (MaxInconclusiveRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInconclusiveRetries));
        }
        if (!Enum.IsDefined(FailureSeverity))
        {
            throw new ArgumentOutOfRangeException(nameof(FailureSeverity));
        }
        if (RequiredCriterionIds.Any(string.IsNullOrWhiteSpace) ||
            RequiredCriterionIds.Distinct(StringComparer.Ordinal).Count() != RequiredCriterionIds.Count)
        {
            throw new ArgumentException("Required criterion IDs must be nonempty and unique.", nameof(RequiredCriterionIds));
        }
        if (NextStep is not null)
        {
            throw new ArgumentException("Acceptance steps use RetryStep and cannot also configure NextStep.", nameof(NextStep));
        }
        if (DataSchema is not { } schema || !JsonElement.DeepEquals(schema, AcceptanceResultSchema.Value))
        {
            throw new ArgumentException("Acceptance steps must use AcceptanceResultSchema.", nameof(DataSchema));
        }
    }
}
