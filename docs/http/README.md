# Target HTTP discovery, session authority and submission

`NativeClient.ForHttp(new NativeClientOptions { Origin = origin })` creates a native
client for an existing target. `await client.Target.DiscoverAsync(ct)` returns
`TargetDiscoveryDocument`. Neither construction nor discovery requires an SDK build
assertion. Construction validates configuration without contacting the target;
the library never starts or manages a target process.

Origins allow HTTPS or HTTP with exactly numeric loopback `127.0.0.1` / `::1`.
Userinfo, non-root paths, query, fragment, whitespace, control characters and
backslashes fail before dispatch, including paths hidden by `System.Uri`
normalization. Discovery sends one GET to `/.well-known/zeroshot-native-v2` with no
body or library-added credentials. There is no HEAD preflight or application retry.

Discovery models retain all fixed fields, OAuth/login descriptors and all eight
known extension descriptors at native 10.9.0 source
`75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa`. Use `NativeJson` for validated serialization.
The hosted-runs and hosted-workspace-recovery descriptor fields use snake_case;
history, connections, profiles and merge plans use their native camelCase fields.
Known objects reject unknown fields; unknown names inside `extensions` are ignored
and are not re-emitted, matching native. Omitted extensions default empty, omitted
`dynamicKinds` defaults empty, and optional descriptor fields accept null. Required
fields, exact authentication tokens and known-field duplicates are validated.
The binding also requires discovery kind `zeroshot.native-v2-target/v2` and audience
`controller`. Descriptor strings are retained as wire data; this operation does
not compile or contact advertised capability routes, establish authentication,
or assert that any capability is supported by the target.

`TransportOptions` configures the #15 execution seam: 10 s connect (DNS/TCP/TLS),
30 s complete request including response reads/parsing, 5 s cleanup, 32 concurrent
requests with four reserved control slots, eight HTTP connections per origin,
4 MiB request / 8 MiB response / 64 KiB error-body bounds. All settings are positive
and finite; reserved slots must leave ordinary capacity. Discovery consumes ordinary
request capacity; session acquisition can use the reserved control slots. Discovery
has no body, so its encoded request-body size is zero. Size
checks count received bytes independently of Content-Length, including a one-byte
overflow probe; they do not truncate a discovery document. A success body whose
declared Content-Length already exceeds the bound fails before it is read. Error bodies use the
smaller response/error ceiling. The default transport uses HTTP/1.1 and registers
each physical connection for its entire pooled lifetime; the handler also limits
its real pool, and handler waits consume the complete request deadline.

The owned default uses normal TLS certificate validation, disables automatic
redirects, cookies and decompression, and rejects 3xx responses and changed response
URLs. `NativeHttpException.Kind` distinguishes `Protocol`, `SizeLimit`, `HttpStatus`,
`Redirect`, `Capacity`, `Deadline` and `Transport`. A refused HTTP operation retains
`StatusCode`; malformed discovery never becomes a 404 or a size failure. Cancellation
uses an `OperationCanceledException` subtype. Invalid arguments and calls after
disposal use normal .NET exceptions. Exceptions contain only fixed operation,
correlation, stage and classification metadata. Raw refusal bytes require
`CaptureRawDiagnostics = true` and explicit `ExportRawDiagnostic()`; returned bytes
are copies and never appear in default exception formatting.

## Supplied HTTP clients

`NativeClient.ForHttp(options, httpClient, ownsHttpClient: false)` borrows by default.
Disposal cancels this native client's operations and refuses new ones; a borrowed
HttpClient remains usable. Set `ownsHttpClient: true` to transfer disposal ownership.
No disposal path invokes native force or other target mutation.

The supported supplied-handler configuration is:

```csharp
var transport = new TransportOptions();
using var handler = NativeClient.CreateHttpHandler(transport);
using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
await using var native = NativeClient.ForHttp(
    new NativeClientOptions { Origin = new Uri("https://target.example/"), Transport = transport },
    http); // borrowed
var discovery = await native.Target.DiscoverAsync();
```

Keep the same limits when creating the handler and native client. The factory
returns a normal `SocketsHttpHandler` with redirects disabled, standard TLS
validation, a finite connect timeout and a bounded physical pool. It cannot be
shared after it has been disposed. Caller-created handlers must preserve these
settings. A supplied HttpClient must have an infinite timeout or a timeout at least
as long as `RequestTimeout`; a shorter setting is rejected before contact.

An arbitrary HttpClient conceals its handler. Supplying one explicitly asserts
that its entire handler chain preserves certificate/hostname validation, never
follows redirects or downgrades, never injects authentication, bounds actual connections per origin and connection
setup at the configured ceilings, honors cancellation, and accurately reports the
requested response URL. Custom handlers must enforce the same physical connection
lifetime accounting as the supported bounded pool. The SDK cannot inspect or repair
these hidden settings. Its shared request deadline, admission and body limits still
apply; detecting a changed response URL is an additional check, not proof that a
hidden handler did not already follow a redirect. Do not mutate a supplied transport
into an incompatible configuration after construction.

Unit/integration coverage is in `HttpDiscoveryTests.cs`. The separate
[`stock native witness`](../../tools/native-witness/README.md) exercises real native
GET discovery, its fixed-route HEAD 404 refusal and direct session acquisition, then
runs a fresh packed-package discovery/session consumer.

## Session acquisition

```csharp
var discovery = await native.Target.DiscoverAsync();
var session = await native.Target.CreateOecpSessionAsync(discovery,
    new TargetOecpSessionRequest { RunId = new RunId("0195af77-1000-7000-8000-000000000001") },
    credentials: new TargetControlCredentials(TargetAuthentication.HostedOauth, currentControlBearer));
// session.Endpoint and session.BearerToken are the returned OECP authority.
```

Pass the discovery document explicitly. The binding makes one JSON POST to its
validated `sessionPath`, with no implicit rediscovery, retry, credential store or
refresh. Omit the request or its run selector to send `{}`; a supplied selector must
be a canonical UUIDv7. Hosted authorities require the selector for routing and may
return a refusal when it is absent; the binding preserves that refusal. Direct
acquisition works without a selector for target-wide operations.

Direct discovery requires no control credentials and a response with no bearer.
Hosted and private discovery require matching `TargetControlCredentials`; the
current bearer is sent only in that request's `Authorization: Bearer` header. A
hosted result carries its separately issued OECP-purpose bearer. A private result
carries its capability. Both require a valid bearer of 1..16384 ASCII graphic bytes,
matching the fixed native transport. No returned session bearer becomes a control
credential. Default HTTP Authorization headers are rejected, and supplied handlers
must not inject credentials. `TargetOecpSession` remains exact wire data; acquiring
one does not open a WebSocket. The [WebSocket binding](../oecp/README.md) revalidates its authority
at dial time.

