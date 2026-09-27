# WebSocket OECP inspection

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
explicit disposal completes it with null. There are no subscription bindings yet.

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
