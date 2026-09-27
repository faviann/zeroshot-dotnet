# Target HTTP discovery and session authority

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
request capacity. It has no body, so its encoded request-body size is zero. Size
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
one does not open a WebSocket. The WebSocket binding must revalidate its authority
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
