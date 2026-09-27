#!/usr/bin/env bash
# Sourced by run.sh after private.sh. Stock `zeroshot run --detach` starts a local portable
# controller; this harness owns that process, and the consumer only connects to its socket.
stop_native
rm -f -- "$witness_dir/controller-ready" "$witness_dir/controller-release"
trap 'touch "$witness_dir/controller-release"; cleanup' EXIT
mkdir -p "$witness_dir"/{controller-consumer,controller-source,controller-fake,controller-home,controller-config}
# A short state root keeps <state>/runs/<run-id>/controller.sock within the Unix socket path limit.
controller_state="$witness_dir/cs"
git -C "$witness_dir/controller-source" init --initial-branch=main > "$witness_dir/controller-git.log"
printf 'Controlled controller source\n' > "$witness_dir/controller-source/README.md"
git -C "$witness_dir/controller-source" add README.md
git -C "$witness_dir/controller-source" -c user.name=Witness -c user.email=witness@example.invalid \
  commit -m 'Controlled controller source' >> "$witness_dir/controller-git.log"
# Local runs require a GitHub origin for source identity; nothing is fetched from it.
git -C "$witness_dir/controller-source" remote add origin https://github.com/fixture/controller.git
cp "$repo_dir/tools/native-witness/controller-provider.py" "$witness_dir/controller-fake/codex"
python3 - "$witness_dir" <<'PY'
import json, pathlib, sys
directory = pathlib.Path(sys.argv[1])
attachment = json.loads((directory / 'attachment-request.json').read_text())['submission']
(directory / 'controller-graph.json').write_text(json.dumps(attachment['graph'], separators=(',', ':')) + '\n')
(directory / 'controller-runtime.json').write_text(json.dumps(attachment['runtime'], separators=(',', ':')) + '\n')
(directory / 'controller-input.json').write_text('null\n')
PY
sha256sum "$witness_dir/controller-graph.json" "$witness_dir/controller-runtime.json" "$witness_dir/controller-fake/codex" >> "$witness_dir/provenance.txt"
printf 'controllerAsset=attachment worker graph/runtime; stock local run --detach portable controller; controlled Codex JSONL producer first on PATH; no provider service\n' >> "$witness_dir/provenance.txt"
# Build before starting the run so a cold build cannot outlast the provider's 60 s gate.
cp "$repo_dir/examples/ControllerConsumer/"*.cs* "$witness_dir/controller-consumer/"
dotnet restore "$witness_dir/controller-consumer/ControllerConsumer.csproj" --packages "$witness_dir/packages" \
  --source "$witness_dir/feed" --source https://api.nuget.org/v3/index.json > "$witness_dir/controller-restore.log"
dotnet build "$witness_dir/controller-consumer/ControllerConsumer.csproj" -c Release --no-restore > "$witness_dir/controller-build.log"
(
  cd "$witness_dir/controller-source"
  exec env -i PATH="$witness_dir/controller-fake:$witness_dir/bin:/usr/bin:/bin" HOME="$witness_dir/controller-home" \
    OPENAI_API_KEY=controlled-not-a-real-key ZEROSHOT_STATE_DIR="$controller_state" ZEROSHOT_CONFIG_DIR="$witness_dir/controller-config" \
    "$witness_dir/bin/zeroshot" run --detach --title 'Native controller witness' --graph "$witness_dir/controller-graph.json" \
    --runtime-config "$witness_dir/controller-runtime.json" --input "$witness_dir/controller-input.json"
) > "$witness_dir/controller-receipt.json" 2> "$witness_dir/controller-native.log"
controller_run=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["runId"])' "$witness_dir/controller-receipt.json")
controller_socket="$controller_state/runs/$controller_run/controller.sock"
[[ -S $controller_socket ]] || { echo 'Native controller socket is missing.' >&2; exit 1; }
printf 'controllerRun=%s socket=%s\n' "$controller_run" "$controller_socket" >> "$witness_dir/provenance.txt"
dotnet run --project "$witness_dir/controller-consumer/ControllerConsumer.csproj" -c Release --no-build --no-restore -- \
  "$controller_socket" "$witness_dir" > "$witness_dir/controller.json"
