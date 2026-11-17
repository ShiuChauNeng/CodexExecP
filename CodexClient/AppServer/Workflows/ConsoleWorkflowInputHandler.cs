namespace CodexClient.AppServer.Workflows;

public sealed class ConsoleWorkflowInputHandler
{
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

        ConsoleWorkflowOutput.WriteLine($"[{request.StepName}] 請回答以下問題：", ConsoleColor.Cyan);
        ConsoleWorkflowOutput.WriteLine(request.Message);
        var answers = new List<string>();
        foreach (string question in request.Questions)
        {
            ConsoleWorkflowOutput.WriteLine(question);
            string answer;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConsoleWorkflowOutput.Write("請輸入答案：", ConsoleColor.Cyan);
                answer = await Console.In.ReadLineAsync(cancellationToken) ?? throw new OperationCanceledException("使用者輸入已結束。");
            }
            while (string.IsNullOrWhiteSpace(answer));

            cancellationToken.ThrowIfCancellationRequested();
            answers.Add(answer);
        }

        return answers;
    }
}
