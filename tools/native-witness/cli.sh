#!/usr/bin/env bash
# Sourced by run.sh after recovery.sh. Reuses its live target and controlled provider. The repository-built (or
# supplied candidate) zeroshot-dotnet drives one run to native's worker_failed terminal (the recovery gate is still
# set), then clears that gate so a second run stays active until the CLI force-stops it.
cli_dir="$witness_dir/cli"
mkdir -p "$cli_dir/work"
if [[ -n ${ZEROSHOT_WITNESS_CANDIDATE:-} ]]; then
  # Release qualification: the candidate tool package from the witness feed, installed at an explicit path.
  NUGET_PACKAGES="$witness_dir/packages" dotnet tool install Zeroshot.Cli --tool-path "$cli_dir/tool" \
    --source "$witness_dir/feed" --version "$client_version" > "$cli_dir/install.log"
  cli_command=("$cli_dir/tool/zeroshot-dotnet")
  cli_entry=$(find "$cli_dir/tool/.store" -name zeroshot-dotnet.dll -path "*/tools/*")
  cli_files=("$cli_entry" "$(dirname -- "$cli_entry")/Zeroshot.Client.dll")
else
  dotnet publish "$repo_dir/src/Zeroshot.Cli/Zeroshot.Cli.csproj" -c Release -o "$cli_dir/bin" > "$cli_dir/publish.log"
  cli_command=(dotnet "$cli_dir/bin/zeroshot-dotnet.dll")
  cli_files=("$cli_dir/bin/zeroshot-dotnet.dll" "$cli_dir/bin/Zeroshot.Client.dll")
fi
sha256sum "${cli_files[@]}" >> "$witness_dir/provenance.txt"
"${cli_command[@]}" --version >> "$witness_dir/provenance.txt"
python3 - "$witness_dir" "$origin" "$source_revision" <<'PY'
import json, pathlib, sys
directory, origin, revision = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
work = directory / 'cli' / 'work'
request = json.loads((directory / 'attachment-request.json').read_text())
(work / 'target.json').write_text(json.dumps({
    'schema': 'zeroshot-dotnet/target-config/v1', 'target': origin + '/',
    'nativeBinding': {'provenance': 'caller-supplied', 'release': '10.9.0', 'sourceRevision': revision},
    'credentials': {'connections': {'openai': {'OPENAI_API_KEY': 'ZEROSHOT_WITNESS_OPENAI_KEY'}}}}) + '\n')
# CLI request files: the native submission fields plus the proposed run ID. Credentials come from the configuration.
for name, run_id, title in (('failed', '0195af77-2300-7000-8000-000000000001', 'Native CLI completion witness'),
                            ('forced', '0195af77-2300-7000-8000-000000000002', 'Native CLI force witness')):
    fields = dict(request['submission'], title=title, submissionKey='native-cli-' + name + '-witness', runId=run_id)
    (work / (name + '-request.json')).write_text(json.dumps(fields, separators=(',', ':')) + '\n')
PY
sha256sum "$cli_dir/work/failed-request.json" "$cli_dir/work/forced-request.json" >> "$witness_dir/provenance.txt"
# Native's public run history is the independent record of admissions and force requests.
cli_history() {
  python3 - "$origin" "$@" <<'PY'
import json, sys, urllib.request
origin, command, arguments = sys.argv[1], sys.argv[2], sys.argv[3:]
def get(path):
    request = urllib.request.Request(origin + path, headers={'Accept': 'application/json', 'Cache-Control': 'no-store'})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)
if command == 'runs':
    runs, after = [], ''
    while True:
        page = get('/native-v2/run-history' + after)
        runs += [run['runId'] for run in page['runs']]
        if page['nextCursor'] is None: break
        after = '?after=' + page['nextCursor']
    print(json.dumps(runs))
else:
    events, cursor = [], 'v2:0'
    while True:
        page = get(f'/native-v2/run-history/{arguments[0]}/page?after={cursor}')
        events += page['events']
        if page['complete'] or not page['events']: break
        cursor = page['nextCursor']
    print(json.dumps(events))
