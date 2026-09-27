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
overflow probe; they do not truncate a discovery document. Error bodies use the
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
wire data; the SDK does not contact the resolver. Native admission owns runtime
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
and `Connection: close`. The last is required: a direct target hands every later
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

Violations are `Protocol` failures. Refusals keep `StatusCode` and a valid `Problem`.
UI-router problems (`origin_rejected`, history codes) share the `{code,message}`
shape, so `Problem` holds both. For history operations `HistoryProblem` maps the
closed native vocabulary (`run_not_found` … `history_incompatible`) to
`RunHistoryProblemCode`. Unknown or malformed problems leave it null and keep the
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
