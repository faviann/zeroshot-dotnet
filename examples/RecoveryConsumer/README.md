# Packed native recovery consumer

This external consumer references `Zeroshot.Client` as a package. Run it through
[`tools/native-witness/run.sh`](../../tools/native-witness/README.md), which copies
the project outside the repository and restores from the freshly packed feed with
an isolated cache.

It admits a controlled worker run whose provider fails after checkout. Native records
`worker_failed` and retains a recoverable workspace with its connection requirements.
The consumer pages that run's checkpoints, resumes it with the selection omitted and
fresh connection values, and checks the source/successor links in both statuses. A
repeated resume of the source is an unknown `INTERNAL_ERROR` that admits nothing. It
then resumes the successor from its worker entry checkpoint and discards the second
successor's workspace twice. The other refusals are the ones a stock direct target can
produce: unknown runs, noncanonical IDs, succeeded, force-stopped and discarded sources.

The consumer performs no process or provider management and never retries a mutation.
