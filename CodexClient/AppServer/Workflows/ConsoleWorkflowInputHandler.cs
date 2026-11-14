namespace CodexClient.AppServer.Workflows;

/// <summary>共用的 Console 問答處理，可將 AnswerAsync 傳給多個 Workflow。</summary>
public sealed class ConsoleWorkflowInputHandler
{
    public async Task<IReadOnlyList<string>> AnswerAsync(AppServerWorkflowInputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Console.WriteLine($"[{request.StepName}] {request.Message}");
        var answers = new List<string>();
        foreach (string question in request.Questions)
        {
            Console.WriteLine(question);
            string answer;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.Write("請輸入答案：");
                answer = await Console.In.ReadLineAsync(cancellationToken) ?? throw new OperationCanceledException("使用者輸入已結束。");
            }
            while (string.IsNullOrWhiteSpace(answer));

            cancellationToken.ThrowIfCancellationRequested();
            answers.Add(answer);
        }

        return answers;
    }
}
