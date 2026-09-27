#!/usr/bin/env bash
# Sourced by run.sh after the retained watch/log witness. All paths are test-owned.
stop_native
rm -f -- "$witness_dir/attachment-output" "$witness_dir/attachment-release"
command -v unshare >/dev/null
command -v mount >/dev/null
command -v git >/dev/null
mkdir -p "$witness_dir"/{attachment-consumer,attachment-source,attachment-fixture}
# Native's isolated writer must traverse the source and provider gate paths.
chmod 755 "$witness_dir"
git -C "$witness_dir/attachment-source" init --initial-branch=main > "$witness_dir/attachment-git.log"
printf 'Controlled attachment source\n' > "$witness_dir/attachment-source/README.md"
git -C "$witness_dir/attachment-source" add README.md
git -C "$witness_dir/attachment-source" -c user.name=Witness -c user.email=witness@example.invalid \
  commit -m 'Controlled attachment source' >> "$witness_dir/attachment-git.log"
git clone --bare "$witness_dir/attachment-source" "$witness_dir/attachment-fixture/source.git" >> "$witness_dir/attachment-git.log" 2>&1
attachment_revision=$(git -C "$witness_dir/attachment-source" rev-parse HEAD)
# Runtime allocation requires stock restic next to zeroshot. Extract it only for
# this phase, preserving the earlier admission witnesses' runtime-unavailable case.
tar -xzf "$witness_dir/$archive" -C "$witness_dir/bin" restic
sha256sum "$witness_dir/bin/restic" >> "$witness_dir/provenance.txt"
cp "$repo_dir/tools/native-witness/attachment-provider.py" "$witness_dir/attachment-fixture/codex"
chmod 755 "$witness_dir/attachment-fixture/codex"
touch "$witness_dir/attachment-provider-ready"
chmod 666 "$witness_dir/attachment-provider-ready"
python3 - "$witness_dir" "$attachment_revision" <<'PY'
import json, pathlib, shlex, sys
directory = pathlib.Path(sys.argv[1])
fixture = directory / 'attachment-fixture'
request = json.loads((directory / 'request.json').read_text())
request['runId'] = '0195af77-2200-7000-8000-000000000001'
submission = request['submission']
submission['title'] = 'Native active attachment witness'
submission['submissionKey'] = 'native-attachment-witness'
submission['source'] = {'repository': 'fixture/attachment', 'branch': 'main', 'revision': sys.argv[2]}
submission['graph']['root'] = {
    'kind': 'seq', 'name': 'root', 'state': {'kind': 'null'}, 'promotedStatePaths': [],
    'children': [
        {'kind': 'step', 'name': 'worker', 'worker': 'agent.worker@1',
         'instructions': 'Exercise the controlled attachment.', 'input': {'kind': 'null'},
         'output': {'kind': 'null'}, 'inputBindings': [], 'writeBindings': [], 'timeoutMs': 60000, 'attempts': 1},
        {'kind': 'choice', 'name': 'worker_result', 'state': {'kind': 'null'}, 'promotedStatePaths': [],
         'branches': [{'when': {'kind': 'in', 'value': {'name': 'worker', 'source': 'error', 'field': None},
                                'labels': ['timeout', 'crash', 'malformed', 'refusal']},
                       'node': {'kind': 'fail', 'name': 'worker_failed', 'reason': 'worker_failed'}}],
         'otherwise': {'kind': 'succeed', 'name': 'done', 'output': {'kind': 'null'}, 'bindings': []}}
    ]
}
submission['runtime']['nodes'] = {'worker': {'kind': 'agent', 'model': 'fixture-owned-model',
    'connections': {'openai': ['OPENAI_API_KEY']}}}
request['connections'] = {'openai': {'OPENAI_API_KEY': 'controlled-not-a-real-key'}}
(directory / 'attachment-request.json').write_text(json.dumps(request, separators=(',', ':')) + '\n')
(fixture / 'gitconfig').write_text('[safe]\n\tdirectory = ' + str(fixture / 'source.git') + '\n' +
    '[url "file://' + str(fixture / 'source.git') + '"]\n\tinsteadOf = https://github.com/fixture/attachment.git\n')
(fixture / 'git').write_text('#!/bin/sh\nGIT_CONFIG_GLOBAL=' + shlex.quote(str(fixture / 'gitconfig')) + ' exec /usr/lib/git-core/git "$@"\n')
PY
chmod 755 "$witness_dir/attachment-fixture/git"
sha256sum "$witness_dir/attachment-request.json" "$witness_dir/attachment-fixture/codex" >> "$witness_dir/provenance.txt"
printf 'attachmentAsset=test-owned checked-out Git revision %s; null worker graph; controlled Codex JSONL producer; stock runtime, ledger and OECP; private mount namespace\n' "$attachment_revision" >> "$witness_dir/provenance.txt"
cp "$repo_dir/examples/AttachmentConsumer/"*.cs* "$witness_dir/attachment-consumer/"
dotnet restore "$witness_dir/attachment-consumer/AttachmentConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/attachment-restore.log"
dotnet build "$witness_dir/attachment-consumer/AttachmentConsumer.csproj" -c Release --no-restore > "$witness_dir/attachment-build.log"
# Fixed hosting paths are overridden only inside this private namespace. No host
# executable is replaced and no paid provider/forge request can be dispatched.
"${native_control[@]}" unshare --mount --propagation private /bin/bash -c '
  set -euo pipefail
  directory=$1; port=$2; origin=$3
  mount -t tmpfs tmpfs /usr/local/bin
  cp "$directory/attachment-fixture/codex" /usr/local/bin/codex
  printf "%s" "$directory" > /usr/local/bin/witness-directory
  mount --bind "$directory/attachment-fixture/git" /usr/bin/git
  printf "%s\n" "$$" > "$directory/attachment-native.pid"
  exec env -i PATH=/usr/bin:/bin "$directory/bin/zeroshot" target serve \
    --listen "127.0.0.1:$port" --public-origin "$origin" --storage "$directory/attachment-state"
' native-attachment "$witness_dir" "$port" "$origin" > "$witness_dir/attachment-native.log" 2>&1 &
native_pid=$!
ready=false
for ((attempt=0; attempt<100; attempt++)); do
  if [[ -s "$witness_dir/attachment-native.pid" ]]; then native_target_pid=$(cat "$witness_dir/attachment-native.pid"); fi
  kill -0 "$native_pid" 2>/dev/null || { cat "$witness_dir/attachment-native.log" >&2; exit 1; }
  if rg --quiet 'Zeroshot direct target listening on' "$witness_dir/attachment-native.log"; then ready=true; break; fi
  sleep 0.1
done
[[ $ready == true && $native_target_pid =~ ^[0-9]+$ ]] || { echo 'Native attachment listener was not ready.' >&2; exit 1; }
dotnet run --project "$witness_dir/attachment-consumer/AttachmentConsumer.csproj" -c Release --no-build --no-restore -- \
  "$origin" "$witness_dir" > "$witness_dir/attachment.json"
