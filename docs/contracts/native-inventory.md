# Complete native HTTP/OECP interface at Zeroshot 10.9.0

> The SDK now binds native 10.10.0 at `3ee1192cec359a0b997f464e703a936e8b67d63c`. `v10.9.0...v10.10.0` changes no file under `protocol/` or `crates/`, so this inventory still describes the 10.10.0 interface. Its source links stay pinned to the 10.9.0 revision it was read from.

Research for [Inventory the complete native HTTP/OECP client surface](https://github.com/faviann/zeroshot-dotnet-sdk/issues/13), completed 2026-09-27. This is an inventory for an independently usable, exhaustive native client beneath the higher-level .NET SDK. The user has also selected dashboard, private operator/controller and host-integration client bindings. This report does not choose .NET names, packages, or retry policy, and does not require implementing native servers or launching native processes.

## Evidence boundary and reading guide

Every native source link in this report is pinned to **`75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa`**, the selected **10.9.0** source. Source was read from Git objects exported to a separate directory; neither the native checkout nor its branch was changed. The older 10.3.0 investigation is context, not evidence for this inventory. No native build, execution, provisioning, live submission, recovery, deletion, credential mutation, or other service mutation was performed. Checks reported below are static source/JSON checks. [Pinned source][source-root], [release][release].

The report separates three different facts: **a contract exists**, **a particular native backend implements it**, and **a host exposes/authorizes access to it**. OpenRPC proves the first only. A complete client inventory cannot be reduced to a DirectTarget run lifecycle, but neither every CLI command nor every controller hook is a public remote operation. [Method registry][methods], [target backend][target-backend], [CLI grammar][cli].

- [OECP methods](#oecp-request-methods-22) covers all 22 request names, six subscription establishments, and four notification names.
- [HTTP surfaces](#http-surface-inventory) separates fixed native server routes, discovered hosted contracts, and UI/private routes.
- [Shared values](#shared-values-and-document-contracts) identifies the reusable payload/type families and their source authorities.
- [Wire behavior](#wire-conventions-errors-and-ownership) covers correlation, errors, strictness, bounds, and ownership.
- [Schema gaps](#schema-dispatch-differences-and-generation-limits) and [coverage evidence](#source-manifest-and-completeness-checks) explain how to establish coverage without confusing schemas with support.
- [Selected boundaries](#selected-scope-and-unsupported-access-paths) records selected scope, prerequisite limits and absent access paths.

## Findings that change the coverage baseline

The authoritative OECP registry contains **22 request methods: 16 unary and six subscriptions**. Twelve belong to the cluster protocol surface (`initialize` through `agent/attach`); ten are native `run/*`. There are **four generic notification names**, outside that request registry. Checked-in OpenRPC has the same method order, parameter names, required flags, and result references. Its schemas do not inventory native HTTP. [Registry][methods], [OpenRPC builder][openrpc-builder], [schema roots][artifact-builder].

On the actual native target WebSocket backend, `initialize` succeeds; `get` returns an empty cluster view; ten older cluster operations inherit `INVALID_PHASE`; `run/submit` explicitly returns `RUN_CONFLICT`; and the other nine `run/*` methods are routed, with checkpoints/recovery additionally gated. `initialize` advertises full graph support and `logs: true`, `agentAttach: true`, even though the older `logs`/`agent/attach` methods are not implemented by this target adapter. Those booleans are therefore insufficient as a per-method support matrix. [Target adapter][target-backend], [controller initialization][cloud-backend], [default backend methods][backend-defaults].

Initial sourceful submission is HTTP. Inventory, checkpoint pagination, workspace resume/discard, hosted profiles and connections, hosted merge plans, and run-history reads are additional interface groups. The research must retain both public generic contracts and backend-specific refusals; it cannot treat the schema's `run/submit` as a working DirectTarget submission path. [Target adapter][target-backend], [HTTP contracts][target-types], [hosted contracts][hosted-types].

## OECP request methods (22)

All names are case-sensitive. Requests use JSON-RPC 2.0 with **named object parameters**. `?` below means an optional field; it does not mean explicit JSON `null` is always equivalent to omission. `R` is read-only, `M` mutates backend state, and `S` establishes observation. A subscription's initial JSON-RPC result is followed by generic notifications; it is not a unary array result. [Unary dispatcher][dispatch], [subscription dispatcher][connection-dispatch], [protocol envelopes][protocol-lib].

### Cluster contract (12 methods)

These are public generic OECP contracts, not aliases for similarly named native `run/*` operations. Their behavior is implemented by the reusable admission/lifecycle/observation infrastructure where a backend supplies it. The production target adapter's support is listed explicitly in the last column. Authentication is provided by the enclosing connection binding, not method parameters. [Admission backend][admission-backend], [identity binding][identity], [target adapter][target-backend].

| Method / kind | Parameters → result | Contract behavior and relevant failures | Native target WebSocket |
| --- | --- | --- | --- |
| `initialize` R | `InitializeParams {protocolVersion}` → `InitializeResult {protocolVersion, capabilities, status}` | Exact token `openengine.cluster/v1`; mismatched request returns application `UNSUPPORTED_PROTOCOL_VERSION` with requested/supported tokens. Capabilities contain `graphProfiles`, `logs`, `agentAttach`. Does not identify product version 10.9.0. [Types][protocol-lib], [dispatch][dispatch] | Succeeds: `openengine.graph.full/v1`, both booleans true, empty cluster status. |
| `plan` R | `PlanParams {graph}` → `PlanResult {ok, diagnostics, bounds?}` | Graph verification result; `GraphDiagnostic[]` and optional `StructuralBounds`; malformed params use `SCHEMA_VIOLATION`. [Admission types][admission-types] | `INVALID_PHASE`. |
| `apply` M, or dry run | `ApplyParams {graph, input?, dryRun?, ifGeneration?, idempotencyKey?}` → `ApplyResult {generation, runId, phase, deduped, diff?}` | `dryRun` defaults false. Input presence is meaningful even when value is null; present generation/key cannot be null. `GraphDiff` lists added/removed/changed node names. Admission supports generation/idempotency checks; applicable errors include `GRAPH_INVALID`, `GENERATION_CONFLICT`, `RUN_CONFLICT`, `IDEMPOTENCY_REUSE`, `INVALID_PHASE`, `CANCELLED`. [Types][admission-types], [admission errors][admission-errors] | `INVALID_PHASE`. |
| `update` M | `UpdateParams {labels?, logLevel?, suspended?, ifGeneration, idempotencyKey}` → `UpdateResult {generation, runId, phase, operational, atCursor, deduped}` | At least one of the three update values is required and present values cannot be null. Labels replace the map, rather than merge it. [Lifecycle types][lifecycle-types] | `INVALID_PHASE`. |
| `stop` M | `StopParams {mode, ifGeneration, idempotencyKey}` → `StopResult {generation, runId, phase, acceptedMode, effectiveMode, operational, atCursor, deduped}` | `mode` is `drain` or `force`; accepted versus effective mode is explicit. This does not add drain to native `run/force`. [Lifecycle types][lifecycle-types] | `INVALID_PHASE`. |
| `retry` M | `RetryParams {ifGeneration, idempotencyKey}` → `RetryResult {generation, runId, phase, retriedTurnId, retryTurnId, operational, atCursor, deduped}` | Retry of a retryable frontier, not workspace successor recovery. `NO_RETRYABLE_FRONTIER` can describe `exhausted`, `success`, `active`, or `consumed`. [Lifecycle types][lifecycle-types], [lifecycle server][lifecycle-server] | `INVALID_PHASE`. |
| `resubmit` M | `ResubmitParams {ifGeneration, ifRunId, idempotencyKey, replacementInput?}` → `ResubmitResult {generation, priorRunId, runId, phase, operational, atCursor, deduped}` | Exact predecessor identity and generation; replacement-input omission differs from explicit null. Not `run/resume`. [Lifecycle types][lifecycle-types] | `INVALID_PHASE`. |
| `delete` M | `DeleteParams {ifGeneration, ifRunId?, idempotencyKey}` → `DeleteResult {deleted, phase, generation?, runId?, atCursor?, deduped}` | Cluster lifecycle deletion, with generation/run fences; not workspace discard and not a native run-deletion API. [Lifecycle types][lifecycle-types] | `INVALID_PHASE`. |
| `get` R | `GetParams {atCursor?}` → `GetResult {spec, status, atCursor, terminalResult?}` | Cluster snapshot, optional retained cursor and terminal value. `ClusterStatus` is distinct from `RunStatus`. [Types][protocol-lib] | Always `GetResult::empty()`; does not read a native run. |
| `watch` S | `WatchParams {runId?, fromCursor?}` → `WatchResult {subscriptionId, runId, atCursor}` | Durable cluster events; optional IDs/cursors in establishment. `NOT_FOUND`, `GONE`, slow-consumer closure are relevant to retained history. [Watch types][watch-types], [watch server][watch-server] | `INVALID_PHASE`. |
| `logs` S | `LogsParams {}` → `LogsResult {subscriptionId}` | Capability-gated, future-only, cursorless, cluster-wide logs; no run selector, replay or filters. [Log types][log-types], [log capability gate][log-server] | `INVALID_PHASE`, despite initialize's boolean. |
| `agent/attach` S | `AgentAttachParams {execution}` → `AgentAttachResult {subscriptionId}` | Capability-gated, future-only, read-only execution observation; no run selector or cursor. [Attach types][attach-types], [attach gate][attach-server] | `INVALID_PHASE`, despite initialize's boolean. |

The ten unavailable rows inherit `ClusterBackend` defaults rather than falling through to unknown-method routing. Consequently a well-formed request normally receives JSON-RPC application code `-32000` with `data.code: "INVALID_PHASE"`, not `-32601 METHOD_NOT_FOUND`. The `initialize` and `get` delegation and the absence of those ten overrides are visible together in `TargetOecpBackend`. [Backend defaults][backend-defaults], [target adapter][target-backend], [error serializer][rpc-errors].

### Native run contract (10 methods)

The native target's access mode authorizes the connection before dispatch. Direct, hosted-mode, and private-mode target transports use the same adapter; **hosted public-service routing is a separate contract**, detailed below. Recovery support is advertised outside `InitializeResult` through discovery extensions. Initial HTTP submission and recovery admission use canonical UUIDv7 identity checks at the target boundary; generic `RunId` itself is merely a string type. [Target routing][target-transport], [target adapter][target-backend], [run types][run-wire].

| Method / kind | Parameters → result | Native target support, effects, and operation-specific facts |
| --- | --- | --- |
| `run/submit` M | `RunSubmitParams {runId, submission}` → `RunSubmitResult {runId}` | Generic trusted-controller bootstrap contract exists. Target OECP deliberately rejects with `RUN_CONFLICT`; use HTTP run request, which also carries credentials/resolver. [Types][run-wire], [adapter][target-backend] |
| `run/list` R | `RunListParams {}` → `RunListResult {runs: RunStatusResult[]}` | Every retained run projection in this controller/ledger namespace; no filter, cursor, pagination, submission-key lookup, or total count limit in this type. [Types][run-wire], [backend][cloud-backend] |
| `run/status` R | `RunStatusParams {runId}` → `RunStatusResult` | Current identity/source/size/cursor/status and optional recovery metadata. Missing run → `NOT_FOUND`. A runtime failure fallback can be visible without a newly durable event; the wire has no durability marker. [Observation types][observation], [fallback][runtime-observation] |
| `run/watch` S | `RunWatchParams {runId, fromCursor?}` → `RunWatchResult {subscriptionId, runId, atCursor}` | Durable status projections, resume exclusively after cursor. Event body includes `cursor` and status, but **does not contain `workspaceRecovery`**, unlike status/force/list results. [Observation types][observation] |
| `run/logs` S | `RunLogsParams {runId, fromCursor?, execution?}` → `RunLogsResult {subscriptionId, runId, atCursor}` | Retained safe log replay then live records; optional exact opaque execution selector accepts active or settled execution. Without the optional fields, returns all retained safe logs. [Observation types][observation] |
| `run/attach` S | `RunAttachParams {runId, execution}` → `RunAttachResult {subscriptionId, runId, execution}` | Exactly one live execution; no history, cursor, replay, or input channel. Unknown execution → `NOT_FOUND`; inactive/not-live → `GONE`. [Types][observation], [error mapping][cloud-errors] |
| `run/force` M | `RunForceParams {runId}` → `RunForceResult` | Records force intent; idempotent per run. Same projection shape as status, potentially `stopping`, so response is not necessarily terminal or proof of cleanup completion. [Observation types][observation], [controller force][cloud-force] |
| `run/checkpoints` R | `RunCheckpointsParams {runId, after?, limit?}` → `RunCheckpointsResult {runId, checkpoints, nextAfter?}` | Requires workspace-checkpoint support. Exclusive opaque `after`; default limit 50, accepted 1–100, at most 100 results. Checkpoints ordered by sequence. Target rejects unsupported capability with `INVALID_PHASE` and noncanonical run ID with `SCHEMA_VIOLATION`. [Checkpoint types][checkpoints], [adapter][target-backend] |
| `run/resume` M | `RunResumeParams {runId, successorRunId, from?, connections?, connectionResolver?, githubToken?}` → `RunResumeResult {runId, resumedFrom}` | Requires workspace recovery, and checkpoint capability when selected. `from` omitted or `{kind:"restart"}` restarts graph on latest retained workspace; `{kind:"checkpoint",checkpointId}` restores an entry checkpoint and settled prerequisites. Admits a successor; no old provider session, secret or token usage import. Both IDs canonical UUIDv7 at target. [Types][run-wire], [checkpoints][checkpoints], [adapter][target-backend] |
| `run/discard_workspace` M | `RunDiscardWorkspaceParams {runId}` → `RunDiscardWorkspaceResult {runId, discarded}` | Requires workspace recovery; destroys retained recovery workspace, not run/history identity. Canonical run ID required; no general run delete route. [Types][run-wire], [adapter][target-backend], [recovery implementation][cloud-recovery] |

Native error mapping is narrower than the set of internal Rust errors: graph admission becomes invalid-params `GRAPH_INVALID`; submission-key conflict becomes `IDEMPOTENCY_REUSE` with `details.runId`; missing run/execution becomes `NOT_FOUND`; inactive execution becomes `GONE`; unreadable durable observation becomes application `SOURCE_UNAVAILABLE`; source-checkout allocation failure has string code `source_checkout_unavailable`; remaining internal failures become `INTERNAL_ERROR`. Recovery-specific failures are not all public typed error variants. Preserve the protocol's received code and structured details rather than infer a richer taxonomy from private error types. [Native error mapping][cloud-errors], [target recovery guards][target-backend].

### Generic notifications and six subscription payloads

| Wire notification | Direction | Body and semantics |
| --- | --- | --- |
| `event` | Server → client | Body is selected by the established subscription, not by another method name. Six bodies listed next. |
| `subscription/closed` | Server → client | `subscriptionId`, `reason`, and, for durable subscriptions, optional `lastDeliveredCursor`. Reasons are exact strings `done`, `SLOW_CONSUMER`, `SOURCE_UNAVAILABLE`. Closing is terminal for that subscription, not evidence of run success. |
| `subscription/cancel` | Client → server | `{subscriptionId}`. Notification, no response. Removes/wakes only that connection's matching subscription; unknown ID is a no-op. Ends observation only. |
| `$/cancelRequest` | Client → server | `{id: RequestId}`. WebSocket binding requests cooperative cancellation of an in-flight unary operation; unknown/completed ID is ignored, no response or rollback claim. It is not an established-subscription cancel and not a force-stop. The NDJSON binding does not implement this interception. |

Sources: [Notification types][watch-types], [connection cancellation][connection-dispatch], [WebSocket cancel registry][ws-server], [NDJSON routing][ndjson-server], [OpenRPC framing extension][openrpc-builder].

| Established by | `event.params` type and fields | Close body |
| --- | --- | --- |
| `watch` | `EventNotification {subscriptionId, runId, cursor, event: WatchEvent}`. `WatchEvent.type`: `phase`, `node_begin`, `node_end`, `bookmark`, `fault`, `finished`; variant-specific cluster status, admission, node address/input/outcome/fault/final status. | `SubscriptionClosedNotification`, optional cursor. |
| `logs` | `LogEventNotification {subscriptionId, record: LogRecord}`. | `LogsClosedNotification`, no cursor. |
| `agent/attach` | `AgentAttachEventNotification {subscriptionId, event: AgentAttachEvent}`. | `AgentAttachClosedNotification`, no cursor. |
| `run/watch` | `RunWatchEventNotification {subscriptionId, runId, title, source, size, cursor, status}`. | `SubscriptionClosedNotification`, last event cursor if delivered. |
| `run/logs` | `RunLogEventNotification {subscriptionId, runId, cursor, timestamp, execution?, record}`. Timestamp is producer-captured positive JavaScript-safe Unix milliseconds, retained across replay. | Same durable close body. |
| `run/attach` | `RunAttachEventNotification {subscriptionId, runId, execution, event}`. `AgentAttachEvent.type`: `working`, `output {text}`, `settled`. | Generic `SubscriptionClosedNotification` with cursor omitted. |

Sources: [Cluster watch algebra][watch-types], [logs][log-types], [attach][attach-types], [native observation][observation], [native notification dispatch][native-subscriptions]. There are no wire methods named `watch/event`, `logs/closed`, `run/watch/cancel`, or `agent/attach/input`. The OpenRPC extension explicitly describes common framing, and the real connection dispatch handles all six establishments. [OpenRPC builder][openrpc-builder], [connection dispatch][connection-dispatch].

### Existing local and caller-owned stream bindings

The transport-neutral server also accepts newline-delimited JSON (NDJSON) on supplied reader/writer streams and has a stdin/stdout adapter. Each outgoing JSON message occupies one line; diagnostics use a separate stream. Incoming lines are bounded at 1,048,576 bytes and the shared outbound queue at 256 messages. The native portable controller uses this same binding over a local socket/pipe, so OECP is not WebSocket-only. Connecting to an existing endpoint or caller-owned stream is distinct from launching a native process. [NDJSON binding][ndjson-server], [portable serving][portable-process].

| Binding | Address/access prerequisite | Native backend support |
| --- | --- | --- |
| Target WebSocket | Session endpoint and the direct/hosted/private authority documented below. | `initialize`, empty `get`, and nine routed/gated `run/*` methods; other refusals as above. [Target adapter][target-backend] |
| Unix local portable controller | `<storage>/controller.sock`; socket permissions `0600`. Caller must know the storage/endpoint and possess local access. | `initialize`, empty `get`, `run/list`, `run/status`, `run/watch`, `run/logs`, `run/attach`, `run/force`, `run/checkpoints`; run-specific operations enforce the controller's one owned run. [Paths][portable-paths], [Unix binding][portable-unix], [portable backend][portable-backend] |
| Windows local portable controller | `\\.\pipe\zeroshot-<sha256(storage-path OS-encoded bytes)>`; current-user/SYSTEM security descriptor, remote clients rejected. Native connector validates the pipe security descriptor. | Same portable backend. Path hashing is native OS-path encoding, not a portable UTF-8 URL formula. [Paths][portable-paths], [Windows binding][portable-windows], [Windows security][windows-security] |
| Supplied NDJSON streams/stdin/stdout | Caller supplies streams and enclosing identity/lifetime binding; no HTTP discovery or HTTP bearer handshake is part of this framing. | Dispatch uses the same registry, but actual support depends on the supplied backend. [NDJSON binding][ndjson-server], [connection binding][identity] |

Portable `run/submit` returns `RUN_CONFLICT` with its owned run ID; it has no `run/resume` or `run/discard_workspace` override, so those and the ten older cluster methods inherit `INVALID_PHASE`. Its local identity is fixed as principal `local-controller`, tenant `local-run`, expiry `u64::MAX`. The normal local process serves until terminal state after accepting at least one connection; later observation reopens the durable ledger. Neither a deterministic endpoint path nor a ready-file path proves a server remains available after termination. HTTP discovery supplies no local socket/pipe catalog. No remote local-controller creation operation is added by these bindings. [Portable backend][portable-backend], [backend defaults][backend-defaults], [portable lifecycle/identity][portable-process], [discovery][target-types].

Subscription cancellation is implemented on both bindings. `$/cancelRequest` interception is implemented only by WebSocket at this pin; the NDJSON path classifies it as an invalid no-ID request. A client must preserve that binding difference rather than promise unary cancellation on every OECP stream. [WebSocket][ws-server], [NDJSON][ndjson-server], [frame classification][frame-classifier].

## HTTP surface inventory

The fixed native target registers **seven plain HTTP method/path operations plus one WebSocket upgrade**: three consumer HTTP operations and four private operations. The optional UI adds **15 explicit method/path operations across 14 paths**, plus **three public run-history GET operations** on a direct target. Native hosted consumers implement **20 capability operations plus three history reads**, and **four OAuth URL/method operations** (token POST supports two grant flows). These are different count domains: public history appears as both a server mount and a discovered consumer contract. An advertised revocation URL has no native caller; an outbound connection-resolution POST is a separate host-integration contract. Tables below enumerate all of them. [Target router][target-transport], [UI router][ui-router], [HTTP authority][http-authority].

The selected lower-client scope includes all these callable surfaces and their shared data, including dashboard/private/host-integration roles. This describes client bindings; it does not entail serving the callback, provisioning a target, running a native executable, or constructing an operator's private authority. Authentication and endpoint ownership remain operation-specific.

### Fixed native target server routes

Path constants and discovery identity are in the protocol crate; route dispatch and success statuses are in the server implementation. `TargetRunRequest`, session, discovery and private HTTP values use strict Serde unknown-field rejection except the extensible `extensions` object. [native_v2_target.rs:L24-L37][http-17] [native_v2_target.rs:L224-L235][http-18] [native_v2_target.rs:L293-L317][http-19] [native_v2_target.rs:L448-L487][http-20]

| Method/path | Request | Success | Authentication / classification |
|---|---|---|---|
| `GET /.well-known/zeroshot-native-v2` | Empty body | 200 `TargetDiscoveryDocument` | No auth required; nonempty body falls through to 404. [transport.rs:L260-L275][http-21] |
| `POST /native-v2/run` | `TargetRunRequest`; canonical UUIDv7 `runId` | 200 `TargetRunReceipt`; `Cache-Control: no-store` | Direct identity, hosted control bearer, or private capability bearer. Malformed request/ID gives 400 `request.invalid`; admission invalid gives 400 `run.rejected`. [transport.rs:L308-L323][http-22] [transport.rs:L433-L456][http-23] [transport_http.rs:L257-L261][http-24] |
| `POST /native-v2/oecp-session` | `TargetOecpSessionRequest` (`{}` or optional canonical UUIDv7 `runId`) | 200 `TargetOecpSession`; no-store | Same control auth. Direct result omits bearer; private result carries capability; hosted authority issues an OECP-purpose bearer. [transport.rs:L325-L379][http-25] |
| `GET /native-v2/oecp` + WebSocket upgrade | HTTP upgrade; OECP follows | Successful WebSocket handshake | Direct identity, hosted OECP-purpose bearer, or private capability. Wrong method/path on an upgrade receives 404. OECP methods/framing are inventoried above. [transport.rs:L227-L257][http-26] [transport.rs:L426-L456][http-27] |
| `POST /native-v2/private-bootstrap` | Strict `TargetPrivateBootstrapRequest {nonce,ciphertext}` | 204 empty | Private constructor only; cryptographic bootstrap envelope, not OAuth. Invalid envelope 400; closed bootstrap or nonprivate server 404. [transport.rs:L291-L305][http-28] |
| `GET /native-v2/operator-diagnostics/{runId}` | Empty body; canonical UUIDv7 path suffix | 200 `TargetOperatorDiagnostics`; no-store | Private capability only. Nonprivate server 404; malformed body/ID 400. [transport.rs:L278-L289][http-29] [transport.rs:L460-L468][http-30] |
| `POST /native-v2/history/definition` | Strict camelCase `{runId}`; max 4096 bytes | 200 `RunDefinition`; no-store | Private capability only, using already-owned history service. [transport_history.rs:L9-L80][http-31] |
| `POST /native-v2/history/page` | Strict camelCase `{runId, after?}` (`Cursor`) max 4096 bytes | 200 `HistoryPage`; no-store | Private capability only; history-specific problems retained. [transport_history.rs:L9-L85][http-32] |

The target reads at most 32 KiB headers (64 parsed header slots), 4 MiB fixed HTTP body, and 16 KiB bearer. Request-head timeout is 10 seconds; connection count max 256. Fixed control HTTP rejects query strings and fragments, transfer encoding is unsupported, `Content-Length` supplies body length, and responses close connections. Only UI/history paths are allowed query strings by the outer parser. Do not infer a `Content-Type` requirement for this hand-written fixed HTTP parser from the separate UI middleware. [transport.rs:L46-L51][http-33] [transport_http.rs:L98-L168][http-34] [transport_http.rs:L271-L291][http-35]

Server authentication values are `none`, `hosted_oauth`, `private_capability`. Hosted control and OECP authentication delegate to separate `TargetSessionAuthority` methods. Private auth checks a shared capability; direct mode returns a fixed configured identity. Exact bearer header syntax is `Authorization: Bearer TOKEN`; duplicate Authorization headers are rejected by the lookup. [native_v2_target.rs:L332-L338][http-36] [transport.rs:L433-L456][http-23] [transport_http.rs:L41-L61][http-37]

`target serve` chooses private mode only when the hidden `--bootstrap-key-file` is supplied; otherwise it chooses direct and mounts UI when compiled with `ui`. It initializes its controller before UI reads. Standalone `with_ui` rejects hosted/private modes. [parser.rs:L334-L351][http-38] [serve.rs:L69-L103][http-39] [transport.rs:L139-L153][http-40]

#### Fixed HTTP wire objects

All fields below are JSON spellings, not Rust snake_case. `?` means optional/may be omitted; `| null` marks explicit nullable output where material. Refer to the OECP section above for `RunSubmission`, graph/runtime/source, IDs, and shared recovery DTO internals.

| Rust owner | JSON shape / notable behavior | Source |
|---|---|---|
| `TargetRunRequest` | `runId`, `submission: RunSubmission`, `connections: map<ConnectionKey,StaticConnectionValues>`, `connectionResolver?`, `githubToken?`. Connections is required (empty map valid); secret values are ephemeral. | [native_v2_target.rs:L177-L235][http-41] |
| `TargetRunReceipt` | `{runId}` | [native_v2_target.rs:L264-L268][http-42] |
| `TargetOecpSessionRequest` / `TargetOecpSession` | `{runId?}` → `{endpoint,bearerToken?}`. Hosted session request contract requires a run for routing; direct allows omission for target-wide operations. | [native_v2_target.rs:L293-L309][http-43] |
| `TargetConnectionResolver` | `{endpoint,bearerToken,keys:ConnectionKey[],sourceConnection?}`; host integration secret, not a durable run record. | [native_v2_target.rs:L201-L221][http-44] |
| `TargetPrivateBootstrapRequest` | `{nonce,ciphertext}` | [native_v2_target.rs:L311-L317][http-45] |
| `TargetOperatorDiagnostics` / diagnostic | `{diagnostics:[{id,runId,code,operation,exitStatus?,stdout,stderr,stdoutTruncated,stderrTruncated}]}`. Store retains two diagnostics total, then filters requested run; stdout/stderr each bounded 4 KiB with truncation flags. | [native_v2_target.rs:L270-L291][http-46] [operator_diagnostics.rs:L7-L8][http-47] [operator_diagnostics.rs:L38-L101][http-48] |

Private bootstrap nonce is 12 bytes encoded lowercase hex; ciphertext is 64-byte token plus 16-byte GCM tag encoded lowercase hex; AES-256-GCM AAD is `zeroshot-capsule-bootstrap-v1`; accepted plaintext is 64 lowercase-hex ASCII bytes. Bootstrap consumes its key on first accepted payload. These are private integration facts, not an SDK login mechanism. [private_access.rs:L11-L52][http-49] [private_access.rs:L64-L73][http-50] [private_access.rs:L123-L156][http-51]

### Discovery and route compilation

Top-level strict camelCase `TargetDiscoveryDocument`: `kind`, `authentication`, `runPath`, `sessionPath`, `oecpPath`, `audience`, `privateBootstrapPath?`, `oauth?`, `loginSession?`, `extensions?`. Expected kind is `zeroshot.native-v2-target/v2`, audience `controller`. The direct server defaults run/session/OECP paths to the fixed paths and emits no OAuth/login descriptor; it conditionally emits workspace recovery/checkpoint kinds and UI run-history. A `new_hosted` binding's own discovery still comes from `TargetDiscoveryDocument::direct(authentication)`, so the complete OAuth/host capability discovery is host-owned. [native_v2_target.rs:L24-L34][http-52] [native_v2_target.rs:L469-L520][http-53] [transport.rs:L407-L424][http-54]

`extensions` defaults missing entries and ignores unknown extension names (no `deny_unknown_fields` on this outer object). Known extension objects remain strict. Their field naming is deliberately **mixed**; do not apply one global casing strategy. [native_v2_target.rs:L360-L487][http-55]

| `extensions` key | Kind | Exact descriptor JSON fields / route keys |
|---|---|---|
| `hosted_runs` | `zeroshot.hosted-runs/v1` | `kind`, **`base_url`**, **`route_templates`** `{list,status,watch,logs,force}`. [native_v2_target.rs:L360-L376][http-56] |
| `hosted_workspace_recovery` | `openengine.hosted-workspace-recovery/v1` | `kind`, **`route_templates`** `{resume,checkpoints,discard_workspace}`; no own base URL, uses hosted-runs base. [native_v2_target.rs:L378-L393][http-57] [hosted_runs.rs:L189-L244][http-58] |
| `run_history` | `zeroshot.run-history/v1` | `kind`, **`baseUrl`**, **`routeTemplates`** `{list,detail,page}`. [native_v2_target.rs:L395-L411][http-59] |
| `connections` | `zeroshot.connections/v1` | `kind`, `baseUrl`, `routeTemplates` `{list,set,delete,resolve}`, `dynamicKinds?` (default empty). [native_v2_target.rs:L413-L430][http-60] |
| `run_profiles` | `zeroshot.run-profiles/v1` | `kind`, `baseUrl`, `routeTemplates` `{list,show,set,delete,default,run}`. [profile.rs:L12-L12][http-61] [profile.rs:L132-L149][http-62] |
| `merge_plans` | `zeroshot.merge-plans/v1` | `kind`, `baseUrl`, `routeTemplates` `{create,status,force}`. [merge_plan.rs:L11-L18][http-63] [merge_plan.rs:L167-L181][http-64] |
| `workspace_recovery` | `openengine.workspace-recovery/v1` | `{kind}`; direct/private OECP capability, no HTTP recovery paths. [native_v2_target.rs:L440-L446][http-65] [transport.rs:L399-L414][http-66] |
| `workspace_checkpoints` | `openengine.workspace-checkpoints/v1` | `{kind}`; direct/private OECP capability, no HTTP checkpoint paths. [native_v2_target.rs:L432-L438][http-67] [transport.rs:L399-L414][http-66] |

Controller discovery validates kind, requested auth mode, and exact audience. Hosted discovery additionally requires OAuth grant type `urn:ietf:params:oauth:grant-type:device_code`, exactly two exchange fields `device_token` and `device_label`, login session method `GET` and cache policy `no-store`, and a valid mandatory hosted-runs capability. Optional known capabilities, when present, must validate. [contract.rs:L62-L119][http-68] [contract.rs:L348-L460][http-69] [hosted_runs.rs:L189-L209][http-70]

All discovered base/OAuth routes are same-origin, no credentials/query/fragment, and must use canonical URL spelling; route templates max 2048 bytes, absolute-path form but not `//`, no backslash/control/whitespace/fragment. Literal segments are nonempty ASCII alphanumeric/`-._~`, excluding `.` and `..`. Capability route segments append to the capability base path rather than replacing it. Unknown/duplicate variables fail. All production HTTP clients disable redirects; normal requests use 10-second connect and 30-second overall timeouts, streams only 10-second connect. Responses must retain the requested URL. [contract.rs:L167-L310][http-71] [controller_authority.rs:L77-L89][http-72]

Hosted lifecycle templates require: list no variables/query; status and force exactly one `{run_id}`, no query; watch exactly `{run_id}` plus suffix `{?from_cursor}`; logs exactly `{run_id}` plus suffix `{?from_cursor,execution}`; recovery each exactly `{run_id}`, no query. Expansion percent-encodes path values and query values. [hosted_runs.rs:L142-L187][http-73] [hosted_runs.rs:L212-L284][http-74]

Profile and connection management route templates are bounded literal paths; even the unused consumer-side `resolve` declaration must validate. `dynamicKinds` must be distinct, nonempty, at most 128 bytes each, and have no control characters; values are open strings, not a hardcoded kind enum. Merge create has no variable; status/force require one `{plan_id}`, with no queries. History list has `{?after}`, detail one `{run_id}` and no query, page one `{run_id}` and `{?after}`. [connections.rs:L18-L47][http-75] [profiles.rs:L17-L35][http-76] [merge_plans.rs:L74-L120][http-77] [history.rs:L78-L122][http-78]

Named target origins require HTTPS except numeric loopback HTTP (`127.0.0.1`, `::1`), no userinfo/query/fragment/path beyond `/`. OECP session endpoints must match target authority and corresponding `wss`/`ws` scheme and effective port; no query/userinfo/fragment/control/whitespace. The client verifies authority at dial time; its validator does not require a fixed endpoint path, while the concrete native server does route only `/native-v2/oecp`. [contract.rs:L54-L109][http-79] [oecp.rs:L33-L99][http-80] [transport.rs:L227-L230][http-81]

### Host-owned operations consumed by native code

Except OAuth forms and streaming/history limits described below, hosted requests send JSON and accept any successful HTTP status with a valid typed JSON body. The native client does not prescribe a specific 200/201/202 status for these host-owned implementations. It rejects direct-target use of hosted lifecycle, merge plans, remote connections, and remote profile management. [http_error.rs:L9-L31][http-82] [hosted_runs.rs:L118-L159][http-83] [merge_plans_http.rs:L20-L41][http-84] [connections.rs:L12-L29][http-85] [profiles.rs:L46-L63][http-86]

All following hosted operations use OAuth access-token Bearer authentication unless their row explicitly says unauthenticated. URLs are discovery values, not hardcoded server endpoints. Request and response pairs are exhaustive for the native consumer methods. [control.rs:L150-L308][http-3]

| Capability operation | Method / request | Result / framing | Source |
|---|---|---|---|
| `hosted_runs.list` | GET, no body | `HostedRunListResult` | [hosted_runs.rs:L162-L172][http-87] [hosted_runs.rs:L201-L221][http-88] |
| `hosted_runs.status` | GET `{run_id}` | `HostedRunStatusResult` | [hosted_runs.rs:L212-L221][http-89] |
| `hosted_runs.watch` | GET `{run_id}`, optional `from_cursor` | NDJSON `HostedRunStreamFrame<HostedRunWatchEventNotification>` | [hosted_runs.rs:L175-L198][http-90] [hosted_runs.rs:L223-L231][http-91] |
| `hosted_runs.logs` | GET `{run_id}`, optional `from_cursor`, `execution` | NDJSON `HostedRunStreamFrame<RunLogEventNotification>` | [hosted_runs.rs:L233-L245][http-92] |
| `hosted_runs.force` | POST `{}` at `{run_id}` route | `HostedRunForceResult` = status result | [hosted_runs.rs:L247-L261][http-93] [native_v2_hosted.rs:L99-L105][http-94] |
| `hosted_workspace_recovery.resume` | POST `RunResumeParams` at `{run_id}` | `RunResumeResult` | [recovery_http.rs:L11-L57][http-95] |
| `hosted_workspace_recovery.checkpoints` | POST `RunCheckpointsParams` at `{run_id}` | `RunCheckpointsResult` | [recovery_http.rs:L23-L57][http-96] |
| `hosted_workspace_recovery.discard_workspace` | POST `RunDiscardWorkspaceParams` at `{run_id}` | `RunDiscardWorkspaceResult` | [recovery_http.rs:L34-L57][http-97] |
| `connections.list` | POST `ConnectionListRequest` | `ConnectionListResult` | [connections.rs:L32-L44][http-98] |
| `connections.set` | POST `ConnectionSetRequest` | `ConnectionMutationResult` | [connections.rs:L47-L59][http-99] |
| `connections.delete` | POST `ConnectionDeleteRequest` | `ConnectionDeleteResult` | [connections.rs:L62-L74][http-100] |
| `run_profiles.list` | POST `RunProfileListRequest` | `RunProfileListResult` | [profiles.rs:L66-L94][http-101] |
| `run_profiles.show` | POST `RunProfileSelector` | `RunProfile` | [profiles.rs:L96-L103][http-102] |
| `run_profiles.set` | POST `RunProfileSetRequest` | `RunProfileMutationResult` | [profiles.rs:L105-L112][http-103] |
| `run_profiles.delete` | POST `RunProfileSelector` | `RunProfileDeleteResult` | [profiles.rs:L114-L121][http-104] |
| `run_profiles.default` | POST `RunProfileDefaultRequest` | `RunProfileDefaultResult` | [profiles.rs:L123-L130][http-105] |
| `run_profiles.run` | POST `RunProfileRunRequest` | `RunSubmitResult` (`{runId}`) | [profiles.rs:L132-L139][http-106] |
| `merge_plans.create` | POST `MergePlanSubmitRequest` | `MergePlan` | [merge_plans_http.rs:L44-L81][http-107] |
| `merge_plans.status` | GET `{plan_id}` | `MergePlan` | [merge_plans_http.rs:L59-L65][http-108] |
| `merge_plans.force` | POST `{}` at `{plan_id}` | `MergePlan` | [merge_plans_http.rs:L67-L81][http-109] |
| `run_history.list` | GET optional `after` = canonical UUIDv7 RunId | `RunHistoryList`; max 4 MiB / 50 entries | [history.rs:L109-L166][http-110] [wire.rs:L12-L21][http-111] |
| `run_history.detail` | GET `{run_id}` | `RunDefinition`; max 8 MiB | [history.rs:L109-L166][http-110] [wire.rs:L12-L21][http-111] |
| `run_history.page` | GET `{run_id}`, `after` cursor | `HistoryPage`; max 8 MiB | [history.rs:L109-L166][http-110] [wire.rs:L12-L21][http-111] |

The history consumer is compiled with `ui`; direct history omits OAuth, hosted history retains auth only server-side. Native connection management has no `show secret`, generic dynamic-kind creation, or end-user `resolve` caller: only list/set/delete, with `set` using static values. `resolve` is the distinct host callback described below. [controller_authority.rs:L6-L12][http-112] [history.rs:L59-L107][http-113] [connections.rs:L27-L35][http-114] [connections.rs:L32-L75][http-115]

#### OAuth consumed contract

Discovery OAuth object is strict camelCase `{metadataUrl,deviceAuthorizationEndpoint,tokenEndpoint,revocationEndpoint,clientId,deviceGrantType,deviceExchangeFields}`; login descriptor strict camelCase `{routeTemplate,method,cachePolicy}`. Metadata response instead uses snake_case `device_authorization_endpoint`, `token_endpoint`, `revocation_endpoint`, and tolerates extra metadata fields. The three endpoints must exactly match discovery and all URLs must share target origin. [native_v2_target.rs:L340-L358][http-116] [contract.rs:L32-L37][http-117] [contract.rs:L416-L448][http-118]

| Operation | HTTP request and authentication | Typed result |
|---|---|---|
| Metadata | Unauthenticated GET discovered `metadataUrl`, `Accept: application/json` | `OAuthMetadataWire` above. [controller_authority.rs:L142-L155][http-119] [controller_authority.rs:L421-L449][http-120] |
| Begin device authorization | Unauthenticated POST form `{client_id}` to discovered device endpoint | Strict snake_case `DeviceCodeWire {device_code,user_code,verification_uri,verification_uri_complete?,expires_in,interval}`. [controller_authority.rs:L197-L204][http-121] [contract.rs:L128-L137][http-122] |
| Poll device token | Unauthenticated POST form `{grant_type,device_code,client_id,device_token,device_label:"zeroshot-cli",audience:"controller"}` | Strict snake_case `TokenWire` on any 2xx; on non-success strict `{error}` only. Supported poll errors: `authorization_pending`, `slow_down` (+5 seconds up to 300), `access_denied`, `expired_token`; unknown response errors fail. [controller_authority.rs:L38-L41][http-123] [controller_authority.rs:L226-L285][http-124] |
| Refresh access | Unauthenticated POST form `{grant_type:"refresh_token",client_id,refresh_token,audience}` | Strict `TokenWire {access_token,refresh_token,token_type,expires_in,refresh_expires_in,scope}`. Verifies session then persists rotated refresh token before caching access token. [controller_authority.rs:L288-L325][http-125] [contract.rs:L139-L148][http-126] |
| Verify login session | Bearer GET discovered login route; `Accept: application/json`, `Cache-Control: no-store` | Strict snake_case `{kind:"openengine.target-session/v1",organization_id}`; nonempty org ID max 256 bytes. [controller_authority.rs:L328-L362][http-127] [contract.rs:L156-L161][http-128] |
| Revocation | Advertised and route-validated only; no outgoing revocation operation is implemented by `TargetHttpControlAuthority` or `TargetControlAuthority` at this pin | **Coverage gap / advertised-only contract**, not a guessed endpoint method or logout API. [contract.rs:L416-L448][http-118] [control.rs:L67-L118][http-129] |

Device/user codes: nonempty, no ASCII controls, device code ≤16 KiB, user code ≤256 bytes. Device expiry 1..86400 seconds; polling interval ≤300 (zero is accepted by validation). Verification URL ≤4096 bytes, HTTPS or numeric-loopback HTTP, no credentials/fragments; base verification URL has no query, optional complete URL may have query but must share base verification origin. Token type must be exact `Bearer`; access and refresh values nonempty ≤16 KiB, no ASCII controls; access expiry 1..86400 seconds; refresh expiry 1..31536000; scope nonempty ≤512 bytes without ASCII controls. [contract.rs:L463-L547][http-130]

Authority clones share one cached issuance keyed by target record, metadata/token/session endpoints, client ID, audience, with a 30-second expiry skew. Login clears the cache. Rejection invalidates only the exact issuance: HTTP 401 or HTTP 403 with Bearer `invalid_token` challenge. Only merge-plan operations have a built-in one-retry-after-auth-rejection loop; do not generalize to every mutating request. [access.rs:L30-L49][http-131] [access.rs:L103-L180][http-132] [control.rs:L76-L92][http-133] [controller_authority.rs:L320-L325][http-134] [merge_plans_http.rs:L84-L106][http-135]

#### Hosted wire object fields

| Family | JSON wire objects | Source |
|---|---|---|
| Connections | `ConnectionScope = "user" \| "org"`; list request `{scope}`; set `{key,scope,values}`; delete `{key,scope}`; summary `{key,scope,kind,fields:string[]}`; list result `{connections:summary[]}`; mutation `{connection:summary}`; deletion `{deleted:bool}`. All objects strict camelCase. `kind` is open string; protocol names `static` and `github-app-installation` but summary does not enforce a closed enum. | [native_v2_target.rs:L34-L47][http-136] [native_v2_target.rs:L129-L175][http-137] |
| Static secret map | `values` is map of valid environment names to nonempty NUL-free strings, 1..64 entries; each value ≤64 KiB; sum of field-name and value bytes ≤256 KiB. Debug exposes only field names. | [native_v2_target.rs:L39-L125][http-138] |
| Profiles | Scope `"user" \| "org"`. Full `RunProfile {id,name,scope,graph,runtime,isDefault}`; summary omits graph/runtime. Set `{name,scope,graph,runtime,setDefault?}` default false. List `{scope}`; selector `{scope,name}`; default request `{scope,name?}`; list result `{profiles:summary[]}`; mutation `{profile:RunProfile}`; deletion `{deleted}`; default result `{scope,name:string\|null}`. Strict camelCase. | [profile.rs:L14-L96][http-139] |
| Profile run | `{runId,profile:RunProfileSelector,title,initialInput,source:ResolvedSource,submissionKey,environment?,connections,githubToken?}`. Omitted environment permits host default, `{}` selects base environment. | [profile.rs:L98-L114][http-140] |
| Hosted status | `{runId,title,source,size,atCursor,status,workspaceRecovery?}`. `status` is untagged union of strict `{phase:"queued"}` and native `RunStatus`; queued is only host-only phase. Force is same type; list `{runs:status[]}`. | [native_v2_hosted.rs:L18-L30][http-141] [native_v2_hosted.rs:L58-L106][http-142] |
| Hosted watch / stream | Watch event `{subscriptionId,runId,title,source,size,cursor,status}` (no workspaceRecovery). Strict frames `{type:"event",event:E}` or `{type:"closed",reason:SubscriptionCloseReason}`; logs E is native `RunLogEventNotification`. | [native_v2_hosted.rs:L116-L157][http-143] |
| Recovery | Resume `{runId,successorRunId,from?,connections?,connectionResolver?,githubToken?}` → `{runId,resumedFrom}`; discard `{runId}` → `{runId,discarded}`. `from` is `{kind:"restart"}` or `{kind:"checkpoint",checkpointId}`; absent means latest-workspace restart. Checkpoints `{runId,after?,limit?}` → `{runId,checkpoints,nextAfter?}`, limit default 50 / max 100; all recovery request/result types are the same OECP Rust DTOs. | [wire.rs:L131-L185][http-144] [checkpoints.rs:L41-L110][http-145] [recovery_http.rs:L1-L57][http-146] |
| Merge submit | Strict `{submissionKey,title,expiresAt,source:{repository,branch},profile:{scope,name},runs:[{name,needs?,initialInput}],environment?,connections?,githubToken?}`; connections defaults empty, needs defaults empty. This is **not** the CLI manifest `{schema,profile:"org:name",runs:{name:{input,needs}}}`. | [merge_plan.rs:L20-L54][http-147] [merge_plans.rs:L26-L54][http-148] |
| Merge result | `{planId,title,state,repository,branch,submittedAt,expiresAt,runs:[{name,runId,state,needs,sourceRevision,readyAt,queueExpiresAt,terminalAt,waitingReason,errorCode}]}`. All six optional values in each run are **required nullable fields**; missing is not equivalent to null. `planId` aliases `RunId`; symbolic names alias `RunProfileName`; 64 maximum runs is a protocol constant (semantic validation lives outside basic Vec deserialization). | [merge_plan.rs:L11-L18][http-63] [merge_plan.rs:L107-L165][http-149] |
| Merge states | Plan `queued,running,succeeded,failed,cancelled,expired`; run `blocked,materializing,queued,provisioning,running,cancelling,succeeded,failed,cancelled,expired`. Terminal states in each are succeeded/failed/cancelled/expired. | [merge_plan.rs:L56-L104][http-150] |

Hosted watch/logs request `Accept: application/x-ndjson` and no-store. Frames are at most 64 KiB, one JSON value per newline; CRLF is accepted, empty lines fail, EOF with an unterminated partial frame is a disconnect. Native code requests NDJSON but does not check response Content-Type in `hosted_stream`; successful status is what admits the stream. A `closed` frame marks subscription closed. [hosted_runs.rs:L23-L24][http-151] [hosted_runs.rs:L49-L115][http-152] [hosted_runs.rs:L175-L198][http-90]

#### Host connection callback

The callback request is strict camelCase `ConnectionResolveRequest {runId,connections:map<ConnectionKey,EnvironmentVariableName[]>}` and response `ConnectionResolveResult {connections:RunConnectionValues}`. The enclosing `TargetConnectionResolver` supplies an HTTPS URL (host required; no userinfo, query or fragment), bearer token ≤16 KiB, distinct nonempty dynamic key set, and optional `sourceConnection` that must belong to the key set. This endpoint may be host-controlled outside the target origin; unlike discovery capability routes, this constructor has no target-origin comparison. [native_v2_target.rs:L185-L209][http-153] [connections.rs:L45-L79][http-154]

Native hosting POSTs JSON with bearer, rejects redirects, times out at 30 seconds, accepts any 2xx valid response up to 300 KiB, and does not parse HTTP problem bodies. Status mapping: 401/403 → `Refused`; 408/429/5xx or network/read failure → `Unavailable`; other non-success, malformed/oversized body → `InvalidResponse`. This is an outbound host integration contract, not an advertised inbound server path. [connections.rs:L17-L42][http-155] [connections.rs:L88-L145][http-156]

### Browser UI and run history

Browser route registry is exhaustive in `profile_ui::router`. UI runs as loopback-only local service by default (`127.0.0.1:4173` CLI), or mounts on an explicitly direct target listener. UI service returns embedded static assets and browser authoring/profile/history services. No UI route starts, resumes, force-stops or deletes a run. [profile_ui.rs:L67-L98][http-2] [server.rs:L149-L186][http-157] [parser.rs:L35-L48][http-158]

The counts and table below enumerate explicit registrations. Axum 0.8.9's `get` routing also accepts `HEAD`, stripping the response body: the 11 UI GET registrations and three conditional history GET registrations therefore have implicit HEAD bindings, with the same route prerequisites. The separate hand-written fixed target router does not map HEAD to GET. [Pinned Axum version][axum-lock], [versioned Axum `get` contract][axum-get], [UI registry][ui-router], [fixed router][target-transport].

| Route | Request / response | Category/source |
|---|---|---|
| GET `/`, GET `/ui` | Temporary redirect to `/ui/` | Browser/static. [profile_ui.rs:L69-L71][http-159] |
| GET `/ui/`, GET `/ui/{*asset}` | Embedded `index.html` / matching asset; missing asset bare404 | Browser/static, no JSON contract. MIME selected by extension. [server.rs:L345-L367][http-160] |
| GET `/ui/api/bootstrap` | `{version:1,templates,workers,runtimeSchema,workspace:{kind:"local"\|"target",id}}` | Browser catalog; runtime schema generated live via `schema_for!(RuntimePlan)`. [profile_ui.rs:L42-L63][http-161] [profile_ui.rs:L100-L104][http-162] [workspace.rs:L43-L50][http-163] |
| GET `/ui/api/profiles` | No body → `RunProfileListResult`, user scope only | Browser local profile store, not hosted management extension. [profile_ui.rs:L106-L114][http-164] |
| GET `/ui/api/profiles/{name}` | Profile name → `{profile:RunProfile,revision:string}` | User scope only. [profile_ui.rs:L115-L128][http-165] [profile_ui.rs:L214-L216][http-166] |
| POST `/ui/api/profiles` | Strict `{name,graph,runtime,expectedRevision?}`, exact `X-Zeroshot-Workspace` header | → `{profile,revision}` after admission+CAS. 409 `workspace_changed` or `profile_conflict`; no set-default or remote scope field. [profile_ui.rs:L129-L142][http-167] [profile_ui.rs:L161-L200][http-168] |
| POST `/ui/api/validate` | Strict `{graph:GraphSpec,runtime:RuntimePlan}` | → `{valid:true}`, native profile admission. [profile_ui.rs:L129-L150][http-169] [workspace.rs:L67-L79][http-170] |
| POST `/ui/api/authoring` | Strict `{graph:GraphSpec,runtime:Value,action}`. action tagged `kind`: `failure_reason {terminal,reason}`, `complete {owner}`, `protect {node}` | → draft `{graph,runtime}`, no save/run. [profile_ui.rs:L151-L155][http-171] [outcomes.rs:L20-L49][http-172] [workspace.rs:L53-L57][http-173] |
| POST `/ui/api/data` | Strict `{graph:Value,runtime:Value,action}`; action/source types below | → draft `{graph,runtime}`, no save/run. [profile_ui.rs:L156-L159][http-174] [data.rs:L12-L85][http-175] |
| GET `/ui/api/runs` | Optional `after` UUIDv7 → `RunHistoryList` | Browser BFF; local or configured-target history. [runs.rs:L622-L635][http-176] |
| GET `/ui/api/runs/{id}` | UUIDv7 → `RunDefinition` | Browser BFF. [runs.rs:L637-L642][http-177] |
| GET `/ui/api/runs/{id}/history` | Optional `after` cursor → `HistoryPage` | Browser BFF. [runs.rs:L644-L652][http-178] |
| GET `/ui/api/runs/{id}/events` | Optional `after`, or one `Last-Event-ID` header taking precedence | SSE `history` events with id=`page.nextCursor` and whole page JSON; `history_error` with `{code,message}`; 15-second keepalive. [runs.rs:L654-L699][http-179] |
| GET `/native-v2/run-history` | Optional `after` UUIDv7 → `RunHistoryList` | Direct target+UI only; advertised list template `/native-v2/run-history{?after}`. [profile_ui.rs:L83-L90][http-180] [server.rs:L27-L29][http-181] [server.rs:L74-L84][http-182] |
| GET `/native-v2/run-history/{id}` | UUIDv7 → `RunDefinition` | Direct target+UI only; advertised template variable `{run_id}`. [profile_ui.rs:L83-L90][http-180] [server.rs:L74-L84][http-182] |
| GET `/native-v2/run-history/{id}/page` | Optional `after` cursor → `HistoryPage` | Direct target+UI only; advertised page suffix `{?after}`. No advertised SSE route. [profile_ui.rs:L83-L90][http-180] [server.rs:L74-L84][http-182] |

Data action union (snake_case `kind`, strict fields): `connect {target:{node,input},source}`; `remove_input {target}`; `map_collection {node,source}`; `run_input_field {before?,name,type:PayloadType,required:bool}`; `remove_run_input {name}`. Source union: `run_input {path}`, `node_output {node,channel,path}`, `map_item {path}`, `loop_input {node,path}`; channel `out|signal|diagnostic`; paths are `FieldName[]`. These are editor implementation contracts, not OECP graph fields to add to `RunSubmission`. [data.rs:L26-L85][http-183]

Every UI request must have exact configured Host, optional exact Origin, and optional Sec-Fetch-Site `same-origin` or `none`; repeated checked headers fail. Forwarded headers are not consulted. POST requires `application/json` media type (optional parameters accepted), body limit 2 MiB. Boundary errors: 403 `origin_rejected`, 503 `server_stopping`, 415 `json_required`; all UI responses add no-store, nosniff, no-referrer and CSP. General UI JSON problems are `{code,message}`; profile invalidity returns 422 `invalid_profile`, storage errors 500 `profile_store_error`. [server.rs:L250-L290][http-184] [server.rs:L293-L342][http-185] [profile_ui.rs:L31-L31][http-186] [profile_ui.rs:L206-L212][http-187] [profile_ui.rs:L234-L270][http-188]

#### History DTOs and semantic contract

All following history objects are camelCase; strict unknown-field rejection applies to the wire records in `wire.rs`. `RuntimeFailure` and `ControlRecord` are separate structs without `deny_unknown_fields`. Cursor text is canonical `v2:<sequence>` with sequence ≤i64::MAX across the host boundary, not an opaque pagination token to invent. [wire.rs:L129-L223][http-189] [control.rs:L28-L45][http-190] [status.rs:L24-L29][http-191] [contract.rs:L172-L177][http-192]

| Type | JSON fields | Source |
|---|---|---|
| `RunHistoryList` | `{runs:RunHistorySummary[],nextCursor:RunId\|null}`. At most 50, UUIDv7 descending; nextCursor equals final returned ID when present. | [wire.rs:L145-L151][http-193] [runs.rs:L502-L541][http-194] |
| `RunHistorySummary` | `{runId,title,phase,cursor:Cursor\|null,terminal:Synopsis\|null,source:ResolvedSource\|null,createdAt:u64\|null,historyAvailable:bool,runtimeFailure?}`. Phase `queued,admitted,running,stopping,finished,unavailable`. Synopsis `{status:"succeeded"}` or `{status:"failed",reason}`. | [wire.rs:L87-L143][http-195] |
| `RunDefinition` | `{version:1,projectionVersion:1,runId,title,createdAt:u64\|null,phase:RunPhase,cursor,terminal:TerminalResult\|null,historyAvailable,graph,runtime,initialInput,source,history:HistoryMetadata,observation:Observation,runtimeFailure?}`. Legacy `snapshot` accepted for decoding but stripped on serialization. | [wire.rs:L153-L178][http-196] [contract.rs:L30-L49][http-197] |
| `HistoryMetadata` | `{initialCursor,cursor,complete,limitations:string[]}` | [wire.rs:L180-L187][http-198] |
| `HistoryPage` | `{events:Value[],nextCursor,headCursor,complete,finished,observation,runtimeFailure?,control?:ControlRecord[],controlError?}`; empty control omitted. The actual raw event contract is enumerated below; it has no generated standalone HTTP schema. | [wire.rs:L189-L204][http-199] |
| `Observation` | `{state,code?}`; state `active,collecting,complete,incomplete,expired,unavailable`. Availability distinct from terminal run status. | [wire.rs:L206-L223][http-200] |
| `RuntimeFailure` | `{atCursor,reason}`; host validation admits only `runtime_failed` or `runtime_lost`, atCursor ≤head. | [status.rs:L24-L29][http-191] [contract.rs:L11-L13][http-201] [contract.rs:L160-L169][http-202] |
| `ControlRecord` | `{cursor,node,mapIndices:u64[],visitId,state,branch?,detail?,output?}`. Explicit `output:null` means present authored null; absence means no output. | [control.rs:L28-L52][http-203] |

History page replay inherits the ledger bounds of **256 events / 2 MiB + 1 KiB**, plus a separate derived-control 4 MiB limit. The opening history-wire comment still says 1 MiB; the authoritative constant is `MAX_REPLAY_BYTES = MAX_PRIOR_EXECUTION_BYTES = 2 * MAX_EVENT_BYTES + 1024`. Ordinary events are bounded at 1 MiB; a prerequisite event can contain two bounded source events. Encoded HTTP envelopes have broader limits: list 4 MiB / definition 8 MiB / page 8 MiB / problems 64 KiB. Host semantic checks require version 1 / projectionVersion 1, requested run identity, historyAvailable true, initialCursor `v2:0`, matching definition/history cursor; page next between requested/head, `complete == (next == head)`, contiguous event cursors, event count matching next, control cursors belonging to ordered page events, coherent runtime-failure state. Do not end follow based solely on `finished`; observation availability is independent. [Ledger bounds][ledger-bounds], [wire.rs:L1-L21][http-204], [contract.rs:L20-L177][http-205], [wire.rs:L206-L223][http-200].

Each raw history element is `{cursor,event}`. `event` is a strict `kind`-tagged union with the following nine variants; it is not a run-watch notification. The HTTP projection serializes `StoredRunEvent` and rewrites selected identities before returning JSON. [Stored events][ledger-events], [history projection][history-projection].

| `event.kind` | Other fields | Projection detail |
| --- | --- | --- |
| `prior_execution` | `execution: DurableExecution` | Preserves the nested durable record as serialized, including numeric identities and snake_case field names. |
| `run_started` | None | Unit event. |
| `node_started` | `reference, occurrence, attempt, input` | `reference.execution` and `reference.nodeInstance` become decimal strings. |
| `node_completed` | `completion:{reference,outcome}` | The same two fields inside `completion.reference` become decimal strings. |
| `execution_voided` | `reference,reason` | The same reference fields become decimal strings; reason `parallel_join` or `map_terminal`. |
| `safe_log` | `execution:null\|string, timestamp, stream, line` | Non-null execution becomes decimal string; stream `output`, `error`, or `system`; line ≤16 KiB and NUL-free. |
| `token_usage_observed` | `execution:string, usage:null\|TokenUsageDelta` | Execution becomes decimal string. |
| `force_stop_requested` | None | Unit event. |
| `terminal` | `result: TerminalResult` | Retained terminal evidence. |

`ExecutionRef` otherwise has `{runId,node,nodeInstance,execution}`. `StructuralOccurrence` has `{node,mapIndices:u64[]}`. `TokenUsageDelta` has `{inputTokens,outputTokens,cacheReadInputTokens:null|count,cacheCreationInputTokens:null|count}`. `DurableExecution` has literal fields `{dispatch_position,node_instance,execution,occurrence,attempt,input,state}`; state is externally tagged as `"Active"`, `{"Settled":{"position":number,"outcome":WorkerOutcome}}`, or `{"Voided":{"position":number,"reason":ExecutionVoidReason}}`. These durable records do not carry the same unknown-field restrictions as the strict outer event. A history reader must not assume that every nested identity is string-encoded. [Native event values][native-event-values], [durable history types][durable-history-types], [history projection][history-projection].

`WorkerOutcome` is a `status`-tagged union: `verified {output,artifacts}`, `verifier {output,signals,diagnostic,artifacts}`, or `error {code,reason}`. Error code is `timeout`, `crash`, `malformed`, or `refusal`; reason is `declared_failure`, `policy_denied`, `interactive_input_required`, `authentication_required`, or `malformed_result`. `ControlRecord.state` is `entered`, `completed`, `succeeded`, `failed`, or synthesized `stopped`; `visitId` is the serialized tuple `(node,map_indices,loop_iterations)`. [Worker outcomes][worker-outcomes], [control projection][history-control].

The history projection does not blanket-redact caller input, output or diagnostic payloads; its transformation is the selected identity conversion above. Immutable admission excludes separately held native credential values, and provider diagnostic/log sanitization happens upstream. Those source-specific safeguards do not imply that arbitrary caller-supplied JSON is secret-free. [History projection][history-projection], [admitted versus invocation data][native-event-values], [separate secrets][cloud-secrets], [provider diagnostics][provider-diagnostics].

Closed history problem vocabulary: `run_not_found`, `not_found`, `forbidden`, `invalid_cursor`, `history_gap`, `runtime_unavailable`, `history_unavailable`, `history_incomplete`, `history_pending`, `history_invalid`, `history_expired`, `history_incompatible`. Native emitted statuses include 404 run_not_found, 503 history_unavailable/runtime_unavailable, 409 history_gap, 400 invalid_cursor. UI BFF uses 502 history_incompatible; remote unknown/malformed problem contracts are converted to unavailable. [wire.rs:L23-L70][http-206] [history.rs:L232-L276][http-207] [runs.rs:L570-L590][http-208] [runs.rs:L755-L760][http-209]

### HTTP errors and limits

`TargetHttpProblem` is strict `{code,message,details?}` with code 1..128 ASCII alphanumeric/underscore/hyphen/dot bytes; message 1..1024 UTF-8 bytes with no Unicode controls; details when non-null must be object ≤61440 encoded bytes. Message-only bodies and unknown extra fields fail deserialization. This is separate from OAuth poll `{error}` and OECP JSON-RPC errors. [problem.rs:L6-L61][http-210] [problem.rs:L85-L101][http-211]

| Fixed server HTTP status | Problem code | Source |
|---|---|---|
| 400 malformed/invalid request | `request.invalid` | [transport_http.rs:L224-L226][http-212] |
| 400 run admission rejection | `run.rejected` | [transport_http.rs:L257-L261][http-24] |
| 401 auth rejected | `request.unauthorized`, safe message `unauthorized` | [transport_http.rs:L244-L254][http-213] |
| 404 unknown/disallowed path | `request.not_found` | [transport_http.rs:L232-L234][http-214] |
| 408 request head timeout | `request.timeout` | [transport_http.rs:L228-L230][http-215] |
| 409 conflict | `request.conflict` | [transport_http.rs:L244-L254][http-213] |
| 503 unavailable | `target.unavailable` | [transport_http.rs:L236-L254][http-216] |
| 500 serialization failure | `target.internal_error` | [transport_http.rs:L188-L221][http-217] |

Normal native HTTP success/error body decoding is bounded at 64 KiB, merge-plan results 1 MiB, history-specific bounds above. If a host error fails shared problem parsing, fallback native codes are 400 `invalid_request`, 401 `unauthorized`, 403 `forbidden`, 404 `not_found`, 409 `request_conflict`, 429 `rate_limited`, 503 `target.unavailable`, otherwise `target.http_error`. Valid native problem code/message/details are retained and local exception metadata injects authoritative `httpStatus`. These are native-client behavior, not proof that every host emits every listed status. [contract.rs:L29-L30][http-218] [contract.rs:L313-L346][http-219] [merge_plans_http.rs:L10-L10][http-220] [http_error.rs:L34-L84][http-221]

### Absent routes and transport limits

- `target add` is local named-target registration; `target login` is the OAuth flow above, not an inbound `/login` API. `target serve` is a listener lifecycle command. `version`, self-update, built-in template list/show, local profile and local connection stores, source resolution and runtime/template materialization are CLI/local behaviors; do not invent native HTTP routes for them. Closed fixed/UI router registries and Clap ownership provide the boundary. [parser.rs:L104-L153][http-231] [parser.rs:L196-L215][http-232] [parser.rs:L304-L351][http-16] [profiles.rs:L28-L35][http-233] [transport.rs:L260-L275][http-21] [profile_ui.rs:L67-L98][http-2]
- Experimental ACP is a separate local stdio session contract, not an HTTP route. Its MVP only accepts a local profile, one active session/text prompts; no maps/delivery/connections/MCP/session reload. Its endpoint belongs to a separate protocol. [parser.rs:L28-L33][http-234] [parser.rs:L97-L102][http-235]
- `plan validate` checks a local manifest without target communication. `plan watch` polls the `status` operation every second and emits changed snapshots; there is no merge-plan watch HTTP route, mutable-plan route, or retry-in-place route in discovery. CLI manifest `schema:"zeroshot.merge-plan/v1"` and profile/runs syntax differ from HTTP submit DTO. [merge_plans.rs:L24-L62][http-236] [merge_plans.rs:L248-L272][http-237] [merge_plan.rs:L167-L181][http-64]
- Direct target does not use hosted remote connection management, hosted profile management, hosted lifecycle, or merge plans. Direct recovery remains OECP. Hosted recovery uses its own outer HTTP extension and does not extend the strict hosted-runs route object. [connections.rs:L13-L25][http-238] [profiles.rs:L47-L59][http-239] [hosted_runs.rs:L119-L127][http-240] [merge_plans_http.rs:L20-L37][http-241] [native_v2_target.rs:L360-L393][http-242]
- Native hosted discovery has no advertised hosted `attach` HTTP route; only list/status/watch/logs/force. Attach continues through run-scoped OECP where supported. There is no history SSE route in `run_history` discovery; SSE exists only in local/browser BFF route registry. [native_v2_target.rs:L360-L375][http-243] [native_v2_target.rs:L395-L411][http-59] [profile_ui.rs:L75-L78][http-244]
- Private exports, dynamic connection callbacks, provider HTTP clients, delivery provider APIs, worker/controller internals, and embedded UI assets are distinct from public consumer target methods. Private exports and the callback contract are selected client-binding scope here; unrelated provider endpoints are not target server routes. [transport.rs:L260-L275][http-21] [connections.rs:L88-L126][http-12] [profile_ui.rs:L67-L98][http-2]


## Shared values and document contracts

The generated schema closure and the Rust modules below are the type inventory, not just the types directly named in a method's result. Graphs, runtime plans, source identity, connection declarations, arbitrary input/output values and recovery metadata are needed even by a client that only submits pre-authored documents. These public documents do not require a client to implement graph execution, compile the graph, expand templates, or resolve provider availability. [Protocol exports][protocol-lib], [run submission][run-wire], [graph][graph-types].

| Family | Values that need faithful representation | Canonical source / generated location |
| --- | --- | --- |
| JSON-RPC and cluster | `RequestId` string or signed 64-bit integer; request/notification/success/error envelopes; `DomainErrorData {code,details?}`; `Initialize*`, `ServerCapabilities`, `ClusterStatus`, `Get*`, `Phase`, `Generation`, `Cursor`, `RunId`. Cluster phase: `empty`, `admitting`, `running`, `finished`, `deleting`. | [protocol `lib.rs`][protocol-lib]; `schema.json`. |
| Admission/lifecycle | `Plan*`, `Apply*`, `GraphDiff`; `Update*`, `Stop*`, `Retry*`, `Resubmit*`, `Delete*`; `Labels`, `LogLevel`, `DispatchState`, `StopMode`, `OperationalStatus`, retry-frontier reason. Dispatch states: `active`, `suspended`, `draining`, `force_stopping`, `stopped`. | [admission][admission-types], [lifecycle][lifecycle-types]; `schema.json`. |
| Native immutable submission | `RunSubmission {title,graph,initialInput,runtime,environment?,source,submissionKey}`. `ResolvedSource {repository,branch,revision}`; `RunTitle`, `ModelId`, repository/branch/revision IDs, connection keys and environment names use custom bounded validators. | [wire][run-wire], [run value definitions][run-values]; `schema.json`. |
| Runtime | `RuntimePlan.harness`: `codex`, `claude`, `copilot`, each with `provider`, `size`, node-name→binding map. Codex providers: `openai`, `openrouter`, `gateway`, `bedrock`; Claude: `anthropic`, `openrouter`, `gateway`, `bedrock`; Copilot: `github`. `RunSize`: `small`, `medium`, `large` (decoder aliases `tiny`, `standard`). | [runtime wire][run-wire], [provider/size enums][run-values]; `schema.json`. |
| Per-node runtime | `NodeRuntimeBinding.kind`: `agent {model,effort?,sessionScope?,connections?}` or `git_delivery {connections?,pullRequestFeedback?}`. Effort: `low`, `medium`, `high`, `xhigh`, `max`; session scope defaults `execution`, also `node_instance`; PR feedback defaults `consider`, also `ignore`. Model IDs are caller-owned strings, not a discoverable model catalog. | [bindings][runtime-bindings], [enums][run-values]; `schema.json`. |
| Environment | `RuntimeEnvironment {setup?,startup?,variables?,connections?}`. Public setup/startup scripts; nonsecret variables; declared hook connection references. Omission can allow host resolution; explicit `{}` selects base environment. No environment catalog CRUD is defined in native HTTP/OECP. | [environment type/validation][environment], [host preparation][hosting-environment]; `schema.json`. |
| Secrets and resolution | `DeclaredConnections`: connection key→declared field set; `RunConnectionValues`: connection key→static values; `TargetConnectionResolver` holds callback endpoint/bearer. `RunConnectionRequirements` are secret-free requirements used in status/recovery. HTTP target requests and resume carry fresh secret values separately from immutable submission. | [runtime connections][runtime-bindings], [target connection types][target-types], [run wire][run-wire]. |
| Native observation | `RunStatus`, `RunStatusResult`, `RunForceResult`, `WorkspaceRecovery`, `ActiveExecution`, `TokenUsage`, `RunMetadata`, timestamp/cursor/log/attach types. Native status phase: `admitted`, `running {activeExecutions}`, `stopping {activeExecutions}`, `finished {terminalResult,metadata}`. | [observation][observation]; `schema.json`, `native-v2-observation.schema.json`. |
| Terminal outcome | `TerminalResult.status`: `succeeded {output: JSON}` or `failed {reason: EnumLabel}`. Failure reason is a validated label, not a closed list of engine failures. Optional token usage has input/output/cache counts and `complete`; output schema belongs to the submitted graph. | [terminal type][protocol-lib], [metadata][observation]. |
| Recovery/checkpoints | `WorkspaceRecovery {recoverable,connectionRequirements?,resumedFrom?,successorRunId?}`; `RunResumeFrom`; `CheckpointId`; `RunCheckpoint {checkpointId,sequence,node,mapIndices,loopIterations,createdAt}`; pagination and successor/discard envelopes. No storage paths, workspace bytes or execution seeds are public. | [observation recovery][observation], [checkpoint types][checkpoints], [run wire][run-wire]; `schema.json`. |
| Graph | `GraphSpec {profile,initialInput,policy,root}`; graph profiles `openengine.graph.full/v1`, `openengine.graph.single-worker/v1`; recursive `GraphNode.kind`: `step`, `verifier`, `seq`, `choice`, `par`, `loop`, `map`, `succeed`, `fail`; selectors, input/write bindings, guards, joins, instructions, fail reasons. | [graph][graph-types], [profiles][graph-profiles], [instructions][instructions]; `graph.schema.json` and schema closure. |
| Payload and diagnostics | `PayloadType.kind`: `null`, `boolean`, `integer`, `number`, `string`, `enum`, `record`, `array`; field names/paths, enum sets, required fields, collection bounds; `GraphDiagnostic`, `StructuralBounds`; typed validation is more than accepting arbitrary JSON. Arbitrary JSON remains intentional for initial input, terminal output, error details. | [payload][payload-types], [diagnostics][diagnostic-types]; graph/main schemas. |
| Worker/artifact/fault contracts | `WorkerDescriptor`, `WorkerProtocolBinding`, `WorkerContract`, verifier contracts, capability/credential/artifact policies; `WorkerOutcome`; runtime error codes; `ArtifactRef` and redaction/type/media/digest IDs; public projected `BackendFault`. These appear in public graph/events/components but define **no additional HTTP/OECP request methods**. External worker protocol/version/profile values are bounded opaque strings; no external worker API catalog follows from them. | [worker][worker-types], [outcomes][worker-outcomes], [artifact][artifact-types], [fault][fault-types]; `worker.schema.json`, main/graph schemas. |
| Compiled graph IR | Canonical compiled graph and related instructions/identities available as a generated artifact; not an independently routed public compilation or download operation. | [compiled graph schema][compiled-schema], [artifact builder][artifact-builder]. |

Submission identity hashes the typed serialization of immutable `RunSubmission`; outer proposed run ID and secret values are not in that digest. Before ordinary contained-target admission, provider connection defaults can normalize runtime requirements. Consequently arbitrary retained HTTP bytes and the native immutable digest are different identities; reproducing a hash over caller bytes does not prove native deduplication identity. Exact retry can return the existing run; an absent response does not prove nonacceptance. This is source behavior, not a selected client retry policy. [Native digest][cloud-errors], [contained submission preparation][hosting], [ledger conflict checks][ledger-submit].

## Wire conventions, errors, and ownership

### Envelopes, correlation, and extensibility

The server accepts one JSON object per WebSocket text message. JSON-RPC batches are rejected; binary WebSocket input closes with code 1003. Requests require `jsonrpc: "2.0"`, string `method`, non-null string or signed-integer `id`, and object parameters for known unary methods. Missing/invalid request envelopes differ from invalid method parameters. The dispatcher recognizes methods without requiring a prior `initialize` call. [Envelope decoder][connection-core], [frame classifier][frame-classifier], [dispatcher][dispatch], [WebSocket binding][ws-server].

Request IDs correlate responses and are distinct from run IDs, execution references and subscription IDs. Duplicate in-flight request IDs receive `INVALID_REQUEST` with `DUPLICATE_REQUEST_ID`. Subscriptions must be registered when the establishment result is received, before immediately following events are lost; the Rust multiplexer installs queues from the response before returning it to the waiting caller. The Rust unary client checks response `jsonrpc` and exact ID. That does not by itself verify application identity/source against caller expectations. [Admission limits][connection-admission], [client demultiplexer][client-pump], [unary client][client].

Do not apply one unknown-field policy to every JSON object. Most parameters, native run documents/results and tagged variants have `deny_unknown_fields`; unknown enum variants fail deserialization. Some older response/envelope types, including `InitializeResult`, `GetResult` and `ClusterStatus`, lack that annotation and accept extra fields. The target discovery `extensions` object ignores unknown extension names; the top-level discovery document and known nested route objects remain strict. Arbitrary JSON payload values/details and opaque strings remain open. Exact omission/null/default semantics belong to each Rust type, including `apply.input` and `resubmit.replacementInput` preserving explicit null versus absent. [Core serde declarations][protocol-lib], [admission option decoding][admission-types], [lifecycle option decoding][lifecycle-types], [target discovery][target-types], [native run wire][run-wire].

Identifiers should not be conflated. `RunId`, `Cursor`, and `SubscriptionId` have generic string representations, while target-created run IDs have boundary-specific canonical UUIDv7 requirements. `CheckpointId` is opaque, bounded and run-scoped; execution references are opaque strings up to 128 UTF-8 bytes. Integer domains differ: request IDs are signed i64; generation/token counts are nonnegative JavaScript-safe integers; checkpoint sequence/timestamps are positive JavaScript-safe integers. Histories also contain string-encoded u64 storage identities. [Core IDs][protocol-lib], [checkpoint types][checkpoints], [attach refs][attach-types], [observation numeric types][observation], [history contract][history-wire].

### Failure layers

| Layer | Observable representation | Consequence for faithful coverage |
| --- | --- | --- |
| HTTP failure | Status plus bounded protocol problem `{code,message,details?}`; malformed/missing problem bodies fail problem decoding; the native HTTP wrapper then uses a status-derived fallback code. | Preserve status, code and safe details. A failed response cannot be reinterpreted as a run result. HTTP-specific mappings are in the HTTP inventory. [Problem type][http-problem], [HTTP decoder][http-error-decoder] |
| JSON-RPC framing | `-32700` parse; `-32600` invalid request; `-32601` method not found; `-32602` invalid params; `-32603` internal error; `-32000` application error. Error body `{code,message,data?:{code,details?}}`, response `id` may be null for uncorrelatable failures. | Numeric RPC code and string domain code carry different information. Internal backend messages/details are suppressed by serializer. [Core constants][protocol-lib], [serializer][rpc-errors] |
| Domain failures | `UNSUPPORTED_PROTOCOL_VERSION`, `SCHEMA_VIOLATION`, `GRAPH_INVALID`, `GENERATION_CONFLICT`, `RUN_CONFLICT`, `IDEMPOTENCY_REUSE`, `INVALID_PHASE`, `CANCELLED`, `NO_RETRYABLE_FRONTIER`, `NOT_FOUND`, `GONE`, `SERVER_BUSY`, `DUPLICATE_REQUEST_ID`, native lower-case strings and `SOURCE_UNAVAILABLE`. | String code is not a closed enum; not every domain code applies to every target/method. [Admission constants][admission-types], [watch constants][watch-types], [task admission][connection-admission], [native mapping][cloud-errors] |
| Subscription close | Generic notification with `done`, `SLOW_CONSUMER`, or `SOURCE_UNAVAILABLE`, optionally last delivered cursor. EOF/disconnection can also occur without that close notification. | Stream completion is distinct from durable run terminal result. `SOURCE_UNAVAILABLE` expressly means incomplete history. [Close types][watch-types], [native stream adapter][cloud-errors] |
| Executed run failure | `RunStatus.finished.terminalResult = {status:"failed",reason}` and optional metadata. | This is successful observation of a failed run, not automatically transport/RPC failure. [Observation][observation], [terminal type][protocol-lib] |

The protocol's generated OpenRPC has no per-method `errors` arrays. Error coverage comes from the real decoder, dispatcher, backend guards/mappers and tests, not schema generation. [OpenRPC][openrpc], [dispatch][dispatch], [target adapter][target-backend].

### Bounds and resource ownership

The WebSocket server bounds incoming message reassembly/text at **1 MiB**, the shared outbound queue at **256** messages and concurrent dispatch/subscription tasks at **256** per connection. These are implementation limits, not a complete application buffering policy: the input frame bound does not promise that an unpaginated `run/list` response fits that size. Oversized input closes 1009; identity expiry is checked before incoming text decoding and closes 4401. Expiry is not a promise of a timer interrupting an otherwise idle ongoing subscription. [WebSocket implementation][ws-server], [admission][connection-admission], [list types][run-wire].

Safe log/assistant-output text is bounded to **16,384 UTF-8 bytes**, targets/execution references to **128 bytes**; older standalone log/attach event types also validate a **65,536-byte encoded** event limit. Native run wrappers reuse the bounded text types but are different outer event types. Durable ledger replay is paged and closes normally only after reaching retained terminal cursor. The Rust WebSocket client offers opt-in subscription backpressure; default queued observation can report local slow-consumer interruption. These details establish acceptance cases, not an obligation to copy native buffer constants into a .NET public API. [Logs][log-types], [attach][attach-types], [native observation][observation], [durable replay][durable-replay], [WebSocket client][ws-client], [client pump][client-pump].

Each subscription belongs to its connection. The connection sends its establishment result before forwarding notifications; cancellation wakes even an idle subscription. Disconnect cancels owned observations and gives outstanding backend work a bounded shutdown interval before aborting tasks. Cancellation does not roll back already committed effects, and disconnect is not `run/force`. The CLI has an additional reconnect loop for watch/logs, resuming after its last written cursor; that behavior is composition above transport and is not a server-side exactly-once delivery guarantee. [Establishment][subscription-core], [connection teardown][connection-dispatch], [cancel request contract][watch-types], [CLI follow loop][cli-follow].

Normal status can be reconstructed from the ledger, but native also exposes an in-memory `runtime_failed` terminal fallback at the last observed durable cursor when persistence fails. It creates no retained terminal event. Retained terminal state takes precedence if later available; unreadable remaining history closes `SOURCE_UNAVAILABLE`. An exhaustive mapping must not erase the difference by naming every status response durable evidence. [Fallback implementation][runtime-observation], [status projection][native-observability], [runtime-failure tests][runtime-failure-tests].

## Schema/dispatch differences and generation limits

The following are observed source facts, not conjectural future compatibility concerns.

| Finding | Evidence and implication |
| --- | --- |
| Method and parameter inventory agrees. | A static comparison found all **22 names in registry order**, all parameter-name sets/required flags, and all result definition references agree between committed OpenRPC and `schema.json`. Registry is also used by dispatch and generator. This establishes coverage metadata, not method support. [Registry][methods], [builder][openrpc-builder], [registry tests][registry-tests] |
| Four recovery parameter references need rebasing. | `run/checkpoints.after` points to `#/$defs/CheckpointId`; `run/resume.from`, `.connections`, `.connectionResolver` point to `#/$defs/RunResumeFrom`, `StaticConnectionValues`, `TargetConnectionResolver`. OpenRPC has no root `$defs`, and these copied parameter schemas carry none. Definitions exist in `schema.json`. Generator copies the property schemas unchanged; the recovery test checks equality with those schemas, not reference rebasing. A generator reading the copied schemas standalone cannot resolve them without the accompanying schema context. [Committed recovery methods][openrpc-recovery], [copying builder][openrpc-builder], [recovery schema test][artifact-tests] |
| `update` requires its object-level rule. | The labels/logLevel/suspended OpenRPC params are individually optional, but the `x-params-schema` extension requires at least one and forbids present null; ifGeneration/idempotencyKey remain required. That extension carries its own `$defs`; treat it as a standalone schema context when extracting it. Dropping the extension loses a runtime rule. [Update builder][openrpc-builder], [custom decoder][lifecycle-types] |
| Four notification names do not describe all event body associations. | OpenRPC's generic `event` entry references the older `EventNotification`, while six distinct event body types exist in the full schema. The extension prose mentions all six; connection dispatch supplies the actual association. [Framing extension][openrpc-builder], [schema roots][artifact-builder], [native subscriptions][native-subscriptions] |
| HTTP is mostly absent. | The main schema's HTTP-related closure contains `TargetConnectionResolver` through resume, but not discovery, sourceful target request, session, hosted route/discovery, hosted profile/connection/merge-plan/history APIs, private HTTP or UI DTOs. They need a separate source manifest. [Schema root builder][artifact-builder], [target types][target-types], [hosted types][hosted-types] |
| Observation-only schema is a subset. | `native-v2-observation.schema.json` has five method pairs (status/watch/logs/attach/force) and three event bodies; it omits submit/list/checkpoints/resume/discard and general subscription close/cancel envelopes. It cannot be the complete client schema. [Observation artifact builder][observation-artifacts] |
| Wire schemas are not dispatch validators. | Envelope `jsonrpc` and `method` are plain strings in generic generated envelope types; actual routing demands 2.0 and registered names. Generic `RunId` permits strings that target submission/recovery rejects unless canonical UUIDv7. The main schema root represents a test collection of envelope samples, not one message union. [Envelope types][protocol-lib], [decoder][connection-core], [target adapter][target-backend], [root builder][artifact-builder] |
| Runtime validation exceeds JSON Schema. | Byte bounds are emitted as JSON Schema character `maxLength`; multi-byte strings can satisfy the schema yet exceed native UTF-8 byte bounds. Encoded-event-size limits, connection-map relationships, semantic graph verification, source formatting and environment restrictions have custom validation. Some map-key schema restrictions are weaker than their Rust newtypes. [Bounded-string schema macro][wire-helpers], [run validators][run-values], [runtime maps][runtime-bindings], [environment][environment] |
| Input aliases are omitted from generated enum. | `RunSize` serializes `small/medium/large` but Rust deserialization also accepts `tiny/standard`; `schema.json` emits only the canonical three. This is a decoder/schema difference at the selected release. [Enum][run-values], [main schema][schema] |
| NDJSON and WebSocket cancellation differ. | OpenRPC documents `$/cancelRequest` generically. WebSocket intercepts it through `cancel_request_id`; NDJSON goes directly through `into_request_kind`, which recognizes subscription cancellation but not unary cancellation. A well-formed no-ID cancel-request notification consequently reaches invalid-request handling there. This difference applies to the selected local-stream bindings. [WebSocket][ws-server], [NDJSON][ndjson-server], [classifier][frame-classifier] |
| History comment and replay constant differ. | History-wire prose says 1 MiB replay, but the selected ledger implements 2 MiB + 1 KiB so imported prerequisite events fit. Raw history also has selectively stringified identities and unmodified nested durable records, outside generated HTTP schemas. [History wire][history-wire], [ledger bounds][ledger-bounds], [history projection][history-projection] |
| Discovery/protocol tokens are not native release evidence. | `zeroshot.native-v2-target/v2` and `openengine.cluster/v1` are interface tokens; initialize contains no native product release/build field. Matching them cannot establish 10.9.0. [Target types][target-types], [initialize type][protocol-lib] |

No claim is made that the Rust artifact-regeneration test was executed. Source inspection identified its byte-for-byte `check_artifacts` mechanism and tests, and static checks below examined committed files. Regeneration equality alone would not repair the reference-context or runtime-validation limitations above. [Artifact checker][artifact-builder], [artifact tests][artifact-tests].

## Source manifest and completeness checks

### Authoritative source manifest

The source revision is the root of this manifest; paths below are relative to it. Follow source links for line-level evidence. A coverage ledger can assign every row/method/route to a client mapping and an explicit per-mode availability/prerequisite result. Merely having an untyped generic HTTP send method does not prove these contracts were covered.

| Manifest group | Files that determine completeness |
| --- | --- |
| OECP names and actual routing | `crates/openengine-cluster-server/src/method_registry.rs`; `dispatch.rs`; `connection.rs`; `connection/dispatch.rs`; `connection/native_v2.rs`; `connection/{logs,agent_attach,subscription,frame,admission}.rs`; `stdio.rs`; `websocket.rs`. [Registry][methods], [dispatch][dispatch], [connection routing][connection-dispatch] |
| Per-host method support | `zeroshot/src/native_v2_target_authority/transport.rs` (`TargetOecpBackend`); `native_v2_cloud/backend.rs`; `native_v2_portable_controller/controller/backend.rs`; default `ClusterBackend` and reusable admission backend. [Target][target-backend], [cloud][cloud-backend], [portable][portable-backend], [defaults][backend-defaults] |
| Existing local OECP bindings | `zeroshot/src/native_v2_portable_controller.rs`; `native_v2_portable_controller/{process,transport}.rs`; `transport/{unix,windows}.rs`; `execution/platform/windows/security.rs`; server `stdio.rs`. [Portable paths][portable-paths], [portable serving][portable-process], [Unix][portable-unix], [Windows][portable-windows], [NDJSON][ndjson-server] |
| Public protocol DTOs | `crates/openengine-cluster-protocol/src/{lib,admission,lifecycle,watch,logs,agent_attach,native_v2_run,native_v2_observation,native_v2_target,native_v2_hosted}.rs`, nested run/environment/checkpoint/profile/runtime, target problem, hosted merge-plan modules; graph/payload/value/worker/artifact/fault modules. [Protocol exports][protocol-lib] |
| HTTP route/auth implementation | `zeroshot/src/native_v2_target_authority/{transport,transport_http,transport_history,operator_diagnostics,private_access}.rs` and target access/discovery types; UI mounts where enabled. [Target router][target-transport], [HTTP parser][http-server], [UI router][ui-router] |
| Hosted contracts consumed by native | `zeroshot/src/native_v2_target/controller_authority/{access,control,connections,profiles,history,hosted_runs}.rs`, `contract/` validators, `hosted_runs/{merge_plans_http,recovery_http}.rs`; `native_v2_hosting/connections.rs` for callback direction. [HTTP authority][http-authority], [hosted types][hosted-types] |
| UI/BFF/history | `zeroshot/src/profile_ui.rs`, `profile_ui/{server,catalog,runs,run_history_transport}.rs` and specialized authoring handlers; `native_v2_observability/history/` types and validators. [UI router][ui-router], [history][history-wire] |
| Generated artifacts and conformance | `crates/openengine-cluster-testkit/src/artifacts.rs`, `artifacts/{openrpc,api_reference}.rs`, `native_v2_observation_artifacts.rs`; generated `protocol/openengine-cluster/v1/`, generated `docs/reference/cluster/api.md`; testkit and server/client tests listed below. [Builder][artifact-builder], [API renderer][api-renderer] |

### Static checks performed

The pinned `protocol/openengine-cluster/v1/` tree has **160 files**. Its six top-level JSON artifacts have the following SHA-256 values, computed over committed bytes. Definition counts are per file, not additive: many definitions recur in multiple artifacts. [Pinned artifact tree][artifact-tree].

| Artifact | `$defs` count | SHA-256 |
| --- | ---: | --- |
| `openrpc.json` | 0 | `00f917d16c782a9e349a41ca39eea0cd86be7f1f302fc1163aa5144bad5a68e2` |
| `schema.json` | 224 | `ba75e3830c805f006720bcaa63d0b5d5632146ae7927d1a0c21e283ddf6374dd` |
| `graph.schema.json` | 33 | `75d9da124e360e0e6237f465f4f8eb33f76d702b67d06a26d16ef7a9b57bc74e` |
| `compiled-ir.schema.json` | 25 | `284b076e04fae83fcbd7baca631ab3b8b2b5ecb300058160ffc677f8495c3c3c` |
| `worker.schema.json` | 19 | `c79043d3783a24740caac62a507833c907dbf99bdeb98f5759999e94a29b4796` |
| `native-v2-observation.schema.json` | 46 | `734fb35dab6fad28754d1d74eebf0778448a34cdd571504ace90b13b5f4f02f1` |

Static comparison resolved native method constants into the registry, compared the ordered result with committed OpenRPC, and compared each method's parameter names/required flags/result type with `schema.json`: **22/22 match**. There are **six** `x-subscription: true` entries and **four** generic notification names; zero method-level `errors` declarations. Schema-reference inspection found the four copied recovery fragments described above. No schema regeneration or live conformance pass was represented as a result of these checks. [Registry][methods], [OpenRPC][openrpc], [main schema][schema].

The finished document was checked for unresolved Markdown reference links, table column consistency, and existence/line bounds of every pinned source-file citation against the exported Git objects. These documentation checks do not establish runtime conformance.

### Acceptance evidence implied by exhaustive coverage

This is a factual test inventory, not a choice of .NET architecture or SDK policy. The native source supplies useful starting cases but does not prove a new client conforms.

1. **Inventory closure:** compare all 22 registry methods, all six event payload associations, all four notification names and every HTTP operation in the tables. Check property presence/null/defaults, all tagged variants, nested documents, unknown-field behavior and structured error preservation. Native registry/artifact tests already assert inventory and generated-byte consistency. [Registry tests][registry-tests], [artifact tests][artifact-tests], [protocol tests][protocol-tests].
2. **Mode-specific support:** demonstrate real DirectTarget HTTP submission and the corresponding OECP refusals, empty `get`, inventory and all nine supported/gated native operations; separately establish hosted discovery/auth/profile/connection/history/merge/recovery mappings with controlled endpoints. Tests must not infer availability from OpenRPC or `logs:true`. [Target tests][target-tests], [hosted client tests][hosted-client-tests], [target adapter][target-backend].
3. **Wire/correlation/lifetime:** out-of-order responses, immediate subscription events, duplicate IDs, multiple subscriptions, mismatched IDs, unknown/closed subscriptions, cancellation while idle, disposal, EOF without close, backpressure/overflow, malformed/oversized frames and grant expiry. Cancellation after acceptance must not claim rollback. [Client transport tests][client-tests], [server transport tests][server-tests], [native subscription tests][native-tests].
4. **Durable observation and recovery:** cursor replay boundaries, filtered logs, non-live attach refusal, source unavailable, runtime-failure fallback, terminal cursor drain, paginated checkpoints, wrong checkpoint/run identity, unsupported capability, duplicate successor admission, fresh credentials and discard. Do not substitute legacy retry/resubmit/delete for native operations. [Observation tests][observation-tests], [recovery tests][recovery-tests], [checkpoint tests][checkpoint-tests], [runtime failure tests][runtime-failure-tests].
5. **Serialization/identity:** preserve explicit null versus omission and caller payloads, enforce runtime byte/number bounds, canonical UUIDv7 at target boundaries, and account for contained provider normalization before native deduplication. Record expected native rejection behavior as well as successful responses. [Wire tests][protocol-tests], [HTTP contract tests][target-tests], [hosting tests][hosting-tests].
6. **Compatibility and generated context:** pin native conformance fixtures to 10.9.0 with external release evidence; do not treat protocol token equality as product compatibility or add the SDK build-assertion gate to the independent lower client. Exercise the four OpenRPC reference-context gaps, alias differences and all non-generated HTTP types. [Release][release], [artifact checker][artifact-builder].

## Selected scope and unsupported access paths

The user's selected coverage now includes **all inventoried native HTTP/OECP roles**: public native mounts, the shared OECP method surface, native-consumed hosted contracts, dashboard APIs, private operator/controller HTTP and host-integration callback contracts. The existence of a method in the client does not grant credentials, make an unavailable capability available, or require implementing its server. This resolves the broad category boundary; the inventory itself introduces no further scope vote.

Several facts must remain explicit in later prototype/handoff work:

- The ten cluster-only methods and OECP `run/submit` have defined contracts but ordinary target refusals. Their inclusion must not promise successful execution against that adapter. [Registry][methods], [target adapter][target-backend].
- Hosted contracts are what the pinned native consumer sends/accepts. The independent hosted-service implementation was not inspected and cannot be silently substituted as an additional authority for native 10.9.0. [Hosted types][hosted-types], [HTTP authority][http-authority].
- Private bootstrap/diagnostic/history and callback bindings require operator/host capabilities; representing their DTOs and issuing authorized calls does not create those capabilities or turn the .NET client into a callback server. Dashboard calls require the documented browser-origin/workspace boundary even when made by a nonbrowser client. [Target router][target-transport], [UI boundary][ui-router], [callback][connection-callback].
- Existing WebSocket, NDJSON, Unix-socket and Windows-pipe access are included client bindings, separate from native controller creation. Connecting to a caller-supplied stream does not introduce a native subprocess dependency. Durable local recovery/terminal reads may require filesystem/controller composition unavailable through a live local OECP endpoint. [Portable serving][portable-process], [portable backend][portable-backend].
- Protocol requirements belong to the lower client. The selected caller-supplied 10.9.0 build assertion gates **SDK run operations**, not the independent lower client. Source-pinned test fixtures still need exact revision evidence; discovery/initialize cannot produce product-version proof they do not contain. [Discovery types][target-types], [initialize][protocol-lib].

Unsupported or non-inventoried-as-remote promises are concrete: no target template/uniform-runtime export RPC, no consumer local-run creation HTTP endpoint, no native `run/wait`, no submission-key lookup operation, no interactive execution input, no general native run delete, no runtime model catalog, no native saved-environment catalog, and no infrastructure-provisioning protocol. CLI target registration/login management, local profiles/connections/templates, native controller launch, self-update, and ACP are separate command/in-process/other-protocol surfaces. UI catalog/authoring can offer related functionality only under that explicit UI surface; it is not evidence of an equivalent native consumer API. These absences are conclusions from the complete request/route sets and native command composition, not promises that an external host cannot add its own APIs. [Method registry][methods], [target router][target-transport], [UI routes][ui-router], [CLI grammar][cli], [local composition][local], [ACP][acp].

The report establishes source coverage and gaps. Runtime support, host availability, authorization and a new .NET client's conformance still require the acceptance evidence listed above; none is established merely by compiling generated types.

[source-root]: https://github.com/the-open-engine/zeroshot/tree/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa
[release]: https://github.com/the-open-engine/zeroshot/releases/tag/v10.9.0
[methods]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/method_registry.rs#L45
[openrpc]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/protocol/openengine-cluster/v1/openrpc.json
[openrpc-recovery]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/protocol/openengine-cluster/v1/openrpc.json#L762
[schema]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/protocol/openengine-cluster/v1/schema.json
[compiled-schema]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/protocol/openengine-cluster/v1/compiled-ir.schema.json
[openrpc-builder]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/src/artifacts/openrpc.rs#L15
[artifact-builder]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/src/artifacts.rs#L59
[artifact-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/tests/artifacts.rs#L119
[artifact-tree]: https://github.com/the-open-engine/zeroshot/tree/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/protocol/openengine-cluster/v1
[observation-artifacts]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/src/native_v2_observation_artifacts.rs#L22
[api-renderer]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/src/artifacts/api_reference.rs
[registry-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/tests/method_registry.rs#L19
[dispatch]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/dispatch.rs#L57
[connection-dispatch]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/connection/dispatch.rs#L79
[connection-core]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/connection.rs#L46
[frame-classifier]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/connection/frame.rs#L151
[connection-admission]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/connection/admission.rs#L13
[subscription-core]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/connection/subscription.rs
[native-subscriptions]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/connection/native_v2.rs#L69
[protocol-lib]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/lib.rs#L49
[rpc-errors]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/wire.rs#L35
[ws-server]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/websocket.rs#L34
[ndjson-server]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/stdio.rs#L21
[ws-client]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-client/src/websocket.rs
[client]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-client/src/lib.rs#L381
[client-pump]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-client/src/ndjson_pump.rs#L36
[identity]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/identity.rs
[backend-defaults]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/lib.rs#L132
[admission-backend]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/admission.rs#L104
[admission-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/admission.rs#L10
[admission-errors]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/admission/errors.rs
[lifecycle-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/lifecycle.rs#L15
[lifecycle-server]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/lifecycle.rs
[watch-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/watch.rs#L35
[watch-server]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/watch.rs
[log-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/logs.rs#L1
[log-server]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/logs.rs
[attach-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/agent_attach.rs#L1
[attach-server]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/agent_attach.rs
[run-wire]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/wire.rs#L73
[run-values]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run.rs#L18
[runtime-bindings]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/runtime.rs
[environment]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/environment.rs
[observation]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_observation.rs#L76
[checkpoints]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/checkpoints.rs#L12
[graph-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/graph.rs#L142
[graph-profiles]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/graph_profile.rs#L10
[instructions]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/graph/instructions.rs
[payload-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/payload.rs#L364
[diagnostic-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/diagnostic.rs
[worker-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/worker.rs#L24
[worker-outcomes]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/worker/outcome.rs
[artifact-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/artifact.rs
[fault-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/fault.rs
[wire-helpers]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/wire.rs#L21
[cli]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs
[cli-follow]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution.rs#L560
[local]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_local.rs
[acp]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/acp.rs
[hosting]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting.rs#L155
[hosting-environment]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/environment.rs
[cloud-backend]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cloud/backend.rs#L3
[cloud-errors]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cloud/backend.rs#L148
[cloud-force]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cloud.rs#L617
[cloud-recovery]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cloud.rs#L390
[ledger-submit]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/v2_run_ledger/sqlite.rs
[target-backend]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L496
[target-transport]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L205
[target-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs
[hosted-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted.rs
[http-problem]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target/problem.rs
[http-server]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs
[http-error-decoder]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/http_error.rs
[http-authority]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs
[ui-router]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs
[history-wire]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs
[connection-callback]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/connections.rs
[portable-backend]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_portable_controller/controller/backend.rs#L16
[portable-process]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_portable_controller/process.rs#L264
[runtime-observation]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/runtime.rs
[native-observability]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability.rs#L88
[durable-replay]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/subscriptions.rs
[runtime-failure-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cloud/tests/runtime_failure.rs
[protocol-tests]: https://github.com/the-open-engine/zeroshot/tree/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/tests
[client-tests]: https://github.com/the-open-engine/zeroshot/tree/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-client/tests
[server-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/tests/websocket.rs
[native-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/tests/native_v2.rs
[target-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/tests.rs
[hosted-client-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/tests/hosted_authority.rs
[observation-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/tests.rs
[recovery-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/tests/hosted_recovery.rs
[checkpoint-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/tests/workspace_checkpoints.rs
[hosting-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/tests.rs

[http-1]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L201-L275
[http-2]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L67-L98
[http-3]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/control.rs#L150-L308
[http-4]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L53-L153
[http-5]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L134-L178
[http-6]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/server.rs#L74-L116
[http-7]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/history.rs#L49-L107
[http-8]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L278-L305
[http-9]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L382-L389
[http-10]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_history.rs#L64-L80
[http-11]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/connections.rs#L21-L42
[http-12]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/connections.rs#L88-L126
[http-13]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/server.rs#L250-L257
[http-14]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L26-L95
[http-15]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L104-L215
[http-16]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L304-L351
[http-17]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L24-L37
[http-18]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L224-L235
[http-19]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L293-L317
[http-20]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L448-L487
[http-21]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L260-L275
[http-22]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L308-L323
[http-23]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L433-L456
[http-24]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L257-L261
[http-25]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L325-L379
[http-26]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L227-L257
[http-27]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L426-L456
[http-28]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L291-L305
[http-29]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L278-L289
[http-30]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L460-L468
[http-31]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_history.rs#L9-L80
[http-32]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_history.rs#L9-L85
[http-33]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L46-L51
[http-34]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L98-L168
[http-35]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L271-L291
[http-36]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L332-L338
[http-37]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L41-L61
[http-38]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L334-L351
[http-39]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/serve.rs#L69-L103
[http-40]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L139-L153
[http-41]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L177-L235
[http-42]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L264-L268
[http-43]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L293-L309
[http-44]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L201-L221
[http-45]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L311-L317
[http-46]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L270-L291
[http-47]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/operator_diagnostics.rs#L7-L8
[http-48]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/operator_diagnostics.rs#L38-L101
[http-49]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/private_access.rs#L11-L52
[http-50]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/private_access.rs#L64-L73
[http-51]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/private_access.rs#L123-L156
[http-52]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L24-L34
[http-53]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L469-L520
[http-54]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L407-L424
[http-55]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L360-L487
[http-56]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L360-L376
[http-57]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L378-L393
[http-58]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/hosted_runs.rs#L189-L244
[http-59]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L395-L411
[http-60]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L413-L430
[http-61]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/profile.rs#L12-L12
[http-62]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/profile.rs#L132-L149
[http-63]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted/merge_plan.rs#L11-L18
[http-64]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted/merge_plan.rs#L167-L181
[http-65]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L440-L446
[http-66]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L399-L414
[http-67]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L432-L438
[http-68]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L62-L119
[http-69]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L348-L460
[http-70]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/hosted_runs.rs#L189-L209
[http-71]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L167-L310
[http-72]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L77-L89
[http-73]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/hosted_runs.rs#L142-L187
[http-74]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/hosted_runs.rs#L212-L284
[http-75]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/connections.rs#L18-L47
[http-76]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/profiles.rs#L17-L35
[http-77]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/hosted_runs/merge_plans.rs#L74-L120
[http-78]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/history.rs#L78-L122
[http-79]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/contract.rs#L54-L109
[http-80]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/oecp.rs#L33-L99
[http-81]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs#L227-L230
[http-82]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/http_error.rs#L9-L31
[http-83]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L118-L159
[http-84]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/merge_plans_http.rs#L20-L41
[http-85]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/connections.rs#L12-L29
[http-86]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L46-L63
[http-87]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L162-L172
[http-88]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L201-L221
[http-89]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L212-L221
[http-90]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L175-L198
[http-91]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L223-L231
[http-92]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L233-L245
[http-93]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L247-L261
[http-94]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted.rs#L99-L105
[http-95]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/recovery_http.rs#L11-L57
[http-96]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/recovery_http.rs#L23-L57
[http-97]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/recovery_http.rs#L34-L57
[http-98]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/connections.rs#L32-L44
[http-99]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/connections.rs#L47-L59
[http-100]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/connections.rs#L62-L74
[http-101]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L66-L94
[http-102]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L96-L103
[http-103]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L105-L112
[http-104]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L114-L121
[http-105]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L123-L130
[http-106]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L132-L139
[http-107]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/merge_plans_http.rs#L44-L81
[http-108]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/merge_plans_http.rs#L59-L65
[http-109]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/merge_plans_http.rs#L67-L81
[http-110]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/history.rs#L109-L166
[http-111]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L12-L21
[http-112]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L6-L12
[http-113]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/history.rs#L59-L107
[http-114]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/connections.rs#L27-L35
[http-115]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/connections.rs#L32-L75
[http-116]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L340-L358
[http-117]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L32-L37
[http-118]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L416-L448
[http-119]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L142-L155
[http-120]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L421-L449
[http-121]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L197-L204
[http-122]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L128-L137
[http-123]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L38-L41
[http-124]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L226-L285
[http-125]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L288-L325
[http-126]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L139-L148
[http-127]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L328-L362
[http-128]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L156-L161
[http-129]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/control.rs#L67-L118
[http-130]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L463-L547
[http-131]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/access.rs#L30-L49
[http-132]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/access.rs#L103-L180
[http-133]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/control.rs#L76-L92
[http-134]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L320-L325
[http-135]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/merge_plans_http.rs#L84-L106
[http-136]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L34-L47
[http-137]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L129-L175
[http-138]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L39-L125
[http-139]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/profile.rs#L14-L96
[http-140]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/profile.rs#L98-L114
[http-141]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted.rs#L18-L30
[http-142]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted.rs#L58-L106
[http-143]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted.rs#L116-L157
[http-144]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/wire.rs#L131-L185
[http-145]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/checkpoints.rs#L41-L110
[http-146]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/recovery_http.rs#L1-L57
[http-147]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted/merge_plan.rs#L20-L54
[http-148]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/merge_plans.rs#L26-L54
[http-149]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted/merge_plan.rs#L107-L165
[http-150]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted/merge_plan.rs#L56-L104
[http-151]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L23-L24
[http-152]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L49-L115
[http-153]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L185-L209
[http-154]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/connections.rs#L45-L79
[http-155]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/connections.rs#L17-L42
[http-156]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/connections.rs#L88-L145
[http-157]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/server.rs#L149-L186
[http-158]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L35-L48
[http-159]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L69-L71
[http-160]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/server.rs#L345-L367
[http-161]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L42-L63
[http-162]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L100-L104
[http-163]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/workspace.rs#L43-L50
[http-164]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L106-L114
[http-165]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L115-L128
[http-166]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L214-L216
[http-167]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L129-L142
[http-168]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L161-L200
[http-169]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L129-L150
[http-170]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/workspace.rs#L67-L79
[http-171]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L151-L155
[http-172]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/outcomes.rs#L20-L49
[http-173]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/workspace.rs#L53-L57
[http-174]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L156-L159
[http-175]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/data.rs#L12-L85
[http-176]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/runs.rs#L622-L635
[http-177]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/runs.rs#L637-L642
[http-178]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/runs.rs#L644-L652
[http-179]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/runs.rs#L654-L699
[http-180]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L83-L90
[http-181]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/server.rs#L27-L29
[http-182]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/server.rs#L74-L84
[http-183]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/data.rs#L26-L85
[http-184]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/server.rs#L250-L290
[http-185]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/server.rs#L293-L342
[http-186]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L31-L31
[http-187]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L206-L212
[http-188]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L234-L270
[http-189]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L129-L223
[http-190]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/control.rs#L28-L45
[http-191]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/status.rs#L24-L29
[http-192]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/contract.rs#L172-L177
[http-193]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L145-L151
[http-194]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/runs.rs#L502-L541
[http-195]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L87-L143
[http-196]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L153-L178
[http-197]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/contract.rs#L30-L49
[http-198]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L180-L187
[http-199]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L189-L204
[http-200]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L206-L223
[http-201]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/contract.rs#L11-L13
[http-202]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/contract.rs#L160-L169
[http-203]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/control.rs#L28-L52
[http-204]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L1-L21
[http-205]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/contract.rs#L20-L177
[http-206]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/wire.rs#L23-L70
[http-207]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history.rs#L232-L276
[http-208]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/runs.rs#L570-L590
[http-209]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui/runs.rs#L755-L760
[http-210]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target/problem.rs#L6-L61
[http-211]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target/problem.rs#L85-L101
[http-212]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L224-L226
[http-213]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L244-L254
[http-214]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L232-L234
[http-215]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L228-L230
[http-216]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L236-L254
[http-217]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport_http.rs#L188-L221
[http-218]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L29-L30
[http-219]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L313-L346
[http-220]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/merge_plans_http.rs#L10-L10
[http-221]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract/http_error.rs#L34-L84
[http-222]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/src/artifacts.rs#L59-L125
[http-223]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/src/artifacts.rs#L157-L181
[http-224]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/src/native_v2_observation_artifacts.rs#L22-L43
[http-225]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-testkit/src/artifacts.rs#L59-L116
[http-226]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/wire.rs#L131-L147
[http-227]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/contract.rs#L128-L161
[http-228]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L54-L83
[http-229]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L119-L125
[http-230]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_hosted/merge_plan.rs#L154-L181
[http-231]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L104-L153
[http-232]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L196-L215
[http-233]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser/profiles.rs#L28-L35
[http-234]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L28-L33
[http-235]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L97-L102
[http-236]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/merge_plans.rs#L24-L62
[http-237]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/merge_plans.rs#L248-L272
[http-238]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/connections.rs#L13-L25
[http-239]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L47-L59
[http-240]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs.rs#L119-L127
[http-241]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/hosted_runs/merge_plans_http.rs#L20-L37
[http-242]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L360-L393
[http-243]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L360-L375
[http-244]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/profile_ui.rs#L75-L78
[portable-paths]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_portable_controller.rs#L41-L97
[portable-unix]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_portable_controller/transport/unix.rs#L5-L23
[portable-windows]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_portable_controller/transport/windows.rs#L17-L49
[windows-security]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/execution/platform/windows/security.rs#L1-L35
[axum-lock]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/Cargo.lock#L262-L265
[axum-get]: https://docs.rs/axum/0.8.9/axum/routing/method_routing/fn.get.html
[ledger-bounds]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/v2_run_ledger.rs#L33-L42
[ledger-events]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/v2_run_ledger.rs#L193-L276
[history-projection]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history.rs#L170-L230
[native-event-values]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_contract.rs#L71-L239
[durable-history-types]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/full_v1_reducer/history.rs#L51-L91
[history-control]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_observability/history/control.rs#L40-L75
[cloud-secrets]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cloud.rs#L116-L119
[provider-diagnostics]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_capsule/provider_process/diagnostic.rs#L10-L21
