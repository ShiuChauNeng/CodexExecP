using CodexClient.Acceptance;
using System.Diagnostics;

namespace CodexClient.AppServer.Workflows.Internal;

/// <summary>
/// 執行單一步驟：解析 thread、組合任務、管理執行額度並保留結果與診斷。
/// </summary>
internal sealed class WorkflowStepExecutor
{
    // Fields
    private readonly IAppServerClient _client;
    private readonly CodexTaskRunner _runner;
    private readonly string _workspaceRoot;
    private readonly Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> _answerQuestionsAsync;


    // Constructors
    /// <summary>
    /// 建立單步執行器，設定用戶端、任務執行器、工作區與問答處理。
    /// </summary>
    internal WorkflowStepExecutor(IAppServerClient client, string workspaceRoot,
        Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> answerQuestionsAsync)
    {
        _client = client;
        _runner = new CodexTaskRunner(client);
        _workspaceRoot = workspaceRoot;
        _answerQuestionsAsync = answerQuestionsAsync;
    }


    // Methods
    /// <summary>
    /// 執行單一 Workflow 步驟並處理其 thread 與輸入要求。
    /// </summary>
    /// <param name="step">要執行的步驟。</param>
    /// <param name="context">執行前的步驟結果與具名 thread 快照。</param>
    /// <param name="progress">Workflow 進度範圍。</param>
    /// <param name="cancellationToken">用於取消步驟的權杖。</param>
    /// <param name="knownCriterionIds">本次 Run 中該驗收步驟累積的項目 ID；一般步驟為 null。</param>
    /// <param name="acceptanceFeedback">前次驗收提供給修正或補查步驟的回饋。</param>
    /// <returns>非同步回傳步驟執行結果。</returns>
    internal async Task<AppServerWorkflowStepResult> ExecuteAsync(
        AppServerWorkflowStep step,
        AppServerWorkflowContext context,
        WorkflowProgressScope progress,
        CancellationToken cancellationToken,
        HashSet<string>? knownCriterionIds,
        AppServerWorkflowStepResult? acceptanceFeedback = null)
    {
        var elapsed = Stopwatch.StartNew();
        string? threadId = null;
        TaskRunResult? execution = null;
        AcceptanceDecision? acceptanceDecision = null;
        Exception? error = null;

        using var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (step.Timeout is { } duration)
        {
            stepCancellation.CancelAfter(duration);
        }

        using var budget = new ExecutionTimeBudget(step.ExecutionTimeout, stepCancellation.Token);
        CancellationToken token = budget.Token;
        var input = new WorkflowStepInput(step, _answerQuestionsAsync, progress, budget);
        var diagnostics = new List<AppServerTurnDiagnostics>();
        AppServerTurnDiagnostics? previousDiagnostics = _client.LastTurnDiagnostics;
        try
        {
            threadId = ResolveThreadId(step, context);
            string prompt = WorkflowPromptBuilder.Create(step, context, threadId, knownCriterionIds, acceptanceFeedback);

            progress.Update(AppServerWorkflowStatus.CreatingThread, threadId is not null ? "準備使用既有 thread。" : "正在建立 thread。", step.Name, threadId);
            threadId ??= await _client.StartThreadAsync(_workspaceRoot, token).WaitAsync(token);
            ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

            progress.Update(AppServerWorkflowStatus.Running, "等待 AI 執行結果。", step.Name, threadId);
            execution = await _runner.RunCoreAsync(threadId, prompt,
                input.AnswerAsync, step.Options with { WorkspaceRoot = _workspaceRoot }, step.DataSchema, token, diagnostics.Add);

            if (step is AppServerDiscussionStep)
            {
                while (!step.StopOnIssues || execution.Issues.Count == 0)
                {
                    string question = await input.AskFollowUpAsync(execution, token);
                    if (string.IsNullOrWhiteSpace(question))
                    {
                        break;
                    }
                    execution = await _runner.RunCoreAsync(threadId, question,
                        input.AnswerAsync, step.Options with { WorkspaceRoot = _workspaceRoot }, step.DataSchema, token, diagnostics.Add);
                }
            }

            if (step is AppServerAcceptanceStep acceptance)
            {
                acceptanceDecision = acceptance.Evaluate(execution, knownCriterionIds!);
            }

            if (step.ValidateCompletionAsync is not null)
            {
                progress.Update(AppServerWorkflowStatus.Validating, "AI 已回覆，正在驗證產出。", step.Name, threadId);
                await step.ValidateCompletionAsync(execution, token).WaitAsync(token);
            }

            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Workflow RunAsync 會捕捉此例外並回報 WorkflowStatus.Cancelled。
            throw;
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            string kind = stepCancellation.IsCancellationRequested ? "overall" : "execution";
            error = new TimeoutException($"Step '{step.Name}' exceeded its {kind} time limit.", ex);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        cancellationToken.ThrowIfCancellationRequested();
        AppendLatestDiagnostics(threadId, previousDiagnostics, diagnostics);
        elapsed.Stop();

        return new(step.Name, threadId, execution, error)
        {
            Duration = elapsed.Elapsed,
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray()),
            Decisions = Array.AsReadOnly(input.Decisions.ToArray()),
            AcceptanceDecision = acceptanceDecision
        };
    }

    /// <summary>
    /// 依步驟的 thread 模式解析既有識別碼；需要新建時回傳 null。
    /// </summary>
    private static string? ResolveThreadId(AppServerWorkflowStep step, AppServerWorkflowContext context)
    {
        return step.Thread.Mode switch
        {
            AppServerThreadMode.Use => context.Threads[step.Thread.Key!],
            AppServerThreadMode.GetOrCreate => context.Threads.GetValueOrDefault(step.Thread.Key!),
            AppServerThreadMode.PerStep => context.Steps.LastOrDefault(result => result.Name == step.Name)?.ThreadId,
            _ => null
        };
    }

    /// <summary>
    /// 補入目前 thread 的最新診斷快照，同一 turn 的舊紀錄以新快照取代。
    /// </summary>
    private void AppendLatestDiagnostics(string? threadId, AppServerTurnDiagnostics? previousDiagnostics,
        List<AppServerTurnDiagnostics> diagnostics)
    {
        var latestDiagnostics = _client.LastTurnDiagnostics;
        if (latestDiagnostics is null || latestDiagnostics.ThreadId != threadId || ReferenceEquals(latestDiagnostics, previousDiagnostics))
        {
            return;
        }

        int diagnosticsIndex = diagnostics.FindIndex(item => item.ThreadId == latestDiagnostics.ThreadId && item.TurnId == latestDiagnostics.TurnId);
        if (diagnosticsIndex >= 0)
        {
            diagnostics[diagnosticsIndex] = latestDiagnostics;
            return;
        }
        diagnostics.Add(latestDiagnostics);
    }
}
