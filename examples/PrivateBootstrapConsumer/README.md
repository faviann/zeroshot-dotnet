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
