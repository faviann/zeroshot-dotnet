# WebSocket OECP inspection and subscriptions

The lower client connects to an existing acquired session and makes individual,
bounded calls. It does not start native processes or retry failed operations.

```csharp
await using var native = NativeClient.ForHttp(new NativeClientOptions { Origin = origin });
var discovery = await native.Target.DiscoverAsync();
var session = await native.Target.CreateOecpSessionAsync(discovery);
await using var oecp = await native.ConnectOecpAsync(session);
var initialized = await oecp.InitializeAsync();
var cluster = await oecp.Cluster.GetAsync();
var inventory = await oecp.Runs.ListAsync();
var status = await oecp.Runs.StatusAsync(runId, expectedSource);
```

Connection, initialization and each unary call consume separate complete request
budgets. They reuse the native client's admission and deadlines, including across
HTTP and WebSocket connections. `TransportOptions` defaults to 18 OECP connections,
1 MiB encoded OECP requests, 8 MiB complete messages/responses, 64 KiB error data,
30 s requests, 10 s connection setup and 5 s cleanup. Concurrent writes are serialized
and their wait consumes the request deadline. Connection disposal aborts the owned
socket within bounded cleanup and releases its capacity. Native-client disposal ends
all its connections; supplied HTTP clients remain governed by their ownership flag.

The WebSocket has one active receive loop, supports fragmented text messages and
correlates concurrent replies. Binary messages, invalid UTF-8/JSON, wrong JSON-RPC
versions, malformed IDs, unexpected IDs and malformed/foreign run results fail
explicitly. Request IDs use native signed 64-bit integers or strings; this binding
allocates positive integer IDs. Retired local IDs are ignored so a delayed response
after cancellation cannot complete another call. No ID is reused on a connection.
`Completion` exposes a connection-wide failure even when no unary call is pending;
explicit disposal completes it with null. Run watch, log and attachment subscriptions share
this receive loop and the native client's observation budget.

Liveness uses .NET's WebSocket PING/PONG implementation: 30 s ping interval and
15 s pong timeout, independently configurable as `WebSocketPingInterval` and
`WebSocketPongTimeout`. `EnableWebSocketLiveness = false` explicitly disables it.
The receive loop processes control frames even while the connection is otherwise
idle. A missing pong interrupts the connection; silence alone is not run failure.

Every dial revalidates the session endpoint's scheme, host and effective port
against the HTTP target origin, plus the optional session bearer. Only that bearer
is sent on the handshake. The owned WebSocket handler uses normal TLS validation
and disables redirects, cookies and decompression. A supplied discovery/session
HttpClient is not reused as a WebSocket transport. There is no credential refresh.

`ClusterStatus` and native `RunStatus` remain distinct. Native run status includes
all active executions or the complete terminal result and metadata, including token
usage. `RunStatusResult` preserves source identity, size, cursor and workspace
recovery. `StatusAsync` requires the requested run ID to match; supplying
`expectedSource` additionally checks repository, branch and revision. Native empty
cluster `get` remains empty even when runs exist. Status does not prove a submission
key, retained request digest or terminal-event durability.

`NativeOecpException` distinguishes resource admission, deadline, size, transport,
protocol and RPC errors. `RpcError` exposes native signed numeric codes and open
`DomainErrorData.Code` plus arbitrary details. Remote text stays out of exception
formatting; raw error export additionally requires `CaptureRawDiagnostics`.
`Dispatch` records the request ID, whether sending started/completed, and whether a
correlated response envelope arrived. These are transport facts, not effect or
rollback guarantees. An invalid response can have `ResponseReceived = true`.

For cooperative unary cancellation, allocate a one-use handle before starting the
call; its ID is available while the call is pending:

```csharp
var request = oecp.CreateRequest();
var pending = oecp.Runs.ListAsync(request);
await oecp.CancelRequestAsync(request.Id); // $/cancelRequest, no acknowledgement
var result = await pending;
```

The notification does not cancel the caller's wait or promise remote rollback. A
caller's cancellation token ends its local wait with `OecpOperationCanceledException`
and retained dispatch facts. On cancellation or timeout after a completed send,
bounded cleanup also attempts the cooperative notification. Responses racing with
cancellation retain observed facts. Neither form sends `subscription/cancel` or
native `run/force`; disposing a connection never stops a native run.

`OecpTests` uses deterministic loopback peers for correlation, malformed/foreign
responses, fragmentation, bounds, cancellation, disconnects and liveness.
`OecpContractTests` verifies the full source-backed inspection closure in
`Schemas/oecp.schema.json`, extracted from the pinned
[`schema.json`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/protocol/openengine-cluster/v1/schema.json)
and [`native-v2-observation.schema.json`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/protocol/openengine-cluster/v1/native-v2-observation.schema.json).
Shared definitions remain in `contracts.schema.json`; `TokenCount` names the native
inline token-counter schema.
`InitializeParams` accepts arbitrary protocol strings, matching native decoding, so
unsupported negotiation reaches the target; successful responses must use v1.
The stock Linux witness separately proves populated inventory, exact run/source
status, unsupported protocol rejection and empty cluster get. Its recorded native
terminal failure is inspection evidence, not a provider execution claim. Live
hosted/private authorities and populated recovery metadata remain unverified;
controlled peers and source-backed fixtures cover those shapes.

## One watch or log subscription

