#!/usr/bin/python3
"""Test-owned Codex JSONL producer; native still owns execution and OECP events."""
import json
import pathlib
import sys
import time

if sys.argv[1:] == ["--version"]:
    print("codex-cli 0.153.4")
    raise SystemExit(0)

directory = pathlib.Path(pathlib.Path("/usr/local/bin/witness-directory").read_text())
sys.stdin.read()
print(json.dumps({"type": "thread.started", "thread_id": "controlled-attachment"}), flush=True)
# The writer identity cannot create files under the witness root. Its existing
# marker is world writable, while the consumer alone controls the release gates.
(directory / "attachment-provider-ready").write_text("ready")


def wait_for(name):
    deadline = time.monotonic() + 60
    while not (directory / name).exists():
        if time.monotonic() >= deadline:
            raise SystemExit("controlled attachment gate timed out")
        time.sleep(0.05)


wait_for("attachment-output")
print(json.dumps({"type": "turn.started"}), flush=True)
wait_for("attachment-release")
print(json.dumps({"type": "item.completed", "item": {"type": "agent_message", "text": '{"response":null}'}}), flush=True)
print(json.dumps({"type": "turn.completed", "usage": {"input_tokens": 1, "cached_input_tokens": 0, "output_tokens": 1}}), flush=True)