Before sending credentials, the binding validates discovery kind/audience/auth mode
and canonical same-origin run/session/OECP paths. Paths reject userinfo, variables,
queries, fragments, whitespace, backslashes, invalid escapes and URL normalization
that would change their spelling. Internal canonical-URL and literal-template
compilation helpers preserve capability base paths, require nonempty ASCII literal
segments and enforce the native 2048-byte template bound. Capability tickets add
exact variable sets and descriptor requirements; unrelated advertised capability
strings remain wire data until their binding consumes them. OAuth flows and host
callback rules are separate bindings.

Received endpoints must use the target's corresponding `wss`/`ws` scheme, host and
effective port, without userinfo, query, fragment or malformed URL text. The path
is host-owned; no fixed `/native-v2/oecp` path is imposed on hosted authorities.
Invalid endpoint or bearer data becomes a `Protocol` failure and is never dialed.
Any 2xx session response must contain valid strict `TargetOecpSession` JSON; the
response ceiling is the smaller of 64 KiB and the configured limit. Requests obey
the smaller of 4 MiB and the configured limit. Error bodies obey the response and
diagnostic ceilings. Redirects and changed response URLs fail without a resend.

`NativeHttpException.StatusCode` retains a received status, including when the body
exceeds a bound. `Problem` retains a valid `TargetHttpProblem` with exact code,
message and optional object details. Its native limits are 128 ASCII code bytes,
1024 non-control UTF-8 message bytes and 60 KiB serialized details. Malformed problem
bodies keep the HTTP status without inventing native facts. Remote text, endpoint
and tokens never appear in default exception, contract or credential formatting;
explicit `Problem` property inspection and optional raw export can expose them.
`HttpSessionTests.cs` covers controlled direct/hosted/private behavior, trusted HTTPS,
WSS authority rules, refusal facts and credential canaries. Live hosted/private
interoperability is unverified.

## Direct submission attempts

```csharp
var prepared = PreparedSubmission.ImportUtf8(retainedBytes);
var attempt = await native.Target.SubmitAttemptAsync(prepared,
    new TargetRunCredentials { Connections = freshConnections, GithubToken = currentGithubToken },
    cancellationToken: ct);
if (attempt.Outcome == NativeAttemptOutcome.Acknowledged)
{
    var acknowledgedId = attempt.AcknowledgedRunId;
    // The caller decides whether to adopt it, including when RunIdsMatch is false.
}
```

`Target.SubmitAttemptAsync(TargetRunRequest, ...)` sends a typed full native envelope.
The retained overload accepts `PreparedSubmission` plus `TargetRunCredentials` for
current connection values, optional resolver and optional GitHub token. Both accept
separate `TargetControlCredentials` for the HTTP Authorization header when needed.
The operation sends exactly one POST to `/native-v2/run`; it does not use discovery's
run path or OECP `run/submit`, refresh credentials, or resend on any failure.

