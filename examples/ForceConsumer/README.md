# Packed native force consumer

This external consumer references `Zeroshot.Client` as a package. Run it through
[`tools/native-witness/run.sh`](../../tools/native-witness/README.md), which copies
the project outside the repository and restores from the freshly packed feed with
an isolated cache.

It admits a second controlled worker run whose provider never passes its first
gate, waits for native status to report the active execution, opens a watch from
that status cursor and sends one `Runs.ForceAsync` request. It records the
acknowledged phase exactly as native returns it. Durable watch history and a later
status query supply the separate `stopping` and `force_stopped` terminal evidence.
A repeated force of the terminal run is acknowledged, and an unknown run is a
rejected `NOT_FOUND` attempt.

The consumer performs no process or provider management and never retries force.
