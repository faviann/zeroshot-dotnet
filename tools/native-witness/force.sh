#!/usr/bin/env bash
# Sourced by run.sh after attachment.sh. Reuses its live target and controlled provider,
# whose closed first gate keeps a second worker active until native force stops it.
rm -f -- "$witness_dir/attachment-output" "$witness_dir/attachment-release"
: > "$witness_dir/attachment-provider-ready"
python3 - "$witness_dir" <<'PY'
import json, pathlib, sys
directory = pathlib.Path(sys.argv[1])
request = json.loads((directory / 'attachment-request.json').read_text())
request['runId'] = '0195af77-2200-7000-8000-000000000002'
request['submission']['title'] = 'Native force witness'
request['submission']['submissionKey'] = 'native-force-witness'
(directory / 'force-request.json').write_text(json.dumps(request, separators=(',', ':')) + '\n')
PY
sha256sum "$witness_dir/force-request.json" >> "$witness_dir/provenance.txt"
mkdir -p "$witness_dir/force-consumer"
cp "$repo_dir/examples/ForceConsumer/"*.cs* "$witness_dir/force-consumer/"
dotnet restore "$witness_dir/force-consumer/ForceConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/force-restore.log"
dotnet build "$witness_dir/force-consumer/ForceConsumer.csproj" -c Release --no-restore > "$witness_dir/force-build.log"
dotnet run --project "$witness_dir/force-consumer/ForceConsumer.csproj" -c Release --no-build --no-restore -- \
  "$origin" "$witness_dir" > "$witness_dir/force.json"
