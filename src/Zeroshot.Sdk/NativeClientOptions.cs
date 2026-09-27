using System.Net;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

public sealed record NativeClientOptions
{
    public required Uri Origin { get; init; }
    public TransportOptions Transport { get; init; } = new();
}

/// <summary>Positive, finite limits shared by one native client.</summary>
public sealed record TransportOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxConcurrentRequests { get; init; } = 32;
    public int ReservedControlRequests { get; init; } = 4;
    public int MaxHttpConnectionsPerOrigin { get; init; } = 8;
    public int MaxRequestBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxErrorBodyBytes { get; init; } = 64 * 1024;
    public bool CaptureRawDiagnostics { get; init; }

    internal OperationLimits Limits()
    {
        var limits = new OperationLimits
        {
            ConnectTimeout = ConnectTimeout, UnaryTimeout = RequestTimeout, CleanupTimeout = CleanupTimeout,
            ConcurrentRequests = MaxConcurrentRequests, ReservedControlRequests = ReservedControlRequests,
            HttpConnectionsPerOrigin = MaxHttpConnectionsPerOrigin, HttpRequestBytes = MaxRequestBytes,
            ResponseBytes = MaxResponseBytes, DiagnosticBytes = MaxErrorBodyBytes,
            CaptureRawDiagnostics = CaptureRawDiagnostics
        };
        limits.Validate();
        return limits;
    }
}

public enum NativeHttpFailureKind { Capacity, Deadline, SizeLimit, Transport, Protocol, HttpStatus, Redirect }

/// <summary>Safe HTTP failure metadata. Raw remote bytes require explicit opt-in and inspection.</summary>
public sealed class NativeHttpException : Exception
{
    private readonly byte[]? rawDiagnostic;
    public string Operation { get; }
    public Guid CorrelationId { get; }
    public string Stage { get; }
    public NativeHttpFailureKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
    /// <summary>Validated remote facts, if received. Never included in default exception formatting.</summary>
    public TargetHttpProblem? Problem { get; }
    internal NativeHttpException(OperationFailure failure, TargetHttpProblem? problem = null,
        HttpStatusCode? receivedStatus = null) : base(failure.Message)
    {
        Operation = failure.Operation;
        CorrelationId = failure.CorrelationId;
        Stage = failure.Stage.ToString();
        Kind = Enum.Parse<NativeHttpFailureKind>(failure.Kind.ToString());
        StatusCode = failure.StatusCode ?? receivedStatus;
        Problem = problem;
        rawDiagnostic = failure.ExportRawDiagnostic();
    }
    public byte[]? ExportRawDiagnostic() => rawDiagnostic?.ToArray();
}
