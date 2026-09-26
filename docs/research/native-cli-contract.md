# Native CLI contracts for the first .NET SDK

Research for [Establish the native CLI contracts for the first .NET SDK](https://github.com/faviann/zeroshot-dotnet-sdk/issues/2), completed 2026-09-26. The destination is a plan for a reusable .NET SDK, a minimal .NET CLI, and Broodling adoption. This report establishes facts and options; it does not choose the transport, API, distribution, or additional workflows.

## Evidence boundary

The detailed inventory starts from **native 10.3.0**, commit `054ad3fd6c763b98d12f5b2e90830b97116561ad`, published September 16. Both native tag `v10.3.0` and Python tag `zeroshot-python-v10.3.0_1` resolve to that commit; the latter uses package version `10.3.0.post1`. Live GitHub API verification on September 26 identifies **10.9.0** as the latest published native release, published at `2026-09-26T05:00:25Z`, with tag/commit `75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa`; it is neither draft nor prerelease. Its Python revision is separately published as `10.9.0.post1`. The supplied checkout is later still: `d9ddeae967f4d5aa247248549fe956ee615bbde6`. The user prefers latest unless its design impact warrants retaining 10.3.0; this report supplies that comparison without deciding the conditional policy. [Latest native release][latest-release], [latest Python release][latest-python-release]. Release identity was checked against first-party GitHub tag/release APIs; checked-in development version placeholders are not release evidence. [Native release][release], [Python release][python-release], [version convention][agents].

Unless marked **newer**, findings below refer to the baseline. Investigation used an isolated local source clone, source and test inspection, and pinned revision diffs. No native build, provider execution, or live DirectTarget conformance run was performed. Native Rust contracts and CLI behavior are primary; Python is secondary evidence only.

## Findings that shape the decisions

Two usable boundaries already exist without Python: the native executable's JSON/NDJSON commands and the direct target's HTTP/OECP contracts. Choosing CLI concepts as the public design reference does not choose between these transports. The native CLI itself uses the direct contracts for named direct targets. [CLI grammar][cli], [HTTP client][http-control], [CLI OECP adapter][cli-oecp].

Three distinctions deserve explicit names in the eventual contract:

- Reconnecting to a **run** uses status/watch/logs. Native `attach` observes one live **execution**, has no history cursor, and accepts no client input.
- A successful **submission receipt** identifies an accepted run. A **terminal result** says whether it succeeded or failed.
- **Stopping observation** leaves a run active. Native force-stop records stop intent; its immediate response need not be terminal.

These are native distinctions, not proposed .NET method names. [Observation types][observation], [execution behavior][execution].

## Native capability groups and first-consumer operations

The baseline CLI groups target registration/login/serving, connections, profiles, templates, hosted merge plans, and run lifecycle commands. Inventory alone does not make all groups part of the first SDK or minimal CLI. [CLI command tree][cli].

| Need | Native executable | DirectTarget boundary and limits |
| --- | --- | --- |
| Prepare a run | `template list/show`, profiles, exact graph/runtime files, uniform runtime expansion, `run --validate-only` | HTTP creation accepts a complete `RunSubmission`; the base direct discovery does not provide a template catalog or uniform-runtime expansion service. Native target admission still validates the submitted graph/runtime. |
| Submit and keep a handle | `run --detach --target NAME ...` | HTTP `POST` to discovered `runPath`, normally `/native-v2/run`; receipt contains `runId`. Although OpenRPC lists `run/submit`, the target OECP adapter explicitly rejects it. |
| Read progress | `status RUN_ID --target NAME`; optionally `list` | OECP `run/status` gives current state; `run/list` gives all retained run projections and has no filters or pagination. |
| Reconnect and follow | `watch RUN_ID --after CURSOR`; `logs RUN_ID --after CURSOR [--execution REF]` | OECP `run/watch` and `run/logs`, resuming exclusively after opaque cursors. Logs and watch are different projections of run history. |
| Wait for completion | Foreground `run` follows watch; `watch` can follow an existing run | No standalone native `wait` command or `run/wait` method. A client can compose status and watch/polling; a terminal result, rather than stream EOF, establishes the outcome. |
| Inspect one live execution | `attach RUN_ID EXECUTION_REF` | OECP `run/attach` is live and read-only, without replay or an input channel. It is unnecessary for reconnecting ordinary run observation. |
| Explicit stop | `force-stop RUN_ID --target NAME` | OECP `run/force` returns the status after recording force intent. Repeated force is idempotent; awaiting final settlement is separate. |

Sources for the table: [CLI grammar][cli], [preparation][submission], [run wire types][run-wire], [target creation rejection over OECP][target-oecp], [observation types][observation], [CLI follow loop][execution].

## What each boundary owns

| Concern | Native executable boundary | Direct HTTP/OECP boundary |
| --- | --- | --- |
| Prerequisites | A compatible native executable, command invocation and stream/process handling; named target registry configuration. Named-target **run submission** also invokes Git in a worktree. | HTTP and WebSocket client facilities plus target origin; no native executable or local Git worktree is required by the request contract itself. Caller must supply the request values. |
| Graph/runtime preparation | Rust expands templates/uniform runtimes, resolves profiles, validates graph/input/runtime, and selects declared connection values. | Caller supplies exact graph/runtime documents or deliberately implements another supported capability. The target performs admission validation. The base contract does not move CLI template expansion into the server. |
| Source | Submission resolves repository/branch from the invoking worktree/upstream, with explicit overrides; a missing revision resolves the remote branch tip. Dirty work is reported, not uploaded. | `source` contains repository, branch, and exact revision. It is part of the immutable submission. |
| Routing/authentication | `target add NAME --url ORIGIN --direct`, then `--target NAME`; hosted login is a separate mode. | Discovery declares `authentication: none` for direct mode. Native direct HTTP/WebSocket calls use no OAuth credential store or Authorization header. Source/delivery/provider credentials are separate request concerns. |
| Results/errors | JSON records on stdout; native structured diagnostics on stderr when `ZEROSHOT_ERROR_FORMAT=json`. Process outcome and run outcome must be interpreted separately. | HTTP receipt/problems plus JSON-RPC responses and subscription notifications. Client owns correlation, decoding, resource limits, reconnect policy, and application-facing error mapping. |

Sources: [source resolution][source], [preparation][submission], [profile resolution][profiles], [target request][target-types], [direct authentication tests][direct-tests], [CLI diagnostics][diagnostic], [process entry point][main].

The worktree constraint is operation-specific. Baseline named-target `run` calls `inspect_worktree` even when repository, branch, and revision are supplied. Inline `--validate-only` skips source resolution; it reports `{"valid":true}`, not a complete materialized submission envelope. Status/watch/logs/force-stop route by configured target and run ID and do not invoke submission's worktree resolution. Therefore an executable adapter may have a submission prerequisite that its reconnect path does not share. [Named source resolver][source], [preflight][submission], [operation routing][execution], [named-target adapter][cli-oecp].

The executable's output needs operation-aware parsing. In named-target detached submission, it writes a source/dirtiness record and then the receipt, so treating the whole stdout as one JSON value is incorrect. A foreground run then emits watch records. Baseline `status`/`watch` may return a failed run projection without the process itself failing; foreground `run` explicitly converts a failed terminal watch outcome into an error. Preserve the typed terminal result. [Submission output][submission], [execution/outcomes][execution], [process command handling][main], [outcome tests][outcome-tests].

### Direct connection sequence

1. Read `/.well-known/zeroshot-native-v2`; validate kind `zeroshot.native-v2-target/v2`, access mode, audience, and same-origin route paths.
2. Create through the discovered HTTP run route. The envelope is `{runId, submission, connections, connectionResolver?, githubToken?}`. HTTP creation requires a canonical UUIDv7 `runId`; the receipt gives the identity to use thereafter.
3. Request an OECP session through `sessionPath`, normally `/native-v2/oecp-session`, with the run ID for run observation. Connect to the returned WebSocket endpoint. Direct sessions return no bearer token; this is not per-run authorization isolation.
4. Use JSON-RPC `run/status`, `run/watch`, `run/logs`, or `run/force`. Subscriptions establish an ID through a response, then use generic `event`, `subscription/closed`, and `subscription/cancel` framing.

Sources: [HTTP/discovery types][target-types], [server HTTP boundary][target-server], [discovery validation][http-contract], [native WebSocket dialer][dialer], [generated protocol API][api].

## Guarantees, limitations, and inference

**Submission identity and deduplication.** Native computes SHA-256 over its typed serialization of the entire `RunSubmission`: title, graph, initial input, runtime, exact source repository/branch/revision, and submission key. The outer proposed run ID, connection secret values, connection resolver, and GitHub token are excluded. Within the target ledger namespace, the same key and digest return the existing run ID; changed immutable content conflicts. A changed proposed run ID on an exact retry does not establish a new run. Reusing a run ID for an unrelated key also conflicts. This is native serialization identity, not a general claim that all semantically equivalent graphs hash alike. [Digest][digest], [retry preflight][hosting], [lookup][cloud], [ledger conflict logic][ledger-sqlite], [retry/revision tests][retry-tests].

An exact retry is resolved before replacement secret processing, and does not update credentials for the existing run. A same-key retry that freshly resolves a moved remote branch is no longer the same immutable request. **Implication:** any retry/reconciliation design needs the original resolved request; a durable intent key alone is insufficient. Native has an internal key lookup, but the examined public run methods expose no lookup-by-submission-key operation. These are constraints on a future design, not a choice of persistence or retry policy. [Hosting retry path][hosting], [secret retry test][secret-tests], [source resolution][source], [method registry][methods].

**Acknowledgement ambiguity.** The target may durably create a run before a submitter loses its response or aborts during allocation. Native tests demonstrate that exact retry finds the same run and can reconcile abandoned allocation to a failed result rather than launching a second attempt. Consequently, transport failure is not proof that submission had no effect. This is an inference from native commit order and the aborted-allocation test; no live dropped-HTTP-response experiment was performed. [Creation path][cloud], [aborted allocation test][retry-tests].

**Identity binding.** Status and watch carry public run ID and immutable title/source/size, allowing a consumer to check them against its stored binding. Receipt identity is authoritative for subsequent calls, including deduplicated submissions. JSON-RPC request IDs and subscription IDs are separate transport correlations. The native client checks those correlations, but the inspected generic result/stream decoder does not comprehensively compare returned run/source fields against caller expectations. Do not infer that transport correlation proves consumer-level identity. The exact .NET mismatch behavior remains a decision. [Observation types][observation], [unary client][client], [subscription decoder][subscription-client], [retry lookup][cloud].

**Status and bounded progress.** Direct phases are `admitted`, `running`, `stopping`, and `finished`. Running/stopping list all active opaque execution references with node names; no percentage or single current-worker field exists. Terminal results are `succeeded {output: JSON}` or `failed {reason}`; successful output is graph-owned, so a pull-request receipt schema is an additional graph/consumer contract. Optional token usage is terminal metadata, not continuously streamed progress. Retained list count is unpaginated. [Observation types][observation], [terminal type][terminal], [run list][run-wire], [projection][projection].

**Replay and reconnect.** Watch/log cursors are opaque and exclusive; preserve them without constructing or ordering them in the SDK. Baseline watch/log replay tests prove no duplicate boundary record, retained execution state after watcher disposal, and replay through terminal history. Native replay reads at most 256 events / 1 MiB per ledger page, while log records are limited to 16 KiB. These are server implementation bounds, not a ready-made .NET buffer policy or history-retention SLA. The CLI reopens durable streams after disconnect, EOF, or slow-consumer close, preserving its last written cursor; permanent errors surface. An initial open failure is not silently retried. [Replay contract][observation], [replay tests][watch-tests], [limits][ledger], [bounded replay tests][replay-tests], [follow loop][execution].

**Observer interruption versus force.** Dropping observation cannot stop execution. Force is the only baseline run stop mode, repeated force is idempotent, and an immediate `stopping` result is valid. Cleanup precedes normal terminal `force_stopped` settlement; a cleanup/persistence failure must not be converted into an invented success. Native `attach` can reopen a live execution after disconnection, but offers no missing-output recovery; use logs for history. [Observer implementation][subscriptions], [force types][observation], [force/cleanup test][retry-tests], [force ledger test][ledger-tests], [attach tests][attach-tests].

**Terminal evidence caveat.** Normal terminal results come from durable run state, but baseline status also has an in-memory `runtime_failed` fallback when persistence fails. It uses the last observed durable cursor and creates no history event; later durable terminal state takes precedence. The public status shape has no explicit durability flag. `SOURCE_UNAVAILABLE` means incomplete observation, not completion, and an ended watch without a terminal result is insufficient evidence of success. This matters to any promised “durable terminal result” abstraction. [Fallback test][status-tests], [runtime observation][runtime-observation], [incomplete-stream handling][execution], [backend error mapping][digest].

**Malformed responses and version checks.** Baseline types are strict about required/unknown fields and phase-specific shapes; HTTP failures have bounded `{code,message,details?}` values. Native discovery validates its kind/access/audience and route authority; JSON-RPC validates `2.0`, response ID, and subscription framing. `initialize` offers `openengine.cluster/v1` and capability negotiation, and the Rust client rejects an incompatible returned protocol version. The CLI's direct connector does not itself call `initialize` before its ordinary run operations. Neither the discovery kind nor the protocol token identifies product release 10.3.0 or negotiates every run-field addition. An SDK compatibility policy must go beyond matching these strings. [HTTP types][target-types], [HTTP validation][http-contract], [problem handling][http-errors], [client][client], [subscription client][subscription-client], [native connector][connector].

## Latest published release versus 10.3.0

The following changes are **present in published 10.9.0**, not merely unreleased main. They must not be attributed to 10.3.0. The core CLI, native target, protocol, and Python files inspected here have no diff between the 10.9.0 tag and the supplied current checkout. [Baseline-to-latest comparison][latest-compare], [latest-to-checkout comparison][post-release-compare].

| Change | Consequence for this map |
| --- | --- |
| Submission interruption fix, commit `9e083753db2de766393448b5a416d94d2be1e4c4` | 10.9.0 CLI cancels before the backend submit attempt begins, but preserves an already-started submit future through receipt/error before detaching. Baseline only installs the observation detach wait after submission. The stronger behavior must not be assumed for a 10.3.0 executable adapter. [Fix][interrupt-fix] |
| Workspace recovery (`5205e147…`) and checkpoints (`22f9d0c0…`) | 10.9.0 adds resume/checkpoints/discard-workspace, discovery extensions, and optional `workspaceRecovery` fields on status/force results. Baseline has none of these. This also supplies a concrete forward-compatibility case for strict decoders. [Recovery change][recovery-change], [checkpoint change][checkpoint-change], [10.9.0 status shape][latest-observation] |
| Run environments (`3e6b8495…`) | 10.9.0 `RunSubmission` can carry setup/startup/public-variable configuration. Baseline has no such field. This is different from the existing secret connection envelope. [Environment change][environment-change] |
| Delivery (`b450f400…`) | 10.9.0 adds push and pull-request-feedback controls. Its default PR template uses `builtin.git-delivery.pr@2`, with a `v2` / `ready` success receipt; the older `pr@1` remains an explicit supported worker with `v1` / `opened`. This can change first-consumer receipt acceptance. [Delivery change][delivery-change] |
| Local Windows execution (`44f7a5d7…`) | Baseline local controllers are explicitly unavailable off Unix. Newer local Windows support should not be projected backwards; named-target execution and package availability are separate questions. [Baseline platform handling][main], [Windows change][windows-change] |

**Bounded impact assessment (inference):** 10.9.0 retains the same basic DirectTarget seam: HTTP creation plus OECP status/watch/logs/force. The new fields/capabilities do not alone require a different SDK transport architecture. Adopting latest still requires its own request/status fixtures, optional run-environment semantics, compatibility behavior for recovery fields, and the published interruption behavior. There is a material consumer-facing graph choice: accept the newer PR readiness contract, or deliberately select the supported older PR worker and preserve its receipt contract. This report does not choose either. [10.9.0 HTTP contract][latest-target], [10.9.0 method registry][latest-methods], [10.9.0 request types][latest-run-wire], [delivery modes][latest-delivery], [template selection][latest-templates], [receipt tests][latest-delivery-tests].

Other published groups include UI, ACP, self-update, native-authentication handling, and more detailed target transport failures. They need not expand the first SDK scope merely because they exist. The supplied checkout adds only two commits after 10.9.0: release sidecar permission restoration and base-branch merge-policy handling. Neither changes the inspected SDK wire/CLI surface, though merge-delivery behavior is not therefore identical. Do not treat those two fixes as shipped in 10.9.0. [Latest-to-checkout comparison][post-release-compare].

## Reusable contract evidence

These sources can seed focused .NET contract tests after a transport/API choice; the Rust tests are inspected evidence, not tests executed by this investigation.

| Evidence | Practical reuse |
| --- | --- |
| [OpenRPC][openrpc], [schema][schema], [observation fixtures][fixtures], [observation shape tests][shape-tests] | Exact method/field names, phases, cursor/ID presence, numeric limits, log timestamps, and rejection of malformed variants. HTTP creation is a separate contract; use its Rust type and [request fixture][http-fixture], not `run/submit` merely because OpenRPC lists it. |
| [Direct authority integration tests][direct-tests] and [HTTP problem tests][problem-tests] | Discovery → submit → session sequence, absent direct authentication, preserved source/envelope fields, bounded structured failure mapping. |
| [Retry/allocation tests][retry-tests], [secret-retry tests][secret-tests], [SQLite ledger tests][ledger-tests] | Exact duplicates, changed revision/content conflict, secret exclusion, receipt ambiguity, persistence after reopen, and idempotent force. |
| [Watch tests][watch-tests], [bounded replay tests][replay-tests], [status/fallback tests][status-tests] | Exclusive replay, parallel active executions, terminal output and metadata, history incompleteness, bounded batches. |
| [CLI outcome tests][outcome-tests], [CLI lifecycle tests][cli-tests], [attach tests][attach-tests] | If using the executable: source record + receipt parsing, cursor reopen/detach, errors versus terminal failures, live attach limitations. |

As secondary evidence, Python's `wait` composes status → watch after the status cursor → status reread if the stream ends, and its `force_stop` additionally waits for settlement. Those are Python conveniences, not a native `wait` endpoint or a requirement to adopt Python packaging/process mechanics. [Python wait implementation][python-wait].

## Questions left for the human decision tickets

1. Which boundary supplies the first consumer: direct HTTP/OECP, native executable commands, or an explicitly justified split? Decide graph/runtime preparation ownership along with this choice; otherwise the apparent transport choice hides a second implementation scope.
2. What should submission cancellation/timeout return before acceptance is known, and what exact request/binding must a caller persist for reconciliation? Native deduplication supplies a mechanism, not the caller's persistence policy.
3. Does stop mean “intent recorded” or “waited for terminal settlement,” and how should observation timeout, source unavailability, identity mismatch, and in-memory runtime-failure observations appear to callers?
4. Does the conditional preference for latest select 10.9.0 given the PR receipt/behavior change, or does this effort retain 10.3.0? Then define exact-release versus range support and unknown-field/capability behavior. The optional recovery field is a concrete decoding case; default PR `v2` / `ready` is a concrete adoption case.
5. Which structured successful output does the first supported graph promise to Broodling, and which receipt checks remain consumer policy? Generic terminal success alone does not prove the requested delivery outcome.

Additional verification belongs with implementation: live protocol conformance for the selected target build, deployed retention/restart assumptions, dropped-response behavior across an actual HTTP boundary, maximum accepted response sizes/backpressure, and selected operating-system/package combinations. The native code proves useful building blocks, but this investigation does not establish those deployment guarantees.

[release]: https://github.com/the-open-engine/zeroshot/releases/tag/v10.3.0
[python-release]: https://github.com/the-open-engine/zeroshot/releases/tag/zeroshot-python-v10.3.0_1
[agents]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/AGENTS.md#L25
[cli]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/parser.rs#L6
[http-control]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target/controller_authority/control.rs#L57
[cli-oecp]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/oecp.rs#L218
[observation]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-protocol/src/native_v2_observation.rs#L136
[execution]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/execution.rs#L135
[submission]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/execution/submission.rs#L30
[run-wire]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-protocol/src/native_v2_run/wire.rs#L72
[target-oecp]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target_authority/transport.rs#L402
[source]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/execution/named_source.rs#L29
[profiles]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/execution/submission/profiles.rs#L18
[preparation]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/execution/submission.rs#L119
[target-types]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-protocol/src/native_v2_target.rs#L1
[direct-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target/tests/integration.rs#L194
[diagnostic]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/diagnostic.rs#L11
[main]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/main.rs#L88
[outcome-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/tests/outcome.rs#L19
[target-server]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target_authority/transport.rs#L229
[http-contract]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target/controller_authority/contract.rs#L55
[dialer]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target/oecp.rs#L24
[api]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/docs/reference/cluster/api.md#L19
[digest]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cloud/backend.rs#L162
[hosting]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_hosting.rs#L139
[cloud]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cloud.rs#L200
[ledger-sqlite]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/v2_run_ledger/sqlite.rs#L338
[retry-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cloud/tests/cases_3.rs#L104
[secret-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cloud/tests/cases_2.rs#L51
[methods]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-server/src/method_registry.rs#L45
[client]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-client/src/lib.rs#L275
[subscription-client]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-client/src/native_v2.rs#L154
[connector]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target.rs#L378
[terminal]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-protocol/src/lib.rs#L232
[projection]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_observability/projection.rs#L16
[watch-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_observability/tests/watch.rs#L62
[ledger]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/v2_run_ledger.rs#L32
[replay-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_observability/tests/replay.rs#L32
[subscriptions]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_observability/subscriptions.rs#L66
[ledger-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/v2_run_ledger/tests.rs#L156
[attach-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/tests/attach.rs#L43
[status-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_observability/tests/status.rs#L95
[runtime-observation]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_observability/runtime.rs#L1
[http-errors]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target/controller_authority/contract/http_error.rs#L10
[interrupt-fix]: https://github.com/the-open-engine/zeroshot/commit/9e083753db2de766393448b5a416d94d2be1e4c4
[recovery-change]: https://github.com/the-open-engine/zeroshot/commit/5205e14738a6d180dabee7bb13229d7a7383788b
[checkpoint-change]: https://github.com/the-open-engine/zeroshot/commit/22f9d0c0fad95cc065560569d2a59e4c9e981abc
[latest-observation]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_observation.rs#L173
[environment-change]: https://github.com/the-open-engine/zeroshot/commit/3e6b8495e62a511e0eaaff64fa1a4ea9808b4252
[delivery-change]: https://github.com/the-open-engine/zeroshot/commit/b450f4004b5f21e2ea2a092b14fc8db12211aa56
[windows-change]: https://github.com/the-open-engine/zeroshot/commit/44f7a5d7f9ca84a3f5b1d93269db282cca0f24ab
[compare]: https://github.com/the-open-engine/zeroshot/compare/054ad3fd6c763b98d12f5b2e90830b97116561ad...d9ddeae967f4d5aa247248549fe956ee615bbde6
[openrpc]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/protocol/openengine-cluster/v1/openrpc.json#L1
[schema]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/protocol/openengine-cluster/v1/schema.json#L1
[fixtures]: https://github.com/the-open-engine/zeroshot/tree/054ad3fd6c763b98d12f5b2e90830b97116561ad/protocol/openengine-cluster/v1/fixtures/native_v2_observation
[shape-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-protocol/tests/native_v2_observation.rs#L1
[http-fixture]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/crates/openengine-cluster-protocol/tests/fixtures/native-v2-target-request.json#L1
[problem-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_target/tests/hosted_authority/problem_errors.rs#L1
[cli-tests]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/zeroshot/src/native_v2_cli/tests.rs#L270
[python-wait]: https://github.com/the-open-engine/zeroshot/blob/054ad3fd6c763b98d12f5b2e90830b97116561ad/sdks/python/src/zeroshot/client.py#L702

[latest-release]: https://github.com/the-open-engine/zeroshot/releases/tag/v10.9.0
[latest-python-release]: https://github.com/the-open-engine/zeroshot/releases/tag/zeroshot-python-v10.9.0_1
[latest-compare]: https://github.com/the-open-engine/zeroshot/compare/054ad3fd6c763b98d12f5b2e90830b97116561ad...75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa
[post-release-compare]: https://github.com/the-open-engine/zeroshot/compare/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa...d9ddeae967f4d5aa247248549fe956ee615bbde6
[latest-target]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L1
[latest-methods]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-server/src/method_registry.rs#L45
[latest-run-wire]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_run/wire.rs#L72
[latest-delivery]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_delivery.rs#L93
[latest-templates]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_templates.rs#L328
[latest-delivery-tests]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_delivery/tests.rs#L229
