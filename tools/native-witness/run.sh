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
mkdir -p "$witness_dir"/{bin,state,assets,consumer,feed,packages}
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
  printf 'assets=empty isolated working directory; discovery only, no submitted assets or provider processes\n'
} > "$witness_dir/provenance.txt"
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
dotnet pack "$repo_dir/src/Zeroshot.Sdk/Zeroshot.Sdk.csproj" -c Release -o "$witness_dir/feed" > "$witness_dir/pack.log"
cp "$repo_dir/examples/DiscoveryConsumer/"*.cs* "$witness_dir/consumer/"
dotnet restore "$witness_dir/consumer/DiscoveryConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/consumer-restore.log"
dotnet run --project "$witness_dir/consumer/DiscoveryConsumer.csproj" -c Release --no-restore -- "$origin" > "$witness_dir/consumer-discovery.json"
printf 'PASS: stock native discovery GET, fixed-route HEAD 404, fresh packed-package consumer\n' | tee "$witness_dir/result.txt"
cat "$witness_dir/provenance.txt"
