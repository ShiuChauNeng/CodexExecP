using System.Collections.ObjectModel;
using CodexClient.Acceptance;
using System.Text.Json;

namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 依加入順序或 NextStep 指定的方向執行步驟。呼叫端負責啟動與釋放 client，執行期間應獨占該 client。
/// 每次 RunAsync 建立新的 thread 對應表，不提供跨程序的 workflow 恢復功能。
/// </summary>
public sealed class AppServerWorkflow
{
    // Fields
    private readonly IAppServerClient _client;
    private readonly CodexTaskRunner _runner;
    private readonly string _workspaceRoot;
    private readonly IProgress<AppServerWorkflowProgress>? _progress;
    private readonly List<AppServerWorkflowStep> _steps = [];
    private readonly object _sync = new();
    private int _running;

    /// <summary>
    /// 使用者回應設定
    /// </summary>
    private readonly Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> _answerQuestionsAsync;

    /// <summary>
    /// 等待 AI 或其他執行階段時的狀態提醒間隔；等待使用者輸入期間不輸出定期提醒。
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 最近一次執行時同步 Report 的錯誤；不影響步驟的執行結果。
    /// </summary>
    public Exception? ProgressError { get; private set; }


    // Constructors
    public AppServerWorkflow(
        IAppServerClient client,
        string workspaceRoot,
        Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> answerQuestionsAsync,
        IProgress<AppServerWorkflowProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(answerQuestionsAsync);

        _client = client;
        _runner = new CodexTaskRunner(client);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _answerQuestionsAsync = answerQuestionsAsync;
        _progress = progress;
    }


    // Methods
    /// <summary>
    /// 將步驟加入 Workflow。
    /// </summary>
    /// <param name="step">要加入的 Workflow 步驟。</param>
    /// <returns>目前的 Workflow 實例，方便串接呼叫。</returns>
    public AppServerWorkflow Add(AppServerWorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(step.Thread);
        ArgumentNullException.ThrowIfNull(step.Options);

        lock (_sync) _steps.Add(step);
        return this;
    }

    /// <summary>
    /// 將多個步驟依序加入 Workflow。
    /// </summary>
    /// <param name="steps">要加入的 Workflow 步驟集合。</param>
    /// <returns>目前的 Workflow 實例，方便串接呼叫。</returns>
    public AppServerWorkflow AddRange(IEnumerable<AppServerWorkflowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        foreach (var step in steps)
        {
            Add(step);
        }

        return this;
    }

