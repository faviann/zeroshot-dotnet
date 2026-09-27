using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public enum NativeOecpFailureKind { Capacity, Deadline, SizeLimit, Transport, Protocol, RpcError }

/// <summary>Observed transport facts, never a claim that a remote mutation did or did not happen.</summary>
public sealed record OecpDispatchFacts(RequestId? RequestId, bool SendStarted, bool SendCompleted, bool ResponseReceived);

/// <summary>Safe metadata for a failed unary call. Remote error text is available only by explicit inspection.</summary>
public sealed class NativeOecpException : Exception
{
    private readonly byte[]? rawDiagnostic;
    public string Operation { get; }
    public Guid CorrelationId { get; }
    public string Stage { get; }
    public NativeOecpFailureKind Kind { get; }
    public OecpDispatchFacts Dispatch { get; }
    public JsonRpcError? RpcError { get; }
    internal NativeOecpException(OperationFailure failure, OecpDispatchFacts dispatch, JsonRpcError? rpcError = null)
        : base(rpcError is null ? failure.Message : $"Native operation {failure.Operation} (Oecp, {failure.CorrelationId:D}) failed: RpcError during {failure.Stage}.")
    {
        Operation = failure.Operation; CorrelationId = failure.CorrelationId; Stage = failure.Stage.ToString();
        Kind = rpcError is not null ? NativeOecpFailureKind.RpcError : Enum.Parse<NativeOecpFailureKind>(failure.Kind.ToString());
        Dispatch = dispatch; RpcError = rpcError; rawDiagnostic = failure.ExportRawDiagnostic();
    }
    public byte[]? ExportRawDiagnostic() => rawDiagnostic?.ToArray();
}

/// <summary>Local cancellation retains dispatch facts; it never means native stop or rollback.</summary>
public sealed class OecpOperationCanceledException : OperationCanceledException
{
    public OecpDispatchFacts Dispatch { get; }
    public Guid CorrelationId { get; }
    internal OecpOperationCanceledException(OperationCancelled error, OecpDispatchFacts dispatch)
        : base(error.Message, error.CancellationToken) { Dispatch = dispatch; CorrelationId = error.CorrelationId; }
}

/// <summary>Connection-wide interruption, exposed even when no unary operation is waiting.</summary>
public sealed record OecpConnectionFailure(NativeOecpFailureKind Kind);
