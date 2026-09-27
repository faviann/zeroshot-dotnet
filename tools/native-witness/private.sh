#!/usr/bin/env bash
# Sourced by run.sh after the direct-target phases. A separate private-mode target with its own
# storage and test-generated bootstrap key and capability. After bootstrap, the same consumer admits
# the inspection request and reads the private operator exports with that capability.
stop_native
private_dir="$witness_dir/private"
mkdir -p "$private_dir/state" "$private_dir/consumer"
(
  umask 077
  od -An -tx1 -v -N32 /dev/urandom | tr -d ' \n' > "$private_dir/bootstrap-key.hex"
  od -An -tx1 -v -N32 /dev/urandom | tr -d ' \n' > "$private_dir/capability.hex"
  # Native requires a 0600 single-link file owned by its user, and unlinks it at startup.
  cp -- "$private_dir/bootstrap-key.hex" "$private_dir/bootstrap-key-file"
)
(
  cd "$private_dir"
  exec env -i PATH=/usr/bin:/bin "$witness_dir/bin/zeroshot" target serve \
    --listen "127.0.0.1:$port" --public-origin "$origin" --storage "$private_dir/state" \
    --bootstrap-key-file "$private_dir/bootstrap-key-file"
) > "$private_dir/native.log" 2>&1 &
native_pid=$!
ready=false
for ((attempt=0; attempt<100; attempt++)); do
  kill -0 "$native_pid" 2>/dev/null || { cat "$private_dir/native.log" >&2; exit 1; }
  if rg --quiet 'target listening on' "$private_dir/native.log"; then ready=true; break; fi
  sleep 0.1
done
[[ $ready == true ]] || { echo 'Native private listener was not ready.' >&2; exit 1; }
[[ ! -e "$private_dir/bootstrap-key-file" ]] || { echo 'Native did not consume its bootstrap key file.' >&2; exit 1; }
printf 'privateTarget pid=%s storage=%s material=test-generated bootstrap key and capability; key file unlinked by native at startup\n' \
  "$native_pid" "$private_dir/state" >> "$witness_dir/provenance.txt"
cp "$repo_dir/examples/PrivateBootstrapConsumer/"*.cs* "$private_dir/consumer/"
dotnet restore "$private_dir/consumer/PrivateBootstrapConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$private_dir/restore.log"
dotnet run --project "$private_dir/consumer/PrivateBootstrapConsumer.csproj" -c Release --no-restore -- \
  "$origin" "$private_dir" "$witness_dir/request.json" > "$witness_dir/private-bootstrap.json"
