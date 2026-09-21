namespace ToolDock.Updating;

public enum UpdateStatus
{
    Completed,
    CompletedWithErrors,
    AlreadyRunning,
    Cancelled,
    Failed
}

public sealed record UpdateResult(
    UpdateStatus Status,
    int Checked = 0,
    int Updated = 0,
    int Failed = 0);
