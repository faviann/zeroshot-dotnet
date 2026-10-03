# WebSocket OECP inspection, subscriptions, force and recovery

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
budgets. Initialize and run status are control operations and can use the client's
reserved control request slots. They reuse the native client's admission and deadlines, including across
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
[`schema.json`](https://github.com/the-open-engine/zeroshot/blob/3ee1192cec359a0b997f464e703a936e8b67d63c/protocol/openengine-cluster/v1/schema.json)
and [`native-v2-observation.schema.json`](https://github.com/the-open-engine/zeroshot/blob/3ee1192cec359a0b997f464e703a936e8b67d63c/protocol/openengine-cluster/v1/native-v2-observation.schema.json).
Shared definitions remain in `contracts.schema.json`; `TokenCount` names the native
inline token-counter schema.
`InitializeAsync` always sends `openengine.cluster/v1`, and a successful response
must name v1. A target that no longer supports v1 answers with an RPC error.
The stock Linux witness separately proves populated inventory, exact run/source
status and empty cluster get. Its recorded native
terminal failure is inspection evidence, not a provider execution claim. The private
witness initializes, reads the empty get and checks run status over a private-capability
session. Live hosted authority remains unverified; controlled peers and source-backed
fixtures cover that shape. The recovery witness below observes populated
workspace recovery metadata.

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

## One native force request

```csharp
var attempt = await oecp.Runs.ForceAsync(runId, cancellationToken: cancellationToken);
if (attempt is { Outcome: NativeAttemptOutcome.Acknowledged, Response: var acknowledged })
    Inspect(acknowledged!.Status); // StoppingRunStatus or FinishedRunStatus
```

`ForceAsync` sends exactly one native `run/force` for the exact run and returns a
`NativeAttempt<RunForceResult>`. It never retries, waits for termination or reads
status. The acknowledged `RunForceResult` is the complete native projection: run
identity, title, source, size, cursor, status and optional workspace recovery. It
must name the requested run. A `stopping` acknowledgement is not terminal, and
neither form proves that all physical activity has ceased. Stock native usually
awaits runtime cleanup before replying, so its acknowledgement can already contain
the `force_stopped` terminal result; durable watch history still records the
intermediate `stopping` projection. Forcing a terminal run is acknowledged with its
existing status.

Outcomes follow the shared attempt model:

| Outcome | Evidence |
| --- | --- |
| `Acknowledged` | A validated response. It wins if caller cancellation lands after validation. |
| `NotSent` | Sending never started: admission, cancellation, request size, or the connection closing between request creation and send. |
| `Rejected` | Native refused before any force effect: `-32601`, `-32602`/`SCHEMA_VIOLATION`, `-32600`/`DUPLICATE_REQUEST_ID`, `-32000`/`SERVER_BUSY` or `-32000`/`NOT_FOUND`. |
| `Unknown` | Anything else after sending started: lost reply, deadline, cancellation, disconnect, malformed, foreign or oversized reply, internal error, `SOURCE_UNAVAILABLE` or another code. |

A connection whose `Completion` has already finished throws `ObjectDisposedException`
from `ForceAsync`, like every other call on it, instead of returning an attempt.

`Failure` retains the `NativeOecpException` or `OecpOperationCanceledException`
with its dispatch facts, RPC codes and correlation ID. A lost reply or dropped
waiter can follow a real stop: native completes force after its waiter is dropped.
Cancellation after sending uses the same cooperative `$/cancelRequest` cleanup as
other unary calls; that notification is not a force request and rolls nothing back.

Force is a control operation. It uses the client's reserved control request
capacity, so ordinary calls filling their share cannot block it. It can share a
connection with active subscriptions, whose observation budgets do not consume
request capacity. Native's per-connection task limit still applies and returns
`SERVER_BUSY`.

`ForceTests` covers the exact request, complete stopping/terminal acknowledgements,
the refusal table, one-send unknown outcomes for each fault, pre-send cancellation,
acknowledgement capture across cancellation and control admission. The stock Linux
witness's packed `ForceConsumer` forces an active controlled execution, records the
acknowledged phase, then verifies durable `stopping` history, the `force_stopped`
terminal record and status, an acknowledged repeated force and unknown-run
`NOT_FOUND`.

## Shared cluster contract and trusted run submit

```csharp
var plan = await oecp.Cluster.PlanAsync(new PlanParams { Graph = graph });
var applied = await oecp.Cluster.ApplyAsync(new ApplyParams
{
    Graph = graph, Input = input, IfGeneration = 0, IdempotencyKey = new("apply-1")
});
await using var watch = await oecp.Cluster.WatchAsync(new WatchParams { RunId = runId });
var submitted = await oecp.Runs.SubmitAsync(new RunSubmitParams { RunId = runId, Submission = submission });
```

`Cluster` binds the twelve generic cluster methods: `InitializeAsync` (on the
connection), `GetAsync`, `PlanAsync`, `ApplyAsync`, `UpdateAsync`, `StopAsync`,
`RetryAsync`, `ResubmitAsync`, `DeleteAsync`, `WatchAsync`, `LogsAsync` and
`AttachAgentAsync`. `Runs.SubmitAsync` binds trusted-controller `run/submit`. These
are shared protocol contracts, not aliases for `run/*`. The client never converts
one into the other, and never treats `InitializeResult.Capabilities` as a support
matrix. Support belongs to the backend: stock DirectTarget refuses all ten
unimplemented cluster methods with `-32000`/`INVALID_PHASE`, even though initialize
advertises `logs` and `agentAttach`. Its `get` stays empty, and it refuses
`run/submit` with `RUN_CONFLICT`. Submit a direct run over HTTP instead.

Contracts in `Contracts/Cluster.cs` come from the pinned `schema.json` closure in
`Schemas/oecp.schema.json`. They preserve these distinctions:

- `ifGeneration`, `ifRunId` and `idempotencyKey` fences. A zero apply generation
  requires an empty cluster.
- `ApplyParams.Input` and `ResubmitParams.ReplacementInput` are `Optional<JsonElement>`.
  Omission and explicit JSON null are different requests.
- Apply `DryRun` is sent only when true. A dry run must omit input and key; native
  reports a violation as `SCHEMA_VIOLATION`.
- `UpdateParams` needs at least one of labels, log level or suspension. Labels
  replace the whole map.
- `PlanResult` diagnostics and bounds, and the `ApplyResult` diff, are kept for dry
  runs. Nullable apply/delete identities stay null.
- `StopResult` keeps the accepted and effective modes separately. The response must
  echo the requested mode as accepted.
- `ResubmitResult` must name the requested predecessor as `PriorRunId`.
- `DomainErrorData.NoRetryableFrontierReason` types the `exhausted`, `success`,
  `active` and `consumed` reasons of `NO_RETRYABLE_FRONTIER`. An unrecognized reason
  is null, and the raw value remains in `Details`.
- `run/submit` accepts any acknowledged run ID, because native deduplication can
  return an earlier run.

The seven mutations return `NativeAttempt<T>` from the same single-send helper as
force; only `StopAsync` uses reserved control capacity. `Rejected` is limited to
dispatcher refusals (`-32601`; `-32602`/`SCHEMA_VIOLATION`;
`-32600`/`DUPLICATE_REQUEST_ID`; `-32000`/`SERVER_BUSY`) and these per-operation
`-32000` codes:

| Operation | Pre-effect refusals |
| --- | --- |
| `apply` | `GRAPH_INVALID`, `GENERATION_CONFLICT`, `IDEMPOTENCY_REUSE`, `INVALID_PHASE`, `CANCELLED` |
| `update`, `stop` | `GENERATION_CONFLICT`, `IDEMPOTENCY_REUSE`, `INVALID_PHASE` |
| `retry` | those three plus `NO_RETRYABLE_FRONTIER` |
| `resubmit`, `delete` | `GENERATION_CONFLICT`, `RUN_CONFLICT`, `IDEMPOTENCY_REUSE`, `INVALID_PHASE`, `CANCELLED` |
| `run/submit` | `-32602`/`GRAPH_INVALID`, `IDEMPOTENCY_REUSE`, `RUN_CONFLICT`, `INVALID_PHASE` |

The cluster rows come from the shared admission server's store-error mapping
(`openengine-cluster-server` admission and lifecycle), which commits nothing before
returning them. No stock backend implements these methods. The only live evidence
is therefore the stock `INVALID_PHASE` and `RUN_CONFLICT` refusals. Every other code,
including `INTERNAL_ERROR`, `SOURCE_UNAVAILABLE` and `source_checkout_unavailable`, is
`Unknown`.

The three cluster subscriptions reuse `NativeSubscription` and the shared
observation budget:

- `WatchAsync` delivers `EventNotification` records with the closed `WatchEvent`
  algebra: `phase`, `node_begin`, `node_end`, `bookmark`, `fault` and `finished`.
  `WatchResult.AtCursor` is the tail captured at establishment, not an echo of
  `FromCursor`. A requested run must be the resolved run. Without one, native may
  return a null run while parked; the first record then fixes the run, and later
  foreign records fail the subscription.
- `LogsAsync` takes no filters and delivers cluster-wide, future-only
  `LogEventNotification` records.
- `AttachAgentAsync` takes only an execution and has no run selector.
- Logs and agent attachment are cursorless. `LastDeliveredCursor` stays null, and a
  `subscription/closed` that carries a cursor is a protocol failure. Native `fault`
  events describe retry dispositions but never authorize a retry.

`ClusterTests` replays the pinned native admission, lifecycle, delete and resubmit
goldens and the admission-error golden through every typed method. It checks exact
request parameters and complete response round trips. It also covers the watch, log
and attachment session goldens, both native backend-fault fixtures, every event
variant, the per-operation refusal table, identity fences, Stop control capacity,
parked watch resolution and cursorless closes. The packed `DiscoveryConsumer` in the
stock Linux witness proves the eleven stock refusals and unchanged empty get and
inventory afterwards.

## Checkpoints and workspace recovery

```csharp
var page = await oecp.Runs.CheckpointsAsync(new() { RunId = failedRunId, Limit = 10 });
var next = await oecp.Runs.CheckpointsAsync(new() { RunId = failedRunId, After = page.NextAfter });
var resumed = await oecp.Runs.ResumeAsync(failedRunId, successorRunId,
    new CheckpointResumeFrom { CheckpointId = page.Checkpoints[^1].CheckpointId }, freshCredentials);
var discarded = await oecp.Runs.DiscardWorkspaceAsync(failedRunId);
```

`CheckpointsAsync` reads one page of native `run/checkpoints`. `After` is an
exclusive, opaque, run-scoped checkpoint ID and is never parsed. An omitted `Limit`
means native's 50; values outside 1–100 throw before sending. The page must name
the requested run, hold at most the effective limit, and report `NextAfter` only
as its last checkpoint. Anything else is a `Protocol`
failure. Refusals throw `NativeOecpException` like other reads.

`ResumeAsync` sends one `run/resume` to admit a successor with the caller's
proposed ID. An omitted selection and an explicit `RestartResumeFrom` both restart
the graph on the latest retained workspace; they stay distinct on the wire.
`CheckpointResumeFrom` restores an entry checkpoint. Fresh connection values, the
resolver and the GitHub token are supplied as a separate `TargetRunCredentials`
argument, sent only with that attempt, and never appear in default formatting.
An empty connection map is omitted, as native does. The acknowledgement must name
the requested successor and source run. Native imports no provider session,
secret or token usage from the source.

`DiscardWorkspaceAsync` sends one `run/discard_workspace`. It destroys the retained
recovery workspace but not the run or its history. `Discarded` is `false` when no
recoverable workspace remained.

Both mutations return `NativeAttempt<T>` with the force outcome model, on ordinary
request capacity. Only these native refusals are `Rejected`, all of them before
any successor is created or workspace touched:

| Refusal | Source |
| --- | --- |
| `-32601`, `-32602`/`SCHEMA_VIOLATION`, `-32600`/`DUPLICATE_REQUEST_ID`, `-32000`/`SERVER_BUSY` | Native dispatch. `SCHEMA_VIOLATION` includes the target's canonical UUIDv7 check on both IDs. |
| `-32000`/`INVALID_PHASE` | The target's capability gate: recovery unavailable, or checkpoints unavailable for a checkpoint selection. |
| `-32000`/`NOT_FOUND` | The source run's ledger lookup. |
| `-32000`/`IDEMPOTENCY_REUSE` | Resume only: the ledger refused to create the successor. `details.runId` is kept. |

Native reports a source that is not recoverable, one already resumed, a successor
ID used by another run and failed discard cleanup all as `INTERNAL_ERROR`, which
can follow successor creation or partial cleanup, so it stays `Unknown`. Native
does not deduplicate resume: repeating a resume whose reply was lost returns that
error. The source run's status reports a recorded successor in
`workspaceRecovery.successorRunId`, and the successor's reports `resumedFrom`;
reconciling an unknown attempt is the caller's decision.

Discovery advertises `workspace_recovery` and `workspace_checkpoints`, but the
connection does not consult it; the native gate answers. Direct and private-mode
targets support both. Hosted-access targets and the portable controller refuse
them with `INVALID_PHASE`, and the portable controller lists checkpoints for its
own run only. Hosted HTTP recovery routes are a separate binding.

`RecoveryTests` covers the exact checkpoint, resume and discard frames, including
omitted versus explicit restart and separate credentials; local limit refusal;
page contract violations; the capability refusal; the refusal tables; and foreign
acknowledgements, lost replies and disconnects as one-send unknown outcomes.

The stock Linux witness's packed `RecoveryConsumer` runs on the attachment target
with a controlled worker that fails after checkout. Native retains that
`worker_failed` workspace with its connection requirements. The consumer pages
the worker entry checkpoint, resumes with the selection omitted, and checks both
status links. It repeats the source resume, which returns `INTERNAL_ERROR` and
admits nothing. It resumes the successor from its checkpoint, then discards the
second successor's workspace (`true`, then `false`). It also records the reachable
refusals: `NOT_FOUND`, `SCHEMA_VIOLATION`, and `INTERNAL_ERROR` for succeeded,
force-stopped and discarded sources. Native disposes a force-stopped workspace.
Each run has one checkpoint, so live paging never gets past the first page.
`INVALID_PHASE` has deterministic coverage only; a stock direct target supports
both capabilities.

## NDJSON streams and Unix controllers

```csharp
// A caller-known controller socket; the connection owns and closes it.
await using var controller = await OecpConnection.ConnectUnixAsync(socketPath, cancellationToken: ct);
// Caller-supplied streams stay open by default.
await using var borrowed = await OecpConnection.FromStreamsAsync(input, output, leaveOpen: true, cancellationToken: ct);
await borrowed.InitializeAsync(cancellationToken: ct);
```

Both factories return an ordinary `OecpConnection`. `Cluster`, `Runs`, subscriptions,
force attempts, correlation, validation, deadlines and observation limits behave as
on WebSocket. They bind existing endpoints only: nothing derives a socket path from
state, launches native, sends an HTTP handshake or reopens a controller. Each
connection has its own request and observation budgets from an optional
`TransportOptions`, because there is no `NativeClient`.

Framing follows native NDJSON: each request is one JSON-RPC message followed by
`\n`, and each received line is one message, split or coalesced across reads.
Received lines use the 8 MiB message bound (`SizeLimit` beyond it); native accepts
request lines up to 1 MiB, the default `MaxOecpRequestBytes`. Empty or non-JSON-RPC
lines are protocol failures. Native writes diagnostics to a separate stream that
the client never reads or requires. EOF, with or without a partial line, is a
`Transport` disconnect: pending calls fail, subscriptions drain validated records
and end with `UnexpectedDisconnect`, and no close body or completion is invented.
There is no heartbeat or idle deadline; a quiet stream is not a failure.

Native NDJSON does not intercept `$/cancelRequest`; it would answer it as an invalid
request. `CancelRequestAsync` therefore throws `NotSupportedException` on these
connections, and cancelling a unary call only ends the local wait with its dispatch
facts. A late reply is ignored. Subscription cancellation still sends
`subscription/cancel`. A force whose send started stays `Unknown` when the reply is
lost. `NativeAttempt.Origin` is the socket's `file://` URI for Unix connections and
null for caller-supplied streams. The URI escapes each path segment and keeps the
path exactly as supplied; `Uri.UnescapeDataString(origin.AbsolutePath)` returns it.

`FromStreamsAsync` borrows its streams unless `leaveOpen: false` transfers
ownership; one duplex stream can serve as both input and output and is disposed
once. A borrowed stream can be reused by a later connection after disposal.
Stream connections draw request IDs from one process-wide sequence, so a late
native reply to an earlier connection's call is ignored as a retired ID and can
never complete a later call; this holds only for connections in the same process.
Disposal fails pending work at once and cancels the pending read. A borrowed
stream that ignores cancellation can keep that read outstanding after
`DisposeAsync` returns, until the caller closes the stream. `ConnectUnixAsync`
requires an absolute path and connects within `ConnectTimeout`; disposal closes its
socket within the cleanup bound.

The stock portable controller serves one run at `<state>/runs/<runId>/controller.sock`
and stops serving at terminal state. Other run IDs are `NOT_FOUND`. It has no
recovery or cluster-method override, so `Runs.ResumeAsync` and
`DiscardWorkspaceAsync` are `Rejected` attempts with `INVALID_PHASE`, as are the
older cluster methods at the pinned source.

`StreamConnectionTests` covers exact framing, split and coalesced replies, oversized
lines, partial-frame EOF with an unknown force and drained subscription, local
cancellation without `$/cancelRequest`, subscription cancellation, a late reply on a
reused borrowed stream, stream ownership including a failing owned stream, and Unix
socket ownership with an exact origin. The stock Linux witness's packed `ControllerConsumer`
connects to a real local-run controller held active by a controlled provider. It
checks initialize, empty get, the one-run list and status, foreign-run refusals,
live `INVALID_PHASE` refusals of resume and discard,
reuse of a borrowed socket stream after its first connection is disposed, and watch
and log delivery through terminal state.

## Windows controller pipes

```csharp
// A caller-known controller pipe; the connection validates, owns and closes it.
await using var controller = await OecpConnection.ConnectNamedPipeAsync(@"\\.\pipe\zeroshot-<digest>", cancellationToken: ct);
```

`ConnectNamedPipeAsync` is Windows-only and returns the same NDJSON `OecpConnection`
as `ConnectUnixAsync`, with the same framing, cancellation, disconnect, request-ID and
budget behavior. It takes the exact local `\\.\pipe\<name>` path. A remote
`\\server\pipe\` path, or a name Windows would canonicalize into a different pipe
(for example one containing `/` or dot segments), is an `ArgumentException`. Nothing
derives the name from a state path: native hashes its OS-encoded storage path, and the
running controller records the result as `socket` in its `controller.ready.json`.
.NET waits for a missing or busy instance until `ConnectTimeout` (`Deadline`); native's
client fails immediately.

Before anything is sent the connection applies native's client check to the pipe's
raw security descriptor: the owner is the user of this process's token (never a
thread's impersonation token), the DACL is present and non-null, and every ACE is an
access-allowed ACE for that user or SYSTEM. A pipe that fails the check, or denies
this user access, is closed and `UnauthorizedAccessException` is thrown; it is never
reported as a `Transport` failure. There is no option to skip the check. The pipe is
opened with identification-only impersonation, as native's client opens it, so the
controller cannot impersonate the caller. Native creates its pipes rejecting remote
clients. `NativeAttempt.Origin` is `file:///` followed by the escaped pipe path, so
`Uri.UnescapeDataString(origin.AbsolutePath)` returns `/` followed by the path.

A pipe the caller opens itself can be passed to `FromStreamsAsync`, which borrows it
and performs no security check; validating that pipe is then the caller's
responsibility.

`NamedPipeConnectionTests` runs only on Windows, in CI on Windows Server 2025 x64 and
Windows 11 arm64. It uses controlled local pipes created with explicit descriptors.
One test covers a private pipe carrying OECP until it disconnects. Refused cases,
one per native rule, each close the pipe before any request: a foreign owner, a
null DACL, an allowed foreign trustee, a non-allowed ACE, and a pipe that denies the
user. Other tests cover owned closure, borrowed-pipe reuse, remote and non-canonical
path refusal, and connect cancellation and deadline. These are simulated endpoints.
Native conformance is recorded separately by
`tools/native-witness/windows-controller.ps1` on Windows x64, the only Windows
architecture native 10.10.0 ships. The packed `ControllerConsumer` runs against a stock
local-run controller's pipe and checks the same support and refusal matrix as the
Unix witness.