```csharp
await using var watch = await oecp.Runs.WatchAsync(
    new RunWatchParams { RunId = runId, FromCursor = lastDeliveredCursor },
    expectedSource, cancellationToken);
var establishment = watch.Establishment;
await foreach (var record in watch.ReadAllAsync(cancellationToken))
{
    // Full status, title, source, size and opaque cursor are available here.
    Inspect(record);
}
var close = await watch.Completion;
var callerCursor = watch.LastDeliveredCursor;
var serverCursor = close.ServerClose?.LastDeliveredCursor;
```

`Runs.LogsAsync(new RunLogsParams { RunId = runId, FromCursor = cursor,
Execution = execution }, cancellationToken)` returns a separate complete log
contract: subscription/run IDs, cursor, producer timestamp, optional execution,
and level/target/message. Omitting the execution selector includes run-wide system
logs; supplying one requires exact equality, including rejecting records without
an execution. Log text follows native UTF-8 byte and control-character bounds.
Watch records preserve all status variants and terminal metadata; they have no
workspace recovery field. `NativeJson` validates both complete native contracts.

Establishment is registered in the receive loop before the next notification can
arrive. The result must identify the requested run and echo a supplied `FromCursor`.
That cursor is an exclusive starting position, not a current-history watermark.
Cursors remain opaque; the client never parses, orders or increments them.
Watch source identity must match `expectedSource` when supplied, or remain equal
to the first validated source. Active subscription records with malformed shapes
or foreign run/source/execution identities fail that subscription without delivery.
Unknown subscription IDs are ignored, including legitimate frames still in flight
after local detach. No retired-ID table is retained.

`NativeSubscription<TEstablishment,TEvent>` has one reader. `Completion` records
server closure, local disposal/cancellation, local failure or unexpected disconnect.
The original `subscription/closed` body preserves `done`, `SLOW_CONSUMER` and
`SOURCE_UNAVAILABLE`, including the server's cursor. The caller cursor advances
only when enumeration hands over a record and can continue advancing as buffered
records drain after completion. It is never replaced by the server cursor or the
establishment cursor. Neither `done` nor EOF proves successful native execution.
After validated buffered records drain, incomplete server closes, local overflow,
protocol failure and disconnect throw `NativeSubscriptionException` with safe typed
metadata. An unexpected disconnect has no fabricated server-close body.

All connections share the existing client observation limits: 16 subscriptions,
256 queued records and 8 MiB encoded record bytes per stream, 32 MiB aggregate.
Both record and byte limits apply. The event's complete encoded frame is charged;
there is no second adapter queue and no task per record. Overflow ends only its
stream, retains already validated records for draining and uses reserved control
capacity to detach. No record is silently dropped from an active healthy stream.

Disposal, breaking enumeration, or either the opening or enumeration token cancels
local observation and discards undelivered records. Cleanup sends the native
`subscription/cancel` notification through the existing connection under the finite
cleanup budget. It has no acknowledgement and never sends native stop. Failed
cleanup aborts the connection to release remote observation ownership; multiplexed
callers share that connection-wide failure exposure. Cancellation of a dispatched
establishment without a response also closes its connection because the remote
subscription ID is still unknown. No operation here automatically reopens or waits
for native execution to finish.

`SubscriptionTests` and `SubscriptionContractTests` exercise immediate events,
complete projections, exact identity/selector checks, opaque cursors, close reasons,
detached stragglers, cancellation and byte/record congestion with usable control
capacity. The packed `ObservationConsumer` in the stock Linux witness proves
history replay and genuinely new watch/log records after establishment, then exact
run/source and exclusive replay after restarting the target with retained storage.
Native environment hooks generate the records and an intentional setup failure;
this is observation evidence and makes no provider execution claim.

## One live execution attachment

```csharp
await using var attachment = await oecp.Runs.AttachAsync(
    new RunAttachParams { RunId = runId, Execution = execution }, cancellationToken);
await foreach (var record in attachment.ReadAllAsync(cancellationToken))
{
    // Event is WorkingAgentAttachEvent, OutputAgentAttachEvent or SettledAgentAttachEvent.
    Inspect(record.RunId, record.Execution, record.Event);
}
var completion = await attachment.Completion;
```

Use the exact opaque execution reference supplied by native status. Both the
establishment and every event must match the requested run and execution.
`OutputAgentAttachEvent.Text` preserves native assistant-display output with its
16,384 UTF-8 byte limit and prohibition on control characters. Invalid or oversized
text fails explicitly; the client never truncates or silently substitutes it.
Native `NOT_FOUND` (unknown execution) and `GONE` (inactive or not live) remain
distinct `NativeOecpException.RpcError.Data.Code` values.

Attachment uses the same bounded `NativeSubscription` lifecycle and shared
observation capacity as watch/logs. It has no input channel, replay or automatic
recovery, and `LastDeliveredCursor` stays null. On interruption, the establishment,
already delivered events and completion origin retain available context; validated
queued events may drain before the failure. Disposal and cancellation detach only.
A `SettledAgentAttachEvent` describes that execution's attachment data. Neither it
nor a normal subscription close creates a terminal run result; query run status or
observe a terminal watch record for that separate evidence.

`AttachmentTests` covers the full closed event algebra, identity mismatches,
malformed/oversized output, encoded message/queue bounds, disposal, cancellation,
interruption and distinct refusals. The stock Linux witness's packed
`AttachmentConsumer` uses a controlled provider execution and real Git checkout
inside a private mount namespace. It proves native working/output/settled delivery,
cursorless close and the actual inactive/unknown refusals.
