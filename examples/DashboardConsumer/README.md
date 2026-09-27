This standalone .NET 10 consumer references the packed `Zeroshot.Client` package and
reads a direct target's stock browser UI mount through `NativeClient.Dashboard`.
Arguments are the target origin, which must equal its `--public-origin`, and a
directory holding the harness-generated complete `graph.json` and `runtime.json`.

It checks the root and `/ui` redirects without following them, the index and every
asset it references with matching HEAD lengths, a bare 404 for a missing asset,
and the complete bootstrap catalog and its HEAD. It validates the complete asset,
provokes native's 422 for an unbound draft and a misplaced authoring edit, applies a
failure-reason edit, and adds then removes a run input. It then discovers the target
on the same client. A consumer-owned handler, which the library does not offer,
injects a foreign Origin and a `text/plain` body to prove native's typed
`origin_rejected` and `json_required` refusals. `tools/native-witness/run.sh` runs it
and checks that the UI profile store stays empty.
