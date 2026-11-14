using System.Text.Json;
using CodexClient.Acceptance;

namespace CodexClient.AppServer.Workflows;

/// <summary>依驗收結果自動判斷通過、返回修正或停止；可直接加入 AppServerWorkflow。</summary>
public sealed class AppServerAcceptanceStep : AppServerWorkflowStep
{
    /// <summary>此等級以上視為失敗；Error 最嚴重，Info 最輕微。</summary>
    public FindingSeverity FailureSeverity { get; init; } = FindingSeverity.Error;
    /// <summary>每次 Run 內獨立累計；達到此失敗次數便停止，不再返回修正。</summary>
    public int MaxFailures { get; init; } = 3;
    public string RetryStep { get; init; } = "";
    /// <summary>選填的必要項目清單，用來發現 Skill 漏查。未指定時只能檢查 Skill 回報的項目。</summary>
    public IReadOnlyList<string> RequiredCriterionIds { get; init; } = [];

    public AppServerAcceptanceStep(string name, string prompt) : base(name, prompt)
    {
        DataSchema = AcceptanceResultSchema.Value;
        StopOnIssues = true;
    }

    public AppServerAcceptanceStep(string name, Func<AppServerWorkflowContext, string> promptFactory)
        : base(name, promptFactory)
    {
        DataSchema = AcceptanceResultSchema.Value;
        StopOnIssues = true;
    }

    internal AcceptanceDecision Evaluate(TaskRunResult execution)
    {
        var result = execution.Data!.Value.Deserialize<AcceptanceResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Acceptance data is missing.");
        result.Validate();
        IReadOnlyCollection<string> criteria = RequiredCriterionIds.Count > 0
            ? RequiredCriterionIds
            : result.Checks.Select(check => check.CriterionId).ToArray();
        if (criteria.Count == 0)
        {
            return AcceptanceDecision.Inconclusive;
        }
        return result.Decide(criteria, FailureSeverity);
    }

    internal override void Validate(string workspaceRoot)
    {
        base.Validate(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(RetryStep);
        ArgumentNullException.ThrowIfNull(RequiredCriterionIds);
        if (MaxFailures <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFailures));
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
