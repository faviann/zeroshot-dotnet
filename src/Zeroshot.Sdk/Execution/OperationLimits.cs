namespace Zeroshot.Native.Execution;

// Internal until the owning native clients expose their accepted configuration surface.
internal sealed record OperationLimits
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan UnaryTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int ConcurrentRequests { get; init; } = 32;
    public int ReservedControlRequests { get; init; } = 4;
    public int OecpConnections { get; init; } = 18;
    public int HttpConnectionsPerOrigin { get; init; } = 8;
    public int HttpRequestBytes { get; init; } = 4 * 1024 * 1024;
    public int OecpRequestBytes { get; init; } = 1024 * 1024;
    public int ResponseBytes { get; init; } = 8 * 1024 * 1024;
    public int MessageBytes { get; init; } = 8 * 1024 * 1024;
    public int DiagnosticBytes { get; init; } = 64 * 1024;
    public bool CaptureRawDiagnostics { get; init; }

    public void Validate()
    {
        ValidateTimeout(ConnectTimeout, nameof(ConnectTimeout));
        ValidateTimeout(UnaryTimeout, nameof(UnaryTimeout));
        ValidateTimeout(CleanupTimeout, nameof(CleanupTimeout));
        Positive(ConcurrentRequests, nameof(ConcurrentRequests));
        Positive(ReservedControlRequests, nameof(ReservedControlRequests));
        if (ReservedControlRequests >= ConcurrentRequests)
            throw new ArgumentException("Control reservation must leave ordinary request capacity.");
        Positive(OecpConnections, nameof(OecpConnections));
        Positive(HttpConnectionsPerOrigin, nameof(HttpConnectionsPerOrigin));
        Positive(HttpRequestBytes, nameof(HttpRequestBytes));
        Positive(OecpRequestBytes, nameof(OecpRequestBytes));
        Positive(ResponseBytes, nameof(ResponseBytes));
        Positive(MessageBytes, nameof(MessageBytes));
        Positive(DiagnosticBytes, nameof(DiagnosticBytes));
    }

    internal static void ValidateTimeout(TimeSpan value, string name)
    {
        // CancellationTokenSource's timer has the same finite upper bound.
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name, "A positive, finite timer duration is required.");
    }

    private static void Positive(int value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, "A positive limit is required.");
    }
}

internal enum OperationTransport { Http, Oecp }
internal enum OperationStage { Admission, Connect, Request, Response, Message, Diagnostic, Operation }
internal enum OperationFailureKind { Capacity, Deadline, SizeLimit, Transport, Protocol, HttpStatus, Redirect }

internal sealed class OperationDescriptor
{
    public string Name { get; }
    public OperationTransport Transport { get; }
    public bool IsControl { get; }
    public int? RequestBytes { get; }
    public int? ResponseBytes { get; }
    public int? MessageBytes { get; }
    /// <summary>A native refusal-body bound below the shared diagnostic ceiling.</summary>
    public int? ProblemBytes { get; }
    /// <summary>Served by a direct target's UI router, which keeps the connection for itself.</summary>
    public bool UiRouter { get; }
    /// <summary>Refusals carry the closed native run-history problem vocabulary.</summary>
    public bool HistoryProblems { get; }
    /// <summary>Refusals carry an OAuth <c>{error}</c> body instead of a target problem.</summary>
    public bool OAuthErrors { get; }

    // Name is a binding-owned operation identifier, never a URL, remote string or caller payload.
    public OperationDescriptor(string name, OperationTransport transport, bool isControl = false,
        int? requestBytes = null, int? responseBytes = null, int? messageBytes = null, int? problemBytes = null, bool uiRouter = false, bool historyProblems = false,
        bool oauthErrors = false)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 ||
            name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-')))
            throw new ArgumentException("A bounded operation identifier is required.", nameof(name));
        if (!Enum.IsDefined(transport)) throw new ArgumentOutOfRangeException(nameof(transport));
        if (requestBytes <= 0 || responseBytes <= 0 || messageBytes <= 0 || problemBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestBytes), "Operation limits must be positive.");
        Name = name;
        Transport = transport;
        IsControl = isControl;
        RequestBytes = requestBytes;
        ResponseBytes = responseBytes;
        MessageBytes = messageBytes;
        ProblemBytes = problemBytes;
        UiRouter = uiRouter;
        HistoryProblems = historyProblems;
        OAuthErrors = oauthErrors;
    }
}
