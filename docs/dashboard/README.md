# Browser dashboard routes

`native.Dashboard` binds native's browser UI router at native 10.9.0 / source
`75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa` (`profile_ui.rs`, `profile_ui/server.rs`,
`workspace.rs`). Construct the native client with the UI's configured public origin:
`zeroshot target serve --public-origin` for a direct target's UI mount, or the
loopback origin of `zeroshot ui`. A target built without UI support answers these
paths from its fixed router, usually as a `request.not_found` refusal.

```csharp
await using var native = NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri("http://127.0.0.1:4173/") });
var bootstrap = await native.Dashboard.GetBootstrapAsync();
var template = bootstrap.Templates.Single(t => t.Id == "single-worker:none");
var draft = await native.Dashboard.AuthorAsync(new DashboardAuthoringRequest
{
    Graph = template.Graph, Runtime = editorRuntimeJson,
    Action = new FailureReasonAuthoringAction { Terminal = new("worker_failed"), Reason = new("worker_gave_up") }
});
```

| Native route | Method | Result |
| --- | --- | --- |
| `GET`/`HEAD /`, `/ui` | `GetRootAsync`, `HeadRootAsync`, `GetUiAsync`, `HeadUiAsync` | `DashboardRedirect` (native 307, `Location: /ui/`) |
| `GET /ui/`, `/ui/{*asset}` | `GetIndexAsync`, `GetAssetAsync(path)` | `DashboardContent`: media type, charset and bounded body copy |
| `HEAD /ui/`, `/ui/{*asset}`, `/ui/api/bootstrap` | `HeadIndexAsync`, `HeadAssetAsync(path)`, `HeadBootstrapAsync` | `NativeHeadResult`: status, media type and advertised length; no body |
| `GET /ui/api/bootstrap` | `GetBootstrapAsync` | `DashboardBootstrap` |
| `POST /ui/api/validate` | `ValidateAsync` | `DashboardValidation` (`{"valid":true}`) |
| `POST /ui/api/authoring` | `AuthorAsync` | `DashboardAuthoringDraft` |
| `POST /ui/api/data` | `TransformDataAsync` | `DashboardDataDraft` |
| `GET /ui/api/profiles` | `ListProfilesAsync` | `RunProfileListResult` (user scope) |
| `GET /ui/api/profiles/{name}` | `GetProfileAsync(name)` | `DashboardProfile`: `RunProfile` and its revision |
| `HEAD` of the two profile routes | `HeadProfilesAsync`, `HeadProfileAsync(name)` | `NativeHeadResult` |
| `POST /ui/api/profiles` | `SaveProfileAsync(request, workspaceId)` | `NativeAttempt<DashboardProfile>` |
| `GET /ui/api/runs{?after}` | `ListRunsAsync` | `RunHistoryList` |
| `GET /ui/api/runs/{id}` | `GetRunAsync` | `RunDefinition` |
| `GET /ui/api/runs/{id}/history{?after}` | `GetHistoryAsync` | `HistoryPage` |
| `GET /ui/api/runs/{id}/events{?after}` | `OpenRunEventsAsync` | `DashboardRunEvents`: one SSE observation |
| `HEAD` of the four run routes | `HeadRunsAsync`, `HeadRunAsync`, `HeadHistoryAsync`, `HeadRunEventsAsync` | `NativeHeadResult` |

Redirects are results for the two redirect routes and are never followed. Any other
route that answers with a redirect fails as `Redirect`. Static content must be 200
with a Content-Type and is returned as bytes, never parsed. Asset paths are relative
literal segments such as `assets/app.js`; empty, dot, escaped, query or fragment
spellings are rejected before dispatch. A missing asset is native's bare 404:
`HttpStatus` with no problem.

## Contracts

`DashboardBootstrap` holds `version` 1, the materialized templates (`GraphSpec` plus
`NodeRuntimeBinding` defaults), worker options tagged by `runtimeKind`, native's live
`runtimeSchema` document and the workspace. Workspace kind is `local` or `target`;
its ID is native's canonical workspace UUID. The agent worker's `runtimeBinding` is
native's blank editor binding (`model: ""`), so it stays JSON; git-delivery workers
carry a typed `GraphNode` and `NodeRuntimeBinding`.

