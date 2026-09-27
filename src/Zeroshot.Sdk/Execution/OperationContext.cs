namespace Zeroshot.Native.Execution;

internal sealed class OperationContext(OperationDescriptor operation, Guid correlationId, OperationLimits limits,
    OperationDeadline deadline, TimeProvider time)
{
    private readonly List<Task> connections = [];
    public CancellationToken CancellationToken => deadline.Token;
    public Guid CorrelationId => correlationId;
    public TimeSpan Remaining => deadline.Remaining;

    public void ThrowIfCancelled()
    {
        if (deadline.Expired) throw Failure(OperationFailureKind.Deadline, OperationStage.Operation);
        CancellationToken.ThrowIfCancellationRequested();
    }

    public async Task ConnectAsync(Func<CancellationToken, Task> connect)
    {
        ThrowIfCancelled();
        var remaining = Remaining;
        if (remaining == TimeSpan.Zero) throw Failure(OperationFailureKind.Deadline, OperationStage.Connect);
        using var connectionDeadline = new OperationDeadline(
            remaining < limits.ConnectTimeout ? remaining : limits.ConnectTimeout, time, CancellationToken);
        try
        {
            var connection = connect(connectionDeadline.Token);
            lock (connections) connections.Add(connection);
            await connection.WaitAsync(connectionDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionDeadline.Expired)
        {
            throw Failure(OperationFailureKind.Deadline, OperationStage.Connect);
        }
    }

    internal Task SettleConnectionsAsync()
    {
        lock (connections) return Task.WhenAll(connections);
    }

    public void CheckRequestSize(long bytes) => CheckSize(bytes,
        Math.Min(operation.Transport == OperationTransport.Http ? limits.HttpRequestBytes : limits.OecpRequestBytes,
            operation.RequestBytes ?? int.MaxValue), OperationStage.Request);

    public void CheckMessageSize(long bytes) => CheckSize(bytes,
        Math.Min(limits.MessageBytes, operation.MessageBytes ?? int.MaxValue), OperationStage.Message);

    // Reads at most the ceiling plus one probe byte, regardless of Content-Length or frame headers.
    // The caller retains stream ownership and releases it through the operation cleanup callback.
    public async Task<byte[]> ReadResponseAsync(Stream stream)
    {
        var ceiling = Math.Min(limits.ResponseBytes, operation.ResponseBytes ?? int.MaxValue);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8192, ceiling)];
        while (true)
        {
            ThrowIfCancelled();
            var count = await stream.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, ceiling - output.Length + 1)), CancellationToken).ConfigureAwait(false);
            ThrowIfCancelled();
            if (count == 0) return output.ToArray();
            CheckSize(output.Length + count, ceiling, OperationStage.Response);
            output.Write(buffer, 0, count);
        }
    }

    public OperationFailure Failure(OperationFailureKind kind, OperationStage stage, ReadOnlySpan<byte> rawDiagnostic = default)
    {
        if (rawDiagnostic.Length > limits.DiagnosticBytes)
            return new OperationFailure(operation, correlationId, OperationFailureKind.SizeLimit, OperationStage.Diagnostic);
        return new OperationFailure(operation, correlationId, kind, stage,
            limits.CaptureRawDiagnostics && !rawDiagnostic.IsEmpty ? rawDiagnostic.ToArray() : null);
    }

    private void CheckSize(long bytes, int ceiling, OperationStage stage)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes > ceiling) throw Failure(OperationFailureKind.SizeLimit, stage);
    }
}
