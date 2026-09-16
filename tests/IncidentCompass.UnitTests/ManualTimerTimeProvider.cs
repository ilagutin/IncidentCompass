namespace IncidentCompass.UnitTests;

/// <summary>
/// A clock that moves only when a test calls <see cref="Advance"/>, with timers that fire during
/// that call once their due time is reached. It lets a test drive a timeout deterministically: the
/// code under test arms a timer through the injected <see cref="TimeProvider"/>, the test waits for a
/// signal that the code is blocked, and then advances past the limit. Nothing depends on real time.
/// </summary>
internal sealed class ManualTimerTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset utcNow = start;

    public ManualTimerTimeProvider()
        : this(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return utcNow;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward and runs, on the calling thread, every timer whose due time has been
    /// reached. A periodic timer is not supported beyond its first firing; nothing under test needs one.
    /// </summary>
    public void Advance(TimeSpan duration)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            utcNow = utcNow.Add(duration);
            due = timers.Where(timer => timer.DueAtUtc is { } dueAt && dueAt <= utcNow).ToList();
            foreach (var timer in due)
            {
                timer.DueAtUtc = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime)
    {
        lock (gate)
        {
            timer.DueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : utcNow.Add(dueTime);
        }

        if (dueTime == TimeSpan.Zero)
        {
            Advance(TimeSpan.Zero);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate)
        {
            timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimerTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAtUtc { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
