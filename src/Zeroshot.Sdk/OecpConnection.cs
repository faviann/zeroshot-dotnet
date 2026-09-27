using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

/// <summary>One owned WebSocket, multiplexing bounded OECP calls and subscriptions. No retries, recovery or implicit native stop.</summary>
public sealed partial class OecpConnection : IDisposable, IAsyncDisposable
{
    public const string ProtocolVersion = "openengine.cluster/v1";
    private readonly ClientWebSocket socket;
    private readonly HttpMessageInvoker invoker;
    private readonly IDisposable lease;
    private readonly OperationExecutor executor;
    private readonly OperationLimits limits;
    private readonly Action<OecpConnection> released;
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Dictionary<long, Pending> pending = [];
    private readonly Dictionary<SubscriptionId, IOecpSubscription> subscriptions = [];
    private readonly ObservationDelivery observations;
    private readonly TaskCompletionSource<OecpConnectionFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long nextId;
    private bool closed;
    private Task receiveTask = Task.CompletedTask;
    public OecpClusterClient Cluster { get; }
    public OecpRunsClient Runs { get; }
    /// <summary>Completes with a safe failure on interruption, or null on explicit disposal.</summary>
    public Task<OecpConnectionFailure?> Completion => completion.Task;

    internal Uri Origin { get; }

    internal OecpConnection(Uri origin, ClientWebSocket socket, HttpMessageInvoker invoker, IDisposable lease,
        OperationExecutor executor, OperationLimits limits, ObservationDelivery observations, Action<OecpConnection> released)
    {
        Origin = origin;
        this.socket = socket; this.invoker = invoker; this.lease = lease; this.executor = executor;
        this.limits = limits; this.released = released;
        this.observations = observations;
        Cluster = new(this); Runs = new(this);
    }

    internal void Start() => receiveTask = ReceiveAsync();

    /// <summary>Allocates a one-use unary ID, available for explicit cooperative cancellation while its call is pending.</summary>
    public OecpRequest CreateRequest()
    {
        lock (gate) { ObjectDisposedException.ThrowIf(closed, this); return new(this, checked(++nextId)); }
    }

    public Task<InitializeResult> InitializeAsync(InitializeParams? parameters = null, OecpRequest? request = null, CancellationToken cancellationToken = default)
    {
        parameters ??= new InitializeParams { ProtocolVersion = ProtocolVersion };
        var requested = parameters.ProtocolVersion;
        return CallAsync<InitializeResult>("initialize", NativeJson.SerializeUtf8(parameters), result =>
        {
            if (result.ProtocolVersion != requested || result.ProtocolVersion != ProtocolVersion) throw new JsonException();
        }, cancellationToken, request);
    }

