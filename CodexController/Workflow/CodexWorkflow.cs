using CodexController.Codex;
using System.Text.Json;

namespace CodexController.Workflow;

public sealed class CodexWorkflow
{
    // Fields
    private readonly Func<string, string, string, CancellationToken, Task<CodexResult>> _executeAsync;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };


    // Constructors
    public CodexWorkflow(CodexRunner runner) : this((workspace, prompt, schema, token) => runner.ExecuteAsync(workspace, prompt,new CodexExecutionOptions() {OutputSchemaPath = schema }, token))
    {
    }

    public CodexWorkflow(Func<string, string, string, CancellationToken, Task<CodexResult>> executeAsync)
    {
        _executeAsync = executeAsync;
    }


    // Methods
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

        CodexResult analysisExecution = await _executeAsync(workspaceRoot, analysisPrompt, analysisSchema, cancellationToken);
        EnsureCompleted(analysisExecution);
        AnalysisResult analysis = Deserialize<AnalysisResult>(analysisExecution.FinalResponse);
        ValidateAnalysis(analysis, target);
        string reviewSchema = GetSchemaPath("review.schema.json");
        string analysisJson = JsonSerializer.Serialize(analysis);

        string reviewPrompt = $$"""
            請獨立審查以下分析：

            {{analysisJson}}

            檢查商業邏輯、相依關係與程式碼證據。
            評估是否有缺漏、錯誤或未解決的問題。

            如果有重大疑慮，approved 必須為 false。
            請列出建議與未解決的問題。
            不要修改任何檔案。
            """;
        CodexResult reviewExecution = await _executeAsync(workspaceRoot, reviewPrompt, reviewSchema, cancellationToken);
        EnsureCompleted(reviewExecution);
        ReviewResult review = Deserialize<ReviewResult>(reviewExecution.FinalResponse);
        ValidateReview(review);

        return new WorkflowResult(analysis, review);
    }

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

    private static void EnsureCompleted(CodexResult result)
    {
        if (!result.Completed)
        {
            throw new InvalidOperationException("Codex execution did not complete.");
        }
    }

    private static T Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Codex returned an empty response.");
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException("Codex returned a null JSON result.");
    }

    private static string GetSchemaPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Workflow", "Schemas", fileName);
    }
}