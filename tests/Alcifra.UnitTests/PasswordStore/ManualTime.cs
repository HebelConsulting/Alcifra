namespace Alcifra.UnitTests.PasswordStore;

// A clock moved by hand: Task.Delay(TimeSpan, TimeProvider) asks it for a timer, which fires when time passes it.
internal sealed class ManualTime : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public ManualTime()
    {
    }

    /// <summary>A clock that starts at <paramref name="start"/> (a verification code's test vector, ADR 1013).</summary>
    public ManualTime(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(_now + dueTime, callback, state, _timers);
        _timers.Add(timer);
        return timer;
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

    // One-shot, as the code under test uses them; Dispose cancels it like a real timer's, so a restarted timer does not
    // also fire at its earlier time.
    private sealed class ManualTimer(DateTimeOffset due, TimerCallback callback, object? state, List<ManualTimer> owner) : ITimer
    {
        public DateTimeOffset Due { get; } = due;

        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
