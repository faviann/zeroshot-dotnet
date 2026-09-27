#!/usr/bin/env bash
# Independently runnable stock-native conformance witness. The SDK never launches a target.
set -euo pipefail
[[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || { echo 'Linux x64 is required.' >&2; exit 1; }
repo_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
witness_dir=$(mktemp -d "${TMPDIR:-/tmp}/zeroshot-native-witness.XXXXXXXX")
readonly native_version=10.9.0
readonly source_revision=75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa
readonly archive=zeroshot-v10.9.0-x86_64-unknown-linux-musl.tar.gz
readonly archive_sha256=ca7305a0a165f3909481ccfcccce367d3bc2c40a9ab65760f6d6cad2a38d002d
readonly executable_sha256=f39952b98652301db58a89c4132a0476ae4ec570749b5945cc5200c2d22fad94
native_pid=''
native_target_pid=''
native_control=()
stop_native() {
  if [[ -n $native_pid ]]; then
    # Release a waiting test hook before stopping its target, including failure cleanup.
    if [[ -n $native_target_pid ]]; then touch "$witness_dir/observation-release" "$witness_dir/attachment-output" "$witness_dir/attachment-release"; fi
    "${native_control[@]}" kill -TERM "${native_target_pid:-$native_pid}" 2>/dev/null || true
    for ((attempt=0; attempt<100; attempt++)); do
      kill -0 "$native_pid" 2>/dev/null || break
      sleep 0.1
    done
    if kill -0 "$native_pid" 2>/dev/null; then
      "${native_control[@]}" kill -KILL "${native_target_pid:-$native_pid}" 2>/dev/null || true
    fi
    wait "$native_pid" 2>/dev/null || true
    native_pid=''
    native_target_pid=''
  fi
}
cleanup() {
  stop_native
  echo "Witness artifacts: $witness_dir"
}
trap cleanup EXIT
mkdir -p "$witness_dir"/{bin,state,assets,consumer,submission-consumer,history-consumer,observation-consumer,feed,packages,config,profile-state}
# A caller may cache the unmodified release archive; its digest is always checked.
if [[ -n ${ZEROSHOT_WITNESS_ARCHIVE:-} ]]; then
  cp -- "$ZEROSHOT_WITNESS_ARCHIVE" "$witness_dir/$archive"
else
  curl --fail --location --proto '=https' --tlsv1.2 \
    "https://github.com/the-open-engine/zeroshot/releases/download/v$native_version/$archive" -o "$witness_dir/$archive"
fi
printf '%s  %s\n' "$archive_sha256" "$witness_dir/$archive" | sha256sum --check
# Extract only the selected executable; discovery needs no bundled restic or provider tools.
tar -xzf "$witness_dir/$archive" -C "$witness_dir/bin" zeroshot
printf '%s  %s\n' "$executable_sha256" "$witness_dir/bin/zeroshot" | sha256sum --check
{
  printf 'nativeVersion=%s\nsourceRevision=%s\n' "$native_version" "$source_revision"
  printf 'source=https://github.com/the-open-engine/zeroshot/tree/%s\n' "$source_revision"
  printf 'release=https://github.com/the-open-engine/zeroshot/releases/tag/v%s\n' "$native_version"
  printf 'archiveSha256=%s\n' "$archive_sha256"
  sha256sum "$witness_dir/bin/zeroshot"
  "$witness_dir/bin/zeroshot" --version
  printf 'assets=test-owned no-worker succeed graph with explicit source identity; native terminal inspection, no provider processes\n'
  printf 'submissionAsset=complete stock software-change PR graph/runtime; Codex gateway, gpt-5.6-sol, medium effort, small, execution sessions\n'
} > "$witness_dir/provenance.txt"
# Generate and readmit a complete native asset in isolated local profile storage.
# These commands prepare test data; the client library has no process/asset compiler.
cat > "$witness_dir/assets/uniform.json" <<'JSON'
{"harness":"codex","provider":"gateway","model":"gpt-5.6-sol","effort":"medium","size":"small","sessionScope":"execution","connections":{"gateway":["GATEWAY_API_KEY","GATEWAY_BASE_URL"]}}
JSON
native_profile() {
  env -i PATH=/usr/bin:/bin ZEROSHOT_CONFIG_DIR="$witness_dir/config" XDG_STATE_HOME="$witness_dir/profile-state" \
    "$witness_dir/bin/zeroshot" profile "$@"
}
native_profile set generated --template software-change --pr --uniform-runtime-config "$witness_dir/assets/uniform.json" > "$witness_dir/profile-set.json"
native_profile show generated > "$witness_dir/assets/profile.json"
python3 - "$witness_dir/assets" <<'PY'
import json, pathlib, sys
assets = pathlib.Path(sys.argv[1])
profile = json.loads((assets / 'profile.json').read_text())
for field in ('graph', 'runtime'):
    (assets / (field + '.json')).write_text(json.dumps(profile[field], ensure_ascii=False, separators=(',', ':')) + '\n')
PY
native_profile set admitted --graph "$witness_dir/assets/graph.json" --runtime-config "$witness_dir/assets/runtime.json" > "$witness_dir/profile-readmit.json"
native_profile show admitted > "$witness_dir/assets/admitted.json"
python3 - "$witness_dir/assets" <<'PY'
import json, pathlib, sys
assets = pathlib.Path(sys.argv[1])
admitted = json.loads((assets / 'admitted.json').read_text())
for field in ('graph', 'runtime'):
    assert (json.dumps(admitted[field], ensure_ascii=False, separators=(',', ':')) + '\n').encode() == (assets / (field + '.json')).read_bytes()
PY
sha256sum "$witness_dir/assets/graph.json" "$witness_dir/assets/runtime.json" >> "$witness_dir/provenance.txt"
# A random unprivileged loopback port keeps concurrent witnesses independent. A caller
# can select a known free port; any bind failure is reported instead of using another target.
port=${ZEROSHOT_WITNESS_PORT:-$(shuf -i 20000-60000 -n 1)}
[[ $port =~ ^[0-9]+$ && $port -ge 1024 && $port -le 65535 ]] || { echo 'Invalid witness port.' >&2; exit 1; }
origin="http://127.0.0.1:$port"
(
  cd "$witness_dir/assets"
  exec env -i PATH=/usr/bin:/bin "$witness_dir/bin/zeroshot" target serve \
    --listen "127.0.0.1:$port" --public-origin "$origin" --storage "$witness_dir/state"
) > "$witness_dir/native.log" 2>&1 &
native_pid=$!
ready=false
for ((attempt=0; attempt<100; attempt++)); do
  kill -0 "$native_pid" 2>/dev/null || { cat "$witness_dir/native.log" >&2; exit 1; }
  if rg --quiet 'Zeroshot direct target listening on' "$witness_dir/native.log"; then ready=true; break; fi
  sleep 0.1
done
[[ $ready == true ]] || { echo 'Native listener was not ready.' >&2; exit 1; }
printf 'origin=%s\n' "$origin" >> "$witness_dir/provenance.txt"
curl --fail --silent --show-error "$origin/.well-known/zeroshot-native-v2" > "$witness_dir/discovery.json"
head_status=$(curl --silent --show-error --head --dump-header "$witness_dir/head.headers" \
  --output /dev/null --write-out '%{http_code}' "$origin/.well-known/zeroshot-native-v2")
[[ $head_status == 404 ]] || { echo "Expected fixed-route HEAD refusal; received $head_status" >&2; exit 1; }
curl --fail --silent --show-error --header 'Content-Type: application/json' --data '{}' \
  --dump-header "$witness_dir/session.headers" "$origin/native-v2/oecp-session" > "$witness_dir/session.json"
rg --quiet --ignore-case '^Cache-Control: no-store' "$witness_dir/session.headers"
# Keep one no-worker run for the existing terminal inspection witness.
cp "$repo_dir/tools/native-witness/inspection-request.json" "$witness_dir/request.json"
curl --fail --silent --show-error --header 'Content-Type: application/json' --data-binary "@$witness_dir/request.json" \
  "$origin/native-v2/run" > "$witness_dir/receipt.json"
sha256sum "$witness_dir/request.json" >> "$witness_dir/provenance.txt"
dotnet pack "$repo_dir/src/Zeroshot.Sdk/Zeroshot.Sdk.csproj" -c Release -o "$witness_dir/feed" > "$witness_dir/pack.log"
cp "$repo_dir/examples/DiscoveryConsumer/"*.cs* "$witness_dir/consumer/"
dotnet restore "$witness_dir/consumer/DiscoveryConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/consumer-restore.log"
dotnet run --project "$witness_dir/consumer/DiscoveryConsumer.csproj" -c Release --no-restore -- "$origin" > "$witness_dir/consumer.json"
cp "$repo_dir/examples/SubmissionConsumer/"*.cs* "$witness_dir/submission-consumer/"
dotnet restore "$witness_dir/submission-consumer/SubmissionConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/submission-restore.log"
dotnet run --project "$witness_dir/submission-consumer/SubmissionConsumer.csproj" -c Release --no-restore -- \
  "$origin" "$witness_dir/assets" "$witness_dir" > "$witness_dir/submission.json"
sha256sum "$witness_dir/complete-retained.json" >> "$witness_dir/provenance.txt"
# Public run history through the direct target UI mount, over the runs retained above.
cp "$repo_dir/examples/HistoryConsumer/"*.cs* "$witness_dir/history-consumer/"
dotnet restore "$witness_dir/history-consumer/HistoryConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/history-restore.log"
dotnet run --project "$witness_dir/history-consumer/HistoryConsumer.csproj" -c Release --no-restore -- \
  "$origin" "$witness_dir" > "$witness_dir/history.json"
# The same stock listener serves its browser UI mount. Drafts must leave the UI profile store empty.
mkdir -p "$witness_dir/dashboard-consumer"
cp "$repo_dir/examples/DashboardConsumer/"*.cs* "$witness_dir/dashboard-consumer/"
dotnet restore "$witness_dir/dashboard-consumer/DashboardConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/dashboard-restore.log"
dotnet run --project "$witness_dir/dashboard-consumer/DashboardConsumer.csproj" -c Release --no-restore -- \
  "$origin" "$witness_dir/assets" > "$witness_dir/dashboard.json"
[[ $(curl --fail --silent --show-error "$origin/ui/api/profiles") == '{"profiles":[]}' ]] || { echo 'Dashboard drafts changed the profile store.' >&2; exit 1; }

# Keep the existing admission witnesses above in their original target. This separate
# target needs native's root-owned preparation boundary but no provider or checkout.
stop_native
if (( EUID != 0 )); then
  sudo -n true || { echo 'Observation witness requires root or passwordless sudo for native setup hooks.' >&2; exit 1; }
  native_control=(sudo -n --)
fi
python3 - "$witness_dir" "$source_revision" <<'PY'
import json, pathlib, shlex, sys
directory = pathlib.Path(sys.argv[1])
request = json.loads((directory / 'request.json').read_text())
request['runId'] = '0195af77-2100-7000-8000-000000000001'
submission = request['submission']
submission['title'] = 'Native watch and logs witness'
submission['submissionKey'] = 'native-observation-witness'
submission['source'] = {'repository': 'the-open-engine/zeroshot', 'branch': 'main', 'revision': sys.argv[2]}
gate = shlex.quote(str(directory / 'observation-release'))
submission['environment'] = {'setup': (
    'printf "history-ready\\n"; '
    f'for ((attempt=0; attempt<600; attempt++)); do if [[ -f {gate} ]]; then '
    'printf "live-after-subscription\\n"; exit 1; fi; sleep 0.1; done; exit 2'
)}
(directory / 'observation-request.json').write_text(json.dumps(request, separators=(',', ':')) + '\n')
PY
sha256sum "$witness_dir/observation-request.json" >> "$witness_dir/provenance.txt"
printf 'observationAsset=no-worker graph; root-owned bounded setup hook; real history/live preparation logs and environment_setup_failed terminal; no checkout/provider execution\n' >> "$witness_dir/provenance.txt"
start_observation_target() {
  local phase=$1
  rm -f -- "$witness_dir/observation-native.pid"
  "${native_control[@]}" env -i PATH=/usr/bin:/bin /bin/sh -c \
    'printf "%s\n" "$$" > "$1"; shift; exec "$@"' native-witness \
    "$witness_dir/observation-native.pid" "$witness_dir/bin/zeroshot" target serve \
    --listen "127.0.0.1:$port" --public-origin "$origin" --storage "$witness_dir/observation-state" \
    > "$witness_dir/observation-native-$phase.log" 2>&1 &
  native_pid=$!
  local ready=false
  for ((attempt=0; attempt<100; attempt++)); do
    if [[ -s "$witness_dir/observation-native.pid" ]]; then native_target_pid=$(cat "$witness_dir/observation-native.pid"); fi
    kill -0 "$native_pid" 2>/dev/null || { cat "$witness_dir/observation-native-$phase.log" >&2; exit 1; }
    if rg --quiet 'Zeroshot direct target listening on' "$witness_dir/observation-native-$phase.log"; then ready=true; break; fi
    sleep 0.1
  done
  [[ $ready == true && $native_target_pid =~ ^[0-9]+$ ]] || { echo 'Native observation listener was not ready.' >&2; exit 1; }
  printf 'observationTargetPhase=%s pid=%s storage=%s\n' "$phase" "$native_target_pid" "$witness_dir/observation-state" >> "$witness_dir/provenance.txt"
}
cp "$repo_dir/examples/ObservationConsumer/"*.cs* "$witness_dir/observation-consumer/"
dotnet restore "$witness_dir/observation-consumer/ObservationConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/observation-restore.log"
dotnet build "$witness_dir/observation-consumer/ObservationConsumer.csproj" -c Release --no-restore > "$witness_dir/observation-build.log"
start_observation_target live
dotnet run --project "$witness_dir/observation-consumer/ObservationConsumer.csproj" -c Release --no-build --no-restore -- \
  "$origin" "$witness_dir" live > "$witness_dir/observation-before-restart.json"
stop_native
start_observation_target restarted
dotnet run --project "$witness_dir/observation-consumer/ObservationConsumer.csproj" -c Release --no-build --no-restore -- \
  "$origin" "$witness_dir" restarted > "$witness_dir/observation-after-restart.json"
source "$repo_dir/tools/native-witness/attachment.sh"
source "$repo_dir/tools/native-witness/force.sh"
source "$repo_dir/tools/native-witness/private.sh"
printf 'PASS: stock native discovery GET, fixed-route HEAD 404, direct session POST with no-store, fresh packed-package consumers: discovery/session, OECP initialize, populated inventory, exact run/source terminal status, unsupported protocol RPC error, empty cluster get, ten cluster INVALID_PHASE refusals despite advertised logs/agentAttach and run/submit RUN_CONFLICT; complete asset generation/readmission/HTTP admission, contained-provider normalization, normalized deduplication, exact retained replay, proposed/acknowledged identity, native admission/conflict refusals; dashboard 307 redirects/index/assets/bare 404 with HEAD, complete bootstrap, validate/authoring/data drafts with an unchanged profile store, fixed-route discovery after UI-routed requests, native origin_rejected and json_required refusals; run history list/detail/page/HEAD on retained runs with closed problem categories and a control request after history on the same client; history while active, at completion and after restart; watch/logs preexisting history, genuinely live new records, authoritative completion, opaque exclusive replay and exact-run observation after target restart; exact active execution attachment working/live output/settled and cursorless close, inactive GONE and unknown NOT_FOUND; one acknowledged force of an active controlled run with its native phase recorded, durable stopping history before force_stopped terminal history/status, idempotent terminal force and unknown-run NOT_FOUND rejection; separate private-mode target: wrong-key bootstrap rejected 400 request.invalid, one acknowledged empty 204 bootstrap, then closed 404 request.not_found\n' | tee "$witness_dir/result.txt"
cat "$witness_dir/provenance.txt"
