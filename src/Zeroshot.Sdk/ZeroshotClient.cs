using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot;

public sealed record ZeroshotClientOptions
{
    /// <summary>The direct or private target origin. Hosted run workflows remain lower-client operations.</summary>
    public required Uri Target { get; init; }
    /// <summary>Required before any run operation is dispatched; construction and preparation work without it.</summary>
    public NativeBinding? NativeBinding { get; init; }
    /// <summary>Control credentials for a private target. A direct target needs none.</summary>
    public TargetControlCredentials? TargetCredentials { get; init; }
    public TransportOptions Transport { get; init; } = new();
}

/// <summary>
/// Ordinary run workflows over one <see cref="NativeClient"/>. Construction, preparation and reopening a run
/// perform no network I/O. Disposal never stops a remote run.
/// </summary>
public sealed class ZeroshotClient : IDisposable, IAsyncDisposable
{
    private readonly NativeClient native;
    private readonly bool ownsClient;
    private readonly TargetControlCredentials? credentials;
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private OecpConnection? control;
    private int disposed;

    public Uri Target => native.Origin;
    public NativeBinding? NativeBinding { get; }

    public ZeroshotClient(ZeroshotClientOptions options)
        : this(NativeClient.ForHttp(new NativeClientOptions { Origin = Checked(options).Target, Transport = options.Transport }),
            options.NativeBinding, ownsClient: true, options.TargetCredentials) { }

    /// <summary>Uses an existing native client, disposing it only when <paramref name="ownsClient"/> is true.</summary>
    public ZeroshotClient(NativeClient native, NativeBinding? binding, bool ownsClient = false,
        TargetControlCredentials? targetCredentials = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        // Holds this client's control connection slot for its lifetime; SDK observation can never take it.
        // Ownership transfers only on success, as with the options constructor's own client.
        try { native.SdkConnections.AddClient(); }
        catch { if (ownsClient) native.Dispose(); throw; }
        this.native = native;
        this.ownsClient = ownsClient;
        NativeBinding = binding;
        credentials = targetCredentials;
    }

    private static ZeroshotClientOptions Checked(ZeroshotClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options;
    }

    public PreparedSubmission Prepare(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Prepare();
    }

    /// <summary>
    /// Prepares <paramref name="request"/>, generating any omitted identity once, then submits it as
    /// <see cref="SubmitAsync(PreparedSubmission, TargetRunCredentials?, CancellationToken)"/> does.
    /// </summary>
    public Task<Run> SubmitAsync(RunRequest request, TargetRunCredentials? credentials = null,
        CancellationToken cancellationToken = default)
        => SubmitAsync(Prepare(request), credentials, cancellationToken);

    /// <summary>
    /// Sends the prepared request once and returns a handle for the acknowledged run, which can differ from the
    /// proposed one; <see cref="Run.Submission"/> keeps both. Any other outcome throws
    /// <see cref="SubmissionException"/>, or <see cref="SubmissionCanceledException"/> on cancellation, with the attempt.
    /// </summary>
    public async Task<Run> SubmitAsync(PreparedSubmission prepared, TargetRunCredentials? credentials = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = await SubmitAttemptAsync(prepared, credentials, cancellationToken).ConfigureAwait(false);
        if (attempt.AcknowledgedRunId is { } acknowledged) return new Run(this, acknowledged, NativeBinding, attempt);
        if (attempt.Failure is OperationCanceledException) throw new SubmissionCanceledException(attempt, cancellationToken);
        throw new SubmissionException(attempt);
    }

    /// <summary>
    /// Sends the exact prepared bytes once with separately supplied credentials (none by default) and returns
    /// acknowledged, rejected, not-sent or unknown evidence, including cancellation. Nothing is regenerated or retried.
    /// A missing or mismatched binding is a not-sent attempt whose failure is the <see cref="NativeBindingException"/>.
    /// Invalid arguments and disposal throw instead.
    /// </summary>
    public Task<TargetSubmissionAttempt> SubmitAttemptAsync(PreparedSubmission prepared,
        TargetRunCredentials? credentials = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        try { EnsureBinding(null); } // Disposal still throws ObjectDisposedException from here.
        catch (NativeBindingException refused)
        {
            return Task.FromResult(new TargetSubmissionAttempt(Target, Guid.NewGuid(), prepared,
                NativeAttemptOutcome.NotSent, null, refused));
        }
        return native.Target.SubmitAttemptAsync(prepared, credentials ?? NoRunCredentials, this.credentials, cancellationToken);
    }

    private static readonly TargetRunCredentials NoRunCredentials =
        new() { Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty };

    /// <summary>A handle for a known run on this client's target and binding. No I/O.</summary>
    public Run GetRun(RunId runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new Run(this, runId, NativeBinding);
    }

    /// <summary>
    /// A handle for a retained reference. A different target is rejected here; the reference's binding must
    /// equal this client's binding when a run operation is dispatched. No I/O.
    /// </summary>
    public Run GetRun(RunReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (reference.Target != Target)
            throw new ArgumentException("The run reference names a different target.", nameof(reference));
        return new Run(this, reference.RunId, reference.NativeBinding);
    }