PY
}
# Runs the CLI in its work directory, keeping stdout, stderr and the exit code, which must be the expected one.
cli_step() {
  local name=$1 expected=$2 code=0
  shift 2
  (cd "$cli_dir/work" && ZEROSHOT_WITNESS_OPENAI_KEY=controlled-not-a-real-key \
    "${cli_command[@]}" "$@" --json) > "$cli_dir/$name.stdout" 2> "$cli_dir/$name.stderr" || code=$?
  printf '%s %s\n' "$name" "$code" >> "$cli_dir/exits.txt"
  [[ $code == "$expected" ]] || { echo "CLI $name exited $code, expected $expected." >&2; cat "$cli_dir/$name.stderr" >&2; exit 1; }
}
# Prints one value from a CLI step's JSON records: python3 expression over `records`.
cli_value() {
  python3 - "$cli_dir/$1.stdout" "$2" <<'PY'
import json, sys
records = [json.loads(line) for line in open(sys.argv[1]).read().splitlines()]
value = eval(sys.argv[2])
print(value if isinstance(value, str) else json.dumps(value))
PY
}
# Starts an attachment, waits for its first live record, then sends Ctrl+C (SIGINT) as a terminal would.
cli_attach_interrupted() {
  local name=$1
  shift
  (cd "$cli_dir/work" && python3 - "$cli_dir" "$name" "${cli_command[@]}" "$@" --json) <<'PY'
import pathlib, signal, subprocess, sys, threading
cli, name, command = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3:]
process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
deadline = threading.Timer(60, process.kill)
deadline.start()
first = process.stdout.readline()
process.send_signal(signal.SIGINT)
rest, stderr = process.communicate(timeout=60)
deadline.cancel()
(cli / (name + '.stdout')).write_text(first + rest)
(cli / (name + '.stderr')).write_text(stderr)
with open(cli / 'exits.txt', 'a') as exits: exits.write(f'{name} {process.returncode}\n')
if process.returncode != 130: sys.exit(f'CLI {name} exited {process.returncode}, expected 130.\n{stderr}')
PY
}
cli_history runs > "$cli_dir/runs-before.json"
# A submitted run that native finishes as worker_failed: submission and result records, exit 3.
cli_step run-failed 3 run --config target.json --request failed-request.json --save-request failed-saved.json --save-run failed-run.json
cli_step status-failed 0 status --run-file failed-run.json
cli_step wait-failed 3 wait --run-file failed-run.json
# Its retained history: native closes each finished stream, so every command ends with 0 and no result record.
cli_step watch-failed 0 watch --run-file failed-run.json
cli_step watch-after 0 watch --run-file failed-run.json "--after=$(cli_value watch-failed 'records[-2]["cursor"]')"
cli_step logs-failed 0 logs --run-file failed-run.json
cli_value logs-failed 'records[-1]["checkpoint"]' > "$cli_dir/work/logs-checkpoint.json"
cli_step logs-checkpoint 0 logs --run-file failed-run.json --checkpoint logs-checkpoint.json
failed_execution=$(cli_value watch-failed 'next(e["execution"] for r in records for e in r["data"]["status"].get("activeExecutions", []))')
cli_step logs-execution 0 logs --run-file failed-run.json --execution "$failed_execution"
# A detached run from a prepared file, kept active by the provider's closed first gate.
rm -f -- "$witness_dir/attachment-fail"
: > "$witness_dir/attachment-provider-ready"
cli_step prepare-forced 0 prepare --request forced-request.json --out forced-prepared.json
cli_step run-forced 0 run --config target.json --prepared forced-prepared.json --detach --save-run forced-run.json
active=false
for ((attempt=0; attempt<300; attempt++)); do
  cli_step status-forced 0 status --run-file forced-run.json
  if [[ -s "$witness_dir/attachment-provider-ready" ]] && python3 -c '
import json, sys
status = json.loads(open(sys.argv[1]).read())["status"]["status"]
assert status["phase"] != "finished", "The controlled CLI run finished before force."
sys.exit(0 if status["phase"] == "running" and status["activeExecutions"] else 1)' "$cli_dir/status-forced.stdout"; then active=true; break; fi
  sleep 0.2
