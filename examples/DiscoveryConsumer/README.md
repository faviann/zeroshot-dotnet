This standalone .NET 10 consumer references the packed `Zeroshot.Client` NuGet
package, with no project reference or SDK build assertion. It reads discovery and
acquires direct OECP sessions, both without a run selector and with a canonical
UUIDv7 selector, from an existing target. It verifies the same-authority `ws`/`wss`
endpoint and absence of session bearers. Acquiring a direct session does not submit
or create the selected run. The consuming process needs neither Python nor a native
executable. `tools/native-witness/run.sh` packs the library, copies this project to
a fresh directory, restores from an isolated package cache, and runs it against
an independently launched stock native target.

For an existing direct target, pack to a local feed, restore this project using
that feed plus nuget.org for dependencies, then run with the target origin as the
only argument. Do not set `--no-restore` until the local package has been restored.

The witness also expects the one test-owned run in
`tools/native-witness/inspection-request.json` to have been admitted by the harness.
It initializes OECP, checks capabilities and empty cluster get, lists and inspects
that exact run/source through terminal status, and verifies the native numeric/domain
unsupported-protocol error. The harness owns raw HTTP admission; this consumer does
not provide the later production submission binding. A native terminal runtime failure
is valid inspection evidence, not a claim of successful provider execution.
