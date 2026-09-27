# Packed native private bootstrap consumer

This external consumer references `Zeroshot.Client` as a package. Run it through
[`tools/native-witness/run.sh`](../../tools/native-witness/README.md), which starts a
private-mode target with isolated test-generated key and capability material, copies
the project outside the repository and restores from the freshly packed feed with an
isolated cache.

It discovers `private_capability` and prepares its own AES-256-GCM envelopes, because
the client performs no bootstrap cryptography. It then sends three
`Private.BootstrapAsync` attempts: a wrong-key envelope rejected with 400
`request.invalid`, the valid envelope acknowledged with an empty 204, and the same
envelope again rejected with 404 `request.not_found` after native closed the bootstrap.
It never retries an attempt.

With the bootstrapped capability as `PrivateCapability` control credentials, it then
admits the harness's inspection request and waits until the private history
definition reports it finished. It reads the complete page and the empty page after
it through `Private.GetHistoryPageAsync`, and the run's operator diagnostics as
received. An unknown run has an empty diagnostics list and `run_not_found` history.
A cursor ahead of the run gets `invalid_cursor`. A wrong capability gets 401
`request.unauthorized` on all three exports. The `exports` object in
`private-bootstrap.json` retains this evidence.

Run it with the target origin, its material directory and the harness's
`request.json`.
