# Packed native controller consumer

This external consumer references `Zeroshot.Client` as a package. Run it through
[`tools/native-witness/run.sh`](../../tools/native-witness/README.md), which copies
the project outside the repository and restores from the freshly packed feed with
an isolated cache.

It receives the socket path of an existing stock local-run controller. Through
`OecpConnection.ConnectUnixAsync` it initializes, reads native's empty cluster get,
lists exactly the controller's one run and waits for its controlled worker to be
active. A foreign run ID is `NOT_FOUND` for both status and force; the force attempt
is `Rejected` and identified by the socket's file URI. Resume and workspace discard
of the owned run are `Rejected` with native `INVALID_PHASE`. `CancelRequestAsync` is
refused locally because native NDJSON does not implement `$/cancelRequest`.

It then connects its own Unix socket and binds it with `OecpConnection.FromStreamsAsync`.
After that connection is disposed, the borrowed stream is still open and a second
connection reuses it for status, watch and log subscriptions. The consumer releases
the provider gate. The stock controller stops serving at terminal state, so each
subscription ends with either the server's close or an observed disconnect, and
the connection ends with a transport failure rather than a completion.

The consumer performs no process or provider management and never reopens a controller.
