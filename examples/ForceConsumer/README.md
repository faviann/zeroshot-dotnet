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

It then admits a third controlled run through the SDK's `SubmitAsync`, waits for
its active execution and stops it with `Run.ForceStopAsync`: one force, then the
acknowledgement's terminal result or the common wait. The result must be
`force_stopped`, as must a later status. `ForceStopAsync` with a zero wait budget on
the already terminal first run must return its acknowledgement's result without
observing, and `Run.ForceAttemptAsync` on the stopped run returns the acknowledged
lower attempt. The `sdk` object in `force.json` records the result's evidence kind
and cursor, which shows the path native's reply took.

The consumer performs no process or provider management and never retries force.
