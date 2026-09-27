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

## Browser boundary

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
mapping, refusals and contracts with controlled peers. The
[native witness](../../tools/native-witness/README.md) runs `examples/DashboardConsumer`
against the stock UI mount of a direct target.
