using System.Text.Json.Serialization;

namespace CodexClient.Acceptance;

[JsonConverter(typeof(JsonStringEnumConverter<FindingSeverity>))]
public enum FindingSeverity
{
    Error,
    Warning,
    Info
}

[JsonConverter(typeof(JsonStringEnumConverter<AcceptanceCheckStatus>))]
public enum AcceptanceCheckStatus
{
    Passed,
    Failed,
    NotEvaluated
}

public enum AcceptanceDecision
{
    Passed,
    NeedsRevision,
    Inconclusive
}

public sealed record AcceptanceCheck(
    [property: JsonRequired] string CriterionId,
    [property: JsonRequired] AcceptanceCheckStatus Status,
    [property: JsonRequired] string Evidence);

public sealed record AcceptanceFinding(
    [property: JsonRequired] string Id,
    [property: JsonRequired] FindingSeverity Severity,
    [property: JsonRequired] string? CriterionId,
    [property: JsonRequired] string Message,
    [property: JsonRequired] string Evidence,
    [property: JsonRequired] string? SuggestedFix);

/// <summary>Skill 負責驗收判斷；此型別只檢查資料完整性並依結果決定流程方向。</summary>
public sealed record AcceptanceResult(
    [property: JsonRequired] IReadOnlyList<AcceptanceCheck> Checks,
    [property: JsonRequired] IReadOnlyList<AcceptanceFinding> Findings,
    [property: JsonRequired] string ReportPath)
{
    /// <summary>驗證資料格式；驗收不合格或尚未完成，不會因此拋出例外。</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Checks);
        ArgumentNullException.ThrowIfNull(Findings);
        ArgumentException.ThrowIfNullOrWhiteSpace(ReportPath);

        var criteria = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in Checks)
        {
            ArgumentNullException.ThrowIfNull(check);
            ArgumentException.ThrowIfNullOrWhiteSpace(check.CriterionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(check.Evidence);
            if (!Enum.IsDefined(check.Status))
            {
                throw new ArgumentOutOfRangeException(nameof(check.Status));
            }
            if (!criteria.Add(check.CriterionId))
            {
                throw new ArgumentException($"Duplicate acceptance criterion: {check.CriterionId}");
            }
        }

        var findingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in Findings)
        {
            ArgumentNullException.ThrowIfNull(finding);
            ArgumentException.ThrowIfNullOrWhiteSpace(finding.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(finding.Message);
            ArgumentException.ThrowIfNullOrWhiteSpace(finding.Evidence);
            if (!Enum.IsDefined(finding.Severity))
            {
                throw new ArgumentOutOfRangeException(nameof(finding.Severity));
            }
            if (!findingIds.Add(finding.Id))
            {
                throw new ArgumentException($"Duplicate acceptance finding: {finding.Id}");
            }
            if (finding.CriterionId is not null && !criteria.Contains(finding.CriterionId))
            {
                throw new ArgumentException($"Finding '{finding.Id}' refers to an unknown criterion: {finding.CriterionId}");
            }
            if (finding.SuggestedFix is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(finding.SuggestedFix);
            }
        }
    }

    /// <summary>
    /// 必要項目由呼叫端依 Spec 提供；缺項或未完成優先回傳 Inconclusive，避免以「沒有 Error」誤判通過。
    /// 此方法不會重新判讀 Spec、執行測試或確認報表存在。
    /// </summary>
    public AcceptanceDecision Decide(IReadOnlyCollection<string> requiredCriterionIds, FindingSeverity failureSeverity = FindingSeverity.Error)
    {
        ArgumentNullException.ThrowIfNull(requiredCriterionIds);
        if (!Enum.IsDefined(failureSeverity))
        {
            throw new ArgumentOutOfRangeException(nameof(failureSeverity));
        }
        if (requiredCriterionIds.Count == 0 || requiredCriterionIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Provide at least one nonempty required criterion ID.", nameof(requiredCriterionIds));
        }

        Validate();
        var checks = Checks.ToDictionary(check => check.CriterionId, StringComparer.Ordinal);
        if (requiredCriterionIds.Any(id => !checks.ContainsKey(id)))
        {
            return AcceptanceDecision.Inconclusive;
        }
        if (Checks.Any(check => check.Status == AcceptanceCheckStatus.NotEvaluated))
        {
            return AcceptanceDecision.Inconclusive;
        }
        if (Checks.Any(check => check.Status == AcceptanceCheckStatus.Failed) ||
            Findings.Any(finding => failureSeverity switch
            {
                FindingSeverity.Error => finding.Severity == FindingSeverity.Error,
                FindingSeverity.Warning => finding.Severity is FindingSeverity.Error or FindingSeverity.Warning,
                FindingSeverity.Info => true,
                _ => false
            }))
        {
            return AcceptanceDecision.NeedsRevision;
        }

        return AcceptanceDecision.Passed;
    }
}