done
[[ $active == true ]] || { echo 'The controlled CLI run never became active.' >&2; exit 1; }
# Live attachment to the active execution, detached by Ctrl+C; once force has stopped it, native refuses it.
forced_execution=$(cli_value status-forced 'records[0]["status"]["status"]["activeExecutions"][0]["execution"]')
cli_attach_interrupted attach-forced attach --run-file forced-run.json "$forced_execution"
cli_step force-stop 3 force-stop --run-file forced-run.json --wait-timeout 60s
cli_step attach-stopped 1 attach --run-file forced-run.json "$forced_execution"
cli_history events 0195af77-2300-7000-8000-000000000002 > "$cli_dir/forced-history.json"
cli_step force-request-only 0 force-stop --run-file forced-run.json --request-only
cli_history runs > "$cli_dir/runs-after.json"
python3 - "$cli_dir" <<'PY'
import json, pathlib, sys
cli = pathlib.Path(sys.argv[1])
def records(name): return [json.loads(line) for line in (cli / (name + '.stdout')).read_text().splitlines()]
failed, forced = '0195af77-2300-7000-8000-000000000001', '0195af77-2300-7000-8000-000000000002'
submission, result = records('run-failed')
assert submission['kind'] == 'submission' and submission['runId'] == failed and submission['attempt']['runIdsMatch'], submission
assert result['kind'] == 'result' and result['runId'] == failed and result['result']['failureReason'] == 'worker_failed', result
status, = records('status-failed')
assert status['kind'] == 'status' and status['result']['failureReason'] == 'worker_failed', status
waited, = records('wait-failed')
assert waited['result']['failureReason'] == 'worker_failed', waited
assert json.loads((cli / 'work' / 'failed-run.json').read_text())['runId'] == failed
assert json.loads((cli / 'work' / 'failed-saved.json').read_text())['runId'] == failed
detached, = records('run-forced')
assert detached['kind'] == 'submission' and detached['runId'] == forced, detached
stopped, = records('force-stop')
assert stopped['kind'] == 'result' and stopped['result']['failureReason'] == 'force_stopped', stopped
# Watch replays the whole finished history and ends on native's close: no synthesized result record.
watched = records('watch-failed')
assert watched and all(r['kind'] == 'watch' and r['runId'] == failed for r in watched), watched
assert watched[-1]['data']['status']['terminalResult']['reason'] == 'worker_failed', watched[-1]
assert all(r['checkpoint']['stream'] == 'watch' and r['checkpoint']['cursor'] == r['cursor'] == r['data']['cursor'] for r in watched), watched
assert len({r['cursor'] for r in watched}) == len(watched), watched
# --after is exclusive: only the final record follows the one before it.
assert [r['cursor'] for r in records('watch-after')] == [watched[-1]['cursor']], records('watch-after')
logged = records('logs-failed')
assert logged and all(r['kind'] == 'log' and r['runId'] == failed and r['timestamp'] == r['data']['timestamp'] for r in logged), logged
assert records('logs-checkpoint') == [], records('logs-checkpoint')
filtered = records('logs-execution')
assert filtered, filtered
assert all(r['execution'] == r['checkpoint']['execution'] == filtered[0]['execution'] for r in filtered), filtered
assert {r['cursor'] for r in filtered} <= {r['cursor'] for r in logged}, (filtered, logged)
# Attachment is live and cursorless; after force stopped the execution, native refuses it as GONE.
attached = records('attach-forced')
assert attached and attached[0]['kind'] == 'attachment' and attached[0]['data']['event']['type'] == 'working', attached
assert all('cursor' not in r and 'checkpoint' not in r for r in attached), attached
interrupted = json.loads((cli / 'attach-forced.stderr').read_text())
assert interrupted['category'] == 'cancelled', interrupted
assert records('attach-stopped') == [], records('attach-stopped')
refused = json.loads((cli / 'attach-stopped.stderr').read_text())
assert refused['category'] == 'operational' and refused['native']['domainCode'] == 'GONE', refused
repeated, = records('force-request-only')
assert repeated['kind'] == 'force' and repeated['attempt']['outcome'] == 'acknowledged' and repeated['status']['status']['phase'] == 'finished', repeated
# Native admitted exactly the two CLI runs, each once, and recorded one force request for the forced run.
before, after = (json.loads((cli / name).read_text()) for name in ('runs-before.json', 'runs-after.json'))
assert sorted(set(after) - set(before)) == [failed, forced] and len(after) == len(set(after)) == len(before) + 2, (before, after)
requested = [event for event in json.loads((cli / 'forced-history.json').read_text()) if event['event']['kind'] == 'force_stop_requested']
assert len(requested) == 1, requested
(cli / 'result.json').write_text(json.dumps({'exits': (cli / 'exits.txt').read_text().splitlines(), 'forceStopRequested': requested}) + '\n')
PY
