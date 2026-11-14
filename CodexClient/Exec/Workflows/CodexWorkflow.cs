
using CodexClient.Exec;
using Microsoft.Extensions.Logging;

namespace CodexClient.Workflows;

public sealed class CodexWorkflow
{
    // Fields
    private readonly CodexExecRunner _runner;
    private readonly ILogger<CodexWorkflow> _logger;
    private readonly string _workspaceRoot;
    private readonly List<CodexWorkflowStep> _steps = [];


    // Constructors
    public CodexWorkflow(CodexExecRunner runner, ILogger<CodexWorkflow> logger, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        _runner = runner;
        _logger = logger;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }


    // Methods
    /// <summary>
    /// 新增一個 Codex 執行步驟。
    /// </summary>
    /// <param name="step">要加入工作流程的執行步驟。</param>
    /// <returns>目前的工作流程實例，以便串接呼叫。</returns>
    public CodexWorkflow Add(CodexWorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(step.Prompt);
        ArgumentNullException.ThrowIfNull(step.Session);

        _steps.Add(step);

        return this;
    }

    /// <summary>
    /// 一次性加入多個 Codex 執行步驟。
    /// </summary>
    /// <param name="steps">要加入工作流程的執行步驟集合。</param>
    /// <returns>目前的工作流程實例，以便串接呼叫。</returns>
    public CodexWorkflow AddRange(IEnumerable<CodexWorkflowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        foreach (var step in steps)
        {
            Add(step);
        }

        return this;
    }

    /// <summary>
    /// 依序執行所有 Step。
    /// </summary>
    /// <param name="cancellationToken">用於取消工作流程執行的取消權杖。</param>
    /// <returns>包含整體執行結果及各步驟結果的工作流程結果。</returns>
    public async Task<WorkflowResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<WorkflowStepResult>();
        var sessions = new Dictionary<string, string>(StringComparer.Ordinal);

        // 每次執行使用獨立的 Step 清單與 Session 對應表。
        CodexWorkflowStep[] steps = _steps.ToArray();

        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 執行前確認 Session 設定正確。
            ValidateSession(step, sessions);

            string? sessionId = ResolveSessionId(step.Session, sessions);
            ExecutionOptions options = CreateExecutionOptions(step.Options, sessionId);

            _logger.LogInformation(
                "Workflow step started. Step: {Step}, SessionMode: {SessionMode}, SessionKey: {SessionKey}",
                step.Name,
                step.Session.Mode,
                step.Session.Key);

            ExecutionResult execution = await _runner.ExecuteAsync(
                _workspaceRoot,
                step.Prompt,
                options,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            results.Add(new WorkflowStepResult(step.Name, execution));
            if (execution.HasIssues)
            {
                _logger.LogWarning(
                    "Workflow step has issues. Step: {Step}, Outcome: {Outcome}, FailureCount: {Count}",
                    step.Name,
                    execution.Outcome,
                    execution.Failures.Count);
            }

            bool accepted = execution.Completed && (!step.StopOnIssues || execution.Succeeded);
            if (!accepted)
            {
                _logger.LogError("Workflow stopped. Step: {Step}, Outcome: {Outcome}", step.Name, execution.Outcome);
                return new WorkflowResult(false, step.Name, results);
            }

            // 僅在建立具名 Session 時保存對應的 Session ID。
            if (step.Session.Mode == SessionMode.Create)
            {
                if (string.IsNullOrWhiteSpace(execution.SessionId))
                {
                    throw new InvalidOperationException($"Step '{step.Name}' completed without a Session ID.");
                }

                sessions.Add(step.Session.Key!, execution.SessionId);
            }

            _logger.LogInformation("Workflow step completed. Step: {Step}, Outcome: {Outcome}", step.Name, execution.Outcome);
        }

        _logger.LogInformation("Workflow completed. StepCount: {Count}", results.Count);
        return new WorkflowResult(true, null, results);
    }

    /// <summary>
    /// 檢查 Session 設定是否符合目前的執行狀態。
    /// </summary>
    private static void ValidateSession(CodexWorkflowStep step, Dictionary<string, string> sessions)
    {
        if (step.Session.Mode == SessionMode.Create && sessions.ContainsKey(step.Session.Key!))
        {
            throw new InvalidOperationException($"Session '{step.Session.Key}' already exists. Step: {step.Name}");
        }

        if (step.Session.Mode == SessionMode.Use && !sessions.ContainsKey(step.Session.Key!))
        {
            throw new InvalidOperationException($"Session '{step.Session.Key}' does not exist. Step: {step.Name}");
        }
    }

    /// <summary>
    /// 取得指定 Step 要使用的 Codex Session ID。
    /// </summary>
    private static string? ResolveSessionId(Session session, Dictionary<string, string> sessions)
    {
        return session.Mode switch
        {
            SessionMode.None => null,
            SessionMode.Create => null,
            SessionMode.Use => sessions[session.Key!],
            _ => throw new ArgumentOutOfRangeException(
                nameof(session))
        };
    }

    /// <summary>
    /// 建立執行選項，並由 Workflow 控制 Session ID。
    /// </summary>
    private static ExecutionOptions CreateExecutionOptions(ExecutionOptions? source, string? sessionId)
    {
        source ??= new ExecutionOptions();

        if (source.SessionId is not null)
        {
            throw new ArgumentException("SessionId must be configured through Workflow Session.", nameof(source));
        }

        if (sessionId is not null && source.OutputSchemaPath is not null)
        {
            throw new InvalidOperationException("OutputSchemaPath cannot be used with Session Resume.");
        }

        return new ExecutionOptions
        {
            SessionId = sessionId,
            Model = source.Model,
            ReasoningEffort = source.ReasoningEffort,
            Sandbox = source.Sandbox,
            NetworkAccess = source.NetworkAccess,
            OutputSchemaPath = source.OutputSchemaPath,
            SkipGitRepoCheck = source.SkipGitRepoCheck,
            Timeout = source.Timeout
        };
    }
}