    /// <summary>The single SDK dispatch guard. Discovery and direct NativeClient calls never pass through it.</summary>
    internal void EnsureBinding(NativeBinding? handle)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (NativeBinding is null)
            throw new NativeBindingException(NativeBindingProblem.Missing, null, Zeroshot.NativeBinding.Supported,
                "No caller-supplied native binding is configured.");
        if (NativeBinding != Zeroshot.NativeBinding.Supported)
            throw new NativeBindingException(NativeBindingProblem.Mismatched, NativeBinding, Zeroshot.NativeBinding.Supported,
                "The configured native binding is not the supported native release.");
        if (handle is not null && handle != NativeBinding)
            throw new NativeBindingException(NativeBindingProblem.Mismatched, handle, NativeBinding,
                "The run reference's native binding differs from this client's binding.");
    }

    /// <summary>The shared control connection, separate from every observation; reopened lazily after it fails.</summary>
    internal async Task<OecpConnection> ControlAsync(CancellationToken cancellationToken)
    {
        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (control is { Completion.IsCompleted: false } open) return open;
            control?.Dispose();
            control = null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var connection = await ConnectAsync(linked.Token).ConfigureAwait(false);
            // Dispose reads control without this gate. The fenced write and re-check mean one of them closes it.
            Interlocked.Exchange(ref control, connection);
            if (Volatile.Read(ref disposed) != 0)
            {
                connection.Dispose();
                throw new ObjectDisposedException(nameof(ZeroshotClient));
            }
            return connection;
        }
        finally { controlGate.Release(); }
    }

    /// <summary>One live read-only attachment on its own connection. Never reopens or replays.</summary>
    internal async IAsyncEnumerable<RunAttachEventNotification> AttachAsync(RunId runId, ExecutionRef execution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        native.SdkConnections.AddObservation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            await using var connection = await ConnectAsync(linked.Token).ConfigureAwait(false);
            await using var subscription = await connection.Runs.AttachAsync(
                new RunAttachParams { RunId = runId, Execution = execution }, linked.Token).ConfigureAwait(false);
            await foreach (var record in subscription.ReadAllAsync(linked.Token).ConfigureAwait(false))
                yield return record;
        }
        finally { native.SdkConnections.RemoveObservation(); }
    }

    private async Task<OecpConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var discovery = await native.Target.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        // A target-wide session: direct and private targets. Hosted sessions need a per-run selector.
        var session = await native.Target.CreateOecpSessionAsync(discovery, null,
            discovery.Authentication == TargetAuthentication.None ? null : credentials, cancellationToken).ConfigureAwait(false);
        var connection = await native.ConnectOecpAsync(session, cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.InitializeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        Volatile.Read(ref control)?.Dispose();
        native.SdkConnections.RemoveClient();
        if (ownsClient) native.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>
/// A client-side handle to a known run. Its lifetime is independent of the native run; nothing here stops it.
/// </summary>
public sealed class Run
{
    private readonly ZeroshotClient client;
    private readonly NativeBinding? binding;
    /// <summary>The acknowledged run ID when this handle came from submission.</summary>
    public RunId Id { get; }
    /// <summary>
    /// The acknowledged attempt that produced this handle, with proposed and acknowledged IDs and whether they match;
    /// null for a reopened run. A different acknowledged ID is valid native data, not proof of deduplication.
    /// </summary>
    public TargetSubmissionAttempt? Submission { get; }

    internal Run(ZeroshotClient client, RunId id, NativeBinding? binding, TargetSubmissionAttempt? submission = null)
    { this.client = client; Id = id; this.binding = binding; Submission = submission; }

    /// <summary>The portable reference for later reconnection. Throws when no native binding is configured.</summary>
    public RunReference Reference => binding is null
        ? throw new NativeBindingException(NativeBindingProblem.Missing, null, NativeBinding.Supported,
            "No caller-supplied native binding is configured.")
        : new RunReference(client.Target, Id, binding);

    /// <summary>
    /// Complete native status over the control connection. A failed run is status data; use
    /// <see cref="RunResult.FromStatus"/> for its terminal result with status-report evidence.
    /// </summary>
    public async Task<RunStatusResult> StatusAsync(CancellationToken cancellationToken = default)
    {
        client.EnsureBinding(binding);
        var control = await client.ControlAsync(cancellationToken).ConfigureAwait(false);
        return await control.Runs.StatusAsync(Id, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Live read-only output of one active execution. Each enumeration opens its own connection and attachment,
    /// and ending, cancelling or disposing it detaches. There is no cursor, replay, input or automatic reopen.
    /// </summary>
    public IAsyncEnumerable<RunAttachEventNotification> AttachAsync(ExecutionRef execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        client.EnsureBinding(binding);
        return client.AttachAsync(Id, execution, cancellationToken);
    }
}
