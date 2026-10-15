namespace SimplArchive.Api.Startup;

/// <summary>
/// A sibling instance held the startup seeding lock for longer than this instance waits (#1773): it is seeding
/// unusually slowly, or it hangs while holding the lock. Startup fails with this, named, instead of waiting forever.
/// </summary>
public sealed class StartupSeedLockTimeoutException(TimeSpan deadline) : Exception(
    $"Another instance has been seeding the database for more than {deadline}; startup stops here. If it is still running, "
    + "this instance can start once it has finished; if it hangs, restart it.")
{
    public TimeSpan Deadline { get; } = deadline;
}