`TargetRunRequest` maps the complete native envelope. `connections` is required,
including when empty. Each static connection contains 1–64 environment fields;
values are nonempty, NUL-free, at most 64 KiB each and at most 256 KiB in aggregate
including field names. Resolver endpoint/bearer/keys/sourceConnection remain native
wire data; submission does not contact the resolver (see the
[host connection resolver callback](#host-connection-resolver-callback)). Native admission owns runtime
connection requirements and provider normalization. The target run ID must be a
canonical UUIDv7. Run submission wire validation covers the full nested graph and
runtime without performing native semantic admission.

The retained envelope stays credential-free and byte-for-byte unchanged. Sending
inserts current outer credentials before its final brace; existing field text,
whitespace, escapes and number spelling are not reserialized. Typed input is
snapshotted as a secret-free `PreparedSubmission` for evidence. No IDs, keys, source
revisions or content are generated by either overload.

`TargetSubmissionAttempt` extends the shared `NativeAttempt<TargetRunReceipt>`.
It retains `Origin`, fixed `Operation`, safe `CorrelationId`, `Prepared`,
`ProposedRunId`, `AcknowledgedRunId` and nullable `RunIdsMatch`. `Response` is the
validated native receipt; it contains only a run ID, with no content-digest proof.
`Failure` is a safe `NativeHttpException` or cancellation exception when present.
An acknowledged different ID is preserved without imposing consumer adoption policy.

| Outcome | Evidence |
| --- | --- |
| `Acknowledged` | HTTP 200 and a valid complete `TargetRunReceipt`. |
| `Rejected` | A valid pinned status/problem pair: 400 `request.invalid` or `run.rejected`, 401 `request.unauthorized`, 404 `request.not_found`, 408 `request.timeout` (native request-head refusal), or 409 `request.conflict`. |
| `NotSent` | Local cancellation, size or capacity admission prevented dispatch. |
| `Unknown` | Dispatch may have occurred without a valid receipt or known refusal. This includes dropped, late, malformed/oversized replies, redirects, unrecognized status/problem pairs and 503 `target.unavailable`, which can follow durable creation. |

A known refusal concerns this attempt, not earlier submissions. A captured valid
receipt survives cancellation or cleanup failure. Cancellation after possible
dispatch returns unknown evidence; the returned attempt does not later change if
a noncooperating supplied transport produces a late response. Cleanup closes that
response. Invalid input or a disposed native client throws normal .NET exceptions.
Submission uses ordinary request capacity and the smaller of configured and native
4 MiB request / 64 KiB response limits, with bounded error diagnostics.

`HttpSubmissionTests.cs` covers controlled transport faults, cancellation/cleanup
races, status/code classification, identity mismatch and exact retained text with
fresh credentials. The [native witness](../../tools/native-witness/README.md) runs
the packed submission binding against stock native with a complete software-change
PR asset and proves native normalization, deduplication and exact replay. Hosted
and private submission interoperability remains unverified.

## Hosted connection records

```csharp
var discovery = await native.Target.DiscoverAsync();
var access = new TargetControlCredentials(TargetAuthentication.HostedOauth, currentAccessToken);
var records = await native.Connections.ListAsync(discovery, new ConnectionListRequest { Scope = ConnectionScope.User }, access);
var set = await native.Connections.SetAsync(discovery, new ConnectionSetRequest
{
    Key = new ConnectionKey("github"), Scope = ConnectionScope.Org,
    Values = ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", token)
}, access);
if (set.Outcome == NativeAttemptOutcome.Unknown) { /* the record may or may not have changed */ }
var deleted = await native.Connections.DeleteAsync(discovery,
    new ConnectionDeleteRequest { Key = new ConnectionKey("github"), Scope = ConnectionScope.Org }, access);
```

`NativeClient.Connections` binds the three native hosted connection-management
operations. Each call makes one JSON POST to the route advertised by the explicitly
supplied discovery, with `Accept: application/json`, `Cache-Control: no-store` and
the caller's current hosted OAuth access bearer. There is no rediscovery, token
refresh, retry, secret store, "show secret" operation or generic dynamic-kind
creation.

Before sending anything, the binding requires `hosted_oauth` discovery with kind
`zeroshot.native-v2-target/v2` and audience `controller`, matching `HostedOauth`
credentials, and a `connections` extension. Native refuses direct targets and a
private target cannot carry this hosted capability. The descriptor must have kind
`zeroshot.connections/v1`; `dynamicKinds` must be distinct, nonempty, at most 128
UTF-8 bytes and free of control characters; `baseUrl` must be same-origin; and all
four templates, including `resolve`, must be bounded literal routes. The resolve
declaration is validated as native does but is never compiled into a caller
operation: it names the host's run-scoped resolver callback, a separate contract.
A wrong target, absent capability, invalid descriptor or invalid request throws
`ArgumentException` or `JsonException`, whichever applies, without dispatch, like
other invalid use.

Requests and results are strict camelCase records. `ConnectionScope` is `user` or
`org`. `ConnectionSummary.Kind` is an open string: native names
`ConnectionKinds.Static` and `ConnectionKinds.GithubAppInstallation`, but a host
can report others. Summaries carry field names only. `ConnectionSetRequest.Values`
applies the native static-value bounds shared with run submission: 1–64 environment
field names, each value nonempty, NUL-free and at most 64 KiB, and at most 256 KiB
in aggregate including names. Hosts may answer with any 2xx status; the response
must be valid JSON within 64 KiB (or the smaller configured limit).

`ListAsync` returns the result or throws `NativeHttpException`. `SetAsync` and
`DeleteAsync` return the shared `NativeAttempt<T>`: `Acknowledged` with a valid
result, `NotSent` when nothing was dispatched, and `Rejected` only for a valid
`TargetHttpProblem` with one of these status/code pairs: 400 `invalid_request`,
401 `unauthorized`, 403 `forbidden` or 404 `not_found`. These are native's
status-derived default codes (`default_http_error_code` in
`contract/http_error.rs`), which native itself uses only when a response has no
parseable problem body. Native serves no connection routes, and a hosted server's
own problem codes are not pinned. Every other received failure, including 409
`request_conflict`, 429 `rate_limited`, 503, other codes and malformed results, is
`Unknown`. `Failure.Problem` keeps
the received problem for the caller to decide.

Secret values are ordinary request data: the caller can read them and they are
sent in the set body. Default formatting of requests, attempts, exceptions and
credentials omits values, bearers and remote problem text; explicit property
access, `NativeJson` serialization and opted-in raw diagnostics reveal them.

`HttpConnectionTests.cs` covers every operation against controlled hosted
authorities. Stock native 10.9.0 only consumes these routes and serves none, so
the native witness cannot exercise them. Live hosted interoperability is
unverified.

## Hosted run profiles

```csharp
var discovery = await native.Target.DiscoverAsync();
var access = new TargetControlCredentials(TargetAuthentication.HostedOauth, currentAccessToken);
var profiles = await native.Profiles.ListAsync(discovery, new RunProfileListRequest { Scope = RunProfileScope.Org }, access);
var set = await native.Profiles.SetAsync(discovery, new RunProfileSetRequest
{
    Name = new RunProfileName("review"), Scope = RunProfileScope.Org, Graph = graph, Runtime = runtime, SetDefault = true
}, access);
var run = await native.Profiles.RunAsync(discovery, new RunProfileRunRequest
{
    RunId = proposedRunId, Profile = new RunProfileSelector { Scope = RunProfileScope.Org, Name = new RunProfileName("review") },
    Title = title, InitialInput = input, Source = source, SubmissionKey = submissionKey,
    Connections = freshConnectionValues
}, access);
if (run.Outcome == NativeAttemptOutcome.Unknown) { /* the run may or may not exist */ }
```

`NativeClient.Profiles` binds the six native hosted run-profile operations: list,
show, set, delete, default and run. Each call makes one JSON POST to the route
advertised by the explicitly supplied discovery, with `Accept: application/json`,
`Cache-Control: no-store` and the caller's current hosted OAuth access bearer. There
is no rediscovery, token refresh, retry, local profile store, template compilation
or profile workflow.

Before sending anything, the binding applies the hosted gate used by connection
records: `hosted_oauth` discovery with kind `zeroshot.native-v2-target/v2` and
audience `controller`, and matching `HostedOauth` credentials. Discovery must carry a
`run_profiles` extension of kind `zeroshot.run-profiles/v1` with a same-origin
`baseUrl`. All six templates must be bounded literal routes, since native compiles
all six before any operation. A wrong target, absent capability, invalid descriptor
or invalid request throws `ArgumentException` or `JsonException` without dispatch.

Requests and results are strict camelCase records. `RunProfileScope` is `user` or
`org`, and `RunProfileName` matches `[A-Za-z0-9][A-Za-z0-9._-]{0,63}`. `RunProfile`
carries the complete `GraphSpec` and `RuntimePlan`, which are validated against the
pinned schema in both directions. Summaries omit graph and runtime.
`RunProfileSetRequest.SetDefault` defaults to false. `RunProfileDefaultRequest` with
a null `Name` omits the field, which clears the scope's default.
`RunProfileDefaultResult.Name` is null when the scope has no default; as in native,
a missing `name` also reads as null.

`RunProfileRunRequest` fixes the proposed run ID, selector, title, initial input,
resolved source and submission key exactly as supplied. A null `Environment` omits
the field so the host may apply its default, while an empty `RuntimeEnvironment`
explicitly selects the base environment. `Connections` is required on every run,
may be empty, and uses the static-value bounds shared with submission. Profile runs
have no connection resolver. `GithubToken` is optional. The acknowledged
`TargetRunReceipt.RunId` can differ from the proposed one because of native
deduplication; comparing them is the caller's job.

`ListAsync` and `ShowAsync` return the result or throw `NativeHttpException`.
`SetAsync`, `DeleteAsync`, `DefaultAsync` and `RunAsync` return the shared
`NativeAttempt<T>` with the same classification as connection records: `Rejected`
only for a valid `TargetHttpProblem` with 400 `invalid_request`, 401
`unauthorized`, 403 `forbidden` or 404 `not_found`. Every other received failure,
including malformed results, is `Unknown`. `NotSent` means nothing was dispatched.
Every result must fit in 64 KiB (or the smaller configured limit), native's hosted
response bound, including list, show and set results that carry profiles.

Default formatting of requests, attempts and exceptions omits connection values,
the GitHub token, bearers and remote problem text. Explicit property access and
`NativeJson` serialization reveal them.

`HttpProfileTests.cs` covers every operation against controlled hosted authorities.
Stock native 10.9.0 only consumes these routes and serves none, so the native
witness cannot exercise them. Live hosted interoperability is unverified.

## Hosted run lifecycle

```csharp
var discovery = await native.Target.DiscoverAsync();
var access = new TargetControlCredentials(TargetAuthentication.HostedOauth, currentAccessToken);
var runs = await native.HostedRuns.ListAsync(discovery, access);
var status = await native.HostedRuns.StatusAsync(discovery, runId, access);   // may be QueuedHostedRunStatus
await using var watch = await native.HostedRuns.WatchAsync(discovery, new RunWatchParams { RunId = runId, FromCursor = checkpoint }, access);
await foreach (var record in watch.ReadAllAsync()) { /* record.Cursor */ }
var closed = await watch.Completion;                 // evidence, not run success
var force = await native.HostedRuns.ForceAsync(discovery, runId, access);
if (force.Outcome == NativeAttemptOutcome.Unknown) { /* force may or may not have been recorded */ }
```

`NativeClient.HostedRuns` binds the five hosted lifecycle operations: list, status,
watch, logs and force. Each is one request to the route advertised by the explicitly
supplied discovery, with the caller's current hosted OAuth access bearer. There is no
rediscovery, token refresh, stream reopen, waiting or retry; the SDK's recovery and
wait defaults do not apply here.

Before sending anything, the binding applies the hosted gate used by connection
records and profiles. Discovery must also carry a `hosted_runs` extension of kind
`zeroshot.hosted-runs/v1` with a same-origin `base_url`. All five templates are
compiled first, as native does: list has no variables; status and force have exactly
one whole `{run_id}` segment; watch ends in `{?from_cursor}` and logs in
`{?from_cursor,execution}`. Segments are appended to the base path. The run ID is
percent-encoded as one path segment with native's encode set (so `run/1` is sent as
`run%2F1`), and a present cursor or execution is form-encoded in the query. Native
would silently skip a `.` or `..` segment and strip tab, CR and LF, so such run IDs
throw `ArgumentException` instead of addressing another route.

`HostedRunStatusResult` is the OECP status shape except that `status` is a
`HostedRunStatus`: either the host-only strict `{"phase":"queued"}`
(`QueuedHostedRunStatus`) or a native `RunStatus` (`TargetHostedRunStatus`). Neither
queued nor stopping is terminal. `workspaceRecovery` keeps its omission. The list
wraps these results, and force returns the same shape. Watch records are
`HostedRunWatchEventNotification`, which has no `workspaceRecovery`; a record that
carries one is malformed. Log records are the native `RunLogEventNotification`.
Status and force results must name the requested run.

`WatchAsync` and `LogsAsync` take the OECP `RunWatchParams` and `RunLogsParams` and
return a `HostedRunStream<T>`. Queue admission happens before dispatch. Any 2xx
response starts the stream, as in native, and a refusal is a `NativeHttpException`.
Frames are NDJSON: one strict `{"type":"event","event":...}` or
`{"type":"closed","reason":...}` per LF, with one preceding CR removed. Each frame is
at most 64 KiB, or the smaller configured message limit, and an empty line is
malformed. Each record must name the requested run, keep the first record's
`subscriptionId` (and, for watch, its `source`) and, for filtered logs, the requested
execution. Only records handed to the caller advance `LastDeliveredCursor`.

`Completion` preserves the received close. A `closed` frame ends the stream as
`ServerClosed`: `done` has no failure, while `SLOW_CONSUMER` and `SOURCE_UNAVAILABLE`
carry those failure kinds. EOF without a `closed` frame, including inside a partial
frame, is `UnexpectedDisconnect`; native's follow loop reconnects there, and it never
completes the stream. Malformed or foreign frames fail with `Protocol`, oversized frames
with `SizeLimit`, and queue overflow with its resource-limit kind. Records validated
before the failure still drain. Disposal and cancellation close only that response, as
for dashboard run events.

`ForceAsync` sends one `POST {}` and returns `NativeAttempt<HostedRunStatusResult>`
with the classification used by connection records and profiles: `Rejected` only for a
valid problem with 400 `invalid_request`, 401 `unauthorized`, 403 `forbidden` or 404
`not_found`. A lost reply, another problem, a malformed or foreign result, or
cancellation after dispatch is `Unknown`. `NotSent` means nothing was dispatched.
Results must fit in 64 KiB (or the smaller configured limit).

`HttpHostedRunTests.cs` covers every route and failure mode against controlled hosted
peers. Stock native 10.9.0 only consumes these routes and serves none, so the native
witness cannot exercise them. Live hosted interoperability is unverified.

## Hosted merge plans

```csharp
var discovery = await native.Target.DiscoverAsync();
var access = new TargetControlCredentials(TargetAuthentication.HostedOauth, currentAccessToken);
var created = await native.MergePlans.CreateAsync(discovery, new MergePlanSubmitRequest
{
    SubmissionKey = submissionKey, Title = title, ExpiresAt = "2026-09-28T00:00:00Z",
    Source = new MergePlanSource { Repository = repository, Branch = branch },
    Profile = new RunProfileSelector { Scope = RunProfileScope.Org, Name = new RunProfileName("software-change") },
    Runs = [buildRun, integrateRun with { Needs = [new RunProfileName("build")] }]
}, access);
if (created.Outcome == NativeAttemptOutcome.Unknown) { /* the plan may or may not exist */ }
var plan = await native.MergePlans.StatusAsync(discovery, created.Response!.PlanId, access);
var forced = await native.MergePlans.ForceAsync(discovery, plan.PlanId, access);
```

`NativeClient.MergePlans` binds the three native hosted merge-plan operations. `CreateAsync`
POSTs one `MergePlanSubmitRequest`, `StatusAsync` GETs the plan with
`Accept: application/json`, and `ForceAsync` POSTs `{}` to the plan's force route. Every
call sends `Cache-Control: no-store` and the caller's current hosted OAuth access bearer,
exactly once. Native's own client resends a merge-plan request once after an auth
rejection; this binding does not, and has no rediscovery, token refresh, polling, watch,
retry-in-place or merge execution.

The hosted gate is the one used by connection records, profiles and hosted runs. Discovery must also
carry a `merge_plans` extension of kind `zeroshot.merge-plans/v1` with a same-origin
`baseUrl`, so the bearer only reaches that origin. Route templates have literal segments:
`create` has no variable, and `status` and `force` have exactly one whole `{plan_id}`
segment and no query, the rules hosted runs apply to `{run_id}`. Native compiles all three
before any operation. The plan ID is opaque and is inserted as one path segment,
percent-encoded as native's `url` crate does.
A `.` or `..` ID, or one containing tab, CR or LF, is refused because native would drop or
rewrite that segment; an empty ID is sent as an empty segment, as native sends it. Invalid use throws `ArgumentException` or
`JsonException` without dispatch.

`MergePlanSubmitRequest` is native's HTTP submit shape, not the native CLI's
`zeroshot.merge-plan/v1` manifest. It carries 1-64 runs, the limit native's CLI enforces
as a protocol constant. Each run's `Needs` is sent verbatim, and a null list omits the
field. Dependency-graph checks, the `expiresAt` deadline and initial-input validation
belong to the host. A null `Environment`, `Connections` or `GithubToken` omits the field.
An empty `RuntimeEnvironment` explicitly selects the base environment. Connection values
use the static-value bounds shared with submission.

`MergePlan` and `MergePlanRunStatus` are strict. `MergePlanState` and `MergePlanRunState`
have exactly native's six and ten values. `sourceRevision`, `readyAt`, `queueExpiresAt`,
`terminalAt`, `waitingReason` and `errorCode` must each be present, and each may be null;
a missing field is malformed. A status or force reply for a different `planId` is
foreign data and is also malformed.

`StatusAsync` returns the plan or throws `NativeHttpException`. `CreateAsync` and
`ForceAsync` return `NativeAttempt<MergePlan>` with the hosted classification: `Rejected`
only for a valid `TargetHttpProblem` with 400 `invalid_request`, 401 `unauthorized`, 403
`forbidden` or 404 `not_found`. Every other received failure, lost reply, malformed or
foreign result is `Unknown`. An acknowledged force returns the plan as reported, which
need not be terminal. Results may be up to 1 MiB (or the smaller configured limit),
native's merge-plan bound.

`HttpMergePlanTests.cs` covers the three operations against controlled hosted
authorities. Stock native 10.9.0 serves no merge-plan routes and refuses them for direct
targets, so the native witness cannot exercise them. Live hosted interoperability is
unverified.

## Hosted workspace recovery

```csharp
var page = await native.HostedRecovery.CheckpointsAsync(discovery, new RunCheckpointsParams { RunId = runId, Limit = 10 }, access);
var resumed = await native.HostedRecovery.ResumeAsync(discovery, runId, successorRunId, access,
    new CheckpointResumeFrom { CheckpointId = page.Checkpoints[0].CheckpointId }, freshRunCredentials);
if (resumed.Outcome == NativeAttemptOutcome.Unknown) { /* the source run's status reports any recorded successor */ }
var discarded = await native.HostedRecovery.DiscardWorkspaceAsync(discovery, runId, access);
```

`NativeClient.HostedRecovery` binds native's hosted checkpoint and workspace-recovery
operations. Each POSTs the OECP request body once, with the caller's hosted OAuth
access bearer, `Accept: application/json` and `Cache-Control: no-store`. Request and
result types are the shared OECP recovery contracts described in the
[OECP guide](../oecp/README.md): the same checkpoint page contract, restart versus
checkpoint selection, and fresh `TargetRunCredentials` kept apart from the identity
arguments. There is no rediscovery, token refresh, retry or reconciliation.

The hosted gate is the one used by hosted runs, and the capability is its own
`hosted_workspace_recovery` extension of kind `openengine.hosted-workspace-recovery/v1`.
It has no base URL: its `resume`, `checkpoints` and `discard_workspace` templates append
to the validated `hosted_runs` base, and each must have exactly one whole `{run_id}`
segment and no query. Native compiles all three together. The run ID in the path is
encoded and restricted as for hosted runs, and the body carries the same ID. Direct and
private targets recover over OECP; their `workspace_recovery` and
`workspace_checkpoints` kinds do not enable these routes. Absent or malformed
descriptors, unaddressable run IDs and invalid requests throw without dispatch.

`CheckpointsAsync` returns the page or throws `NativeHttpException`; a page for another
run, beyond the effective limit or with a `nextAfter` that is not its last checkpoint
fails as `Protocol`. `ResumeAsync` and `DiscardWorkspaceAsync` return `NativeAttempt<T>`
with the hosted classification: `Rejected` only for 400 `invalid_request`, 401
`unauthorized`, 403 `forbidden` or 404 `not_found` problems. The OECP binding's
JSON-RPC refusal codes have no pinned hosted equivalent, so every other received
failure, a lost reply, and a malformed or foreign acknowledgement (a successor or source
other than the requested ones, or another run's discard) is `Unknown`. Results must fit
in 64 KiB (or the smaller configured limit), native's hosted bound.

`HttpHostedRecoveryTests.cs` covers the three operations against controlled hosted peers
with source-backed fixtures. Stock native 10.9.0 only consumes these routes and serves
none, so the native witness cannot exercise them. Live hosted interoperability is
unverified.

## Hosted OAuth operations

```csharp
var discovery = await native.Target.DiscoverAsync();
var metadata = await native.OAuth.MetadataAsync(discovery);
var code = await native.OAuth.BeginDeviceAuthorizationAsync(discovery);
// Show code.VerificationUriComplete ?? code.VerificationUri and code.UserCode, then poll yourself.
var exchange = await native.OAuth.ExchangeDeviceTokenAsync(discovery, code, registeredDeviceToken);
if (exchange.Failure is NativeHttpException { DeviceTokenError: DeviceTokenError.SlowDown }) { /* wait longer */ }
var tokens = exchange.Response; // Acknowledged only
var session = await native.OAuth.VerifySessionAsync(discovery,
    new TargetControlCredentials(TargetAuthentication.HostedOauth, tokens!.AccessToken));
var refreshed = await native.OAuth.RefreshAsync(discovery, tokens.RefreshToken);
```

`NativeClient.OAuth` binds the five native hosted OAuth calls individually. Each
call sends one request to a URL from the explicitly supplied discovery. There is no
login store, polling loop, `slow_down` backoff, token cache, automatic refresh or
resend after refresh. Callers own storage and timing, including native's
+5 s (maximum 300 s) `slow_down` adjustment.

Before sending anything, every operation requires `hosted_oauth` discovery with
kind `zeroshot.native-v2-target/v2` and audience `controller`, plus:

- `oauth` with device grant `urn:ietf:params:oauth:grant-type:device_code`, exactly
  the exchange fields `device_token` and `device_label`, a client ID of 1–256 UTF-8
  bytes without ASCII control characters, and canonical same-origin metadata, device,
  token and revocation URLs with no credentials, query or fragment;
- `loginSession` with method `GET`, cache policy `no-store` and a same-origin route.

Other discovery capabilities are not examined. Invalid discovery or caller input
throws `ArgumentException` or `JsonException` without dispatch.

| Operation | Request | Result |
| --- | --- | --- |
| `MetadataAsync` | Unauthenticated GET `metadataUrl`, `Accept: application/json` | `OAuthMetadata`; other metadata fields are ignored |
| `BeginDeviceAuthorizationAsync` | Unauthenticated form POST `client_id` | `DeviceAuthorization` |
| `ExchangeDeviceTokenAsync` | Unauthenticated form POST `grant_type`, `device_code`, `client_id`, `device_token`, `device_label=zeroshot-cli`, `audience` | `NativeAttempt<OAuthTokens>` |
| `RefreshAsync` | Unauthenticated form POST `grant_type=refresh_token`, `client_id`, `refresh_token`, `audience` | `NativeAttempt<OAuthTokens>` |
| `VerifySessionAsync` | Bearer GET login route, `Accept: application/json`, `Cache-Control: no-store` | `TargetLoginSession` |

Forms use native's `application/x-www-form-urlencoded` serialization and send
`Accept: */*`, the default of native's pinned reqwest client. `grant_type` for the device exchange and `audience`
come from discovery. The device token is the caller's registered hosted-target
UUID, sent in lowercase hyphenated form. Any 2xx status is accepted, and responses
are limited to 64 KiB.

Metadata fails as a protocol error unless its device, token and revocation
endpoints are canonical same-origin URLs equal to the discovered ones. Device
authorization and token results are strict snake_case records with native bounds:

- device code at most 16 KiB and user code at most 256 bytes, both nonempty without
  ASCII controls; expiry 1–86400 s; interval at most 300 s (zero is valid);
- verification URLs at most 4096 bytes, HTTPS or HTTP on `127.0.0.1`, with no
  userinfo or fragment. Only the complete URL may carry a query, and it must share
  the base URL's origin. Native's loopback test never matches `[::1]`, so neither
  does this one;
- token type exactly `Bearer`; access and refresh tokens 1–16 KiB without ASCII
  controls; access expiry 1–86400 s; refresh expiry 1–31536000 s; scope 1–512 bytes
  without ASCII controls;
- session kind `openengine.target-session/v1` and an organization ID of 1–256 bytes.

Both token grants have effects: a device code is consumed once tokens are issued,
and refresh may rotate the refresh token. They therefore return `NativeAttempt`
evidence. A lost reply or malformed token response is `Unknown`, and the caller
cannot assume its refresh token is still current. For the device exchange, a strict
`{error}` body naming `authorization_pending`, `slow_down`, `access_denied` or
`expired_token` is `Rejected`, and `NativeHttpException.DeviceTokenError` carries the
typed code. Any other error text, extra fields or a non-OAuth body stays `Unknown`
with the status retained. Refresh refusals are `Rejected` only for the problem pairs
used by connection management (400 `invalid_request`, 401 `unauthorized`,
403 `forbidden`, 404 `not_found`); every other received failure is `Unknown`.
Native parses refresh failures as target problems, so a refusal carried as an OAuth
`{error}` body, such as 400 `invalid_grant`, is also `Unknown`. A caller therefore
cannot distinguish a revoked or expired refresh token from a lost reply by outcome
alone; `Failure.StatusCode` and the opted-in raw diagnostic keep what was received.
Metadata, device authorization and session verification return their result or
throw `NativeHttpException`.

`VerifySessionAsync` takes the access token as `HostedOauth`
`TargetControlCredentials`. That type requires 1–16384 ASCII graphic bytes, which is
stricter than native's rule for access tokens (nonempty, at most 16 KiB, no ASCII
controls). A token native would accept but that contains spaces or non-ASCII
characters cannot be verified through this binding.

`revocationEndpoint` stays visible on discovery and `OAuthMetadata.RevocationEndpoint`
and is validated like native. Native 10.9.0 never calls it, so there is no revocation
or logout operation; the coverage ledger keeps it as an advertised-only gap.

Default formatting of device authorizations, tokens, attempts,
exceptions and credentials omits device codes, device tokens, access and refresh
tokens and remote error text. Explicit property access and `NativeJson`
serialization reveal them.

`HttpOAuthTests.cs` covers every flow and failure against controlled hosted
authorities without production credentials. Stock native 10.9.0 consumes these
endpoints but serves none, so the native witness cannot exercise them. Live hosted
interoperability is unverified.

## Host connection resolver callback

```csharp
await using var resolver = ConnectionResolverClient.ForHttp(targetConnectionResolver); // endpoint, bearer, keys
var resolved = await resolver.ResolveAsync(new ConnectionResolveRequest
{
    RunId = runId,
    Connections = ImmutableDictionary<string, ImmutableArray<EnvironmentVariableName>>.Empty
        .Add("github", [new EnvironmentVariableName("GH_TOKEN")])
});
```

`ConnectionResolverClient` makes the outbound call that native hosting makes to a
`TargetConnectionResolver` (`native_v2_hosting/connections.rs`). The direction is
caller to a host-owned HTTPS callback. It is not a target route, so it needs no
discovery or `NativeClient`, and the endpoint is never compared with a target
origin or capability route. The library does not serve the callback or store its
credentials.

`ForHttp` binds one resolver, as native builds one per run, and checks it before
any contact: the endpoint is HTTPS with a host and no userinfo, query or fragment;
keys are nonempty and distinct; and a `sourceConnection` must be one of them. Two
rules are narrower than native. The endpoint must use the canonical spelling that
target routes use (lowercase host, no explicit default port, a path, no dot
segments or invalid escapes), so `System.Uri` never dials a URL native would not.
The bearer must be 1..16384 ASCII graphic bytes, while native allows any
non-control text; .NET cannot send non-ASCII header values by default. Invalid
descriptors, and requests naming a key outside the declared keys, throw
`ArgumentException` without dispatch.

`ResolveAsync` sends one JSON POST with `Authorization: Bearer` set to the
resolver's bearer on that request only. There is no retry, and redirects are
refused, never followed. A supplied HttpClient is borrowed, as with `NativeClient`,
and has the same caller contract. In particular it must disable automatic
redirects: a redirect-following client resends the request body (run ID, keys and
field names) to the `Location` before the SDK sees and refuses the redirect. .NET
drops the `Authorization` header on that resend. The request deadline is the smaller of the
configured `RequestTimeout` and native's fixed 30 s; the response bound is the
smaller of `MaxResponseBytes` and 300 KiB. Any 2xx with a valid strict
`ConnectionResolveResult` succeeds. Its values follow the static bounds shared with
run submission. The result is not matched against the requested fields: that check
belongs to native's supervisor, not the callback.

Failures throw `ConnectionResolutionException` with native's category. The inner
`NativeHttpException` keeps the failure kind, status and opt-in raw diagnostics.
The category depends only on the status and failure kind; problem bodies are
ignored, as native ignores them.

| `Error` | Evidence |
| --- | --- |
| `Refused` | 401 or 403. |
| `Unavailable` | 408, 429 or 500–599; transport or read failure, including a truncated body; the deadline; local capacity refusal. |
| `InvalidResponse` | Any other non-2xx, including 3xx; a redirect even if a supplied client followed it; a malformed body; a received or declared oversized body. |

Cancellation throws an `OperationCanceledException` subtype. Default formatting of
the client, results and exceptions omits the bearer and resolved values.

`ConnectionResolverTests.cs` covers the exact wire, descriptor rules, the status
table, malformed/oversized/declared-oversized/truncated bodies, the deadline and cancellation, and
controlled HTTPS endpoints: an off-target resolver that receives only its own
bearer, a 307 whose `Location` is never contacted, and a redirect that a
non-compliant supplied client follows, still classified `InvalidResponse`. Stock native 10.9.0 only
calls this contract and serves none, so no native witness applies.

## Run history

```csharp
var discovery = await native.Target.DiscoverAsync();
var runs = await native.History.ListAsync(discovery);                 // optional `after` run ID
var definition = await native.History.DetailAsync(discovery, runId);
var page = await native.History.PageAsync(discovery, runId);           // after defaults to v2:0
while (!page.Complete)
    page = await native.History.PageAsync(discovery, runId, page.NextCursor);
```

`History` binds the discovered `run_history` capability (`zeroshot.run-history/v1`).
Pass the discovery document explicitly; the binding never rediscovers. Direct targets
advertise it only when their UI is mounted, at `/native-v2/run-history{?after}`,
`/native-v2/run-history/{run_id}` and `/native-v2/run-history/{run_id}/page{?after}`
under the public origin. Hosted history uses the matching `HostedOauth`
`TargetControlCredentials` bearer. Direct history takes no credentials. Private
authorities are refused because native defines no private history consumer.
An absent or unknown capability, mismatched authority or malformed template throws
`ArgumentException` before dispatch, as session acquisition does.

Templates follow native `compile_route`: at most 2048 bytes, literal ASCII segments,
exactly one whole `{run_id}` segment for detail/page and none for list, and a
`{?after}` suffix only for list/page. Segments append to the capability base path.
`after` is form-encoded as native does (`v2:5` → `v2%3A5`). Run IDs and the list
position must be canonical UUIDv7; a page cursor must be canonical `v2:<sequence>`
at most i64::MAX. Requests send `Accept: application/json`, `Cache-Control: no-store`
and, for the direct UI mount, `Connection: close`. The last is required: a direct target hands every later
request on a UI-routed connection to its UI router, so a pooled connection would
answer the next session or submission request with 404.

Responses obey the smaller of the configured limit and native's 4 MiB list,
8 MiB detail/page and 64 KiB problem bounds. Any 2xx status is accepted. Decoded
records then pass native's host checks:

- list: at most 50 UUIDv7 runs, strictly descending, all below `after`, and a
  `nextCursor` equal to the last run; each summary's cursor, availability, phase,
  terminal and runtime failure must be coherent;
- definition: version and projection version 1, the requested run, available
  history, `initialCursor` `v2:0` and equal canonical cursors; a runtime failure must
  be `runtime_failed`/`runtime_lost` at or before the head with a matching failed
  terminal, finished phase and incomplete history;
- page: requested ≤ next ≤ head, `complete` iff next is head, at most 256 events with
  contiguous canonical cursors ending at next, no empty page before head, control
  records anchored to page events in order, and a runtime failure only on a finished
  page.

Violations are `Protocol` failures. Refusals keep `StatusCode`. The direct UI mount
answers with native `ApiError` `{code,message}` bodies (history codes and boundary
codes such as `origin_rejected`). Operations marked as UI-routed parse these as
`UiProblem`, bounded only by the operation's problem-body limit: native messages can
exceed `TargetHttpProblem`'s 1 KiB single-line rule. Hosted history is not UI-routed;
native's reader parses its refusals as `TargetHttpProblem`, exposed as `Problem`.
For history operations `HistoryProblem` maps the code from whichever problem was
received onto the closed native vocabulary (`run_not_found` … `history_incompatible`)
as `RunHistoryProblemCode`. Unknown or malformed problems leave it null and keep the
observed status; the native browser sanitization to `history_unavailable` is not
reproduced.

`HeadListAsync`, `HeadDetailAsync` and `HeadPageAsync` send HEAD to the same URLs.
Stock native answers HEAD only on the direct UI mount (Axum `get` routes), with the
GET status and headers and no body. A 2xx returns `NativeHeadResult` (status, content
length, media type); a refusal is an `HttpStatus` failure without a problem body.
The shared JSON/HEAD core and `Connection: close` for UI-routed operations are
reusable by other UI-mounted bindings.

The records keep native encodings. `nextCursor`, `createdAt`, summary `cursor`/
`terminal`/`source`, definition `terminal`, token-usage cache counts and safe-log
`execution` must be present and may be null; they are serialized as null.
`runtimeFailure`, `control`, `controlError`, observation `code` and control
`branch`/`detail` are omitted when absent. Control `output` distinguishes present
null from absence. `RuntimeFailure`, `ControlRecord`, `DurableExecution` and unit
events ignore unknown fields, as native serde does; other records are strict. The
legacy definition `snapshot` is accepted and never serialized.

Each page event is a `HistoryEventRecord {cursor, event}` whose `HistoryEvent` is one
of nine variants: `prior_execution`, `run_started`, `node_started`, `node_completed`,
`execution_voided`, `safe_log`, `token_usage_observed`, `force_stop_requested` and
`terminal`. An unknown kind is a `Protocol` failure. The projection encodes reference
`execution`/`nodeInstance` and safe-log/token-usage `execution` as canonical positive
decimal strings (`HistoryIdentity`), while `prior_execution` keeps native's
snake_case `DurableExecution` with numeric identities and an externally tagged
`Active`/`Settled`/`Voided` state. Graph, runtime, source, terminal result and worker
outcome values reuse the existing contracts and validate against the pinned schemas.
Inputs, outputs and diagnostics are arbitrary caller JSON and are not redacted.

Observation availability (`observation.state`) is independent of run phase and
terminal status; `finished` alone does not end observation. A `runtimeFailure` is
status-only evidence, distinct from a retained `terminal` event. The binding asserts
no retention period, and native advertises no history SSE route.

`HistoryTests.cs` covers the golden records, wire-shape and contract tables, routes,
authority, problem categories and bounds. The [native witness](../../tools/native-witness/README.md)
reads real retained runs through the direct UI mount.

## Private target bootstrap

```csharp
var discovery = await native.Target.DiscoverAsync();
var envelope = new TargetPrivateBootstrapRequest { Nonce = nonceHex, Ciphertext = ciphertextHex };
var attempt = await native.Private.BootstrapAsync(discovery, envelope);
if (attempt.Outcome == NativeAttemptOutcome.Unknown) { /* the key may be consumed; do not resend blindly */ }
```

`Private.BootstrapAsync` sends one caller-prepared envelope to a private-mode
target and returns `NativeAttempt<EmptyResponse>`. The client never encrypts,
generates or stores keys or capabilities. Native `private_access.rs` defines the
envelope: `nonce` is 12 bytes and `ciphertext` is the 64-byte token plus the
16-byte AES-256-GCM tag, both lowercase hex. The AAD is
`zeroshot-capsule-bootstrap-v1`, and the plaintext token is 64 lowercase-hex
characters. The strict `{nonce,ciphertext}` request is checked for exact hex and
lengths before sending. A malformed envelope throws `JsonException` and is never
sent.

The route is unauthenticated. The call takes no credentials and sends no
`Authorization` header, and acceptance issues no client credential. Using the
bootstrapped capability as `TargetControlCredentials` is a separate caller
decision.

Native answers a closed bootstrap and a nonprivate target with the same 404, so the
target mode comes from the explicitly supplied discovery. Before any request is
sent, discovery must be `private_capability` with a same-origin
`privateBootstrapPath`. Other discovery throws `ArgumentException`. If the target at
the origin changes mode after that discovery, its 404 still reads as a closed
bootstrap.

`Acknowledged` requires exactly 204 with an empty body; native then has consumed its
key. `Rejected` covers only native's pre-effect refusals: 400 `request.invalid`
(malformed request, or an envelope that fails authentication or token checks, which
leaves the bootstrap open), 404 `request.not_found` (closed bootstrap) and 408
`request.timeout`. `Failure` retains the status and `TargetHttpProblem`, which
distinguish invalid from closed. A pre-dispatch cancellation or local admission
failure is `NotSent`. Any other status or code, any other 2xx, a 204 body, a
malformed problem, a lost reply, a deadline or post-dispatch cancellation is
`Unknown`. The effect is then unknown and there is no retry.

`PrivateBootstrapTests.cs` covers the exact request, refusal classification,
unknown-effect contact and pre-dispatch refusals. The
[native witness](../../tools/native-witness/README.md) runs a stock 10.9.0
private-mode target with isolated key and capability material. It proves invalid,
accepted and closed outcomes.

## Private operator exports

```csharp
var operatorAuthority = new TargetControlCredentials(TargetAuthentication.PrivateCapability, capability);
TargetOperatorDiagnostics diagnostics = await native.Private.GetOperatorDiagnosticsAsync(runId, operatorAuthority);
RunDefinition definition = await native.Private.GetHistoryDefinitionAsync(runId, operatorAuthority);
HistoryPage page = await native.Private.GetHistoryPageAsync(runId, operatorAuthority, after: cursor);
```

These three reads use a private-mode target's fixed routes:

| Method | Native route | Result |
| --- | --- | --- |
| `GetOperatorDiagnosticsAsync` | `GET /native-v2/operator-diagnostics/{runId}`, no body | `TargetOperatorDiagnostics` |
| `GetHistoryDefinitionAsync` | `POST /native-v2/history/definition` with `{"runId"}` | `RunDefinition` |
| `GetHistoryPageAsync` | `POST /native-v2/history/page` with `{"runId","after"?}` | `HistoryPage` |

They take a canonical UUIDv7 run ID and an optional cursor, like the public
[run history](#run-history) methods. There is no discovery parameter because native
does not advertise these routes. Credentials are required and must be
`PrivateCapability`. Missing or hosted credentials, a run ID that is not a canonical
UUIDv7 and a non-canonical cursor throw `ArgumentException` before sending. The
client never turns ordinary run credentials into operator authority.

The history exports reuse the public history records and checks: requested run
identity, versions, cursors, contiguity, control placement, runtime failure and the
8 MiB response bound. Native limits each request body to 4096 bytes; a validated
request is far below it. A missing `after` is omitted from the body, and native reads
from `v2:0`.

Each diagnostic keeps native's `id`, `runId`, `code`, `operation`, optional
`exitStatus`, `stdout`, `stderr` and both truncation flags. Native keeps at most two
diagnostics in memory across all runs and cuts each text to 4 KiB. A response is
rejected as malformed when a diagnostic names another run or a text exceeds 4096
UTF-8 bytes; the whole response is bounded at 64 KiB. An empty list does not show
whether the run exists. The text is command output. Default record and exception
formatting never shows it or a capability; inspect it explicitly.

Refusals keep their status and `TargetHttpProblem`:

- A target that is not in private mode answers 404 `request.not_found`, before any
  capability check.
- A wrong capability gets 401 `request.unauthorized`.
- A malformed request gets 400 `request.invalid`.
- The history exports also set `HistoryProblem` from native's history codes, such
  as 404 `run_not_found` and 400 `invalid_cursor`.

`PrivateExportsTests.cs` covers the exact requests, authority and argument
refusals, the refusal categories, and malformed or oversized responses. The
[native witness](../../tools/native-witness/README.md) reads a real admitted run
through a stock private-mode target and checks the refusals of a direct target.
Non-empty and truncated diagnostics are fixture-only: stock native records
diagnostics only for checkout, push and local-controller failures, which that
target does not reach.
