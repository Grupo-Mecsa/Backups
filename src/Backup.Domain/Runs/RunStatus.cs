namespace Backup.Domain.Runs;

public enum RunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
}