    /// <summary>
    /// 執行目前設定的 Workflow。
    /// </summary>
    /// <param name="cancellationToken">用於取消 Workflow 的權杖。</param>
    /// <returns>非同步回傳 Workflow 執行結果。</returns>
    public async Task<AppServerWorkflowResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("This workflow is already running.");
        }

        // 進度通知工具
        ProgressError = null;
        WorkflowProgressScope? progress = null;
        try
        {
            progress = new(_progress, ProgressInterval);
            progress.Update(AppServerWorkflowStatus.Starting, "開始執行，檢查 Workflow 設定。");
            cancellationToken.ThrowIfCancellationRequested();

            AppServerWorkflowStep[] steps;
            lock (_sync) steps = _steps.ToArray();
            ValidatePlan(steps);

            var results = new List<AppServerWorkflowStepResult>();
            var threads = new Dictionary<string, string>(StringComparer.Ordinal);
            var failures = new Dictionary<string, int>(StringComparer.Ordinal);
            var inconclusiveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var indices = steps.Select((step, index) => (step.Name, index)).ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.Ordinal);

            int nextIndex = 0;
            AppServerWorkflowStepResult? retryFeedback = null;
            while (nextIndex < steps.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = steps[nextIndex];
                AppServerWorkflowStepResult result = await ExecuteStepAsync(step, results, threads, progress, cancellationToken, retryFeedback);
                retryFeedback = null;
                bool taskAccepted = result.Completed && (!step.StopOnIssues || !result.HasIssues);
                int failureCount = failures.GetValueOrDefault(step.Name);
                int inconclusiveCount = inconclusiveCounts.GetValueOrDefault(step.Name);
                // 如果步驟被接受但驗收不通過，則增加失敗計數。
                if (taskAccepted && result.AcceptanceDecision == AcceptanceDecision.NeedsRevision)
                {
                    failures[step.Name] = ++failureCount;
                }
                if (taskAccepted && result.AcceptanceDecision == AcceptanceDecision.Inconclusive)
                {
                    inconclusiveCounts[step.Name] = ++inconclusiveCount;
                }

                // 步驟被接受且驗收通過，才算是最終接受。
                bool accepted = taskAccepted && (result.AcceptanceDecision is null or AcceptanceDecision.Passed);
                result = result with { Accepted = accepted, FailureCount = failureCount, InconclusiveCount = inconclusiveCount };
                results.Add(result);
                ReportStepResult(result, accepted, progress);

                // 如果步驟未被接受，則停止 Workflow 並回傳結果。
                if (!taskAccepted)
                {
                    progress.Update(AppServerWorkflowStatus.Stopped, $"Workflow 停止於 {step.Name}。", step.Name, result.ThreadId);
                    return Finish(false, step.Name, results, threads, result.Error);
                }

                if (step.Thread.Mode is AppServerThreadMode.Create or AppServerThreadMode.GetOrCreate)
                {
                    // 儲存命名 thread；Create 每次更新，GetOrCreate 延續既有對應。
                    threads[step.Thread.Key!] = result.ThreadId!;
                }

                // 無法判定時補查驗收；已知缺陷則返回實作，兩種次數獨立限制。
                if (step is AppServerAcceptanceStep acceptance)
                {
                    if (result.AcceptanceDecision == AcceptanceDecision.Inconclusive)
                    {
                        if (inconclusiveCount <= acceptance.MaxInconclusiveRetries)
                        {
                            retryFeedback = result;
                            progress.Update(AppServerWorkflowStatus.Validating,
                                $"{step.Name} 尚有必要項目無法判定，補查驗收（第 {inconclusiveCount}/{acceptance.MaxInconclusiveRetries} 次）。",
                                step.Name, result.ThreadId);
                            continue;
                        }

                        progress.Update(AppServerWorkflowStatus.Stopped,
                            $"{step.Name} 無法完成驗收，已達補查上限（{acceptance.MaxInconclusiveRetries} 次），需要確認。",
                            step.Name, result.ThreadId);
                        return Finish(false, step.Name, results, threads);
                    }

                    if (result.AcceptanceDecision == AcceptanceDecision.NeedsRevision)
                    {
                        if (failureCount >= acceptance.MaxFailures)
                        {
                            var error = new InvalidOperationException($"Acceptance step '{step.Name}' reached its failure limit ({acceptance.MaxFailures}). Failures: {failureCount}.");
                            progress.Update(AppServerWorkflowStatus.Stopped, error.Message, step.Name, result.ThreadId);
                            return Finish(false, step.Name, results, threads, error);
                        }

                        nextIndex = indices[acceptance.RetryStep];
                        retryFeedback = result;
                        progress.Update(AppServerWorkflowStatus.NeedsRevision, $"{step.Name} 驗收不通過，返回 {acceptance.RetryStep}（累計失敗 {failureCount} 次）。", step.Name, result.ThreadId);
                        continue;
                    }
                }

                int sequentialIndex = nextIndex + 1;
                try
                {
                    string? target = step.NextStep?.Invoke(CreateContext(results, threads));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (target is null)
                    {
                        nextIndex = sequentialIndex;
                    }
                    else
                    {
                        if (!indices.TryGetValue(target, out int targetIndex))
                        {
                            throw new InvalidOperationException($"Step '{step.Name}' selected an unknown next step: '{target}'.");
                        }

                        nextIndex = targetIndex;
                        progress.Update(AppServerWorkflowStatus.Running, $"由 {step.Name} 前往 {target}。", step.Name, result.ThreadId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    progress.Update(AppServerWorkflowStatus.Faulted, $"步驟分流失敗：{ex.Message}", step.Name, result.ThreadId);
                    return Finish(false, step.Name, results, threads, ex);
                }
            }
            progress.Update(AppServerWorkflowStatus.Completed, $"Workflow 已完成，共 {results.Count} 個步驟。");

            return Finish(true, null, results, threads);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.Update(AppServerWorkflowStatus.Cancelled, "Workflow 已取消。");
            throw;
        }
        catch (Exception ex)
        {
            progress?.Update(AppServerWorkflowStatus.Faulted, $"Workflow 發生錯誤：{ex.Message}");
            throw;
        }
        finally
        {
            try
            {
                if (progress is not null)
                {
                    await progress.DisposeAsync();
                    ProgressError = progress.ReportingError;
                }
            }
            finally { Volatile.Write(ref _running, 0); }
        }
    }

    /// <summary>
    /// 執行單一 Workflow 步驟並處理其 thread 與輸入要求。
    /// </summary>
    /// <param name="step">要執行的步驟。</param>
    /// <param name="results">目前已完成的步驟結果。</param>
    /// <param name="threads">目前已建立的 thread 對應表。</param>
    /// <param name="progress">Workflow 進度範圍。</param>
    /// <param name="cancellationToken">用於取消步驟的權杖。</param>
    /// <param name="acceptanceFeedback">前次驗收提供給修正或補查步驟的回饋。</param>
    /// <returns>非同步回傳步驟執行結果。</returns>
    private async Task<AppServerWorkflowStepResult> ExecuteStepAsync(
        AppServerWorkflowStep step,
        List<AppServerWorkflowStepResult> results,
        Dictionary<string, string> threads,
        WorkflowProgressScope progress,
        CancellationToken cancellationToken,
        AppServerWorkflowStepResult? acceptanceFeedback = null)
    {
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
        int inputRounds = 0;
        var diagnostics = new List<AppServerTurnDiagnostics>();
        AppServerTurnDiagnostics? previousDiagnostics = _client.LastTurnDiagnostics;
        try
        {
            threadId = step.Thread.Mode switch
            {
                AppServerThreadMode.Use => threads[step.Thread.Key!],
                AppServerThreadMode.GetOrCreate => threads.GetValueOrDefault(step.Thread.Key!),
                AppServerThreadMode.PerStep => results.LastOrDefault(result => result.Name == step.Name)?.ThreadId,
                _ => null
            };
            // 只有目標步驟已在同一 thread 執行過，才能省略原始指令。
            bool resumeForRevision = acceptanceFeedback?.AcceptanceDecision == AcceptanceDecision.NeedsRevision && threadId is not null &&
                results.Any(result => result.Name == step.Name && result.ThreadId == threadId);
            string prompt = resumeForRevision ? "" : step.CreatePrompt(CreateContext(results, threads));
            if (acceptanceFeedback is not null)
            {
                if (!string.IsNullOrEmpty(prompt))
                {
                    prompt += "\n\n";
                }
                bool recheck = acceptanceFeedback.AcceptanceDecision == AcceptanceDecision.Inconclusive;
                prompt += (recheck
                    ? "前次驗收尚有必要項目無法判定，請依下列結果與原因補查缺漏或 NotEvaluated 的必要項目，保留完整驗收清單、已知缺陷與證據並更新報表。遵守原驗收範圍，不修改實作或放寬必要條件；仍無法判定時保留具體原因，不可當作通過：\n"
                    : "前次驗收不通過，請依下列問題、證據與建議修正，完成後將再次驗收：\n") +
                    JsonSerializer.Serialize(new
                    {
                        acceptanceStep = acceptanceFeedback.Name,
                        failureCount = acceptanceFeedback.FailureCount,
                        inconclusiveCount = acceptanceFeedback.InconclusiveCount,
                        issues = acceptanceFeedback.Execution!.Issues,
                        acceptance = acceptanceFeedback.Execution!.Data
                    });
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

            progress.Update(AppServerWorkflowStatus.CreatingThread, threadId is not null ? "準備使用既有 thread。" : "正在建立 thread。", step.Name, threadId);
            threadId ??= await _client.StartThreadAsync(_workspaceRoot, token).WaitAsync(token);
            ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

            progress.Update(AppServerWorkflowStatus.Running, "等待 AI 執行結果。", step.Name, threadId);
            execution = await _runner.RunCoreAsync(threadId, prompt,
                async (request, ct) =>
                {
                    if (step.MaxInputRounds is { } max && inputRounds >= max)
                    {
                        throw new InvalidOperationException($"Step '{step.Name}' exceeded its input round limit ({max}).");
                    }

                    inputRounds++;
                    budget.Pause();
                    using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (step.InputTimeout is { } inputTimeout)
                    {
                        inputCancellation.CancelAfter(inputTimeout);
                    }
                    try
                    {
                        var answers = await AnswerQuestionsAsync(step.Name, request, progress, inputCancellation.Token).WaitAsync(inputCancellation.Token);
                        inputCancellation.Token.ThrowIfCancellationRequested();
                        budget.Resume();
                        return answers;
                    }
                    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && inputCancellation.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Step '{step.Name}' exceeded its input time limit.", ex);
                    }
                }, step.Options with { WorkspaceRoot = _workspaceRoot }, step.DataSchema, token, diagnostics.Add);

            if (step is AppServerAcceptanceStep acceptance)
            {
                acceptanceDecision = acceptance.Evaluate(execution);
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
        if (_client.LastTurnDiagnostics is { } last && last.ThreadId == threadId && !ReferenceEquals(last, previousDiagnostics))
        {
            int index = diagnostics.FindIndex(item => item.ThreadId == last.ThreadId && item.TurnId == last.TurnId);
            if (index >= 0)
            {
                diagnostics[index] = last;
            }
            else
            {
                diagnostics.Add(last);
            }
        }

        return new(step.Name, threadId, execution, error)
        {
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray()),
            AcceptanceDecision = acceptanceDecision
        };
    }

    // Prompt 與分流 callback 取得固定快照，後續執行不會改變已保留的 Context。
    /// <summary>
    /// 建立供步驟提示使用的工作流程內容。
    /// </summary>
    /// <param name="results">目前已完成的步驟結果。</param>
    /// <param name="threads">目前已建立的 thread 對應表。</param>
    /// <returns>工作流程內容。</returns>
    private static AppServerWorkflowContext CreateContext(List<AppServerWorkflowStepResult> results, Dictionary<string, string> threads)
        => new(Array.AsReadOnly(results.ToArray()),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(threads, StringComparer.Ordinal)));

    /// <summary>
    /// 暫停執行並取得使用者對模型問題的回答。
    /// </summary>
    /// <param name="request">使用者輸入要求。</param>
    /// <param name="cancellationToken">用於取消等待輸入的權杖。</param>
    /// <returns>非同步回傳使用者回答。</returns>
    private async Task<IReadOnlyList<string>> AnswerQuestionsAsync(string stepName, TaskInputRequest request, WorkflowProgressScope progress, CancellationToken cancellationToken)
    {
        progress.Update(AppServerWorkflowStatus.WaitingForInput, $"等待使用者回答，共 {request.Questions.Count} 個問題。", stepName, request.ThreadId);

        // TaskRunner 已負責取消這次等待，這裡只轉接輸入介面與狀態提示。
        var answers = await _answerQuestionsAsync(new(stepName, request.ThreadId, request.TurnId, request.Message, request.Questions), cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        progress.Update(AppServerWorkflowStatus.Running, "已收到回答，繼續等待 AI 執行結果。", stepName, request.ThreadId);

        return answers;
    }

    /// <summary>
    /// 將步驟結果轉換為 Workflow 進度通知。
    /// </summary>
    /// <param name="result">步驟執行結果。</param>
    /// <param name="accepted">步驟是否被接受。</param>
    /// <param name="progress">Workflow 進度範圍。</param>
    private static void ReportStepResult(AppServerWorkflowStepResult result, bool accepted, WorkflowProgressScope progress)
    {
        string message;
        if (result.Error is not null)
        {
            message = $"步驟失敗：{result.Error.Message}";
        }
        else if (!accepted && result.HasIssues)
        {
            message = $"步驟有未解決問題：{string.Join("；", result.Execution!.Issues)}";
        }
        else if (result.AcceptanceDecision == AcceptanceDecision.NeedsRevision)
        {
            message = $"驗收不通過，累計失敗 {result.FailureCount} 次。";
        }
        else if (result.AcceptanceDecision == AcceptanceDecision.Inconclusive)
        {
            message = "驗收尚未完成或無法判斷。";
        }
        else if (result.HasIssues)
        {
            message = $"步驟完成，保留 {result.Execution!.Issues.Count} 個問題。";
        }
        else
        {
            message = "步驟完成。";
        }
        AppServerWorkflowStatus status = accepted ? AppServerWorkflowStatus.StepCompleted
            : result.Error is null && result.AcceptanceDecision == AcceptanceDecision.NeedsRevision
                ? AppServerWorkflowStatus.NeedsRevision
                : result.Error is null && result.AcceptanceDecision == AcceptanceDecision.Inconclusive
                    ? AppServerWorkflowStatus.Validating : AppServerWorkflowStatus.StepFailed;
        progress.Update(status,
            message, result.Name, result.ThreadId);
    }

    private void ValidatePlan(AppServerWorkflowStep[] steps)
    {
        if (!Directory.Exists(_workspaceRoot))
        {
            throw new DirectoryNotFoundException(_workspaceRoot);
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!names.Add(step.Name))
            {
                throw new InvalidOperationException($"Duplicate step name: {step.Name}");
            }

            step.Validate(_workspaceRoot);

            if (step.Thread.Mode == AppServerThreadMode.Create && !keys.Add(step.Thread.Key!))
            {
                throw new InvalidOperationException($"Thread '{step.Thread.Key}' already exists. Step: {step.Name}");
            }

            if (step.Thread.Mode == AppServerThreadMode.GetOrCreate)
            {
                keys.Add(step.Thread.Key!);
            }

            if (step.Thread.Mode == AppServerThreadMode.Use && !keys.Contains(step.Thread.Key!))
            {
                throw new InvalidOperationException($"Thread '{step.Thread.Key}' must be created before step '{step.Name}'.");
            }
        }
        foreach (var acceptance in steps.OfType<AppServerAcceptanceStep>())
        {
            if (!names.Contains(acceptance.RetryStep))
            {
                throw new InvalidOperationException($"Acceptance step '{acceptance.Name}' refers to an unknown retry step: '{acceptance.RetryStep}'.");
            }
        }
    }

    /// <summary>
    /// 建立 Workflow 結束結果。
    /// </summary>
    /// <param name="succeeded">Workflow 是否成功完成。</param>
    /// <param name="failedStep">失敗或停止的步驟名稱。</param>
    /// <param name="results">已執行的步驟結果。</param>
    /// <param name="threads">已建立的 thread 對應表。</param>
    /// <param name="error">導致 Workflow 結束的錯誤。</param>
    /// <returns>Workflow 結果。</returns>
    private static AppServerWorkflowResult Finish(bool succeeded, string? failedStep, List<AppServerWorkflowStepResult> results, Dictionary<string, string> threads, Exception? error = null)
        // 回傳後這次執行不再變更 threads，唯讀包裝即可，不需再複製。
        => new(succeeded, failedStep, Array.AsReadOnly(results.ToArray()), new ReadOnlyDictionary<string, string>(threads))
        {
            Error = error
        };
}
