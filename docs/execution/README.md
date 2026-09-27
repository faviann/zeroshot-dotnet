# Internal operation execution

`Zeroshot.Native.Execution` is the internal resource seam for HTTP discovery (#16) and
future OECP bindings (#18). The public HTTP client maps its failures into typed
`NativeHttpException` metadata; this seam adds no SDK workflow. One
`OperationExecutor` belongs to one native client; bindings share it rather than
creating one per call. Operations execute once, with no retries or mutation
outcome classification. `Zeroshot.Native.Observations` supplies the shared
transport-independent observation delivery seam described below.

The defaults in `OperationLimits` are configurable and validated: connect 10 s,
whole unary operation 30 s, cleanup 5 s; 32 requests including four reserved
control slots; 18 registered OECP connections; eight HTTP connections per origin;
4 MiB HTTP / 1 MiB OECP requests; 8 MiB responses and messages; 64 KiB diagnostics.
All limits are positive and finite; reserved slots must leave ordinary capacity.
Time values must fit the runtime timer's finite range. A connect budget larger
than its enclosing budget is allowed and clamped to the remaining time.

The request deadline starts before size checks and immediate admission. Ordinary
requests use at most `ConcurrentRequests - ReservedControlRequests` slots;
control operations can use any available slot, up to the total. Saturation fails
explicitly without queueing. An optional remaining enclosing budget further
bounds the complete unary operation, including response-body reads. The injected
`TimeProvider` supplies monotonic elapsed time and deadline timers.

Bindings must:

- Pass a fixed operation identifier and the final encoded request size, including
  transport envelope fields. Mark control operations explicitly. Binding-specific
  request/response/message limits can only reduce the configured ceilings.
- Keep the entire unary send, body read, parse and validation inside the execution
  callback. Use `ReadResponseAsync` for bounded body accumulation; it reads at most
  the ceiling plus one probe byte and rejects oversize explicitly. Use
  `CheckMessageSize` while accumulating frames, before retaining bytes beyond the
  limit. These limits measure encoded data, not exact CLR heap usage.
- Use `ConnectAsync` for connection setup. Its non-generic callback stores resources
  in adapter-owned state; no resource is returned through a cancellable wait.
  Cleanup must own late allocations too: abort establishment or await it and close
  the eventual resource. Nested connection tasks remain counted until they settle.
- Register each owned OECP connection and each HTTP connection with the client,
  holding the returned idempotent lease until that resource is actually closed.
  HTTP scheme, host and effective port identify an origin; paths, credentials and
  queries do not. The HTTP adapter must apply the same bound to its actual handler
  pool; registering requests is not a substitute for limiting HTTP connections.
- Supply asynchronous cleanup that releases transport resources independently of
  caller cancellation. Callbacks must not synchronously block the invoking thread.
  Cleanup has its own finite budget and cannot replace an already validated result
  or the original failure. Client disposal cancels active calls and refuses new
  calls; transport owners remain responsible for closing their registered resources.

A successful callback return means the complete response is validated. That result
survives subsequent cancellation, disposal or cleanup failure. Adapters that
capture an acknowledgement must return it immediately rather than inserting more
cancellable work. A failure carries no inferred mutation acceptance or rejection;
those bindings will retain the operation's actual evidence.

Caller waits are bounded even if an injected callback ignores cancellation. Its
request reservation remains held until execution, nested connects and cleanup
settle, preventing unbounded abandoned work. Cleanup timeout does not assert that
an uncooperative resource has been physically closed. Detached task exceptions
are observed and never used as raw diagnostic messages.

`OperationFailure` formats only a binding-owned operation name, locally generated
correlation ID, transport, stage and failure kind. Arbitrary foreign exceptions
are replaced without retaining their messages or inner exceptions. No URL,
credentials, payload or remote error body enters default formatting. Raw
inspection requires `CaptureRawDiagnostics`; `ExportRawDiagnostic` returns a copy
of bounded bytes and default formatting stays safe. Oversized diagnostic input
produces an explicit size failure, without silent truncation. These are internal
failure facts for future public exception/evidence mapping, not new public errors.

Deterministic injected tests cover deadlines, response reads, saturation, reserved
control capacity, connection limits, cancellation/disposal/cleanup, uncooperative
callbacks, exact byte boundaries and diagnostic safety. They establish no live
native, six-platform, or production HTTP/OECP adapter conformance claim.

## Observation delivery

Each `NativeClient` owns one `ObservationDelivery`. Forthcoming OECP (#21), hosted
NDJSON and dashboard SSE bindings must open their generic
`ObservationQueue<TRecord,TPosition>` through that owner, so all streams share
the same admission and aggregate byte budget. This is an internal seam; public
`NativeSubscription` and wire observation bindings remain unimplemented.

`TransportOptions` exposes these positive, configurable limits (binary units):

| Option | Default |
| --- | --- |
| `MaxConcurrentSubscriptions` | 16 |
| `MaxQueuedObservationRecords` | 256 per stream |
| `MaxQueuedObservationBytes` | 8 MiB per stream |
| `MaxAggregateObservationBytes` | 32 MiB per client |

Record and both byte ceilings apply independently. Admission and record
reservations are immediate: there are no queued producers or admission waiters.
An admission failure is explicit. `TryEnqueue` atomically reserves both stream
and client bytes, or ends only the affected stream with a typed local record,
stream-byte or aggregate-byte overflow failure. Overflow is thrown to the
producer and surfaced to the consumer after already validated records drain in
order. A later enqueue returns false after a stop, without accepting that record.
An oversized first record reserves no bytes. Failure formatting includes only
the local failure kind and correlation ID, never a cursor or record payload.

`LastReceivedPosition` includes the validated record rejected by an overflow;
`LastBufferedPosition` includes only accepted records; `LastDeliveredPosition`
advances only when a consumer's `MoveNextAsync` hands off a record. Null positions
do not advance a cursor. Positions are opaque and never compared or interpreted.
The delivered position remains available after failure/disposal and reflects
records drained after overflow, rather than an overflow-time snapshot. There is
no processing checkpoint, persistence, resumption or automatic reopen.

One consumer may call `ReadAllAsync`. Cancelling its token also detaches while
it is idle between reads. Breaking enumeration, queue disposal, opening-token
cancellation and client disposal discard undelivered records, release their
encoded bytes and wake a waiting reader. Discarded records never advance the
delivered position. Normal completion or a binding failure retains queued data
and its budget until delivered or explicitly discarded. An abandoned consumer
without cancellation/disposal can retain only its bounded queue; the owning
client can dispose it without waiting for that consumer.

Bindings must:

- Apply operation/frame limits and validate identity, shape and the complete
  record before enqueueing. Supply the actual retained encoded byte length,
  including any retained envelope, and the record's available cursor. Do not add
  a second unbounded adapter queue or launch a task per record. These counts
  measure encoded queued data, not total CLR heap usage or caller-held records.
- Observe the single `StopRequested` task in their existing receive/cleanup
  lifecycle. All signal continuations run asynchronously; no binding or consumer
  callbacks run under the accounting lock. No extra per-stop task or callback
  runner is created by this seam.
- On stop, detach/cancel the observation and perform transport cleanup under
  the existing finite cleanup budget. `StopReceiving` ends reads (after draining)
  as soon as the binding learns of normal closure or failure, independently of
  cleanup that may still be settling. `Complete` acknowledges that the producer
  and its cleanup have settled, on normal completion and on every failure or
  cancellation path. Supply only already sanitized, binding-owned errors.
  The queue does not close sockets, send remote cancellation or stop a run.
- Keep a subscription reservation until both producer completion and release
  of its queued records. Cancelling/discarding immediately returns queued bytes,
  but an unsettled producer retains its slot, preventing cancel/reopen loops
  from accumulating abandoned producer work. Physical connection and cleanup
  ownership remain subject to the operation seam's separate reservations;
  a cleanup deadline never proves an uncooperative resource was closed.

The observation budgets do not consume request/control capacity. Deterministic
`ObservationDeliveryTests` cover default and configured ceilings, competing
record types, exact byte recovery, slow and abandoned consumers, cancellation,
disposal, producer settlement, ordered draining, cursor separation and a real
`NativeClient` control request while observation and ordinary request capacity
are congested. These tests establish internal delivery behavior, not native
wire interoperability or implemented OECP/NDJSON/SSE observation support.
