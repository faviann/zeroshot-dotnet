This standalone .NET 10 consumer references the packed `Zeroshot.Client` package.
`tools/native-witness/run.sh` runs it against the stock native target after the
inspection and submission consumers have admitted runs there.

It reads the discovered run-history capability through the direct target UI mount:
the list and a list resumed after its first run, the inspection run's definition
compared with the admitted request, its complete page and the empty page after it,
and HEAD for all three routes. It checks `run_not_found` for an unknown UUIDv7 run,
`invalid_cursor` for a cursor ahead of the run, and a status-only HEAD refusal.
It also calls the three private operator exports with a private capability. This
direct target is in the wrong mode, so each one must be refused with 404
`request.not_found` and no history category.
Finally it acquires an OECP session with the same client, which fails with 404 if
a history connection were reused for control traffic.

Run it with the target origin and a directory containing the harness's
`request.json`.
