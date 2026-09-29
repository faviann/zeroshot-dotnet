# Stock native Linux x64 witness

Run `tools/native-witness/run.sh` from any directory on Linux x64 with .NET 10,
Bash, Python 3, curl, tar, sha256sum, shuf and ripgrep. The observation phase also
requires root or passwordless `sudo`, because stock native owns setup hooks and
isolated runtime identities as root. The attachment phase additionally needs Git,
`mount`, `unshare`, `/usr/lib/git-core/git` and permission to create a private mount
namespace. It downloads the pinned official stock
release, verifies the archive and extracted executable, and launches a loopback
native target with fresh state and asset directories and a
scrubbed environment. Test-owned no-worker graphs cover admission and durable
observation; one controlled worker covers live attachment. No user assets or
credentials are used. Process management and raw HTTP admission belong to this
developer witness.

The harness writes every artifact to a fresh `/tmp/zeroshot-native-witness.*`
directory and prints its path on success or failure. It terminates the native
process on exit and retains artifacts for inspection. The port is chosen randomly;
`ZEROSHOT_WITNESS_PORT` can select an available unprivileged port. A bind collision
fails explicitly. `ZEROSHOT_WITNESS_ARCHIVE` can supply a cached archive, which still
must pass the pinned checksum check. Python prepares and compares test assets;
consumers and the client library do not depend on it.

Pinned provenance:

