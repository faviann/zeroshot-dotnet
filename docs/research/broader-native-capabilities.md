# Broader SDK capabilities through native 10.9.0

Research for [Establish native access paths for broader SDK capabilities](https://github.com/faviann/zeroshot-dotnet-sdk/issues/12), completed 2026-09-27. All native findings below use **10.9.0, commit `75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa`**. This is source inspection, not a live deployment or provider-run witness. Inclusion, API shape, packaging and transport selection remain decisions for [Choose the first SDK capabilities and ownership boundary](https://github.com/faviann/zeroshot-dotnet-sdk/issues/4).

## Findings that answer the effort questions

Broader execution does not require reimplementing native execution. Existing public commands already cover local and named-target runs, templates, observations, profiles, connections, registration, hosted login and direct-target serving. A .NET process adapter can reuse those commands. Its additional work is executable availability, argument/file/environment handling, bounded output, cancellation, process lifetime and mapping native results. The estimate that this is less work than implementing native controllers, materialization or OAuth again is an engineering inference from the existing seams. [Command tree][parser], [backend selection][main-routing].

Native templates are particularly accessible: `template list` and `template show` emit JSON, and `run --template ...` owns materialization. There is also an existing **prepare-only graph/runtime route**: `profile set` materializes and validates a profile, returns it as JSON, and persists it; `profile show` reads the complete graph/runtime back. A new upstream export API is therefore **not required merely to produce an execution asset**. A stateless one-command export of the complete resolved submission is a different promise: `run --validate-only` returns only `{"valid":true}`. [Static template output][template-output], [profile preparation][profile-execution], [profile storage/result][profile-store], [validation-only output][submission].

The feasible split is direct HTTP/OECP for Broodling's caller-owned exact submissions and optional native-executable adapters for broader workflows. That split is an option, not a selected architecture. It preserves an executable-free DirectTarget path while making native conveniences available without porting their internals. [HTTP envelope][http-contract], [named-target submission][target-submit], [native routing][main-routing].

## Execution and management are separate capabilities

| Capability | Existing reusable interface | Practical boundary and relative work |
| --- | --- | --- |
| Connect to an existing DirectTarget | Discovery and HTTP creation/session requests; OECP run operations | No native executable or local Git checkout is required by the HTTP request. The caller supplies graph/runtime, input, exact source and identity, plus separate connection values. Protocol implementation and conformance tests remain SDK work. [HTTP contract][http-contract] |
| Run locally | `run` without `--target`; local `list/status/watch/logs/attach/force-stop` | Native launches its own one-run controller. The run operates as the invoking user in the same Git workspace; it needs Git, an attached branch, a GitHub origin and the selected harness/runtime dependencies. Native owns controller startup, storage, native IPC and reconnect. Wrapping the public executable avoids treating its hidden controller mode as an SDK contract. [Local backend][local-backend], [local workspace semantics][local-composition] |
| Run on an existing named direct or hosted target | `run --target NAME` with exact files, a template or a profile | Native handles preparation and target routing. Even explicit repository/branch/revision overrides still require an invoking Git worktree. It allocates the proposed run ID internally; this is not a drop-in replacement for Broodling's preallocated ID/no-checkout path. [Source resolution][named-source], [request preparation][submission], [submission routing][target-submit] |
| Register a target | `target add NAME --url ORIGIN [--direct]` | Performs discovery, then writes local target configuration. It connects to an already existing target; it does not launch one. The public target command group has add/login/serve, not a general target inventory/update/delete API. Small command wrapper; preserve native errors and store ownership. [Registration][target-registration], [target command group][parser] |
| Launch a direct target | `target serve --listen ... --public-origin ... --storage ...` | Starts a long-running native server around a ledger/controller and installed Git/harness programs. It logs readiness text to stderr and handles termination signals. A wrapper needs process ownership, readiness/failed-start detection and shutdown behavior; registration alone does none of this. [Server lifecycle][serve], [server dependencies][hosting] |
| Hosted-target login and use | `target login NAME`, then named-target commands | Existing OAuth device authorization, token refresh and native credential storage can be reused. The public login flow displays a URL/code on stderr and waits for human authorization; it does not expose a structured JSON device-code callback. An interactive CLI can relay this; a headless application login API needs a deliberate interface or separate OAuth implementation. [Login][login], [device-code notifier and stores][credentials] |
| Provision hosted infrastructure | No operation in the selected public command tree or target control surface | Hosting an existing server, registering its URL and signing into it are supported. Creating cloud resources, accounts, DNS/TLS or deployments is a separate integration, not a hidden consequence of these wrappers. This is a bounded inference from the command/control inventory. [Command tree][parser], [HTTP control operations][control], [server lifecycle][serve] |

Named detached submission can emit a source/dirtiness record followed by a receipt; foreground submission then follows watch events. A process adapter must parse operation-specific records, distinguish process failure from a failed terminal run, and preserve the 10.9.0 interruption rule: before submission starts, interruption cancels; once it starts, native awaits its receipt/error before detaching. [Submission and interrupt handling][submission], [foreground output/outcome][foreground].

## Templates and complete asset preparation

The complete built-in catalog is:

| Template | Supported delivery selections |
| --- | --- |
| `single-worker` | None |
| `software-change` | None, push, pull request, merge |
| `auto-research` | None, push |

The catalog and delivery validation are explicit in the pinned native implementation. [Catalog][catalog].

Three different conveniences should stay distinguishable:

1. **List or inspect templates.** `template list` returns names; `template show TEMPLATE` materializes graph JSON, with supported delivery options. This is a small wrapper and needs neither target nor execution. It does not produce a runtime. [Template output][template-output], [catalog][catalog].
2. **Execute a template.** `run --template TEMPLATE --uniform-runtime-config FILE ...` expands one authored harness/provider/model binding across executable graph nodes and inserts native delivery bindings. Exact runtime files and stored profiles are also supported. Native validates the graph/runtime and input, materializes placement-specific provider access, selects connection values and resolves source. A wrapper can retain all this native behavior. [Runtime arguments][runtime-arguments], [materialization][materialization], [profile resolution][profile-resolution], [submission preparation][submission].
3. **Prepare graph/runtime for direct HTTP.** `profile set NAME --template TEMPLATE --uniform-runtime-config FILE` runs the same graph/runtime materialization and validation, stores it and returns `{"profile": ...}` including graph/runtime. `profile show NAME` returns that profile directly. Neither operation submits a run. Native config/state directories can be isolated, so the temporary profile need not touch the user's normal profiles. [Profile commands][profile-parser], [profile execution][profile-execution], [stored result][profile-store], [configuration paths][profile-path], [state paths][state-path].

For the already-selected stock software-change PR asset, the existing route is an isolated `profile set` using `--template software-change --pr` and caller-authored runtime settings, then retaining only the returned graph/runtime rather than random profile identity metadata. Native inserts its delivery binding; default feedback is `Consider`. This is a source-backed candidate recipe, **not an executed generation witness**. It still needs the selected executable and an asset-generation validation check before adoption. [Profile materializer][profile-execution], [default feedback and delivery binding][materialization], [catalog binding][catalog].

There is one placement detail to preserve. Local profile storage leaves omitted provider connections as authored; remote profile storage applies contained defaults. Actual named runs and the production direct HTTP target also apply contained provider requirements; the latter does so **before computing submission identity**. An asset producer can supply explicit expected connection declarations to make its reviewed runtime complete, rather than assuming that local profile JSON always equals the target's effective runtime. This does not require porting provider defaults into .NET. [Stored provider access][profile-execution], [provider requirements][provider-access], [HTTP admission normalization][hosting-submit].

The profile route exports graph/runtime, not title, input, exact source, submission key, proposed run ID or secrets. Those remain caller-owned parts of the HTTP envelope. There is no public stateless export command in this command tree that returns every resolved submission field; `--validate-only` does not. A requirement for that exact convenience would need a composite helper or an upstream extension, while ordinary template use and isolated asset generation already have viable native routes. [Command tree][parser], [validation output][submission], [HTTP envelope][http-contract].

## Store isolation

| Setting or mechanism | What it controls |
| --- | --- |
| `ZEROSHOT_CONFIG_DIR` | Native profile storage and `targets.json`; target refresh-lock and Linux file-credential locations derive from the target registry's parent directory. Use an absolute private directory. [Profiles][profile-path], [target registry][registry-path], [credential construction][authority-construction] |
| `ZEROSHOT_STATE_DIR` | Local run/controller state and the local connection store. Separate from the configuration directory. [State root][state-path], [local backend][local-backend], [connection store][local-connections] |
| `ZEROSHOT_CREDENTIAL_STORE=file` on Linux | Forces native hosted refresh tokens into its private file backend under the configured credential directory. `auto`/`system` can use the user's Secret Service. Other platforms select the native keyring; changing config paths is not a universal credential sandbox. [Linux selection][linux-credentials], [platform selection][credentials] |
| `target serve --storage` | The launched target's own persistent state; separate from the client's configuration and local-run state. [Server setup][serve] |

No `ZEROSHOT_HOME` reference exists in the selected native `zeroshot/src` tree. For profile-only preparation, isolated config/state directories suffice and no hosted login is involved. These settings do not isolate the user's Git workspace, harness login state or all ambient environment; local execution deliberately uses the invoking user's workspace/harness context. [Local composition][local-composition].

## Observation and adjacent groups

`status` gives current phase and active executions. `watch` streams durable status projections; `logs` streams durable safe log records, optionally filtered by execution. Their cursors are opaque and exclusive, so reconnect supplies the last delivered cursor. `attach` observes one live execution and has no replay, cursor or client-input channel. Known-run reconnection does not require execution attach. The native public CLI already maps these to JSON/NDJSON and implements follow/reconnect loops; a direct .NET implementation must supply bounded subscription framing, cancellation, cursor preservation and close/error handling itself. [10.9.0 observation contract][observation], [CLI durable follow][durable-follow], [attach follow][attach-follow].

Local and direct-target paths use native OECP operations. Hosted mode adds a meaningful difference: native chooses hosted HTTP observations while runs are cloud-owned, uses task OECP while active, and translates the relevant cursor boundary. Attach always requires a live run session and has no retained hosted HTTP fallback. A native wrapper reuses this routing; a direct-only .NET observer should not accidentally promise full hosted behavior. [Named-target observation routing][oecp].

| Adjacent group | Existing surface and scope consequence |
| --- | --- |
| Recovery | `resume`, `checkpoints`, `discard-workspace`; direct/local OECP and advertised hosted routes. Resume creates a successor run, requires fresh credentials and, for native direct-target use, stored authorization for the original connection requirements. This is additional control behavior, not ordinary reconnect. [Recovery CLI][recovery-cli], [recovery routing][oecp], [direct authorization][target-recovery] |
| Environments | Run `--environment`/`--no-environment` cover setup, startup, public variables and hook connections. Local execution rejects setup/startup/hook connections and uses its invoking machine environment. [Runtime arguments][runtime-arguments], [local validation][local-composition] |
| Profiles and connections | Local storage and advertised hosted user/org operations exist. Native direct-target HTTP control rejects remote profile/connection management; a local profile can still be materialized and used to submit to a direct target. [Profile commands][profile-parser], [profile routing][profile-resolution], [direct profile limit][remote-profiles], [direct connection limit][remote-connections] |
| Hosted merge plans | Public `plan validate/submit/status/watch/force-stop` group; native explicitly rejects hosted merge-plan submission on a direct target. Separate workflow breadth. [Command tree][parser], [target control][target-submit] |
| UI, ACP, update | Additional public commands, with their own UI build/lifetime, agent-protocol or executable replacement concerns. They need explicit inclusion if “all” means full command parity. [Command tree][parser], [UI feature handling][main-routing] |

## Relative work and validation obligations

**Small increments after a reliable process runner exists:** template catalog/graph output, profile preparation/CRUD, connection CRUD and registration are bounded command adapters. The asset workflow can compose existing commands. This does not imply zero cost: metadata versus asset extraction, isolated stores, validation failures and secret-free diagnostics need coverage. The supporting native seams are linked above.

**Meaningful but supported additions:** local execution, detached observation, all streaming methods, target-process ownership and hosted login/routing require lifecycle and operating-system coverage. The native engine/authentication already exist; the SDK needs to exercise their public boundaries rather than porting their internals. Relevant checks include receipt parsing, interruption during submission, detached-run survival, process cleanup, replay after disconnect, slow-consumer behavior, unavailable history, live attach termination, target failed startup/shutdown and login cancellation. [Native output/lifecycle][foreground], [stream loops][durable-follow], [attach][attach-follow], [server][serve], [login][login].

**Larger new obligations only if demanded:** an entirely .NET hosted OAuth/control implementation; direct use of private local controller/bootstrap/state details; cloud infrastructure provisioning; or a stateless complete resolved-submission exporter. None is required merely to offer native templates, native local runs and existing-target access through optional executable adapters. This is an engineering inference from the inventory, not a product scope decision.

No binaries were built or executed, no providers contacted, and no infrastructure or tracker state changed during this investigation. The source proves available seams; implementation must validate the chosen executable/OS combinations and Broodling's exact newly generated 10.9.0 asset against the stock target.

[parser]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L27
[main-routing]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/main.rs#L77
[template-output]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/submission.rs#L80
[profile-execution]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/profiles.rs#L56
[profile-store]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/profiles.rs#L165
[submission]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/submission.rs#L59
[http-contract]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_target.rs#L234
[target-submit]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target.rs#L282
[local-backend]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/local.rs#L58
[local-composition]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_local.rs#L1
[named-source]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/named_source.rs#L29
[target-registration]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target.rs#L165
[serve]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/serve.rs#L35
[hosting]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting.rs#L45
[login]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L180
[credentials]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/credentials.rs#L38
[control]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/control.rs#L67
[foreground]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution.rs#L365
[catalog]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_templates/catalog.rs#L17
[runtime-arguments]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser.rs#L413
[materialization]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/submission.rs#L369
[profile-resolution]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/submission/profiles.rs#L18
[profile-parser]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/parser/profiles.rs#L7
[profile-path]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/profiles.rs#L348
[state-path]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/support.rs#L59
[provider-access]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_candidate/provider_access.rs#L40
[hosting-submit]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_hosting.rs#L153
[registry-path]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/registry.rs#L268
[authority-construction]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority.rs#L67
[local-connections]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/local/connections.rs#L19
[linux-credentials]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/credentials/linux.rs#L12
[observation]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/crates/openengine-cluster-protocol/src/native_v2_observation.rs#L146
[durable-follow]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution.rs#L432
[attach-follow]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution/attach.rs#L18
[oecp]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/oecp.rs#L253
[recovery-cli]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_cli/execution.rs#L147
[target-recovery]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target.rs#L435
[remote-profiles]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/profiles.rs#L46
[remote-connections]: https://github.com/the-open-engine/zeroshot/blob/75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa/zeroshot/src/native_v2_target/controller_authority/connections.rs#L12
