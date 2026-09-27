namespace Zeroshot.Native.Execution;

internal sealed class OperationFailure : Exception
{
    private readonly byte[]? rawDiagnostic;
    public string Operation { get; }
    public Guid CorrelationId { get; }
    public OperationTransport Transport { get; }
    public OperationStage Stage { get; }
    public OperationFailureKind Kind { get; }

    internal OperationFailure(OperationDescriptor operation, Guid correlationId,
        OperationFailureKind kind, OperationStage stage, byte[]? rawDiagnostic = null)
        : base($"Native operation {operation.Name} ({operation.Transport}, {correlationId:D}) failed: {kind} during {stage}.")
    {
        Operation = operation.Name;
        CorrelationId = correlationId;
        Transport = operation.Transport;
        Stage = stage;
        Kind = kind;
        this.rawDiagnostic = rawDiagnostic;
    }

    // Explicit inspection returns owned bytes. Raw data is never an inner exception or message.
    public byte[]? ExportRawDiagnostic() => rawDiagnostic?.ToArray();
}

internal sealed class OperationCancelled : OperationCanceledException
{
    public Guid CorrelationId { get; }

    internal OperationCancelled(OperationDescriptor operation, Guid correlationId, CancellationToken token)
        : base($"Native operation {operation.Name} ({operation.Transport}, {correlationId:D}) was cancelled.", token)
        => CorrelationId = correlationId;
}