Validation takes a typed `GraphSpec` and `RuntimePlan` and runs native profile
admission. Authoring takes a typed graph, an uninterpreted runtime draft and a
`failure_reason`, `complete` or `protect` action. Data edits take arbitrary draft
graph/runtime JSON, because native accepts incomplete drafts, and a `connect`,
`remove_input`, `map_collection`, `run_input_field` or `remove_run_input` action with
`run_input`, `node_output`, `map_item` or `loop_input` sources. Paths are `FieldName`
lists, and field types reuse `PayloadType`. Native publishes no schema for these
serde DTOs. The client models them as strict types and validates nested execution
definitions against the pinned generated schema. No transformation admits, saves or
runs anything.

## Profiles

The profile routes read and write the UI host's own user-scope store: the `--storage`
directory of a direct target's UI mount, or the local profile store of `zeroshot ui`. They
are not the hosted [run-profile](../http/README.md#hosted-run-profiles) routes and have no
scope, default or delete operation. Results reuse the hosted `RunProfile` and
`RunProfileListResult` types.

```csharp
var workspaceId = (await native.Dashboard.GetBootstrapAsync()).Workspace.Id;
var current = await native.Dashboard.GetProfileAsync(new RunProfileName("review"));
var saved = await native.Dashboard.SaveProfileAsync(new DashboardProfileSaveRequest
{
    Name = new("review"), Graph = graph, Runtime = runtime, ExpectedRevision = current.Revision
}, workspaceId);
if (saved.Failure is NativeHttpException { UiProblem.Code: "profile_conflict" }) { /* reload; nothing was written */ }
```

A save is a compare-and-swap. Native admits the graph and runtime, then under its store lock
compares `expectedRevision` with the stored profile's current revision. An omitted
revision only creates: an existing name conflicts. The revision is native's opaque string
(a digest of the whole stored profile, including `isDefault`), sent and returned verbatim.
`workspaceId` is sent verbatim as the single `X-Zeroshot-Workspace` header. Native answers
`workspace_changed` when it does not equal the host's workspace ID or the store was
replaced since the host started.

Each save is sent once and returns attempt evidence with the operation `dashboard.saveProfile`,
the origin and a correlation ID:

| Native answer | Outcome |
| --- | --- |
| 200 `{profile,revision}` | `Acknowledged` |
| 409 `workspace_changed` or `profile_conflict`, 422 `invalid_profile` | `Rejected`: answered before the write |
| 403 `origin_rejected`, 415 `json_required`, 503 `server_stopping` | `Rejected`: the browser boundary answered |
| 500 `profile_store_error`, any other status or code, lost or malformed reply, deadline | `Unknown`: the write may have happened |
| Cancelled or refused before dispatch | `NotSent` |

The native `{code,message}` stays on `Failure.UiProblem`. An `Unknown` save is never resent;
read the profile to learn its current revision. There is no automatic conflict resolution.
Native reports a missing profile on `GetProfileAsync` as 500 `profile_store_error`, not 404.

## Run history and events

Native serves the run routes with the same handlers as the discovered direct-target
[run history](../http/README.md#run-history), so they reuse its records, 4 MiB list /
8 MiB detail and page / 64 KiB problem bounds, host contract checks and `HistoryProblem`
categories. Run IDs and the list position must be canonical UUIDv7 and cursors canonical
`v2:<sequence>`, checked before dispatch. No discovery document is needed: the routes are
fixed on the UI origin. An omitted page cursor is omitted on the wire and validated
from `v2:0`.

```csharp
await using var events = await native.Dashboard.OpenRunEventsAsync(runId, lastEventId: retainedCursor);
await foreach (var entry in events.ReadAllAsync(ct))
{
    if (entry is DashboardHistoryPageEvent { Page: var page }) { /* page.NextCursor was the SSE id */ }
    else if (entry is DashboardHistoryErrorEvent error) { /* native {code,message}; native ends the stream */ }
}
var closed = await events.Completion; // ServerClosed is closure evidence, not run success
```

`OpenRunEventsAsync` sends `after` as the query and `lastEventId` as one `Last-Event-ID`
header, each only when supplied; native resumes after `Last-Event-ID` in preference to
`after`, and the binding validates the first page against the same choice. It returns once
native answers 200 `text/event-stream`. A refusal before that is a `NativeHttpException`
with `UiProblem`, as for the other routes (`run_not_found`, `invalid_cursor`,
`origin_rejected`). The unary deadline and request slot cover only that exchange.

Native emits `history` events whose id is the page's `nextCursor` and whose data is the
whole page, `history_error` events with `{code,message}` data and no id, and a `:`
keepalive comment every 15 s. Each `history` page must carry its own matching id and
continue the previous page (or the resume cursor) under the run-history page rules; any
other event, a mismatched or missing id, a gap or rewind, or malformed data ends the
observation as a `Protocol` failure after already accepted events drain. `history_error`
is delivered in order as `DashboardHistoryErrorEvent` and does not advance
`LastDeliveredCursor`. The reader accepts LF, CR or CRLF line ends split anywhere, a
leading BOM and multi-line data. One event's data may hold up to
min(`TransportOptions.MaxOecpMessageBytes`, 8 MiB), matching native's page bound; its
other bytes since the last dispatch (field names, id, event type, comments, line ends)
share a fixed 1 KiB allowance. Exceeding either is a `SizeLimit` failure.

Events go through the client's shared observation delivery: subscription admission
happens before the request is sent, and the per-stream record/byte and aggregate budgets
apply. A consumer too slow for those budgets gets an explicit record or byte-limit
failure after the queued pages drain. `Completion` reports `ServerClosed` when native ends
the stream on an event boundary, `UnexpectedDisconnect` for a read failure or a stream
ending inside an event, `LocalFailure` for validation, size or overflow, and `Disposed` or
`Cancelled` for local closure. Disposal, the opening token or client disposal close only
this response. There is no automatic reopen: pass `LastDeliveredCursor` as `lastEventId`
to open a new observation. An open stream holds one of the origin's HTTP connections for
its lifetime, because UI-routed connections are never reused.

This type is separate from `NativeSubscription<TEstablishment,TEvent>`: an SSE response
has no establishment result, subscription ID, close notification or remote cancellation,
so the HTTP response is the whole subscription. It reuses the same completion, origin and
failure types.


Every request uses the configured origin's exact Host and sends no Origin or
Sec-Fetch-Site header, which native accepts. POSTs send `application/json` and are
limited to native's 2 MiB body before dispatch. Every request sends `Connection: close`:
a direct target hands all later requests on a UI-routed connection to its UI router,
so a pooled connection would misroute later target calls.

Native UI refusals are `{code,message}` problems exposed as
`NativeHttpException.UiProblem`, distinct from `TargetHttpProblem`: 403
`origin_rejected` (Host, Origin or Sec-Fetch-Site mismatch), 415 `json_required`,
503 `server_stopping`, 422 `invalid_profile` (malformed drafts or failed admission)
and 500 `profile_store_error`. Messages can carry admission detail and never appear in
default exception formatting. Native strips HEAD response bodies, so a HEAD refusal carries only its status,
with no `UiProblem`.

`DashboardTests.cs` and `DashboardContractTests.cs` cover the request shapes, result
mapping, refusals and contracts with controlled peers. `DashboardProfileTests.cs` covers the
profile wire shapes, save outcome classification and unretried lost replies. `DashboardHistoryTests.cs` covers
the run routes and SSE framing, validation, bounds, slow consumers and closure. The
[native witness](../../tools/native-witness/README.md) runs `examples/DashboardConsumer`
against the stock UI mount of a direct target, including profile create, update, conflicts
and a two-save race in its isolated store, and `examples/ObservationConsumer` streams
an active run's pages through its terminal event. `history_error` events and keepalive
comments are fixture-only: stock native emits them only when a later page read fails or
observation ends incomplete, and after 15 idle seconds.
