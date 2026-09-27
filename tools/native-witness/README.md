# Stock native Linux x64 witness

Run `tools/native-witness/run.sh` from any directory on Linux x64 with .NET 10,
Bash, curl, tar, sha256sum, shuf and ripgrep. It downloads the pinned official stock
release, verifies the archive and extracted executable, and launches a loopback
native target with a fresh state directory, empty asset working directory and
scrubbed environment. No user assets, credentials, provider tools or submitted runs
are used. The library consumer only performs discovery; process management belongs
to this developer witness.

The harness writes every artifact to a fresh `/tmp/zeroshot-native-witness.*`
directory and prints its path on success or failure. It terminates the native
process on exit and retains artifacts for inspection. The port is chosen randomly;
`ZEROSHOT_WITNESS_PORT` can select an available unprivileged port. A bind collision
fails explicitly. `ZEROSHOT_WITNESS_ARCHIVE` can supply a cached archive, which still
must pass the pinned checksum check. Python is not required.

Pinned provenance:

- Native version: **10.9.0**.
- [Source revision](https://github.com/the-open-engine/zeroshot/tree/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa): `75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa`.
- [Release](https://github.com/the-open-engine/zeroshot/releases/tag/v10.9.0) names that source revision in its `target_commitish`.
- Linux x64 musl archive SHA-256: `ca7305a0a165f3909481ccfcccce367d3bc2c40a9ab65760f6d6cad2a38d002d` (official release asset digest / `SHA256SUMS`).
- Actual extracted `zeroshot` executable SHA-256: `f39952b98652301db58a89c4132a0476ae4ec570749b5945cc5200c2d22fad94` (measured and independently rechecked each run).

These are release provenance and local execution evidence, not remote executable
attestation or a reproducible-build claim. Discovery itself publishes no product
version or checksum.

The harness currently proves GET discovery from the real executable, HEAD 404 on
the fixed discovery route, and typed direct discovery from a freshly copied external
consumer using a locally packed `Zeroshot.Client` package and isolated package cache.
It retains provenance, native logs, raw discovery, HEAD headers, package/restore logs
and consumer output. Later native binding issues should extend this same harness;
this witness claims no run submission, OECP, hosted OAuth, or other binding evidence.

The wire and dispatch authorities are
[`native_v2_target.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs),
[`transport.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target_authority/transport.rs),
and [`serve.rs`](https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/serve.rs).
