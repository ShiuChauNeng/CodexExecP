using System.Collections.ObjectModel;
using CodexClient.Acceptance;
using CodexClient.AppServer.Workflows.Internal;

namespace CodexClient.AppServer.Workflows;

/// <summary>
/// 依加入順序或 NextStep 指定的方向執行步驟。呼叫端負責啟動與釋放 client，執行期間應獨占該 client。
/// 每次 RunAsync 建立新的 thread 對應表，不提供跨程序的 workflow 恢復功能。
/// </summary>
public sealed class AppServerWorkflow
{
    // Fields
    private readonly WorkflowStepExecutor _stepExecutor;
    private readonly string _workspaceRoot;
    private readonly IProgress<AppServerWorkflowProgress>? _progress;
    private readonly List<AppServerWorkflowStep> _steps = [];
    private readonly object _sync = new();
    private int _running;

    /// <summary>
    /// 等待 AI 或其他執行階段時的狀態提醒間隔；等待使用者輸入期間不輸出定期提醒。
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 最近一次執行時同步 Report 的錯誤；不影響步驟的執行結果。
    /// </summary>
    public Exception? ProgressError { get; private set; }


    // Constructors
    /// <summary>
    /// 建立工作流程，設定工作區、單步執行器、問答處理與進度回報。
    /// </summary>
    public AppServerWorkflow(
        IAppServerClient client,
        string workspaceRoot,
        Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> answerQuestionsAsync,
        IProgress<AppServerWorkflowProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(answerQuestionsAsync);

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _stepExecutor = new WorkflowStepExecutor(client, _workspaceRoot, answerQuestionsAsync);
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

        lock (_sync)
        {
            _steps.Add(step);
        }
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

        ProgressError = null;
        WorkflowProgressScope? progress = null;
        try
        {
            progress = new(_progress, ProgressInterval);
            progress.Update(AppServerWorkflowStatus.Starting, "開始執行，檢查 Workflow 設定。");
            cancellationToken.ThrowIfCancellationRequested();

            AppServerWorkflowStep[] steps;
            lock (_sync)
            {
                steps = _steps.ToArray();
            }
            ValidatePlan(steps);

            return await RunStepsAsync(steps, progress, cancellationToken);
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
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }

    /// <summary>
    /// 依執行結果決定繼續、補查、返回修正或停止。
    /// </summary>
    private async Task<AppServerWorkflowResult> RunStepsAsync(AppServerWorkflowStep[] steps, WorkflowProgressScope progress, CancellationToken cancellationToken)
    {
        var results = new List<AppServerWorkflowStepResult>();
        var threads = new Dictionary<string, string>(StringComparer.Ordinal);
        var acceptanceTracker = new WorkflowAcceptanceTracker(steps);
        var indices = steps.Select((step, index) => (step.Name, index)).ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.Ordinal);

        int nextIndex = 0;
        AppServerWorkflowStepResult? retryFeedback = null;
        while (nextIndex < steps.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = steps[nextIndex];
            AppServerWorkflowStepResult result = await _stepExecutor.ExecuteAsync(step, CreateContext(results, threads), progress, cancellationToken, acceptanceTracker.GetKnownCriterionIds(step.Name), retryFeedback);
            retryFeedback = null;
            bool taskAccepted = result.Completed && (!step.StopOnIssues || !result.HasIssues);
            result = acceptanceTracker.Record(result, taskAccepted);
            results.Add(result);
            progress.ReportStepResult(result);

            // 如果步驟未被接受，則停止 Workflow 並回傳結果。
            if (!taskAccepted)
            {
                string reason = result.Error is not null
                    ? $"步驟執行或驗證失敗：{result.Error.Message}"
                    : $"步驟有未解決問題：{string.Join("；", result.Execution!.Issues)}";
                progress.Update(AppServerWorkflowStatus.Stopped, $"Workflow 停止於 {step.Name}。", step.Name, result.ThreadId);
                return Finish(false, step.Name, results, threads, result.Error, reason);
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
                    if (result.InconclusiveCount <= acceptance.MaxInconclusiveRetries)
                    {
                        retryFeedback = result;
                        progress.Update(AppServerWorkflowStatus.Validating,
                            $"{step.Name} 尚有必要項目無法判定，補查驗收（第 {result.InconclusiveCount}/{acceptance.MaxInconclusiveRetries} 次）。",
                            step.Name, result.ThreadId);
                        continue;
                    }

                    string reason = $"必要驗收項目仍無法判定，已達補查上限（{acceptance.MaxInconclusiveRetries} 次）。";
                    progress.Update(AppServerWorkflowStatus.Stopped, $"{step.Name}：{reason}", step.Name, result.ThreadId);
                    return Finish(false, step.Name, results, threads, stopReason: reason);
                }

                if (result.AcceptanceDecision == AcceptanceDecision.NeedsRevision)
                {
                    if (result.FailureCount >= acceptance.MaxFailures)
                    {
                        var error = new InvalidOperationException($"Acceptance step '{step.Name}' reached its failure limit ({acceptance.MaxFailures}). Failures: {result.FailureCount}.");
                        string reason = $"驗收不通過，已達失敗上限（{acceptance.MaxFailures} 次）。";
                        progress.Update(AppServerWorkflowStatus.Stopped, $"{step.Name}：{reason}", step.Name, result.ThreadId);
                        return Finish(false, step.Name, results, threads, error, reason);
                    }

                    nextIndex = indices[acceptance.RetryStep];
                    retryFeedback = result;
                    progress.Update(AppServerWorkflowStatus.NeedsRevision, $"{step.Name} 驗收不通過，返回 {acceptance.RetryStep}（累計失敗 {result.FailureCount} 次）。", step.Name, result.ThreadId);
                    continue;
                }
            }

            try
            {
                string? target = step.NextStep?.Invoke(CreateContext(results, threads));
                cancellationToken.ThrowIfCancellationRequested();
                if (target is null)
                {
                    nextIndex++;
                    continue;
                }
                if (!indices.TryGetValue(target, out int targetIndex))
                {
                    throw new InvalidOperationException($"Step '{step.Name}' selected an unknown next step: '{target}'.");
                }
                nextIndex = targetIndex;
                progress.Update(AppServerWorkflowStatus.Running, $"由 {step.Name} 前往 {target}。", step.Name, result.ThreadId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string reason = $"步驟分流失敗：{ex.Message}";
                progress.Update(AppServerWorkflowStatus.Faulted, reason, step.Name, result.ThreadId);
                return Finish(false, step.Name, results, threads, ex, reason);
            }
        }
        progress.Update(AppServerWorkflowStatus.Completed, $"Workflow 已完成，共 {results.Count} 個步驟。");

        return Finish(true, null, results, threads);
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
    /// 驗證工作區、步驟設定、名稱唯一性、thread 宣告順序與驗收返回目標。
    /// </summary>
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
    /// <param name="stopReason">流程停止的原因；成功時省略。</param>
    /// <returns>Workflow 結果。</returns>
    private static AppServerWorkflowResult Finish(bool succeeded, string? failedStep, List<AppServerWorkflowStepResult> results, Dictionary<string, string> threads, Exception? error = null, string? stopReason = null)
        // 回傳後這次執行不再變更 threads，唯讀包裝即可，不需再複製。
        => new(succeeded, failedStep, Array.AsReadOnly(results.ToArray()), new ReadOnlyDictionary<string, string>(threads))
        {
            Error = error,
            StopReason = stopReason,
            Decisions = Array.AsReadOnly(results.SelectMany(result => result.Decisions).ToArray())
        };
}
