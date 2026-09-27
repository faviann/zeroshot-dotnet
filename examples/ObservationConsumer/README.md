# Packed native observation consumer

`tools/native-witness/run.sh` copies this project outside the repository, restores
the freshly packed `Zeroshot.Client` package into an isolated cache, and invokes it
against stock native 10.9.0 before and after a real target restart.

The `live` phase submits the harness's test-owned preparation request, proves
preexisting history and new watch/log records, and retains exact events and opaque
cursors. Both `live` and `restarted` verify history replay, exclusive cursor boundaries,
server completion, and exact run/source identity. It also reads run history while
the run is admitted and not terminal, the retained terminal event after completion,
and the identical retained page after the restart. The hook intentionally fails before
checkout or provider execution; all observation records come from native itself.

The consumer connects to an existing target and has a 30-second budget. Only the
shell harness launches targets, grants setup privileges, or restarts processes.
