namespace Zeroshot.Client.Tests;

// Only one-shot timers are needed for injected operation deadlines. No wall-clock sleeps.
internal sealed class ManualTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<Timer> timers = [];
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (gate) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        long target;
        lock (gate) target = ticks + by.Ticks;
        while (true)
        {
            Timer? next;
            lock (gate)
            {
                next = timers.Where(t => t.Due <= target).MinBy(t => t.Due);
                if (next is null) { ticks = target; return; }
                ticks = next.Due;
                next.Due = long.MaxValue;
            }
            next.Fire();
        }
    }

    private sealed class Timer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; set; } = long.MaxValue;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Fixture supports one-shot timers only.");
            lock (time.gate)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : time.ticks + dueTime.Ticks;
                return true;
            }
        }
        public void Fire() => callback(state);
        public void Dispose()
        {
            lock (time.gate)
            {
                disposed = true;
                time.timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