- Native version: **10.9.0**.
- [Source revision](https://github.com/the-open-engine/zeroshot/tree/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa): `75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa`.
- [Release](https://github.com/the-open-engine/zeroshot/releases/tag/v10.9.0) names that source revision in its `target_commitish`.
- Linux x64 musl archive SHA-256: `ca7305a0a165f3909481ccfcccce367d3bc2c40a9ab65760f6d6cad2a38d002d` (official release asset digest / `SHA256SUMS`).
- Actual extracted `zeroshot` executable SHA-256: `f39952b98652301db58a89c4132a0476ae4ec570749b5945cc5200c2d22fad94` (measured and independently rechecked each run).

Each run also appends the SDK build under test to `provenance.txt`: the repository
commit, whether the worktree was clean, `dotnet --version` and the SHA-256 of the packed
`Zeroshot.Client` package that every consumer restores.

These are release provenance and local execution evidence, not remote executable
attestation or a reproducible-build claim. Discovery itself publishes no product
version or checksum.

The harness proves GET discovery from the real executable, HEAD 404 on the fixed
route, and direct POST session acquisition with `Cache-Control: no-store`. A freshly
copied external consumer uses a locally packed `Zeroshot.Client` package and isolated
package cache to discover and acquire two sessions: `{}` and an explicit canonical
UUIDv7 run selector. It checks the returned same-authority `ws` endpoint and absence
of a bearer. Direct session acquisition does not require the selected run to exist.
The packed consumer then initializes WebSocket OECP, verifies capabilities, reads
native's empty cluster get, lists the admitted run, checks exact run/source status
through a terminal projection and verifies unsupported-protocol rejection. It then
sends well-formed requests for the ten shared cluster methods that the target does
not implement (plan, apply, update, stop, retry, resubmit, delete, watch, logs,
agent/attach). Each must return `-32000`/`INVALID_PHASE`, although initialize
advertises logs and agent attachment. Trusted `run/submit` must be a rejected
`RUN_CONFLICT` attempt. Afterwards cluster get stays empty and the inventory is
unchanged. `consumer.json` retains every refusal body. This proves stock refusals,
not cluster execution. This host
reports `runtime_unavailable`; that real terminal status is inspection evidence,
not successful provider execution or terminal-event durability. The source identity
is a fixture label, not evidence of checkout. The 30 s witness wait fails explicitly
if terminal evidence cannot be obtained.

A second packed-package consumer exercises `Target.SubmitAttemptAsync`. The harness
uses stock native `profile set --template software-change --pr` with Codex/gateway,
`gpt-5.6-sol`, medium effort, small size, execution sessions and explicit gateway
connection declarations. Isolated `ZEROSHOT_CONFIG_DIR` and `XDG_STATE_HOME` prevent
ambient profile access. It extracts complete graph/runtime bytes, admits them again
via `profile set --graph --runtime-config` and compares the extracted bytes exactly.
The generated asset retains PR delivery and the default `Consider` feedback policy.

The consumer omits agent connection declarations, then proves native contained
normalization by observing admission refusal without gateway values and acceptance
with test-owned values. Submitting the explicit equivalent runtime with another
proposed ID returns the first run: normalized deduplication happens before replacement
credential inspection. An exact retained-request replay with an empty outer map also
returns that run. A changed immutable title under the same key yields the pinned
409 conflict. `submission.json` records these outcomes and both identities, plus
asset/retained hashes. These are admission/replay proofs, not provider or forge
execution. The fake gateway points to a closed numeric loopback port; no external
provider or forge authority is supplied.

A history consumer then reads the discovered run-history capability through the
direct target UI mount over the runs retained so far: list and resumed list, the
inspection run's definition and pages, HEAD for all three routes, and the native
`run_not_found` and `invalid_cursor` problem categories. The three private operator
exports sent to this direct target with a private capability must each get 404
`request.not_found`: a target in the wrong mode refuses them. It then acquires an OECP
session on the same client. Without `Connection: close` on history requests, native
would route that reused connection to its UI router and return 404. `history.json`
retains the evidence.

A dashboard consumer then reads the same listener's stock browser UI mount
through `NativeClient.Dashboard`: root and `/ui` 307 redirects (reported, not
followed), the index and its referenced assets with HEAD length parity, a bare 404
for a missing asset, the complete bootstrap catalog and its HEAD, native admission of
the generated asset and a 422 for an unbound draft, and authoring/data draft edits.
The same client then reads discovery, proving UI-routed requests do not capture a
pooled connection. A consumer-owned handler injects a foreign Origin and a `text/plain`
body to show native's typed `origin_rejected` and `json_required` refusals; `dashboard.json`
retains the evidence.

The consumer then uses the UI profile store in the target's fresh `--storage`. It first
lists it empty, which proves that the drafts stored nothing, and reads a missing profile
(native's 500 `profile_store_error`). It creates a profile from the generated asset with no
expected revision and reads it back with the same revision, also by HEAD. A second
create of that name is a rejected `profile_conflict`. An update with the read revision is
acknowledged with a new revision and the same profile ID, and a save with the old revision
is a rejected `profile_conflict`. Two concurrent saves from the same revision end with
exactly one acknowledged and one `profile_conflict`. A foreign workspace ID is rejected with
`workspace_changed` and an unbound runtime with 422 `invalid_profile`, and neither changes
the stored profile. The harness then reads the list and profile with curl and requires the
last acknowledged ID and revision. A store replaced under a running host is fixture-only.

The same consumer reads the browser run routes: the list, a finished run's definition and
page must equal the discovered run-history binding's results, and all four run routes
answer HEAD. One SSE observation of that run delivers its retained page and then native
closes the stream (`ServerClosed`). A second one sends `after=v2:0` with a `Last-Event-ID`
and native resumes after the latter. Opening events for an unknown run or a cursor ahead
of the run gives `run_not_found` and `invalid_cursor`, and a cross-site `Sec-Fetch-Site`
header gives `origin_rejected`. Stock native emitted no `history_error` or keepalive in
these phases; those are fixture-only.

A third packed-package consumer exercises `Runs.WatchAsync` and `Runs.LogsAsync`
against a separate target with fresh observation storage. The original target is
stopped first. This target runs with root privileges and a scrubbed environment.
The admitted no-worker graph has a test-owned setup hook that prints `history-ready`,
waits for a release file, prints `live-after-subscription`, and exits with status 1.
The hook has a 60-second bound and runs before source checkout. Native captures its
output, appends real preparation logs to its own ledger, and records the terminal
`environment_setup_failed` status. The witness never edits the ledger or injects
observation events. Its source is the pinned native repository/revision; that exact
source identity is checked without claiming a checkout or provider execution.

The consumer first observes the setup's waiting point and retains its opaque cursor.
It then establishes fresh watch/log subscriptions, reads preexisting log history,
checks exact run/source status, and releases the hook only after both subscriptions
are acknowledged. It verifies the subsequent live log records and live terminal
watch event, distinct cursors, and authoritative `done` closes with delivery positions.
After completion it replays logs exclusively after the retained history cursor and
replays the terminal watch history. Requests from each stream's final cursor must
return no records. Cursors are reused verbatim and are never parsed or incremented.
Before the release, the consumer also opens two dashboard SSE observations of the
active run and disposes one; the other stays open, delivers the preexisting and live
pages, which together equal the finished retained page, and ends with native's close
after the terminal event.

The shell stops that target, starts the same executable against the same observation
storage, and launches a new packed consumer process. It verifies the exact run/source
and terminal cursor, then repeats both history and exclusive-boundary checks. This
proves terminal observation survives an actual target restart; it does not claim
automatic reconnect, uninterrupted execution, or provider success. Consumer phases
have a 30-second budget. Cleanup releases any waiting hook and terminates the target.

Both consumer phases repeat the replay through the SDK's `Run.LogsAsync` and
`Run.WatchAsync`. After completion, the live phase reads all logs through the SDK. It
requires them to equal the retained records and saves the scoped `HistoryCheckpoint` of
the `history-ready` record as `observation-checkpoint.json`. Each phase then parses that
checkpoint and resumes exclusively after it; the second phase does so in a new process after
the target restart. Each phase also replays the terminal watch history and requires both
final checkpoints to return nothing. This exercises checkpointed SDK replay. It does not
exercise interruption recovery, which deterministic tests cover.

The observation consumer also reads run history before the gate is released, while
the run is admitted and not terminal: an available definition without a terminal
result and a complete but unfinished page. After completion it requires the retained
`terminal` event at the watch cursor and saves `observation-history.json`; after the
restart the same page events must be served. This is observed retention across one
restart, not a retention guarantee.

It retains provenance, request bytes and hash, receipt, native logs, raw discovery/
session data, response headers, complete/readmitted asset bytes and hashes, exact
retained submission bytes, package/restore logs and all consumer outputs. Observation
evidence includes `observation-request.json`, original `observation-logs.json` and
`observation-watch.json` records, `observation-live.json` establishment and delivery
evidence, `observation-before-restart.json`, `observation-after-restart.json`, and
native logs/PIDs for both target starts. Live hosted/private authorities and provider success
remain unverified. Hosted/private contracts currently use source-backed controlled
HTTP peers; HTTPS acquisition uses a temporary trusted certificate in deterministic
tests, and WSS scheme/authority/port rules are tested without dialing WebSockets.

The wire and dispatch authorities are
[`native_v2_target.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs),
[`transport.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs),
and [`serve.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/serve.rs).

The preparation witness uses stock
[`preparation.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cloud/preparation.rs)
for ledger-owned `SafeLog` records,
[`allocator.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/allocator.rs)
for setup-before-checkout ordering, and
[`environment.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting/environment.rs)
for hook execution and output capture.

The fourth packed consumer exercises `Runs.AttachAsync` on an actual active
execution. `attachment.sh` seeds a tiny local Git repository, uses its exact commit
as the submitted source, and supplies a null-input/null-output worker graph with a
test-owned Codex JSONL producer. The stock archive's bundled `restic` is extracted
beside native for runtime workspace allocation. Its checksum is recorded along
with the already verified archive and native executable provenance.

This target runs in a private mount namespace. A temporary `/usr/local/bin`
filesystem supplies the controlled Codex producer; a private bind mount at
`/usr/bin/git` redirects only the fixture GitHub URL to the local bare repository.
The host executables and Git configuration are untouched. No external provider or
forge credentials are used. The writer's readiness marker lives in the fresh
witness directory; the consumer controls separate output and settlement gates.
The producer has finite gate deadlines, and harness cleanup releases both gates
before stopping native.

The consumer takes its exact execution selector from `run/status`, establishes
attachment, and releases output only after receiving `working`. It receives native
live output before releasing execution, then verifies `settled` and the native
cursorless `done` close. Separate status evidence verifies the graph result.
Reattaching the inactive execution must return `GONE`; an unknown selector under
the same real run must return `NOT_FOUND`. `attachment.json` retains establishment,
every event, close, before/after status and both refusal bodies. This proves stock
native runtime and attachment behavior with a controlled provider, without claiming
real provider service integration. Hosted/private endpoints and native remote
slow-consumer behavior remain outside this witness.

`force.sh` then reuses that live target and controlled provider for the fifth
packed consumer. It resets the provider gates and ready marker, derives a second
request with its own run ID and submission key, and never releases the provider's
first gate. `ForceConsumer` waits for native status to report the active execution,
opens a watch from that status cursor and sends one `Runs.ForceAsync` request. The
acknowledgement must be `Acknowledged` for the exact run with a `stopping` or
`finished` phase; the witness records the phase native returns rather than assuming
one. Stock native awaits runtime cleanup before replying, so this acknowledgement is
normally already `finished` with `force_stopped`. Separately, durable watch history
must contain a `stopping` record before the `force_stopped` terminal record, and a
later status query must report that terminal result. A repeated force of the
terminal run is acknowledged with its existing status; an unknown canonical run ID
is a rejected `NOT_FOUND` attempt. `force.json` retains the active status, the
acknowledgement, every history record, terminal status and both follow-up attempts.

The same consumer then admits a third controlled run through the SDK, waits for its
active execution and calls `Run.ForceStopAsync`, which sends one force and returns
the acknowledgement's terminal result or waits through the common helper. It
requires `force_stopped` from the result and a later status. On the first, already
terminal run, `ForceStopAsync` with a zero wait budget must return the
acknowledgement's result without observing, and `ForceAttemptAsync` on the stopped
run is an acknowledged attempt. The `sdk` object in `force.json` records these,
including the result's evidence kind, which shows whether native's reply was
already terminal. This proves native force against a controlled execution, not
physical-cessation guarantees, provider service behavior or hosted force routes.

`recovery.sh` then reuses the same target and sets a gate that makes the
controlled provider fail each worker after checkout. The sixth packed consumer
admits a derived request under its own run ID and submission key. Native records
`worker_failed` and retains a recoverable workspace, so `RecoveryConsumer` can
exercise the following on real native storage:

- A first checkpoint page with limit 1 matching the default page.
- A latest-workspace resume and a checkpoint resume. Each admits a successor with
  fresh connection values, and the source and successor statuses link them.
- A repeated resume of the source, which is `Unknown` and admits nothing.
- Discard and repeated discard.
- The refusals reachable on a direct target.

A force-stopped workspace is disposed by native and is not resumable.
`recovery.json` retains every status, checkpoint page, attempt and RPC error.
This does not claim that provider execution succeeds, and it does not cover
hosted recovery routes or live `INVALID_PHASE`, which needs a target without
recovery support.

`private.sh` then stops that target and starts a separate private-mode target on the
same port with fresh storage. Native selects private mode only when
`--bootstrap-key-file` is supplied. The harness generates an isolated 32-byte
bootstrap key and a 64-character lowercase-hex capability in `private/`; the key
file is `0600`, and the harness checks that native unlinked it at startup. It then
records the target PID, storage and material provenance. The sixth packed consumer,
`PrivateBootstrapConsumer`, discovers `private_capability` with
`privateBootstrapPath`. It prepares AES-256-GCM envelopes itself, playing the caller,
because the client performs no bootstrap cryptography. It then sends three
`Private.BootstrapAsync` attempts. A well-formed envelope under another key is
`Rejected` with 400 `request.invalid` and leaves the bootstrap open. The valid
envelope is `Acknowledged` with an empty 204. Resending it is `Rejected` with 404
`request.not_found` because native consumed its key. `private-bootstrap.json`
retains discovery and all three attempts.

The same consumer then uses the bootstrapped capability as `PrivateCapability`
control credentials. It admits the inspection request on this target and reads the
private exports:

- The history definition, polled until native reports the run finished.
- The complete page ending with its terminal event, and the empty page after it.
- The run's operator diagnostics as received. Stock native records diagnostics only
  for checkout, push and local-controller failures, so this read is normally an empty
  list; non-empty and truncated diagnostics are fixture-only.
- For an unknown run: an empty diagnostics list and `run_not_found` history.
- `invalid_cursor` for a cursor ahead of the run.
- 401 `request.unauthorized` on all three exports for a wrong capability.

The `exports` object in `private-bootstrap.json` retains this evidence.

The consumer then acquires an OECP session for that run with the same capability. Native
issues a session bearer, which is not recorded. Over that WebSocket it checks initialize,
native's empty cluster get and the exact run/source finished status, and records them in
the `oecp` object. This proves private session authority for inspection only; no other
private OECP operation is exercised.

`controller.sh` then exercises the NDJSON bindings against a stock local-run
portable controller. It seeds a local Git repository with a fixture GitHub origin
(nothing is fetched) and runs `zeroshot run --detach` with the attachment worker
graph under a fresh short `ZEROSHOT_STATE_DIR`. A test-owned Codex JSONL producer
first on `PATH` holds the worker at a gate; the controller runs as the invoking user
with a scrubbed environment and no provider service. The harness owns this process.
The packed `ControllerConsumer` receives only the socket path. Through
`ConnectUnixAsync` it verifies initialize, native's empty get, a list containing only
the controller's run, active status, `NOT_FOUND` for a foreign run's status and a
rejected force attempt identified by the socket's file URI, and the local refusal of
`$/cancelRequest`. It then binds its own socket with `FromStreamsAsync`, disposes that
connection, reuses the still-open stream in a second connection, opens watch and log
subscriptions and releases the gate. Native stops serving at terminal state, so each
subscription ends with native's close or an observed disconnect, and the connection
ends with a transport failure. `controller.json` retains every response, record and
completion. Resume and workspace discard of the owned run are rejected attempts
with native `INVALID_PHASE`; the older cluster methods are not exercised here.

## Stock native Windows x64 controller pipe

`windows-controller.ps1` is the Windows counterpart of `controller.sh`. Run it with
PowerShell 7 on Windows x64 with .NET 10, Git, Python 3 and tar; the
`windows-native-witness` workflow runs it on `windows-2025`. Native 10.9.0 ships no
Windows arm64 build, so no arm64 native witness exists. The script downloads the
pinned `x86_64-pc-windows-msvc` archive, verifies it and the extracted executable,
packs the library and restores the packed `ControllerConsumer` from that feed with an
isolated cache. It seeds a local Git repository with a fixture GitHub origin and runs
`zeroshot run --detach` with the attachment worker graph and runtime committed in
`windows/`. The environment is
scrubbed, and state, configuration and profile directories are fresh. The same
controlled Codex producer sits first on `PATH` behind a `codex.cmd` shim and holds
the worker at a gate. The harness owns the controller process. GitHub Actions runs
each step inside a Windows Job that forbids breakaway, which native's detach refuses,
so the script starts its own launcher through WMI (`Win32_Process.Create`) outside that
Job; the launcher scrubs the environment and runs native. It reads the pipe path
from native's own `controller.ready.json`; nothing derives it from the state path.
The consumer checks the same matrix as on Unix through `ConnectNamedPipeAsync`, which
validates the pipe's security descriptor, and through a borrowed pipe stream.
`controller.json` and `provenance.txt` are printed to the job log.
