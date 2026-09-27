namespace Zeroshot.Native.Execution;

internal sealed class OperationExecutor : IDisposable
{
    private readonly OperationLimits limits;
    private readonly TimeProvider time;
    private readonly OperationCapacity capacity;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private int disposed;

    public OperationExecutor(OperationLimits? limits = null, TimeProvider? time = null)
    {
        this.limits = limits ?? new OperationLimits();
        this.limits.Validate();
        this.time = time ?? TimeProvider.System;
        capacity = new OperationCapacity(this.limits);
        lifetimeToken = lifetime.Token;
    }

    // Invoke exactly once. Completion means the adapter has validated the complete response.
    public async Task<T> ExecuteAsync<T>(OperationDescriptor operation, long requestBytes,
        Func<OperationContext, Task<T>> execute, Func<CancellationToken, Task>? cleanup = null,
        CancellationToken cancellationToken = default, TimeSpan? enclosingBudget = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(execute);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (requestBytes < 0) throw new ArgumentOutOfRangeException(nameof(requestBytes));
        if (enclosingBudget is { } enclosing) OperationLimits.ValidateTimeout(enclosing, nameof(enclosingBudget));
        var duration = enclosingBudget is { } budget && budget < limits.UnaryTimeout ? budget : limits.UnaryTimeout;
        using var deadline = new OperationDeadline(duration, time, cancellationToken, lifetimeToken);
        var context = new OperationContext(operation, Guid.NewGuid(), limits, deadline, time);
        IDisposable? reservation = null;
        Task<T>? execution = null;
        Task cleanupTask = Task.CompletedTask;
        try
        {
            context.ThrowIfCancelled();
            context.CheckRequestSize(requestBytes);
            reservation = capacity.TryRequest(operation.IsControl)
                ?? throw context.Failure(OperationFailureKind.Capacity, OperationStage.Admission);
            context.ThrowIfCancelled();
            execution = execute(context);
            try
            {
                return await execution.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (execution.IsCompletedSuccessfully)
            {
                // A fully validated result remains evidence even if cancellation wins the wait race.
                return execution.Result;
            }
        }
        catch (OperationFailure) { throw; }
        catch (OperationCanceledException)
        {
            if (deadline.Expired) throw context.Failure(OperationFailureKind.Deadline, OperationStage.Operation);
            if (!cancellationToken.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
                throw context.Failure(OperationFailureKind.Transport, OperationStage.Operation);
            throw new OperationCancelled(operation, context.CorrelationId,
                cancellationToken.IsCancellationRequested ? cancellationToken : lifetimeToken);
        }
        catch (Exception)
        {
            // Foreign exception messages/inner exceptions may contain URLs, credentials or bodies.
            throw context.Failure(OperationFailureKind.Transport, OperationStage.Operation);
        }
        finally
        {
            deadline.Cancel();
            if (reservation is not null)
            {
                if (cleanup is not null)
                {
                    using var cleanupDeadline = new OperationDeadline(limits.CleanupTimeout, time, default);
                    try
                    {
                        cleanupTask = cleanup(cleanupDeadline.Token);
                        await cleanupTask.WaitAsync(cleanupDeadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception) { /* Cleanup never replaces an operation outcome. */ }
                }
                // A non-cooperating adapter cannot create unlimited abandoned operations: retain
                // capacity until both tasks settle, even though the caller's wait is bounded.
                _ = ReleaseWhenSettledAsync(execution, context, cleanupTask, reservation);
            }
        }
    }

    public IDisposable RegisterOecpConnection(OperationDescriptor operation) => Register(operation, null);
    public IDisposable RegisterHttpConnection(OperationDescriptor operation, Uri origin) => Register(operation, origin);

    private IDisposable Register(OperationDescriptor operation, Uri? origin)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return (origin is null ? capacity.TryOecpConnection() : capacity.TryHttpConnection(origin))
            ?? throw new OperationFailure(operation, Guid.NewGuid(), OperationFailureKind.Capacity, OperationStage.Admission);
    }

    private static async Task ReleaseWhenSettledAsync(Task? execution, OperationContext context, Task cleanup, IDisposable reservation)
    {
        try { if (execution is not null) await execution.ConfigureAwait(false); }
        catch (Exception) { }
        try { await context.SettleConnectionsAsync().ConfigureAwait(false); }
        catch (Exception) { }
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception) { }
        reservation.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { lifetime.Cancel(); }
        catch (AggregateException) { /* Cancellation callbacks cannot expose raw transport errors. */ }
        lifetime.Dispose();
    }
}
