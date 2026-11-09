using CodexClient.Exec;
using System.Text.Json;

namespace CodexController.Workflow;

/// <summary>
/// 執行程式碼分析與結果審查的 Codex 工作流程。
/// </summary>
public sealed class CodexWorkflow
{
    // Fields
    private readonly Func<string, string, string, CancellationToken, Task<ExecutionResult>> _executeAsync;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };


    // Constructors
    public CodexWorkflow(CodexRunner runner) : this((workspace, prompt, schema, token) => runner.ExecuteAsync(workspace, prompt, new ExecutionOptions() { OutputSchemaPath = schema }, token))
    {
    }

    /// <summary>
    /// 使用指定的 Codex 執行委派初始化工作流程。
    /// </summary>
    /// <param name="executeAsync">執行工作區、提示與結構描述的非同步委派。</param>
    public CodexWorkflow(Func<string, string, string, CancellationToken, Task<ExecutionResult>> executeAsync)
    {
        _executeAsync = executeAsync;
    }


    /// <summary>
    /// 執行分析與審查工作流程。
    /// </summary>
    /// <param name="workspaceRoot">要分析的工作區路徑。</param>
    /// <param name="target">要分析的目標名稱。</param>
    /// <param name="cancellationToken">用於取消工作流程的權杖。</param>
    /// <returns>非同步回傳分析與審查結果。</returns>
    public async Task<WorkflowResult> ExecuteAsync(string workspaceRoot, string target, CancellationToken cancellationToken = default)
    {
        string analysisSchema = GetSchemaPath("analysis.schema.json");
        string analysisPrompt = $$"""
            請分析 {{target}}.cs。

            說明主要商業邏輯、相依元件及潛在風險。
            請提供實際程式碼位置作為證據。

            找不到目標時，found 必須為 false。
            不可虛構程式碼證據。
            不要修改任何檔案。
            """;

        ExecutionResult analysisExecution = await _executeAsync(workspaceRoot, analysisPrompt, analysisSchema, cancellationToken);
        EnsureExecutionSucceeded(analysisExecution);
        AnalysisResult analysis = Deserialize<AnalysisResult>(analysisExecution.FinalResponse);
        ValidateAnalysis(analysis, target);
        string analysisJson = JsonSerializer.Serialize(analysis);

        string reviewSchema = GetSchemaPath("review.schema.json");
        string reviewPrompt = $$"""
            請獨立審查以下分析：

            {{analysisJson}}

            檢查商業邏輯、相依關係與程式碼證據。
            評估是否有缺漏、錯誤或未解決的問題。

            如果有重大疑慮，approved 必須為 false。
            請列出建議與未解決的問題。
            不要修改任何檔案。
            """;
        ExecutionResult reviewExecution = await _executeAsync(workspaceRoot, reviewPrompt, reviewSchema, cancellationToken);
        EnsureExecutionSucceeded(reviewExecution);
        ReviewResult review = Deserialize<ReviewResult>(reviewExecution.FinalResponse);
        ValidateReview(review);

        return new WorkflowResult(analysis, review);
    }

    /// <summary>
    /// 驗證程式碼分析結果是否包含必要資料。
    /// </summary>
    /// <param name="analysis">要驗證的分析結果。</param>
    /// <param name="expectedTarget">預期的分析目標名稱。</param>
    public static void ValidateAnalysis(AnalysisResult analysis, string expectedTarget)
    {
        if (!analysis.Found)
        {
            throw new InvalidOperationException("Analysis target was not found.");
        }

        //if (!string.Equals(analysis.Target, expectedTarget, StringComparison.OrdinalIgnoreCase))
        //{
        //    throw new InvalidOperationException("Analysis target does not match.");
        //}

        if (string.IsNullOrWhiteSpace(analysis.Summary) || analysis.Evidence is not { Length: > 0 } || analysis.Evidence.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Analysis is missing summary or evidence.");
        }

        if (analysis.Dependencies is null || analysis.Risks is null)
        {
            throw new InvalidOperationException("Analysis data is incomplete.");
        }
    }

    /// <summary>
    /// 驗證審查結果是否通過且資料完整。
    /// </summary>
    /// <param name="review">要驗證的審查結果。</param>
    public static void ValidateReview(ReviewResult review)
    {
        if (!review.Approved)
        {
            throw new InvalidOperationException("Review was not approved.");
        }

        if (string.IsNullOrWhiteSpace(review.Summary) || review.Concerns is null || review.Recommendations is null)
        {
            throw new InvalidOperationException("Review data is incomplete.");
        }

        if (review.Concerns.Length > 0)
        {
            throw new InvalidOperationException("Review has unresolved concerns.");
        }
    }

    /// <summary>
    /// 確認 Codex 執行已完成，並記錄執行過程中的問題。
    /// </summary>
    /// <param name="result">要檢查的 Codex 執行結果。</param>
    private void EnsureExecutionSucceeded(ExecutionResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        string failures = string.Join(Environment.NewLine, result.Failures.Select(x => $"{x.Kind}: {x.Message}"));

        if (result.Completed)
        {
            //_logger.LogWarning("Codex completed with issues. Outcome: {Outcome}. Failures: {Failures}", result.Outcome, failures);

            return;
        }

        throw new InvalidOperationException($"Codex execution failed. Outcome: {result.Outcome}" + Environment.NewLine + failures);
    }

    /// <summary>
    /// 將 Codex 回覆的 JSON 反序列化為指定型別。
    /// </summary>
    /// <typeparam name="T">反序列化的目標型別。</typeparam>
    /// <param name="json">要反序列化的 JSON 字串。</param>
    /// <returns>反序列化後的物件。</returns>
    private static T Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Codex returned an empty response.");
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException("Codex returned a null JSON result.");
    }

    /// <summary>
    /// 取得工作流程結構描述檔案的完整路徑。
    /// </summary>
    /// <param name="fileName">結構描述檔案名稱。</param>
    /// <returns>結構描述檔案的完整路徑。</returns>
    private static string GetSchemaPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Workflow", "Schemas", fileName);
    }
}