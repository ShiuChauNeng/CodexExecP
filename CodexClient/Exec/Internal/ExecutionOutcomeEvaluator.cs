namespace CodexClient.Exec.Internal;

/// <summary>
/// 根據程序結束代碼與事件集合評估 Codex 執行結果。
/// </summary>
internal static class ExecutionOutcomeEvaluator
{
    // Methods
    /// <summary>
    /// 評估執行結果並彙整所有失敗資訊。
    /// </summary>
    /// <param name="processExitCode">Codex 程序的結束代碼。</param>
    /// <param name="events">此次執行收到的事件集合。</param>
    /// <param name="standardError">程序失敗時附加的標準錯誤輸出。</param>
    /// <returns>彙整後的執行結果。</returns>
    public static ExecutionResult Evaluate(int processExitCode, IReadOnlyList<ExecutionEvent> events, string? standardError = null)
    {
        var failures = new List<ExecutionFailure>();

        foreach (ExecutionEvent executionEvent in events)
        {
            if (executionEvent.Type == "turn.failed")
            {
                failures.Add(new ExecutionFailure(ExecutionFailureKind.Turn, executionEvent.ErrorMessage ?? "Codex turn failed."));
            }

            if (executionEvent.Type == "error")
            {
                failures.Add(new ExecutionFailure(ExecutionFailureKind.Stream, executionEvent.ErrorMessage ?? "Codex emitted a stream error."));
            }

            if (executionEvent.Type != "item.completed")
            {
                continue;
            }

            if (executionEvent.ItemType == "error")
            {
                failures.Add(new ExecutionFailure(
                    ExecutionFailureKind.AgentItem,
                    executionEvent.ErrorMessage ?? "Codex reported an item error.",
                    ItemId: executionEvent.ItemId,
                    ItemType: executionEvent.ItemType));

                continue;
            }

            bool isTool = executionEvent.ItemType is "command_execution" or "file_change" or "mcp_tool_call" or "collab_tool_call";

            if (!isTool)
            {
                continue;
            }

            bool failedStatus = executionEvent.ItemStatus is "failed" or "declined";
            bool validCompletedStatus = executionEvent.ItemStatus == "completed";
            bool failedCommand = executionEvent.ItemType == "command_execution" && executionEvent.ExitCode is int exitCode && exitCode != 0;
            bool hasError = !string.IsNullOrWhiteSpace(executionEvent.ErrorMessage);
            if (validCompletedStatus && !failedCommand && !hasError)
            {
                continue;
            }

            string message = executionEvent.ErrorMessage
                ?? executionEvent.Command
                ?? executionEvent.ToolName
                ?? "Codex tool execution failed.";

            failures.Add(new ExecutionFailure(
                ExecutionFailureKind.Tool,
                message,
                ItemId: executionEvent.ItemId,
                ItemType: executionEvent.ItemType,
                ItemStatus: executionEvent.ItemStatus,
                Command: executionEvent.Command,
                ToolName: executionEvent.ToolName,
                ExitCode: executionEvent.ExitCode));
        }

        bool hasTurnFailed = events.Any(x => x.Type == "turn.failed");

        bool hasTurnCompleted = events.Any(x => x.Type == "turn.completed");

        bool hasStreamError = events.Any(x => x.Type == "error");

        ExecutionOutcome outcome;

        // 具順序性處理
        if (processExitCode != 0)
        {
            string message = $"Codex process exited with code {processExitCode}.";
            if (!string.IsNullOrWhiteSpace(standardError))
            {
                message += $"{Environment.NewLine}stderr:{Environment.NewLine}{standardError}";
            }

            failures.Add(new ExecutionFailure(ExecutionFailureKind.Process, message, ExitCode: processExitCode));
            outcome = ExecutionOutcome.ProcessFailed;
        }
        else if (hasTurnFailed)
        {
            outcome = ExecutionOutcome.TurnFailed;
        }
        else if (hasStreamError)
        {
            outcome = ExecutionOutcome.StreamFailed;
        }
        else if (!hasTurnCompleted)
        {
            outcome = ExecutionOutcome.Incomplete;
        }
        else
        {
            outcome = failures.Count > 0 ? ExecutionOutcome.CompletedWithIssues : ExecutionOutcome.Completed;
        }

        string? sessionId = events
            .Where(x => x.Type == "thread.started")
            .Select(x => x.ThreadId)
            .LastOrDefault(x => x is not null);

        string? finalResponse = events
            .Where(x => x.Type == "item.completed" && x.ItemType == "agent_message")
            .Select(x => x.Text)
            .LastOrDefault(x => x is not null);

        return new ExecutionResult(
            sessionId,
            finalResponse,
            outcome,
            processExitCode,
            events.ToArray(),
            failures.ToArray());
    }
}
