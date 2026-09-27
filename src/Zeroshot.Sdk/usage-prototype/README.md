# .NET usage prototype — draft for discussion

Naming review: the user proposed `Zeroshot.Client`, root namespace `Zeroshot`, `ZeroshotClient` and `Zeroshot.Native.NativeClient`. After discussing coexistence with native tooling, the user selected `zeroshot-dotnet` for the CLI. The rest of this draft remains open for feedback.

**Disposable design sketch.** These C# APIs and CLI commands are proposed, not implemented. No request is sent by this artifact. The browser walkthrough simulates the already selected lifecycle so that the calls, results and evidence can be reviewed together. It is not a conformance witness.

Question: what concrete C# interface and small command-line interface make the exhaustive native client, ordinary SDK workflows and durable-consumer path clear?

The settled inputs are [Define the .NET run lifecycle and failure contract](https://github.com/faviann/zeroshot-dotnet-sdk/issues/6#issuecomment-5851998854), [the complete native interface inventory](https://github.com/faviann/zeroshot-dotnet-sdk/blob/3c64d3ba39ddd8599b748e5938cba35bc1026e11/docs/research/complete-native-http-oecp-interface.md), [Choose .NET platform support and distribution](https://github.com/faviann/zeroshot-dotnet-sdk/issues/5#issuecomment-5851567384), and the pinned [Python Client and Run](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/sdks/python/src/zeroshot/client.py). The names, options and defaults below are proposals awaiting human feedback. The linked decisions continue to govern behavior.

## 1. Names and object model

| Item | Proposal |
| --- | --- |
| Published package / assembly | `Zeroshot.Client` / `Zeroshot.Client.dll`; only one library package |
| Ordinary interface | `Zeroshot.ZeroshotClient`, `Run`, `RunRequest`, `RunResult` |
| Direct native interface | `Zeroshot.Native.NativeClient`, `OecpConnection`, `NativeSubscription<TEstablishment,TEvent>` |
| Native wire models | `Zeroshot.Native.Contracts`; typed named DTOs and tagged variants |
| CLI command | `zeroshot-dotnet` |
| Locally built tool package | `Zeroshot.Cli`; version matches the library |
| First preview example | `0.1.0-preview.1` |

`ZeroshotClient` owns convenience workflows and internally uses `NativeClient`. A consumer can construct `NativeClient` alone without a native build assertion. The two namespaces are public in the same assembly. There is no separate client package. A `Run` is a reference to known identity, not an owned native process. SDK-created clients own their transports; wrapping a supplied native client requires explicit ownership.

```csharp
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

var binding = NativeBinding.CallerSupplied(
    release: "10.9.0",
    sourceRevision: "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa");

await using var sdk = new ZeroshotClient(new ZeroshotClientOptions
{
    Target = new Uri("https://target.example/"),
    NativeBinding = binding,
    // Optional target authentication is obtained per operation.
    // Connection/provider credentials are separately supplied at submission.
    TargetCredentials = targetCredentials,
});

// Alternative: reuse a direct client; disposing the SDK leaves this client alive.
await using var native = NativeClient.ForHttp(new NativeClientOptions
{
    Origin = new Uri("https://target.example/"),
    TargetCredentials = targetCredentials,
});
await using var sdkOverNative = new ZeroshotClient(native, binding, ownsClient: false);
```

Construction performs no network I/O. Discovery and protocol diagnostics are explicit lower-client calls and remain available when the SDK binding is missing or mismatched. Run-operation dispatch through the SDK validates the binding. An asserted build is recorded as caller supplied, never reported as remote attestation. The selected release/source is fixed by the earlier decision; it is not a new prototype choice.

## 2. Ordinary submission and completion

```csharp
var request = new RunRequest
{
    Title = "Fix the failing build",
    Graph = GraphSpec.Parse(await File.ReadAllTextAsync("graph.json", ct)),
    Runtime = RuntimePlan.Parse(await File.ReadAllTextAsync("runtime.json", ct)),
    InitialInput = JsonSerializer.SerializeToElement(new { task = "Fix the build" }),
    Source = new ResolvedSource(
        repository: "https://github.com/example/project.git",
        branch: "main",
        revision: "0123456789abcdef0123456789abcdef01234567"),
};

Run run = await sdk.SubmitAsync(request, credentials: runCredentials,
    cancellationToken: ct);
Console.WriteLine(run.Id); // The acknowledged ID; not necessarily the proposed ID.
RunResult result = await run.WaitAsync(cancellationToken: ct); // No wait deadline.

if (result.IsSuccess)
    UseOutput(result.Output); // Generic JSON. Application acceptance is still yours.
else
    ShowRunFailure(result.FailureReason); // An executed graph failure is result data.

// The same convenience in one call; wait budget starts after acknowledgement.
RunResult combined = await sdk.RunAsync(request,
    credentials: runCredentials,
    waitTimeout: TimeSpan.FromMinutes(20), cancellationToken: ct);

// Optional Python-style raise-on-failure helper.
combined.EnsureSuccess(); // Throws RunFailedException with this terminal result.
```

`RunRequest` requires title, graph, runtime, initial input and exact source. Optional environment preserves omitted versus explicitly empty. Omitted proposed run ID becomes a canonical UUIDv7; omitted submission key becomes `dotnet-` plus a UUIDv4 in lowercase hexadecimal. Those values are generated once per preparation. Optional caller-supplied IDs are validated at the relevant boundary. This does not add task-string, preset, worktree inference or runtime expansion helpers.

`RunResult` contains `RunId`, `IsSuccess`, `Output` or `FailureReason`, native metadata (including token usage when available), and `Evidence`. Evidence distinguishes `StatusReport` from `RetainedTerminalEvent` and preserves the observed cursor/source. Neither means consumer-retained completion or physical cessation. `Run.Submission` holds submission evidence when this handle came from submission; it is absent on a reopened handle.

```csharp
try
{
    RunResult result = await run.WaitAsync(
        timeout: TimeSpan.FromMinutes(5), cancellationToken: ct);
}
catch (RunWaitTimeoutException error)
{
    // error.Run, error.LatestStatus, error.LastDeliveredCursor, error.Evidence.
    // The native run may still be running. No force request was sent.
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    // Observation detached. The native run continues under its own lifecycle.
}
```

Waiting reads status, then watches from that status cursor. An already terminal status returns immediately with status-report evidence. A retained terminal event supplies stronger evidence. Normal subscription completion triggers a final status read; a still nonterminal result is an incomplete-observation error. All setup, reads, reconnect delays and reconnections share the remaining optional wait budget.

## 3. Retain before send; inspect an uncertain attempt

```csharp
PreparedSubmission prepared = sdk.Prepare(request); // Local; fixes IDs and content.
await store.SaveRequestAsync(prepared.ExportUtf8(), ct); // Consumer transaction.

// Target/binding and approval are separate consumer-owned records.
await store.SaveBindingAsync(sdk.Target, binding, approvalReference, ct);

SubmissionAttempt attempt = await sdk.SubmitAttemptAsync(
    prepared, credentials: runCredentials, cancellationToken: ct);

switch (attempt.Outcome)
{
    case MutationOutcome.Acknowledged:
        // ProposedRunId, AcknowledgedRunId and RunIdMatches are explicit.
        await store.SaveAttemptAsync(attempt, ct);
        if (!attempt.RunIdMatches)
            await ApplyApplicationIdentityPolicyAsync(attempt, ct);
        else
            await ObserveAsync(sdk.GetRun(attempt.AcknowledgedRunId), ct);
        break;
    case MutationOutcome.NotSent:
    case MutationOutcome.Rejected:
    case MutationOutcome.Unknown:
        await store.SaveAttemptAsync(attempt, ct);
        // Caller decides what to do next. No automatic mutation resend.
        break;
}

// Later process, after the caller authorizes replay:
PreparedSubmission retained = PreparedSubmission.ImportUtf8(
    await store.ReadRequestAsync(ct));
SubmissionAttempt replay = await sdk.SubmitAttemptAsync(
    retained, credentials: freshRunCredentials, cancellationToken: ct);
```

The proposed retained representation is UTF-8 JSON containing only the native `{runId, submission}` fields. Export returns a defensive copy. Import validates UTF-8 and wire shape but retains the exact bytes, including whitespace and property order; it never serializes them afresh. Authentication, provider secrets, connection resolver and GitHub token are supplied separately for each send. Arbitrary authored input/assets may themselves be sensitive, so credential-free does not mean safe to log.

`PreparedSubmission` lives with the lower client's submission types. Direct consumers can create one from `(RunId, RunSubmission)` without constructing an SDK `ZeroshotClient`; SDK `Prepare` adapts the convenient `RunRequest` to that helper. The native HTTP operation also accepts a typed `TargetRunRequest` when byte retention is unnecessary.

The explicit attempt API returns operational failures/cancellation as evidence; invalid arguments and disposed resources still throw normal .NET exceptions. Ordinary `SubmitAsync` calls this same operation, returns an acknowledged `Run`, or throws `SubmissionException`. Cancellation uses `SubmissionCanceledException : OperationCanceledException` with the same `Attempt` and the caller's token. `run.Submission` and exceptions preserve both run IDs, outcome, target, caller binding where present, retained request, safe correlation data and bounded native error facts. A valid acknowledgement wins over a racing cancellation or cleanup failure.

The explicit outcome is operation-specific. Known admission rejection can mean rejected for this attempt; `503/target.unavailable`, a malformed acknowledgement or transport loss after possible dispatch means unknown. Neither rejection nor not-found proves no earlier attempt succeeded. An acknowledged different ID is valid native data, not proof of deduplication. Broodling may reject that ID under its stricter application rule.

## 4. Reopen, stream, checkpoint and attach

```csharp
Run run = sdk.GetRun(retainedRunId); // No I/O; uses this client's exact target/binding.
RunStatusResult status = await run.StatusAsync(cancellationToken: ct);

// Optional versioned convenience reference: target, run ID and caller binding only.
RunReference reference = run.Reference;
await File.WriteAllTextAsync("run.json", reference.ToJson(), ct);
Run reopened = sdk.GetRun(RunReference.Parse(await File.ReadAllTextAsync("run.json", ct)));
// A reference for a different target/binding is rejected; no silent retargeting.

await foreach (RunWatchRecord record in run.WatchAsync(
    after: checkpoint?.Cursor, cancellationToken: ct))
{
    await ProcessAsync(record, ct);
    await store.CommitCheckpointAsync(record.Checkpoint, ct);
}

await foreach (RunLogRecord record in run.LogsAsync(
    after: logCheckpoint?.Cursor,
    execution: executionId,
    recovery: StreamRecovery.None, cancellationToken: ct))
{
    await ProcessLogAsync(record, ct);
    await store.CommitCheckpointAsync(record.Checkpoint, ct);
}

// Explicit subscription object when inspection/control beyond await foreach is useful.
await using RunObservation<RunLogRecord> logs = await run.OpenLogsAsync(
    new LogOptions { Execution = executionId, After = logCheckpoint?.Cursor }, ct);
await foreach (RunLogRecord record in logs.ReadAllAsync(ct))
    await ProcessLogAsync(record, ct);
ObservationEnd end = await logs.Completion; // Close origin/reason and delivered cursor.

await foreach (RunAttachmentEvent output in run.AttachAsync(executionId, ct))
    Show(output); // working / output / settled; no cursor and no input channel.
```

The simpler `WatchAsync` / `LogsAsync` enumerable owns and disposes its subscription when enumeration ends or is abandoned. The explicit `OpenWatchAsync` / `OpenLogsAsync` / `OpenAttachmentAsync` methods expose an asynchronously disposable, single-reader observation; enumeration failure and `Completion` preserve the same final context. Merely constructing an enumerable does not open a connection. Re-enumeration opens a new observation from its original requested cursor.

`HistoryCheckpoint` binds the opaque cursor to target, run, watch/log kind and execution filter; `record.Checkpoint` supplies that context. Passing a bare cursor is allowed for native-like usage; choosing and persisting the matching checkpoint is the caller's responsibility. A convenience `WatchAsync(checkpoint, ...)` / `LogsAsync(checkpoint, ...)` overload validates scope before dispatch. `LastDeliveredCursor` advances only as records are handed to the caller, not when received or buffered. Retaining a checkpoint after processing remains a consumer action; automatic recovery does not commit it.

Default SDK recovery reopens an established watch/log after disconnection, unexpected EOF or remote `SLOW_CONSUMER`, from the last delivered cursor. It never replays a mutation. An initial or reopened establishment failure surfaces. Local overflow/size failure, malformed or foreign data, permanent refusal and `SOURCE_UNAVAILABLE` surface with context. `done` ends an observation without proving a successful run. Execution-filtered logs exclude run-wide records without an execution. Attachment interruption cannot recover lost output; starting another attachment is a new live view. Durable logs are a separate option.

## 5. Explicit stop and independent control

```csharp
RunResult result = await run.ForceStopAsync(
    waitTimeout: TimeSpan.FromMinutes(2), cancellationToken: ct);
// One native force attempt, then common waiting if its status was not terminal.

ForceAttempt attempt = await run.ForceAttemptAsync(cancellationToken: ct);
await store.SaveForceAttemptAsync(attempt, ct);
if (attempt.Outcome == MutationOutcome.Acknowledged)
    await run.WaitAsync(timeout: TimeSpan.FromMinutes(2), cancellationToken: ct);
```

`ForceStopException` and `ForceStopCanceledException : OperationCanceledException` preserve attempt evidence for the ordinary helper. A timeout or failure during its subsequent wait also retains `ForceAcknowledgement`; the separate wait above does not magically inherit an earlier independent call. An acknowledged `stopping` status means the request was acknowledged, not that the run is terminal. Lost replies can leave the effect unknown. Timeouts, Ctrl+C, disposing a `RunObservation`, disposing a `ZeroshotClient`, or losing a connection do not send force.

## 6. Independently usable native client

```csharp
await using var native = NativeClient.ForHttp(new NativeClientOptions
{
    Origin = new Uri("https://target.example/"),
    TargetCredentials = targetCredentials,
}); // No SDK native build assertion required.

TargetDiscoveryDocument discovery = await native.Target.DiscoverAsync(ct);
SubmissionAttempt attempt = await native.Target.SubmitAttemptAsync(
    prepared, runCredentials, ct); // POST /native-v2/run, one attempt.
TargetOecpSession session = await native.Target.CreateOecpSessionAsync(
    new TargetOecpSessionRequest { RunId = knownRunId }, ct);
await using OecpConnection oecp = await native.ConnectOecpAsync(session, ct);
InitializeResult initialized = await oecp.InitializeAsync(
    new InitializeParams("openengine.cluster/v1"), ct);
RunStatusResult status = await oecp.Runs.StatusAsync(new(knownRunId), ct);

await using NativeSubscription<RunWatchResult, RunWatchEventNotification> watch =
    await oecp.Runs.WatchAsync(new(knownRunId, fromCursor: savedCursor), ct);
await foreach (var record in watch.ReadAllAsync(ct))
    Consume(record);
NativeSubscriptionEnd close = await watch.Completion;
// One subscription. No automatic reopening, terminal wait or force composition.

NativeAttempt<RunForceResult> force = await oecp.Runs.ForceAsync(new(knownRunId), ct);
// Its returned status can still be stopping. No hidden wait.

try
{
    // This method exists in the shared protocol, but the DirectTarget adapter refuses it.
    PlanResult plan = await oecp.Cluster.PlanAsync(new(graph), ct);
}
catch (NativeRpcException error) when (error.NativeCode == "INVALID_PHASE")
{
    // Preserve native numeric RPC code, string domain code and bounded details.
}
```

The lower read API returns typed results and throws typed transport/protocol exceptions. Lower mutation methods return `NativeAttempt<T>`; the selected HTTP submission overload enriches it as `SubmissionAttempt`. Valid responses, known refusals, no dispatch and uncertain effects stay distinct. Mutation cancellation is returned on the explicit outcome path. Refusal classification is per operation; a shared result wrapper does not invent stronger rejection evidence for other native mutations. The upper SDK converts those same facts into its ordinary exceptions and convenience results.

HTTP `Target.SubmitAttemptAsync(TargetRunRequest, ct)` and retained-request overloads share one operation implementation. A generic raw send escape hatch is optional and contributes nothing to the typed coverage claim.

```csharp
// Wider HTTP surfaces are independent named bindings, not new SDK workflows.
DashboardBootstrap boot = await native.Dashboard.GetBootstrapAsync(ct);
DashboardProfile profile = await native.Dashboard.GetProfileAsync("review", ct);
NativeAttempt<DashboardProfile> saved = await native.Dashboard.SaveProfileAsync(
    new DashboardProfileSaveRequest("review", graph, runtime, expectedRevision),
    workspaceId: boot.Workspace.Id, cancellationToken: ct);

await using var pages = await native.Dashboard.OpenRunEventsAsync(
    new DashboardRunEventsRequest(knownRunId, after: historyCursor,
        lastEventId: null), ct); // SSE history/history_error, native precedence.

TargetOperatorDiagnostics diagnostics =
    await native.Private.GetOperatorDiagnosticsAsync(knownRunId, ct);
RunDefinition definition = await native.Private.GetHistoryDefinitionAsync(new(knownRunId), ct);
NativeAttempt<EmptyResponse> bootstrap = await native.Private.BootstrapAsync(
    new TargetPrivateBootstrapRequest(nonce, ciphertext), ct);

ConnectionListResult connections = await native.Connections.ListAsync(
    new ConnectionListRequest(ConnectionScope.User), ct);
NativeAttempt<ConnectionMutationResult> changed = await native.Connections.SetAsync(
    new ConnectionSetRequest(key, ConnectionScope.User, secretValues), ct);
// Secret-bearing fields are real API fields; default formatting redacts their values.

await using NativeBodyResponse asset = await native.Dashboard.GetAssetAsync("app.js", ct);
await asset.CopyBodyToAsync(destination, ct); // MIME/status preserved; bounded body.
NativeRedirectResponse redirect = await native.Dashboard.GetRootAsync(ct);
// Expose Location; do not automatically follow a credential-bearing redirect.
```

Capabilities discovered at one origin bind only validated routes at that origin. A missing hosted descriptor fails locally with `NativeCapabilityException`; a present descriptor is not a guarantee that the server accepts the operation. Dashboard and private calls keep their endpoint/workspace/private-authority prerequisites. Ordinary run credentials do not confer operator authority. Calls on the wrong kind of target preserve the received native refusal, including 404. OAuth exposes individual metadata/device/token/refresh/session operations; it does not install a login store, poll for the caller, or resend a mutation after refreshing credentials. Advertised revocation remains metadata without an invented operation.

```csharp
// Existing endpoints and explicitly borrowed streams; no subprocess creation.
await using OecpConnection unix = await OecpConnection.ConnectUnixAsync(
    "/caller/known/controller.sock", ct);
await using OecpConnection pipe = await OecpConnection.ConnectNamedPipeAsync(
    callerKnownPipeName, validateNativeSecurity: true, cancellationToken: ct);
await using OecpConnection stream = await OecpConnection.FromStreamsAsync(
    input, output, leaveOpen: true, cancellationToken: ct);
// Explicit initialize remains visible for a direct consumer.
```

The existing-endpoint factories do not derive a pipe name from a guessed UTF-8 hash or start a controller. NDJSON has no HTTP bearer handshake, no unary `$/cancelRequest` interception and no invented heartbeat; cancellation detaches local waiting, preserving uncertain mutation effect when applicable. Subscription cancellation is supported on both native bindings. `leaveOpen: true` is the default for supplied streams. `NativeClient.ForHttp(options, httpClient, ownsHttpClient: false)` borrows an HTTP client explicitly; a supplied transport must satisfy redirect/TLS requirements, which cannot be inferred from an arbitrary existing handler. The safe default factory creates its own compliant handler. Reject incompatible supplied options before dispatch and document the caller contract for opaque handlers.

Native `RunStatusResult`, watch notifications, hosted status and history records remain separate types. A watch event has no `workspaceRecovery`; hosted status can be `queued`; Python's narrower projection does not justify dropping native fields. Use `JsonElement` only where the native type deliberately accepts arbitrary JSON. Model omitted/present-null values with `Optional<T>` where required, tagged variants with typed alternatives, open native error strings as strings, and contract-specific strictness per DTO. Do not add one global serializer casing/null/unknown-field policy.

## 7. Coverage map for named lower-client bindings

This table is a proposed public grouping of the existing inventory, not proof of implementation. Each listed operation gets its named typed request/result/error mapping; generated models must also preserve the shared graph/runtime/worker/artifact/recovery/history type closure documented by the inventory.

| Surface | Proposed members | Native contract |
| --- | --- | --- |
| OECP connection | `InitializeAsync` | `initialize` |
| OECP `Cluster` | `PlanAsync`, `ApplyAsync`, `UpdateAsync`, `StopAsync`, `RetryAsync`, `ResubmitAsync`, `DeleteAsync`, `GetAsync`, `WatchAsync`, `LogsAsync`, `AttachAgentAsync` | `plan`, `apply`, `update`, `stop`, `retry`, `resubmit`, `delete`, `get`, `watch`, `logs`, `agent/attach` |
| OECP `Runs` | `SubmitAsync`, `ListAsync`, `StatusAsync`, `WatchAsync`, `LogsAsync`, `AttachAsync`, `ForceAsync`, `CheckpointsAsync`, `ResumeAsync`, `DiscardWorkspaceAsync` | All ten `run/*` methods; shared `run/submit` remains different from target HTTP submission |
| Subscription | typed `Establishment`, `ReadAllAsync`, `Completion`, `DisposeAsync` | Six distinct establishment/event pairs; `event`, `subscription/closed`, `subscription/cancel`; WebSocket-only unary `$/cancelRequest` |
| HTTP `Target` | `DiscoverAsync`, `SubmitAttemptAsync`, `CreateOecpSessionAsync`; `ConnectOecpAsync` | Fixed discovery, run, session and WebSocket-upgrade contracts |
| HTTP `Private` | `BootstrapAsync`, `GetOperatorDiagnosticsAsync`, `GetHistoryDefinitionAsync`, `GetHistoryPageAsync` | Four private operations; bootstrap is empty 204 |
| HTTP `HostedRuns` | `ListAsync`, `StatusAsync`, `WatchAsync`, `LogsAsync`, `ForceAsync` | Discovered GET/POST routes; watch/logs are NDJSON frames |
| HTTP `HostedRecovery` | `ResumeAsync`, `CheckpointsAsync`, `DiscardWorkspaceAsync` | Discovered POST routes with typed run DTOs |
| HTTP `Connections` | `ListAsync`, `SetAsync`, `DeleteAsync` | Discovered secret-management POST routes |
| HTTP `Profiles` | `ListAsync`, `ShowAsync`, `SetAsync`, `DeleteAsync`, `DefaultAsync`, `RunAsync` | Six discovered POST routes; no local template compiler |
| HTTP `MergePlans` | `CreateAsync`, `StatusAsync`, `ForceAsync` | Three discovered routes; no invented watch endpoint |
| HTTP `History` | `ListAsync`, `DetailAsync`, `PageAsync` | Three discovered history GET routes, including the native direct-target mount |
| HTTP `OAuth` | `MetadataAsync`, `BeginDeviceAuthorizationAsync`, `ExchangeDeviceTokenAsync`, `RefreshAsync`, `VerifySessionAsync` | Four URL/method operations; token POST has two grants. Revocation is advertised metadata only |
| Host integration | `ConnectionResolverClient.ResolveAsync` | Explicit caller/host-owned HTTPS callback, with its own endpoint and bearer scope |
| HTTP `Dashboard` assets | `GetRootAsync`, `GetUiRedirectAsync`, `GetIndexAsync`, `GetAssetAsync` | GET `/`, `/ui`, `/ui/`, `/ui/{*asset}`; typed redirect or bounded body, not JSON |
| HTTP `Dashboard` authoring | `GetBootstrapAsync`, `ValidateAsync`, `AuthoringAsync`, `DataAsync` | GET bootstrap; three POST operations with separate typed tagged actions |
| HTTP `Dashboard` profiles | `ListProfilesAsync`, `GetProfileAsync`, `SaveProfileAsync` | Two GET operations plus workspace/revision-protected POST |
| HTTP `Dashboard` history | `ListRunsAsync`, `GetRunAsync`, `GetHistoryAsync`, `OpenRunEventsAsync` | Three GET JSON operations and one GET SSE operation |
| HTTP HEAD | Named `Head...Async` counterparts for the 11 dashboard GETs and three direct history GETs | Metadata-only Axum bindings; no invented HEAD for the hand-written fixed target router |

`NativeSubscription<TEstablishment,TEvent>` exposes the actual establishment result, its subscription ID, optional initial cursor, events and close evidence. It retains the server's close cursor separately from the client's last delivered cursor. Normal `done` completes reading; other close/failure categories throw with structured context and remain inspectable through `Completion`. Hosted NDJSON and dashboard SSE keep their own typed frame/close shapes rather than pretending to be OECP notifications.

`History` binds discovered paths. Named direct-history HEAD calls are available only where that route exists; a custom hosted route does not inherit an assumed HEAD contract. Header-only responses expose status, content type, length and redirect location where supplied, with a filtered diagnostic view. Dashboard calls keep native Host/Origin/fetch-site validation; no default spoofed browser origin or disabled checks. Private capability, hosted control token, hosted OECP token and callback bearer are distinct credential purposes. Redirects and transport downgrades are rejected for credential-bearing operations.

Absent native contracts remain absent: no guessed revocation body, interactive attachment input, native process manager, remote template compiler, general run deletion, submission-key lookup, or automatic hosting/provisioning. Broader client bindings do not expand the initial SDK/CLI workflows.

## 8. Proposed finite resource defaults

These are configurable .NET defaults, not claims about native limits. Smaller operation-specific native limits remain enforced; raising a client limit cannot raise a server limit. Byte units are binary: 1 KiB = 1,024 bytes, 1 MiB = 1,048,576 bytes.

| Option | Proposed default | Control / meaning |
| --- | --- | --- |
| `ConnectTimeout` | 10 seconds | DNS/connect/TLS phase; bounded by the enclosing request/setup deadline |
| `RequestTimeout` | 30 seconds | Complete unary HTTP/OECP operation, including dispatch admission and reading the response |
| `SubscriptionOpenTimeout` | 30 seconds | Session acquisition, connect/initialize and subscription establishment as one budget |
| `CleanupTimeout` | 5 seconds | Bounded detach/owned-resource cleanup; no force request |
| `WaitTimeout` | `null` (indefinite) | Optional per-workflow `TimeSpan`; finite wait includes every observation/recovery phase |
| `StreamRecovery` | `EstablishedInterruptions` | SDK durable watch/log only; lower client and attachment never auto-resume |
| `RecoveryDelay` | 250 milliseconds | Cancellable delay before one eligible reopen; repeated established interruptions may recover while observation remains active |
| `ReadRetries` | 0 | No separate automatic unary-read retry in this preview |
| Mutation retries | 0, fixed | Not configurable into hidden mutation replay |
| WebSocket keepalive | Ping every 30 seconds; pong deadline 15 seconds | Transport liveness only; no native run idle deadline. Disable only with an explicit transport option |
| NDJSON / stream idle deadline | None | No invented ping protocol or quiet-run failure; cancellation/disposal still works |
| `MaxResponseBytes` | 8 MiB | Bounded aggregate unary response; public history list remains at most 4 MiB, detail/page at most 8 MiB |
| `MaxMessageBytes` | 8 MiB | Inbound OECP message/SSE event body ceiling; hosted NDJSON retains its stricter 64 KiB frame bound |
| `MaxRequestBytes` | 4 MiB HTTP, 1 MiB OECP | Fixed target inbound limits; smaller operation-specific bounds win (private history request 4 KiB). Known larger future contracts would need their own profile |
| `MaxBufferedRecordsPerStream` | 256 | Both count and byte ceiling apply |
| `MaxBufferedBytesPerStream` | 8 MiB | Includes complete retained encoded records in this queue |
| `MaxBufferedBytesTotal` | 32 MiB per native client | Shared ceiling for queued observation records; not a claim about total CLR heap usage |
| `MaxConcurrentSubscriptions` | 16 per native client | Additional opens fail before dispatch with a resource-limit error |
| `MaxConcurrentRequests` | 32 per native client, including 4 reserved control slots | Overflow fails locally; status/stop/session/initialize do not wait behind an unlimited ordinary-operation queue |
| `MaxOecpConnections` | 18 per native client | Ceiling across owned or registered connections; SDK reserves a control connection separate from observations |
| `MaxHttpConnectionsPerOrigin` | 8 | Handler limit; request budget includes any handler wait |
| `MaxErrorBodyBytes` | 64 KiB | Diagnostic payload cap; overrun is explicit. Raw payload inspection is opt-in and never default exception text |

The SDK uses a separate control connection from observation connections; opening a subscription never consumes the reserved control connection. The shared queue limit and nonblocking receive dispatch ensure one slow observer ends its affected stream instead of blocking reply correlation. Direct consumers may multiplex manually on one connection, so transport-wide failure can still affect all its operations; status/stop can use a separately created control connection. This is isolation from client backpressure, not a guarantee about server/network response time.

Finite connection/request/setup/cleanup timeouts must be positive. A zero wait timeout performs no new observation I/O and returns timeout with any evidence already available. `null` is the only infinite wait spelling. Limits must be positive and internally coherent; an incoming record larger than either applicable queue byte cap fails explicitly. Queue overflow drains only already validated queued records before reporting failure with the final caller-delivered cursor. No records, terminal JSON or native field values are silently truncated. Transient parsing/object overhead exists outside the encoded queue budget and is bounded by message/response/concurrency ceilings, without an exact heap promise.

```csharp
await using var sdk = new ZeroshotClient(new ZeroshotClientOptions
{
    Target = origin,
    NativeBinding = binding,
    Transport = new TransportOptions
    {
        RequestTimeout = TimeSpan.FromSeconds(45),
        MaxResponseBytes = 8 * 1024 * 1024,
        MaxConcurrentSubscriptions = 4,
    },
    Observation = new ObservationOptions
    {
        Recovery = StreamRecovery.EstablishedInterruptions,
        RecoveryDelay = TimeSpan.FromMilliseconds(250),
    },
});
```

## 9. Small CLI grammar

Proposed common configuration: `--config target.json` with explicit origin, caller binding, credential-source references and optional transport/observation limits. No target registry, implicit worktree selection or background database. For a one-off, `--target URL --native-binding binding.json` supplies the same context. An explicit saved run reference supplies its target/binding; conflicting explicit options fail rather than retarget it.

```json
{
  "schema": "zeroshot-dotnet/target-config/v1",
  "target": "https://target.example/",
  "nativeBinding": {
    "provenance": "caller-supplied",
    "release": "10.9.0",
    "sourceRevision": "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa"
  },
  "credentials": { "targetBearerEnvironment": "ZEROSHOT_TARGET_TOKEN" },
  "transport": { "requestTimeout": "30s", "maxBufferedBytesTotal": 33554432 },
  "observation": { "recovery": "established-interruptions", "recoveryDelay": "250ms" }
}
```

Target bearer configuration is omitted for a target with no bearer requirement. Run credential inputs use `--credentials-file credentials.json` or an explicitly named environment reference map; neither is copied into request/run files or printed by default. No secret literal command-line flags. Request JSON contains the `RunRequest` fields from the C# example; prepared JSON is the exact exported `{runId,submission}` representation.

| Command | SDK calls | Result |
| --- | --- | --- |
| `prepare --request request.json --out prepared.json` | Local request parser + shared preparation helper | Fix identity and write credential-free request; no target I/O/binding required |
| `run --request request.json` | `SubmitAsync` then `Run.WaitAsync` | Submit and wait; `--detach` returns after acknowledgement |
| `run --prepared prepared.json` | Import + `SubmitAsync(prepared)` then optional wait | Explicit send/replay of fixed bytes/identities; no regenerated IDs |
| `status RUN_ID` | `GetRun(...).StatusAsync` | Current status and available terminal data |
| `wait RUN_ID` | `GetRun(...).WaitAsync` | Terminal result; optional `--timeout 10m` |
| `watch RUN_ID [--after CURSOR]` | `GetRun(...).WatchAsync` | Retained status events then live observation |
| `logs RUN_ID [--after CURSOR] [--execution EXECUTION]` | `GetRun(...).LogsAsync` | Retained log events then live observation |
| `attach RUN_ID EXECUTION` | `GetRun(...).AttachAsync` | Live read-only output; no after/checkpoint flag |
| `force-stop RUN_ID` | `GetRun(...).ForceStopAsync` | One force request, then wait; optional `--wait-timeout 2m` |
| `force-stop RUN_ID --request-only` | `GetRun(...).ForceAttemptAsync` | Attempt/returned status only; acknowledged does not mean terminal |

Every known-run command may replace the positional ID with `--run-file run.json`. `run` can write `--save-request prepared.json` before sending and `--save-run run.json` after acknowledgement. An existing output file is refused unless its explicit `--overwrite` flag is supplied; writing failures before dispatch are not-sent, and writing failures after acknowledgement report that acknowledgement. Neither writing a run file nor printing stdout is a promise of consumer transactionality or fsync durability. Callers needing that guarantee use their own store through the SDK.

`watch`/`logs` accept `--checkpoint checkpoint.json` as an alternative to `--after`; scope mismatch fails locally. They output cursors but never automatically advance a consumer processing checkpoint. `--recovery none` disables SDK recovery; default is `established-interruptions`. Shared resource controls are in the configuration file; `--request-timeout 45s` overrides the unary timeout. Durations accept `ms`, `s`, `m`, `h`, require an explicit unit, and use `infinite` only for a wait budget. Byte limits are integer bytes in configuration. CLI parsing/formatting and explicit file reads/writes stay in the CLI; request preparation, replay, waiting, recovery and failure classification stay in the libraries.

```bash
# All commands here are proposed interfaces, not available in the current scaffold.
zeroshot-dotnet prepare --request request.json --out prepared.json
zeroshot-dotnet run --config target.json --prepared prepared.json --detach --save-run run.json --json
# stdout, one JSON object:
# {"schema":"zeroshot-dotnet/cli/v1","kind":"submission","runId":"019f6ba6-7c00-7000-8000-000000000001","proposedRunId":"019f6ba6-7c00-7000-8000-000000000001","runIdMatches":true,"outcome":"acknowledged"}

zeroshot-dotnet wait --run-file run.json --timeout 20m --json
# stdout, terminal example (application output may be any JSON, including null):
# {"schema":"zeroshot-dotnet/cli/v1","kind":"result","runId":"019f6ba6-7c00-7000-8000-000000000001","status":"succeeded","output":null,"evidence":{"kind":"retained-terminal-event","cursor":"opaque-terminal-cursor"}}

zeroshot-dotnet logs --run-file run.json --execution execution-opaque --after opaque-log-cursor --json
# stdout: one versioned JSON record per line, including kind, runId, cursor,
# execution (possibly null for unfiltered logs), timestamp and native log record.
# stderr: one safe error object if observation fails; no synthetic success record.

zeroshot-dotnet attach --run-file run.json execution-opaque
zeroshot-dotnet force-stop --run-file run.json --request-only --json
```

Without `--json`, print concise human status/result summaries and explicitly requested log/output content. With `--json`, unary commands emit one versioned JSON object, streams emit versioned NDJSON; records include native fields/metadata without promoting them to application approval. Known fields keep their type and meaning within a minor line. Additional optional envelope fields may be added. Consumers ignore unknown optional envelope fields; native strictness inside typed data still follows its contract. A breaking field/command change requires the next minor version.

In JSON mode errors go to stderr as one versioned `kind:error` record, including category, operation, safe target/run identifiers, mutation outcome where applicable and last delivered cursor when available. No raw request, input, output, token or remote message is included in default diagnostics. Explicit successful status/result/log commands intentionally output the content requested. Partial stream stdout remains valid; final exit/stderr communicates interruption. Broken stdout ends observation and releases resources without sending force; it preserves any captured mutation acknowledgement in stderr when possible.

| Exit code | Proposed meaning |
| --- | --- |
| `0` | Successful command; acknowledged detached/request-only mutation; successful terminal result for completion commands |
| `1` | Operational/transport/protocol/resource/incomplete-observation error, including known native rejection |
| `2` | Invalid command/configuration/input, including locally missing/mismatched native binding |
| `3` | A completion command (`run` without detach, `wait`, full `force-stop`) obtained a failed terminal run |
| `4` | Observation wait timeout; no stop implied |
| `5` | Mutation may have been dispatched but its effect/acceptance is unknown |
| `130` | User cancelled before mutation dispatch, or cancelled observation after a known acknowledgement |

`status` exits 0 if it successfully reports even a failed run. `watch`/`logs`/`attach` exit 0 on a normal native close, independent of run success. Cancellation after possible mutation dispatch with no acknowledged outcome exits 5, so ambiguity remains visible; its error category also records cancellation. A captured acknowledgement followed by Ctrl+C remains acknowledged, exit 130. Other classification precedence is: invalid local invocation before dispatch → 2; uncertain mutation → 5; acknowledged mutation followed by wait timeout → 4; observed failed terminal → 3; read/call failure → 1.

The native terminology comes from its `run`, `status`, `watch`, `logs`, `attach`, and `force-stop` commands. Separate `prepare`/`wait`, explicit files, machine envelopes and richer exit codes are deliberate .NET proposals. Native 10.9.0 uses exit 1 broadly for errors, exits 0 for Ctrl+C detachment, and reports a failed run as a nonzero exit only for foreground `run`; its `status`/`watch`/`force-stop` behavior is not the richer proposed contract above.

## 10. Restore, build and local installation

These examples describe the proposed first preview after implementation and publication. No package was published and no tool was installed in this session. Only `Zeroshot.Client` will be published to the selected GitHub feed. The CLI is built from the matching repository revision. The current source project is still named `src/Zeroshot.Sdk`; that existing directory name is not a public namespace promise and is not changed by this prototype.

The following `nuget.config` contains feed locations and package mapping, not credentials:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github" value="https://nuget.pkg.github.com/faviann/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="github"><package pattern="Zeroshot.Client" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

Consumer project reference (exact preview):

```xml
<PackageReference Include="Zeroshot.Client" Version="[0.1.0-preview.1]" />
```

```bash
# GITHUB_USER and GITHUB_PACKAGES_TOKEN are supplied by the caller's secret mechanism.
# Do not place the token value in a committed file or a literal shell-history command.
export NuGetPackageSourceCredentials_github="Username=${GITHUB_USER};Password=${GITHUB_PACKAGES_TOKEN};ValidAuthenticationTypes=Basic"
dotnet restore --configfile ./nuget.config
```

PowerShell equivalent:

```powershell
$env:NuGetPackageSourceCredentials_github = "Username=$env:GITHUB_USER;Password=$env:GITHUB_PACKAGES_TOKEN;ValidAuthenticationTypes=Basic"
dotnet restore --configfile ./nuget.config
```

GitHub requires authentication even when the package is public. Use a classic PAT with `read:packages` for human consumption, or `GITHUB_TOKEN` in Actions where the workflow repository has access to the package. The credential environment variable suffix matches the source key exactly. The more specific source pattern selects `Zeroshot.Client` from GitHub; other packages use NuGet.org. Future NuGet.org publication and name reservation are separate work.

Sources: [GitHub NuGet authentication](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry#authenticating-to-github-packages), [NuGet environment credentials](https://learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds#credentials-in-environment-variables), [package source mapping](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping), [exact version ranges](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning#version-ranges).

Repository build/run at the source tag matching the preview:

```bash
dotnet build Zeroshot.sln --configuration Release -p:Version=0.1.0-preview.1
dotnet run --project src/Zeroshot.Cli --configuration Release --no-build -- --help
dotnet run --project src/Zeroshot.Cli --configuration Release --no-build -- status --run-file run.json --json
```

Optional tool packaging requires the future CLI metadata `PackageId=Zeroshot.Cli`, `ToolCommandName=zeroshot-dotnet`, `PackAsTool=true`; the library requires `PackageId=Zeroshot.Client`, `AssemblyName=Zeroshot.Client` and the selected public namespaces. This metadata is not present in the current scaffold.

```bash
dotnet pack src/Zeroshot.Cli/Zeroshot.Cli.csproj --configuration Release \
  --output ./artifacts/packages -p:Version=0.1.0-preview.1

# Optional global installation, from the local package source only:
dotnet tool install --global Zeroshot.Cli --source ./artifacts/packages --version 0.1.0-preview.1
zeroshot-dotnet --help

# Or install into a project-local manifest:
dotnet new tool-manifest
dotnet tool install --local Zeroshot.Cli --source ./artifacts/packages --version 0.1.0-preview.1
dotnet tool run zeroshot-dotnet --help

# An isolated explicit path is useful for preview validation:
dotnet tool install Zeroshot.Cli --tool-path ./artifacts/tools \
  --source ./artifacts/packages --version 0.1.0-preview.1
./artifacts/tools/zeroshot-dotnet --help
```

Run `dotnet new tool-manifest` only when the consuming directory has no manifest; otherwise use its existing one. On Windows the explicitly installed command has an `.exe` suffix. `--source` chooses the local package feed; `--add-source` alone would also search other configured feeds. Building/restoring/managing tools requires the .NET SDK; running the built framework-dependent command requires a compatible .NET 10 runtime. No Python or native executable is required by these client calls. Native tooling used to prepare assets or run release conformance witnesses remains separate.

Sources: [create a .NET tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create#package-the-tool), [pack](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-pack), [tool install](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install), [local tool invocation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-run).

The existing release decision still requires package/CLI checks on all six selected OS/architecture combinations and a Linux x64 controlled native 10.9.0 conformance witness. This prototype does not provide any of that evidence. CLI-only and asset-preparation capabilities are not being implemented by a usage sketch.

## 11. Questions for this prototype review

1. **API shape:** retain the compact `SubmitAsync` / `RunAsync` / `GetRun` ordinary surface and explicit `Prepare` / `SubmitAttemptAsync` advanced path, with systematic native groups and typed contracts underneath?
2. **Observation surface:** keep both the simple asynchronous enumerables and explicit disposable observation objects, plus optional versioned `RunReference` / `HistoryCheckpoint` files? The extra objects expose close/checkpoint evidence without making ordinary callers manage it.
3. **CLI shape:** use prepared/request JSON files, native-like run verbs and explicit target configuration, rather than offering a second graph/runtime authoring interface? Are the proposed completion-aware exit codes useful?
4. **Default controls:** adopt the values in the resource table as initial adjustable .NET defaults, especially 30-second unary requests, indefinite cancellable waits, 250-millisecond recovery delay, and 16 observations with 32 MiB aggregate queued records?

The next round depends on feedback to these questions. This ticket remains open until live review is complete. The map's final adoption/handoff decision is a separate session; this draft does not settle it.