    /// <summary>Sends WebSocket $/cancelRequest for one unary ID. This has no acknowledgement and is neither subscription cancellation nor native run stop.</summary>
    public async Task CancelRequestAsync(RequestId requestId, CancellationToken cancellationToken = default)
    {
        lock (gate) ObjectDisposedException.ThrowIf(closed, this);
        var bytes = CancellationBytes(requestId);
        var state = new Pending(null);
        try
        {
            await executor.ExecuteAsync(new("oecp.cancelRequest", OperationTransport.Oecp, isControl: true), bytes.Length,
                async context => { await SendAsync(bytes, state, context.CancellationToken).ConfigureAwait(false); return true; },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationFailure failure) { throw new NativeOecpException(failure, state.Facts); }
        catch (OperationCancelled error) { throw new OecpOperationCanceledException(error, state.Facts); }
    }

    internal async Task<T> CallAsync<T>(string method, byte[] parameters, Action<T>? validate, CancellationToken cancellationToken, OecpRequest? request = null,
        Action<T>? register = null, bool control = false, Action<Guid, T>? onResponse = null)
    {
        request ??= CreateRequest();
        var id = request.Claim(this);
        var state = new Pending(new RequestId(id));
        // Establishment must register on the receive loop before it can read the next
        // notification. The caller continuation cannot provide that ordering.
        if (register is not null) state.BeforeResponse = response =>
        {
            if (!response.TryGetProperty("result", out var wireResult)) return;
            var result = NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(wireResult.GetRawText()));
            validate?.Invoke(result);
            register(result);
        };
        var bytes = RequestBytes(method, id, parameters);
        JsonRpcError? rpcError = null;
        try
        {
            return await executor.ExecuteAsync(new(method, OperationTransport.Oecp, control), bytes.Length, async context =>
            {
                lock (gate)
                {
                    if (closed) throw context.Failure(OperationFailureKind.Transport, OperationStage.Request);
                    pending.Add(id, state);
                }
                try
                {
                    await SendAsync(bytes, state, context.CancellationToken).ConfigureAwait(false);
                    var response = await state.Response.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (response.TryGetProperty("error", out var error))
                        {
                            var errorBytes = Encoding.UTF8.GetBytes(error.GetRawText());
                            if (errorBytes.Length > limits.DiagnosticBytes)
                                throw context.Failure(OperationFailureKind.SizeLimit, OperationStage.Diagnostic);
                            rpcError = NativeJson.DeserializeUtf8<JsonRpcError>(errorBytes);
                            throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response, errorBytes);
                        }
                        var result = NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(response.GetProperty("result").GetRawText()));
                        validate?.Invoke(result);
                        onResponse?.Invoke(context.CorrelationId, result);
                        return result;
                    }
                    catch (Exception error) when (error is JsonException or ArgumentException)
                    {
                        Close(NativeOecpFailureKind.Protocol);
                        throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response);
                    }
                }
                catch (ConnectionInterrupted interrupted)
                { throw context.Failure(Enum.Parse<OperationFailureKind>(interrupted.Kind.ToString()), OperationStage.Response); }
                finally { lock (gate) pending.Remove(id); }
            }, cleanup: async token =>
            {
                if (register is not null)
                {
                    // Cleanup may race the send continuation. Any started establishment
                    // can allocate a remote stream, even before SendCompleted is published.
                    // Sharing SendAsync's gate makes this decision atomic with its token
                    // check and SendStarted update; a later send sees the cancelled token.
                    lock (gate)
                        if (state.SendStarted && !state.ResponseReceived) Close(NativeOecpFailureKind.Transport);
                    return;
                }
                // An abandoned read does not stop a run. The native WebSocket binding accepts
                // this cooperative notification; late replies keep their original ID and are ignored.
                if (state.SendCompleted && !state.ResponseReceived)
                {
                    try { await SendAsync(CancellationBytes(new(id)), new Pending(null), token).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationFailure failure) { throw new NativeOecpException(failure, state.Facts, rpcError); }
        catch (OperationCancelled error) { throw new OecpOperationCanceledException(error, state.Facts); }
    }

    /// <summary>Sends one mutation and returns its evidence. Only isRefusal errors prove that native had no effect.</summary>
    // afterCapture lets tests place cancellation between capture and operation completion. That placement is
    // deterministic only because the cancelled WaitAsync continuation runs inline during Cancel().
    internal async Task<NativeAttempt<T>> AttemptAsync<T>(string method, byte[] parameters, Action<T>? validate,
        Func<JsonRpcError, bool> isRefusal, bool control, OecpRequest? request, CancellationToken cancellationToken,
        Action? afterCapture = null) where T : class
    {
        var correlationId = Guid.Empty;
        T? acknowledged = null;
        Exception? failure = null;
        var sendStarted = false;
        try
        {
            await CallAsync(method, parameters, validate, cancellationToken, request, control: control, onResponse: (id, result) =>
            {
                correlationId = id;
                Volatile.Write(ref acknowledged, result);
                afterCapture?.Invoke();
            }).ConfigureAwait(false);
        }
        catch (NativeOecpException error) { (failure, correlationId, sendStarted) = (error, error.CorrelationId, error.Dispatch.SendStarted); }
        catch (OecpOperationCanceledException error) { (failure, correlationId, sendStarted) = (error, error.CorrelationId, error.Dispatch.SendStarted); }

        // Cancellation can end the call after validation but before the operation completes.
        var captured = Volatile.Read(ref acknowledged);
        var outcome = captured is not null ? NativeAttemptOutcome.Acknowledged
            : !sendStarted ? NativeAttemptOutcome.NotSent
            : failure is NativeOecpException { RpcError: { } rpcError } && isRefusal(rpcError) ? NativeAttemptOutcome.Rejected
            : NativeAttemptOutcome.Unknown;
        return new(Origin, method, correlationId, outcome, captured, captured is null ? failure : null);
    }

    // Connection admission and dispatch answer these before any backend method runs
    // (openengine-cluster-server connection/admission.rs and dispatch.rs).
    internal static bool IsDispatchRefusal(JsonRpcError error) => (error.Code, error.Data?.Code) is
        (-32601, _) or
        (-32602, "SCHEMA_VIOLATION") or
        (-32600, "DUPLICATE_REQUEST_ID") or
        (-32000, "SERVER_BUSY");

    private async Task SendAsync(byte[] bytes, Pending state, CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (closed) throw new ConnectionInterrupted(NativeOecpFailureKind.Transport);
                token.ThrowIfCancellationRequested();
                state.SendStarted = true;
            }
            try
            {
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                state.SendCompleted = true;
            }
            catch
            {
                // ClientWebSocket cancels an active write by aborting its connection.
                Close(NativeOecpFailureKind.Transport);
                throw;
            }
        }
        finally { sendGate.Release(); }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            var ceiling = Math.Min(limits.MessageBytes, limits.ResponseBytes);
            var buffer = new byte[Math.Min(8192, ceiling)];
            while (true)
            {
                using var body = new MemoryStream();
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, ceiling - body.Length + 1)), lifetime.Token).ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close) { Close(NativeOecpFailureKind.Transport); return; }
                    if (frame.MessageType != WebSocketMessageType.Text) { Close(NativeOecpFailureKind.Protocol); return; }
                    if (body.Length + frame.Count > ceiling) { Close(NativeOecpFailureKind.SizeLimit); return; }
                    body.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                var bytes = body.ToArray();
                _ = new UTF8Encoding(false, true).GetCharCount(bytes);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct().Count() != root.EnumerateObject().Count() ||
                    !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
                    throw new JsonException();
                if (root.TryGetProperty("method", out _)) { ReceiveNotification(root, bytes.Length); continue; }
                if (!root.TryGetProperty("id", out var wireId) || root.TryGetProperty("result", out _) == root.TryGetProperty("error", out _)) throw new JsonException();
                var id = NativeJson.DeserializeUtf8<RequestId>(Encoding.UTF8.GetBytes(wireId.GetRawText()));
                lock (gate)
                {
                    if (id.Number is not { } number || number <= 0 || number > nextId) throw new JsonException();
                    if (pending.TryGetValue(number, out var state))
                    {
                        if (!state.SendStarted || state.ResponseReceived) throw new JsonException();
                        state.ResponseReceived = true;
                        state.BeforeResponse?.Invoke(root);
                        state.Response.TrySetResult(root.Clone());
                    }
                    // Retired local IDs may be legitimate replies after cancellation; no unbounded tombstone table.
                }
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or DecoderFallbackException or InvalidOperationException)
        { Close(NativeOecpFailureKind.Protocol); }
        catch (Exception) { Close(NativeOecpFailureKind.Transport); }
    }

    private void Close(NativeOecpFailureKind? failure)
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            foreach (var call in pending.Values) call.Response.TrySetException(new ConnectionInterrupted(failure ?? NativeOecpFailureKind.Transport));
            pending.Clear();
            foreach (var subscription in subscriptions.Values) subscription.Disconnected(failure);
            subscriptions.Clear();
        }
        lifetime.Cancel();
        socket.Abort(); socket.Dispose(); invoker.Dispose(); lease.Dispose(); released(this);
        completion.TrySetResult(failure is { } kind ? new(kind) : null);
    }

    public void Dispose() => Close(null);
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await receiveTask.WaitAsync(limits.CleanupTimeout).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static byte[] RequestBytes(string method, long id, byte[] parameters)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteString("jsonrpc", "2.0"); writer.WriteNumber("id", id);
            writer.WriteString("method", method); writer.WritePropertyName("params"); writer.WriteRawValue(parameters); writer.WriteEndObject();
        }
        return output.ToArray();
    }
    private static byte[] CancellationBytes(RequestId id)
    {
        var encoded = NativeJson.SerializeUtf8(id);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteString("jsonrpc", "2.0"); writer.WriteString("method", "$/cancelRequest");
            writer.WriteStartObject("params"); writer.WritePropertyName("id"); writer.WriteRawValue(encoded);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        return output.ToArray();
    }
    private sealed class Pending(RequestId? id)
    {
        public volatile bool SendStarted;
        public volatile bool SendCompleted;
        public volatile bool ResponseReceived;
        public Action<JsonElement>? BeforeResponse;
        public TaskCompletionSource<JsonElement> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OecpDispatchFacts Facts => new(id, SendStarted, SendCompleted, ResponseReceived);
    }
    private sealed class ConnectionInterrupted(NativeOecpFailureKind kind) : Exception
    { public NativeOecpFailureKind Kind { get; } = kind; }
}

