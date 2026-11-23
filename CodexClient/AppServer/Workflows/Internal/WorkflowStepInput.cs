namespace CodexClient.AppServer.Workflows.Internal;

/// <summary>
/// 處理單一步驟的問答額度、輸入時限與使用者明確確認的需求決策。
/// </summary>
internal sealed class WorkflowStepInput
{
    // Fields
    private readonly AppServerWorkflowStep _step;
    private readonly Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> _answerQuestionsAsync;
    private readonly WorkflowProgressScope _progress;
    private readonly ExecutionTimeBudget _budget;
    private readonly List<AppServerWorkflowDecision> _decisions = [];
    private int _inputRounds;

    internal IReadOnlyList<AppServerWorkflowDecision> Decisions => _decisions;


    // Constructors
    /// <summary>
    /// 建立單一步驟的問答協調器，設定進度、執行額度與回答處理。
    /// </summary>
    internal WorkflowStepInput(AppServerWorkflowStep step,
        Func<AppServerWorkflowInputRequest, CancellationToken, Task<IReadOnlyList<string>>> answerQuestionsAsync,
        WorkflowProgressScope progress, ExecutionTimeBudget budget)
    {
        _step = step;
        _answerQuestionsAsync = answerQuestionsAsync;
        _progress = progress;
        _budget = budget;
    }


    // Methods
    /// <summary>
    /// 檢查問答次數並暫停執行額度，限時收集回答與需求決策後恢復計時。
    /// </summary>
    internal async Task<IReadOnlyList<string>> AnswerAsync(TaskInputRequest request, CancellationToken cancellationToken)
    {
        if (_step.MaxInputRounds is { } max && _inputRounds >= max)
        {
            throw new InvalidOperationException($"Step '{_step.Name}' exceeded its input round limit ({max}).");
        }

        _inputRounds++;
        _budget.Pause();
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_step.InputTimeout is { } inputTimeout)
        {
            inputCancellation.CancelAfter(inputTimeout);
        }
        try
        {
            var answers = await CollectAnswersAsync(request, inputCancellation.Token).WaitAsync(inputCancellation.Token);
            inputCancellation.Token.ThrowIfCancellationRequested();
            _budget.Resume();
            return answers;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && inputCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Step '{_step.Name}' exceeded its input time limit.", ex);
        }
    }

    /// <summary>
    /// 暫停執行並取得使用者對模型問題的回答。
    /// </summary>
    /// <param name="request">使用者輸入要求。</param>
    /// <param name="cancellationToken">用於取消等待輸入的權杖。</param>
    /// <returns>非同步回傳使用者回答。</returns>
    private async Task<IReadOnlyList<string>> CollectAnswersAsync(TaskInputRequest request, CancellationToken cancellationToken)
    {
        _progress.Update(AppServerWorkflowStatus.WaitingForInput, $"等待使用者回答，共 {request.Questions.Count} 個問題。", _step.Name, request.ThreadId);

        // 原始問答與決策確認共用輸入時限；只有明確確認的原始問答才記錄。
        var answers = await _answerQuestionsAsync(new(_step.Name, request.ThreadId, request.TurnId, request.Message, request.Questions), cancellationToken).WaitAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (answers is null || answers.Count != request.Questions.Count || answers.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Provide one nonempty answer for each question.");
        }
        var answerSnapshot = answers.ToArray();
        await RecordConfirmedDecisionsAsync(request, answerSnapshot, cancellationToken);

        _progress.Update(AppServerWorkflowStatus.Running, "已收到回答，繼續等待 AI 執行結果。", _step.Name, request.ThreadId);

        return Array.AsReadOnly(answerSnapshot);
    }

    /// <summary>
    /// 逐題詢問是否記錄為需求決策，僅保存使用者明確確認的回答。
    /// </summary>
    private async Task RecordConfirmedDecisionsAsync(TaskInputRequest request, string[] answerSnapshot,
        CancellationToken cancellationToken)
    {
        if (_step.RecordDecisionsTo is not { } group)
        {
            return;
        }
        for (int index = 0; index < request.Questions.Count; index++)
        {
            string confirmationQuestion = request.Questions.Count > 1
                ? $"是否將第 {index + 1} 題的回答記錄為需求補充或變更？"
                : "是否記錄為需求補充或變更？";
            var confirmation = new AppServerWorkflowInputRequest(_step.Name, request.ThreadId, request.TurnId,
                $"確認第 {index + 1}/{request.Questions.Count} 題的回答。",
                [$"{confirmationQuestion}\n1 記錄　2 不記錄"])
            {
                IsDecisionConfirmation = true,
                DecisionGroup = group
            };
            var choice = await _answerQuestionsAsync(confirmation, cancellationToken).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (choice is null || choice.Count != 1 || choice[0] is not ("1" or "2"))
            {
                throw new ArgumentException("Decision confirmation requires one answer: 1 (record) or 2 (skip).");
            }
            if (choice[0] == "1")
            {
                _decisions.Add(new(group, _step.Name, request.ThreadId, request.TurnId,
                    request.Message, request.Questions[index], answerSnapshot[index], DateTimeOffset.UtcNow));
            }
        }
    }
}
