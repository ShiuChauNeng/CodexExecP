namespace CodexClient.Exec;

public enum ExecutionState
{
    Created,
    Running,
    Completed,
    Failed,
    Cancelled,
    Incomplete
}