public sealed partial class OecpRunsClient
{
    private readonly OecpConnection connection;
    internal OecpRunsClient(OecpConnection connection) => this.connection = connection;
    public Task<RunListResult> ListAsync(OecpRequest? request = null, CancellationToken cancellationToken = default)
        => connection.CallAsync<RunListResult>("run/list", NativeJson.SerializeUtf8(new RunListParams()), null, cancellationToken, request);

    /// <summary>Checks the exact run and, when supplied, its known source. Status does not prove a submission key or request digest.</summary>
    public Task<RunStatusResult> StatusAsync(RunId runId, ResolvedSource? expectedSource = null, OecpRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (expectedSource is not null) _ = NativeJson.SerializeUtf8(expectedSource);
        return connection.CallAsync<RunStatusResult>("run/status", NativeJson.SerializeUtf8(new RunStatusParams { RunId = runId }), result =>
        {
            if (result.RunId != runId || (expectedSource is not null && result.Source != expectedSource)) throw new JsonException();
        }, cancellationToken, request);
    }
}

/// <summary>A connection-owned one-use unary correlation ID. Retain it to request cooperative cancellation.</summary>
public sealed class OecpRequest
{
    private readonly OecpConnection connection;
    private readonly long number;
    private int claimed;
    public RequestId Id => new(number);
    internal OecpRequest(OecpConnection connection, long number) { this.connection = connection; this.number = number; }
    internal long Claim(OecpConnection owner)
    {
        if (owner != connection || Interlocked.Exchange(ref claimed, 1) != 0)
            throw new ArgumentException("A request belongs to its connection and can be used only once.");
        return number;
    }
}
