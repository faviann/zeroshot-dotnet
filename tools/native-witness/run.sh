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
cleanup() {
  if [[ -n $native_pid ]]; then
    kill "$native_pid" 2>/dev/null || true
    wait "$native_pid" 2>/dev/null || true
  fi
  echo "Witness artifacts: $witness_dir"
}
trap cleanup EXIT
mkdir -p "$witness_dir"/{bin,state,assets,consumer,submission-consumer,feed,packages,config,profile-state}
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
printf 'PASS: stock native discovery GET, fixed-route HEAD 404, direct session POST with no-store, fresh packed-package consumers: discovery/session, OECP initialize, populated inventory, exact run/source terminal status, unsupported protocol RPC error, empty cluster get; complete asset generation/readmission/HTTP admission, contained-provider normalization, normalized deduplication, exact retained replay, proposed/acknowledged identity, native admission/conflict refusals\n' | tee "$witness_dir/result.txt"
cat "$witness_dir/provenance.txt"
