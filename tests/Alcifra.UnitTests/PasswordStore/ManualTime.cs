namespace Alcifra.UnitTests.PasswordStore;

// A clock moved by hand: Task.Delay(TimeSpan, TimeProvider) asks it for a timer, which fires when time passes it.
internal sealed class ManualTime : TimeProvider
{
    private readonly List<(DateTimeOffset Due, TimerCallback Callback, object? State)> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _timers.Add((_now + dueTime, callback, state));
        return new NoTimer();
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var due in _timers.Where(t => t.Due <= _now).ToList())
        {
            _timers.Remove(due);
            due.Callback(due.State);
        }
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
