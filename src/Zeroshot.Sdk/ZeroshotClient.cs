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
    /// <summary>Watch/log recovery policy and setup budget.</summary>
    public ObservationOptions Observation { get; init; } = new();
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
    private readonly ObservationOptions observation;
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private OecpConnection? control;
    private int disposed;

    public Uri Target => native.Origin;
    public NativeBinding? NativeBinding { get; }
    /// <summary>The clock for wait budgets; replaced only by tests.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Runs once a force acknowledgement is captured, before its call completes; set only by tests.</summary>
    internal Action? AfterForceCapture { get; init; }

    public ZeroshotClient(ZeroshotClientOptions options)
        : this(NativeClient.ForHttp(new NativeClientOptions { Origin = Checked(options).Target, Transport = options.Transport }),
            options.NativeBinding, ownsClient: true, options.TargetCredentials, options.Observation) { }

    /// <summary>Uses an existing native client, disposing it only when <paramref name="ownsClient"/> is true.</summary>
    public ZeroshotClient(NativeClient native, NativeBinding? binding, bool ownsClient = false,
        TargetControlCredentials? targetCredentials = null, ObservationOptions? observation = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        // Holds this client's control connection slot for its lifetime; SDK observation can never take it.
        // Ownership transfers only on success, as with the options constructor's own client.
        try
        {
            this.observation = (observation ?? new()).Validated();
            native.SdkConnections.AddClient();
        }
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
        // The failure's token is the caller's, or the native client's lifetime when it was disposed mid-flight.
        if (attempt.Failure is OperationCanceledException cancelled) throw new SubmissionCanceledException(attempt, cancelled.CancellationToken);
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

    /// <summary>
    /// Prepares <paramref name="request"/> and runs it as
    /// <see cref="RunAsync(PreparedSubmission, TargetRunCredentials?, TimeSpan?, CancellationToken)"/> does.
    /// </summary>
    public Task<RunResult> RunAsync(RunRequest request, TargetRunCredentials? credentials = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunAsync(Prepare(request), credentials, timeout, cancellationToken);

    /// <summary>
    /// Submits as <see cref="SubmitAsync(PreparedSubmission, TargetRunCredentials?, CancellationToken)"/> does, then
    /// waits as <see cref="Run.WaitAsync"/> does. <paramref name="timeout"/> bounds only the wait and starts after
    /// acknowledgement; it is not a native run deadline. Every wait exception carries the acknowledged
    /// <see cref="Run"/>, so its <see cref="Run.Submission"/> survives a later timeout, failure or cancellation.
    /// </summary>
    public async Task<RunResult> RunAsync(PreparedSubmission prepared, TargetRunCredentials? credentials = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        RunWait.ValidateTimeout(timeout); // before anything is sent
        var run = await SubmitAsync(prepared, credentials, cancellationToken).ConfigureAwait(false);
        return await run.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Durable watch/log observation on its own connection. After an eligible interruption of an established
    /// stream it reopens exclusively after the last record yielded to the caller. Validated queued records drain
    /// before the interruption surfaces, and received, buffered or server-reported positions are never used.
    /// </summary>
    internal async IAsyncEnumerable<HistoryRecord<TEvent>> ObserveAsync<TEstablishment, TEvent>(RunId runId,
        HistoryStream stream, ExecutionRef? execution, HistoryCheckpoint? after,
        Func<OecpConnection, TEvent?, Cursor?, CancellationToken, Task<NativeSubscription<TEstablishment, TEvent>>> open,
        Func<TEvent, Cursor> cursorOf, [EnumeratorCancellation] CancellationToken cancellationToken)
        where TEvent : class
    {
        var resumeAfter = after;
        var recoveries = 0;
        RunObservationException Failure(RunObservationFailureKind kind, Exception inner)
            => new(kind, runId, stream, resumeAfter, recoveries, inner);

        try { native.SdkConnections.AddObservation(); }
        catch (NativeSubscriptionException admission) { throw Failure(RunObservationFailureKind.Establishment, admission); }
        OecpConnection? connection = null;
        NativeSubscription<TEstablishment, TEvent>? subscription = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var token = linked.Token;
            TEvent? last = null;
            while (true)
            {
                // One budget per (re)establishment. The subscription keeps only the observation token, so a
                // deadline reached during or just after subscribing closes the connection and fails setup instead.
                using (var setup = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    setup.CancelAfter(observation.SetupTimeout);
                    try
                    {
                        connection = await ConnectAsync(setup.Token).ConfigureAwait(false);
                        using (setup.Token.UnsafeRegister(state => ((OecpConnection)state!).Dispose(), connection))
                            subscription = await open(connection, last, resumeAfter?.Cursor, token).ConfigureAwait(false);
                        setup.Token.ThrowIfCancellationRequested();
                    }
                    catch (Exception error) when (!token.IsCancellationRequested)
                    {
                        throw Failure(RunObservationFailureKind.Establishment, setup.IsCancellationRequested
                            ? new TimeoutException("SDK observation setup exceeded its budget.") : error);
                    }
                }

                NativeSubscriptionException? interruption = null;
                await using (var reader = subscription.ReadAllAsync(token).GetAsyncEnumerator(token))
                {
                    while (true)
                    {
                        try { if (!await reader.MoveNextAsync().ConfigureAwait(false)) break; }
                        catch (NativeSubscriptionException failure) { interruption = failure; break; }
                        last = reader.Current;
                        resumeAfter = new HistoryCheckpoint(Target, runId, stream, execution, cursorOf(last));
                        yield return new(last, resumeAfter);
                    }
                }
                // A normal end is native "done" for this subscription; it says nothing about the run's outcome.
                if (interruption is null) yield break;
                RunObservationFailureKind? unrecovered = interruption.Kind switch
                {
                    NativeSubscriptionFailureKind.UnexpectedDisconnect or NativeSubscriptionFailureKind.SlowConsumer
                        => observation.Recover ? null : RunObservationFailureKind.Interrupted,
                    NativeSubscriptionFailureKind.SourceUnavailable => RunObservationFailureKind.SourceUnavailable,
                    NativeSubscriptionFailureKind.Protocol => RunObservationFailureKind.Protocol,
                    // Message size and queue limits; admission cannot occur after establishment.
                    _ => RunObservationFailureKind.ResourceLimit,
                };
                if (unrecovered is { } kind) throw Failure(kind, interruption);
                // Every reopen uses a fresh connection; a dropped one may still be closing.
                await subscription.DisposeAsync().ConfigureAwait(false);
                subscription = null;
                await connection!.DisposeAsync().ConfigureAwait(false);
                connection = null;
                recoveries++;
                await Task.Delay(observation.ReopenDelay, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            native.SdkConnections.RemoveObservation();
        }
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
/// A client-side handle to a known run. Its lifetime is independent of the native run; only an explicit
/// <see cref="ForceAttemptAsync"/> or <see cref="ForceStopAsync"/> requests a stop.
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
    /// Waits for this run's terminal result: reads status and returns a reported terminal result, otherwise watches
    /// from that status's cursor (recovering eligible interruptions per <see cref="ObservationOptions"/>) and returns
    /// the first retained terminal event. If the watch ends normally without one, status is read once more; a
    /// nonterminal status is <see cref="RunWaitFailureKind.Incomplete"/>. A failed run is a result, not an exception.
    /// <para>
    /// <paramref name="timeout"/> null (the default) waits until cancelled. A finite budget covers every status read,
    /// watch setup, reopen delay and recovery, and expiry throws <see cref="RunWaitTimeoutException"/>; zero reads
    /// nothing. Other failures throw <see cref="RunWaitException"/> and cancellation throws
    /// <see cref="RunWaitCanceledException"/>, each with the latest validated evidence. Nothing here stops the run.
    /// </para>
    /// </summary>
    public async Task<RunResult> WaitAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        RunWait.ValidateTimeout(timeout);
        client.EnsureBinding(binding);
        return await WaitAsync(timeout, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The common wait; every exception it throws carries <paramref name="force"/> when composed after force.</summary>
    private async Task<RunResult> WaitAsync(TimeSpan? timeout, NativeAttempt<RunForceResult>? force, CancellationToken cancellationToken)
    {
        RunStatusResult? status = null;
        RunWatchEventNotification? lastEvent = null;
        HistoryCheckpoint? resumeAfter = null;
        RunWaitEvidence Evidence() => new(status, lastEvent, resumeAfter);
        if (timeout == TimeSpan.Zero) throw new RunWaitTimeoutException(this, TimeSpan.Zero, Evidence(), null, force);

        using var budget = timeout is { } finite ? new CancellationTokenSource(finite, client.Time) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var token = linked.Token;
        var stage = RunWaitFailureKind.Status;
        try
        {
            status = await StatusAsync(token).ConfigureAwait(false);
            if (RunResult.FromStatus(status) is { } reported) return reported;

            resumeAfter = new HistoryCheckpoint(client.Target, Id, HistoryStream.Watch, null, status.AtCursor);
            stage = RunWaitFailureKind.Observation;
            await foreach (var record in WatchAsync(resumeAfter, token).ConfigureAwait(false))
            {
                lastEvent = record.Event;
                resumeAfter = record.Checkpoint;
                if (RunResult.FromEvent(record.Event) is { } retained) return retained;
            }

            // Native "done" ends the stream only; the final status decides, and EOF is never success.
            stage = RunWaitFailureKind.Status;
            status = await StatusAsync(token).ConfigureAwait(false);
        }
        // Budget or caller cancellation can surface as any failure of the interrupted operation.
        catch (Exception error) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new RunWaitTimeoutException(this, timeout!.Value, Evidence(), error, force); }
        // The caller's token, or the client's lifetime when it was disposed mid-wait.
        catch (Exception error) when (cancellationToken.IsCancellationRequested || error is OperationCanceledException)
        {
            throw new RunWaitCanceledException(this, Evidence(), error, cancellationToken.IsCancellationRequested
                ? cancellationToken : ((OperationCanceledException)error).CancellationToken, force);
        }
        catch (Exception error)
        {
            throw new RunWaitException(stage, this, Evidence(), stage == RunWaitFailureKind.Status
                ? "Reading the run's status failed while waiting." : "Watching the run failed while waiting.", error, force);
        }
        return RunResult.FromStatus(status) ?? throw new RunWaitException(RunWaitFailureKind.Incomplete, this, Evidence(),
            "The run's watch ended without a terminal event and its status is not terminal.", null, force);
    }

    /// <summary>
    /// Sends one native force request for this run over the control connection and returns the lower attempt:
    /// acknowledged with the returned status (which can still be stopping), rejected, not sent or unknown, including
    /// cancellation. It never retries or waits. A missing or mismatched binding is a not-sent attempt whose failure is
    /// the <see cref="NativeBindingException"/>. A disposed client throws; disposal during the attempt makes it not sent
    /// while the control connection is opening, or unknown once the request may have been sent.
    /// </summary>
    public async Task<NativeAttempt<RunForceResult>> ForceAttemptAsync(CancellationToken cancellationToken = default)
    {
        OecpConnection control;
        try
        {
            client.EnsureBinding(binding);
            control = await client.ControlAsync(cancellationToken).ConfigureAwait(false);
        }
        // Nothing was dispatched: a binding refusal, cancellation, or a failure to (re)open the control connection.
        catch (Exception error) when (error is not ObjectDisposedException)
        {
            return new(client.Target, OecpRunsClient.ForceMethod, Guid.NewGuid(), NativeAttemptOutcome.NotSent, null, error);
        }
        return await control.Runs.ForceAsync(Id, null, client.AfterForceCapture, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes one force attempt as <see cref="ForceAttemptAsync"/> does, then returns the terminal result already in
    /// the acknowledgement or waits as <see cref="WaitAsync(TimeSpan?, CancellationToken)"/> does. Acknowledged
    /// stopping is not terminal. <paramref name="timeout"/> bounds only the wait and starts after acknowledgement; it
    /// is not a deadline for the force request or the run. An unacknowledged attempt throws
    /// <see cref="ForceStopException"/>, or <see cref="ForceStopCanceledException"/> on cancellation; every later wait
    /// exception carries the acknowledgement as <see cref="RunWaitException.ForceAttempt"/>. Force is never resent.
    /// </summary>
    public async Task<RunResult> ForceStopAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        RunWait.ValidateTimeout(timeout); // before anything is sent
        var attempt = await ForceAttemptAsync(cancellationToken).ConfigureAwait(false);
        if (attempt.Response is not { } acknowledgement)
        {
            // Prefer the caller's token; otherwise the failure's own (such as disposal while connecting).
            if (attempt.Failure is OperationCanceledException cancelled)
                throw new ForceStopCanceledException(this, attempt, cancellationToken.IsCancellationRequested ? cancellationToken : cancelled.CancellationToken);
            throw new ForceStopException(this, attempt);
        }
        return RunResult.FromForce(acknowledgement) ?? await WaitAsync(timeout, attempt, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Retained run status history replayed from the start, then followed live until native closes it.
    /// See <see cref="WatchAsync(HistoryCheckpoint?, CancellationToken)"/>.
    /// </summary>
    public IAsyncEnumerable<HistoryRecord<RunWatchEventNotification>> WatchAsync(CancellationToken cancellationToken = default)
        => WatchAsync(null, cancellationToken);

    /// <summary>
    /// Retained run status history replayed exclusively after <paramref name="after"/>, then followed live until
    /// native closes it. Each enumeration opens its own connection and reopens eligible interruptions after the
    /// last record it yielded, per <see cref="ObservationOptions"/>. Normal completion ends that stream only;
    /// it is not run completion. Unrecovered failures throw <see cref="RunObservationException"/>.
    /// </summary>
    public IAsyncEnumerable<HistoryRecord<RunWatchEventNotification>> WatchAsync(HistoryCheckpoint? after,
        CancellationToken cancellationToken = default)
    {
        CheckScope(after, HistoryStream.Watch, null);
        client.EnsureBinding(binding);
        return client.ObserveAsync<RunWatchResult, RunWatchEventNotification>(Id, HistoryStream.Watch, null, after,
            (connection, last, from, token) => connection.Runs.WatchAsync(new() { RunId = Id, FromCursor = from }, last?.Source, token),
            record => record.Cursor, cancellationToken);
    }

    /// <summary>Run-wide retained logs from the start, then live. See <see cref="LogsAsync(ExecutionRef?, HistoryCheckpoint?, CancellationToken)"/>.</summary>
    public IAsyncEnumerable<HistoryRecord<RunLogEventNotification>> LogsAsync(CancellationToken cancellationToken = default)
        => LogsAsync(null, null, cancellationToken);

    /// <summary>
    /// Retained logs replayed exclusively after <paramref name="after"/>, then followed live, optionally only for one
    /// exact execution (run-wide records are then excluded). Recovery and failures follow
    /// <see cref="WatchAsync(HistoryCheckpoint?, CancellationToken)"/>.
    /// </summary>
    public IAsyncEnumerable<HistoryRecord<RunLogEventNotification>> LogsAsync(ExecutionRef? execution, HistoryCheckpoint? after,
        CancellationToken cancellationToken = default)
    {
        CheckScope(after, HistoryStream.Logs, execution);
        client.EnsureBinding(binding);
        return client.ObserveAsync<RunLogsResult, RunLogEventNotification>(Id, HistoryStream.Logs, execution, after,
            (connection, _, from, token) => connection.Runs.LogsAsync(new() { RunId = Id, FromCursor = from, Execution = execution }, token),
            record => record.Cursor, cancellationToken);
    }

    private void CheckScope(HistoryCheckpoint? after, HistoryStream stream, ExecutionRef? execution)
    {
        if (after is null) return;
        var mismatch = after.Target != client.Target ? "target" : after.RunId != Id ? "run"
            : after.Stream != stream ? "stream kind" : after.Execution != execution ? "execution filter" : null;
        if (mismatch is not null)
            throw new ArgumentException($"The checkpoint belongs to a different {mismatch}.", nameof(after));
    }
}
