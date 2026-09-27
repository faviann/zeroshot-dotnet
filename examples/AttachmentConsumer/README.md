# Packed native attachment consumer

This external consumer references `Zeroshot.Client` as a package. Run it through
[`tools/native-witness/run.sh`](../../tools/native-witness/README.md), which copies
the project outside the repository and restores from the freshly packed feed with
an isolated cache.

It admits the test-owned graph and source, reads the exact active execution from
native status, then attaches. The controlled provider emits output only after the
consumer has received `working`; it settles only after the consumer receives that
live output. Native owns execution, event projection, subscription close and error
responses. The consumer verifies `settled`, cursorless completion, native `GONE`
for the now-inactive execution and `NOT_FOUND` for an unknown execution. A separate
status query supplies the successful graph result.

The consumer performs no process or provider management. The shell witness owns
the target, private mount namespace and controlled provider/source fixtures.
