using System.Net;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

public sealed record NativeClientOptions
{
    public required Uri Origin { get; init; }
    public TransportOptions Transport { get; init; } = new();

    /// <summary>The clock behind operation deadlines; tests substitute a manual one.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;
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
    public int MaxOecpConnections { get; init; } = 18;
    public int MaxOecpRequestBytes { get; init; } = 1024 * 1024;
    public int MaxOecpMessageBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxConcurrentSubscriptions { get; init; } = 16;
    public int MaxQueuedObservationRecords { get; init; } = 256;
    public int MaxQueuedObservationBytes { get; init; } = 8 * 1024 * 1024;
    public long MaxAggregateObservationBytes { get; init; } = 32 * 1024 * 1024;
    public bool EnableWebSocketLiveness { get; init; } = true;
    public TimeSpan WebSocketPingInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan WebSocketPongTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public bool CaptureRawDiagnostics { get; init; }

    internal OperationLimits Limits()
    {
        if (MaxConcurrentSubscriptions <= 0) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentSubscriptions));
        if (MaxQueuedObservationRecords <= 0) throw new ArgumentOutOfRangeException(nameof(MaxQueuedObservationRecords));
        if (MaxQueuedObservationBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxQueuedObservationBytes));
        if (MaxAggregateObservationBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxAggregateObservationBytes));
        OperationLimits.ValidateTimeout(WebSocketPingInterval, nameof(WebSocketPingInterval));
        OperationLimits.ValidateTimeout(WebSocketPongTimeout, nameof(WebSocketPongTimeout));
        var limits = new OperationLimits
        {
            ConnectTimeout = ConnectTimeout, UnaryTimeout = RequestTimeout, CleanupTimeout = CleanupTimeout,
            ConcurrentRequests = MaxConcurrentRequests, ReservedControlRequests = ReservedControlRequests,
            HttpConnectionsPerOrigin = MaxHttpConnectionsPerOrigin, HttpRequestBytes = MaxRequestBytes,
            ResponseBytes = MaxResponseBytes, DiagnosticBytes = MaxErrorBodyBytes,
            OecpConnections = MaxOecpConnections, OecpRequestBytes = MaxOecpRequestBytes,
            MessageBytes = MaxOecpMessageBytes, CaptureRawDiagnostics = CaptureRawDiagnostics
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
    /// <summary>A validated problem from a direct target's UI router, for UI-routed operations only.
    /// Never included in default exception formatting.</summary>
    public UiProblem? UiProblem { get; }
    /// <summary>The closed native history category of the received problem, for history operations only.
    /// An unknown or malformed history problem leaves it null and keeps the observed status.</summary>
    public RunHistoryProblemCode? HistoryProblem { get; }
    /// <summary>The recognized OAuth <c>{error}</c> of a device-token refusal, for that operation only.
    /// An unrecognized or malformed error body leaves it null and keeps the observed status.</summary>
    public DeviceTokenError? DeviceTokenError { get; }
    internal NativeHttpException(OperationFailure failure, TargetHttpProblem? problem = null,
        HttpStatusCode? receivedStatus = null, UiProblem? uiProblem = null, RunHistoryProblemCode? historyProblem = null,
        DeviceTokenError? deviceTokenError = null)
        : base(failure.Message)
    {
        Operation = failure.Operation;
        CorrelationId = failure.CorrelationId;
        Stage = failure.Stage.ToString();
        Kind = Enum.Parse<NativeHttpFailureKind>(failure.Kind.ToString());
        StatusCode = failure.StatusCode ?? receivedStatus;
        Problem = problem;
        UiProblem = uiProblem;
        HistoryProblem = historyProblem;
        DeviceTokenError = deviceTokenError;
        rawDiagnostic = failure.ExportRawDiagnostic();
    }
    public byte[]? ExportRawDiagnostic() => rawDiagnostic?.ToArray();
}
