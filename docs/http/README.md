# Direct HTTP discovery

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
follows redirects or downgrades, bounds actual connections per origin and connection
setup at the configured ceilings, honors cancellation, and accurately reports the
requested response URL. Custom handlers must enforce the same physical connection
lifetime accounting as the supported bounded pool. The SDK cannot inspect or repair
these hidden settings. Its shared request deadline, admission and body limits still
apply; detecting a changed response URL is an additional check, not proof that a
hidden handler did not already follow a redirect. Do not mutate a supplied transport
into an incompatible configuration after construction.

Unit/integration coverage is in `HttpDiscoveryTests.cs`. The separate
[`stock native witness`](../../tools/native-witness/README.md) exercises real native
GET discovery and its fixed-route HEAD 404 refusal, then runs a fresh package consumer.
