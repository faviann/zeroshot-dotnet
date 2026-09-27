#!/usr/bin/python3
"""Test-owned Codex JSONL producer for the local controller; native owns execution and OECP."""
import json
import pathlib
import sys
import time

if sys.argv[1:] == ["--version"]:
    print("codex-cli 0.153.4")
    raise SystemExit(0)

# Installed at <witness>/controller-fake/codex; native passes only a minimal environment.
directory = pathlib.Path(__file__).resolve().parent.parent
sys.stdin.read()
print(json.dumps({"type": "thread.started", "thread_id": "controlled-controller"}), flush=True)
(directory / "controller-ready").write_text("ready")
deadline = time.monotonic() + 60
while not (directory / "controller-release").exists():
    if time.monotonic() >= deadline:
        raise SystemExit("controlled controller gate timed out")
    time.sleep(0.05)
print(json.dumps({"type": "turn.started"}), flush=True)
print(json.dumps({"type": "item.completed", "item": {"type": "agent_message", "text": '{"response":null}'}}), flush=True)
print(json.dumps({"type": "turn.completed", "usage": {"input_tokens": 1, "cached_input_tokens": 0, "output_tokens": 1}}), flush=True)
