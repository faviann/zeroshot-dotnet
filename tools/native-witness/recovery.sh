#!/usr/bin/env bash
# Sourced by run.sh after force.sh. Reuses its live target. Native disposes a force-stopped
# workspace, so the controlled provider now fails each worker after checkout; the graph's
# worker_failed terminal retains a recoverable workspace for the source and every successor.
touch "$witness_dir/attachment-fail"
python3 - "$witness_dir" <<'PY'
import json, pathlib, sys
directory = pathlib.Path(sys.argv[1])
request = json.loads((directory / 'attachment-request.json').read_text())
request['runId'] = '0195af77-2200-7000-8000-000000000003'
request['submission']['title'] = 'Native recovery witness'
request['submission']['submissionKey'] = 'native-recovery-witness'
(directory / 'recovery-request.json').write_text(json.dumps(request, separators=(',', ':')) + '\n')
PY
sha256sum "$witness_dir/recovery-request.json" >> "$witness_dir/provenance.txt"
mkdir -p "$witness_dir/recovery-consumer"
cp "$repo_dir/examples/RecoveryConsumer/"*.cs* "$witness_dir/recovery-consumer/"
dotnet restore "$witness_dir/recovery-consumer/RecoveryConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/recovery-restore.log"
dotnet build "$witness_dir/recovery-consumer/RecoveryConsumer.csproj" -c Release --no-restore > "$witness_dir/recovery-build.log"
dotnet run --project "$witness_dir/recovery-consumer/RecoveryConsumer.csproj" -c Release --no-build --no-restore -- \
  "$origin" "$witness_dir" > "$witness_dir/recovery.json"
