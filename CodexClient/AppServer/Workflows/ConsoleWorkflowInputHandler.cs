namespace CodexClient.AppServer.Workflows;

public sealed class ConsoleWorkflowInputHandler
{
    /// <summary>每批 AI 問答或需求決策確認提醒一次；無效輸入重問不重複通知。</summary>
    public bool EnableNotifications { get; init; } = true;

    /// <summary>
    /// 在主控台逐題取得 Workflow 所需的使用者回答。
    /// </summary>
    /// <param name="request">使用者輸入要求。</param>
    /// <param name="cancellationToken">用於取消等待輸入的權杖。</param>
    /// <returns>非同步回傳使用者回答集合。</returns>
    public async Task<IReadOnlyList<string>> AnswerAsync(AppServerWorkflowInputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.IsDecisionConfirmation)
        {
            ConsoleWorkflowOutput.WriteLine("");
        }
        else
        {
            ConsoleWorkflowOutput.WriteLine($"[{request.StepName}] 請回答以下問題：", ConsoleColor.Magenta);
            ConsoleWorkflowOutput.WriteLine(request.Message);
        }
        if (EnableNotifications && request.Questions.Count > 0)
        {
            ConsoleWorkflowNotifier.Notify();
        }
        var answers = new List<string>();
        foreach (string question in request.Questions)
        {
            if (request.IsDecisionConfirmation)
            {
                var lines = question.Split('\n', 2);
                ConsoleWorkflowOutput.WriteLine(lines[0].TrimEnd('\r'), ConsoleColor.Magenta);
                if (lines.Length > 1)
                {
                    ConsoleWorkflowOutput.WriteLine(lines[1]);
                }
            }
            else
            {
                ConsoleWorkflowOutput.WriteLine(question);
            }
            string answer;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConsoleWorkflowOutput.Write("請輸入答案：", ConsoleColor.Cyan);
                answer = await Console.In.ReadLineAsync(cancellationToken) ?? throw new OperationCanceledException("使用者輸入已結束。");
                if (request.IsDecisionConfirmation)
                {
                    answer = answer.Trim();
                    if (answer is not ("1" or "2"))
                    {
                        ConsoleWorkflowOutput.WriteLine("請輸入 1（不記錄）或 2（確認記錄）。", ConsoleColor.Magenta);
                    }
                }
            }
            while (string.IsNullOrWhiteSpace(answer) || request.IsDecisionConfirmation && answer is not ("1" or "2"));

            cancellationToken.ThrowIfCancellationRequested();
            answers.Add(answer);
        }

        return answers;
    }
}
