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
through a terminal projection and verifies unsupported-protocol rejection. This host
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

The shell stops that target, starts the same executable against the same observation
storage, and launches a new packed consumer process. It verifies the exact run/source
and terminal cursor, then repeats both history and exclusive-boundary checks. This
proves terminal observation survives an actual target restart; it does not claim
automatic reconnect, uninterrupted execution, or provider success. Consumer phases
have a 30-second budget. Cleanup releases any waiting hook and terminates the target.

It retains provenance, request bytes and hash, receipt, native logs, raw discovery/
session data, response headers, complete/readmitted asset bytes and hashes, exact
retained submission bytes, package/restore logs and all consumer outputs. Observation
evidence includes `observation-request.json`, original `observation-logs.json` and
`observation-watch.json` records, `observation-live.json` establishment and delivery
evidence, `observation-before-restart.json`, `observation-after-restart.json`, and
native logs/PIDs for both target starts. Live hosted/private authorities, provider success
and populated workspace recovery remain unverified. Hosted/private contracts currently use source-backed controlled
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
