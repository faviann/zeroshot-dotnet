namespace Zeroshot.Native.Execution;

internal sealed class OperationDeadline : IDisposable
{
    private readonly TimeProvider time;
    private readonly long started;
    private readonly TimeSpan duration;
    private readonly CancellationTokenSource timer;
    private readonly CancellationTokenSource linked;
    public CancellationToken Token { get; }
    public bool Expired => timer.IsCancellationRequested || Remaining == TimeSpan.Zero;
    public TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, (duration - time.GetElapsedTime(started)).Ticks));

    public OperationDeadline(TimeSpan duration, TimeProvider time, CancellationToken caller, CancellationToken owner = default)
    {
        OperationLimits.ValidateTimeout(duration, nameof(duration));
        this.duration = duration;
        this.time = time;
        started = time.GetTimestamp();
        timer = new CancellationTokenSource(duration, time);
        linked = CancellationTokenSource.CreateLinkedTokenSource(caller, owner, timer.Token);
        Token = linked.Token;
    }

    public void Cancel()
    {
        try { linked.Cancel(); }
        catch (AggregateException) { /* Adapter cancellation callbacks cannot replace the outcome. */ }
    }

    public void Dispose()
    {
        linked.Dispose();
        timer.Dispose();
    }
}
