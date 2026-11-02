# API contract: Company OS 2.3 local workspace

Base: `http://127.0.0.1:4173/api/v1`. Bootstrap with `GET /api/session`; retain the HttpOnly session cookie. Every write requires exact local `Origin`. Workflow create/ACK/approval/pause/resume/cancel additionally require an identifier-shaped `Idempotency-Key` (1–100 ASCII letters, digits, `_`, `-`).

Errors use `{ "error": { "code": "...", "message": "...", "correlationId": "..." } }` where available. Framework binding/404/429 responses can have no JSON body. The UI handles both. Expected command conflicts return 409; an invalid ACK returns 422; unknown companies/records return 404. Integrity failures fail closed with 503; invalid backup input returns 400.

## Organization

| Method | Route | Behavior |
|---|---|---|
| GET | `/health` | Active storage/runtime/workflow versions and the explicit local/Controlled-Cloud/GitHub network boundaries |
| GET/POST | `/companies` | List/create isolated local company; create body `{companyId,name}`; ID is the natural idempotency key; case-only aliases are rejected for portable file paths |
| GET | `/providers/local/models` | Explicitly query installed local GGUF models; cloud/remote entries are filtered |
| GET | `/roles`, `/skills` | Built-in immutable versioned contracts/metadata |
| GET/POST | `/companies/{companyId}/agents` | Agent creation `{agentId,name,roleIds}`; no tool grant fields are accepted |
| PUT | `/companies/{companyId}/agents/{agentId}/roles` | `{roleIds,expectedVersion}`; stale version → 409 |
| PUT | `/companies/{companyId}/agents/{agentId}/identity` | `{name,roleIds,expectedVersion}`; bounded display name/known roles, preserves tool grants and previous run snapshots; stale version → 409 |
| GET | `/companies/{companyId}/workspace/snapshot` | Consistent runs/agents/event summaries/sources/canonical/proposals/Office projection at one cursor |

## Workflow and replay

| Method | Company-scoped suffix | Behavior |
|---|---|---|
| GET/POST | `/workflow-runs` | Create `{objective,sourceRefs?,baAgentId?,leaderAgentId?,executionMode?,modelId?}`. Mode defaults to scaffold; local_model requires an installed model ID and source IDs that resolve in this company. Agents must currently hold the roles. Bindings/canonical memory pin to the new run |
| GET | `/workflow-runs/{runId}` | State, artifacts, packet, Owner decision, lastSequence and paused resume state |
| POST | `/workflow-runs/{runId}/handoff/ack` | `{packetHash,receiverAgentId,expectedSequence?}`; verifies immutable spec digest/destination |
| POST | `/workflow-runs/{runId}/approval` | `{approved,expectedSequence?}`; only awaiting_approval can transition |
| POST | `/workflow-runs/{runId}/pause`, `/resume`, `/cancel` | `{expectedSequence}`; stale checkpoint → 409; cancelled runs cannot resume |
| POST | `/workflow-runs/{runId}/retry` | `{expectedSequence}` and Idempotency-Key; blocked local AI only, total max 2 attempts per role |
| GET | `/workflow-runs/{runId}/report` | Produced artifact IDs, model invocation/context/output/usage and static-delivery receipt IDs where applicable; software execution uses the separate `/software-runs` contract |
| GET | `/context/{snapshotId}` | Immutable spec/accepted-memory snapshot and packet; no permissions granted |
| GET | `/events?afterSeq=N` | Full persisted events strictly after cursor |
| GET | `/events/stream?afterSeq=N` | SSE `workspace` events with numeric `id`; `Last-Event-ID` takes precedence |
| GET | `/office/snapshot` | Event-derived agent statuses/cursor; avatar position is a client projection |

For SSE, deduplicate by sequence, replay after reconnect, and reload on `reset` (cursor beyond the current log). Heartbeats are SSE comments. Event payloads are JSON, not model private reasoning. The UI's combined snapshot avoids mixing old run state with a newer event cursor.

`approved` is terminal for the current plan slice (scaffold or local AI), **not verified code delivery**. `revision_requested` and `cancelled` are terminal; create a new run for revised work. Pause/resume preserve the current gate. Local-model runs add queued/running/blocked; pausing a running attempt invalidates its lease. Resume can consume its remaining attempt; an exhausted budget stays blocked. A recovered orphan requires explicit retry. See LOCAL_INFERENCE.md. Visual pause is client-only and makes no workflow command.

## Knowledge

| Method | Company-scoped suffix | Behavior |
|---|---|---|
| GET/POST | `/sources` | Import `{sourceId,title,content,expectedRevision?,kind?,classification?,parserVersion?,originalName?}`; changed content creates an immutable revision |
| POST | `/sources/file` | Multipart `file,sourceId,title,expectedRevision,classification`; bounded offline TXT/MD/JSON/CSV/TSV/YAML/HTML/DOCX/XLSX/text-layer-PDF parsing |
| GET | `/sources/{sourceId}/revisions/{revision}` | Exact parsed source text/hash and optional original filename/file-byte hash |
| GET | `/knowledge/search?q=...` | Current-revision `local-hybrid-v1` lexical + deterministic local feature-vector ranking with exact citations and score components |
| GET | `/knowledge/index-status` | Derived index version/mode/chunk/source counts and honest neural/ANN limitations |
| GET/POST | `/memory/proposals` | Propose `{proposalId,stableKey,kind,body,citations}`; kind is fact/decision/constraint |
| POST | `/memory/proposals/{proposalId}/review` | `{accepted,expectedCanonicalVersion}`; stale source or canonical version conflicts block acceptance |
| GET | `/memory/canonical` | Current Owner-accepted records; historical versions remain in events |

Citation: `{sourceId,revision,start,end,contentSha256}`. Start/end are zero-based UTF-16 code-unit offsets; end is exclusive. They must resolve in this company to the exact immutable source version/hash and an in-bounds range. Owner acceptance approves the proposed meaning; the runtime only deterministically validates reference integrity. Plain workflow `sourceRefs` are labels and never execution evidence.

## Storage and backup

| Method | Route | Behavior |
|---|---|---|
| GET | `/storage/verify` | Backend-independent company counts/digests; explicit legacy-event count |
| POST | `/storage/migrate` | `{backend,expectedDigest}`; backend is file/sqlite, freeze-copy-verify-switch, stale preview → 409 |
| POST | `/backups/export` | `{passphrase}` → encrypted binary `.aicos`; 12–256 characters, max 64 MiB plaintext / 128 MiB envelope |
| POST | `/backups/validate` | `{archiveBase64,passphrase}` → counts/digest only; no current-data mutation |

Storage migration and backup are profile-scoped Owner operations. No arbitrary filesystem paths or SQL connection strings are accepted through HTTP. Real restore is local CLI only and requires an empty destination; see README.

## Static delivery 1.0

All routes below use `/companies/{companyId}/workflow-runs/{runId}/delivery`. Writes require exact Origin, session, `Idempotency-Key` and the current `expectedSequence`.

| Method | Suffix | Contract |
|---|---|---|
| POST | `/prepare` | `{expectedSequence,mode:"manual"|"local_model",criteria:[{id,path,contains}],assignments?:{dev,tester,soc,qa,subpm}}`; only an approved plan without an existing delivery; omitted assignments use role-agent defaults; 1–12 literal criteria. Local mode requires the run's pinned model |
| POST | `/artifacts` | `{expectedSequence,draft:{title,summary,files:[{path,content}]}}`; only manual input or failed/rejected delivery checkpoints; at most 3 versions. Produces files, checks and SubPM summary atomically |
| POST | `/review` | `{expectedSequence,artifactSha256,approved,acceptanceConfirmed,rightsConfirmed}`; only current passing artifact; both confirmations required for acceptance. Rejection permits another bounded version |
| GET | `/preview` | Current passing `index.html`, checked local stylesheets inlined; sandbox CSP; paused/cancelled/failed artifacts cannot preview |
| GET | `/export` | ZIP of accepted file bytes, `delivery-report.json` and `SHA256SUMS.txt`; rejects unapproved, cancelled or mismatched artifacts/receipts |

`WorkflowRun.delivery` includes the immutable preparation/bindings/criteria, latest artifact, executed receipts, SubPM report and separate Owner acceptance. Earlier artifact versions remain in the event journal. `WorkflowReport.evidenceRefs` contains actual static-check receipt IDs when present; `modelInvocations` stays separate. For Dev, the output digest hashes the normalized `ArtifactDraft` found in `delivery.artifact.draft`, while its brief in `invocations` summarizes those files.

New states: `awaiting_delivery_input`, `awaiting_delivery_approval`, `delivery_blocked`, `delivery_revision_requested`, `delivered`; queued/running/blocked/paused/cancelled are reused for model/lifecycle handling. A stale checkpoint, wrong hash, invalid transition or missing rights confirmation returns 409. Invalid draft shape/path/size or known credential patterns return 400. Unsafe markup can be a valid draft shape but produces BLOCK receipts and a failed delivery gate rather than being executed.

The static contract is deliberately narrower than arbitrary code generation; [RELEASE_1.0.md](RELEASE_1.0.md) defines the supported format and check boundaries.


## Company and executable software delivery 2.0

Global routes:

| Method | Route | Behavior |
|---|---|---|
| GET | `/execution/capabilities?image=node:22-alpine` | Check local Linux Docker and an already installed image; no pull or code execution |
| POST | `/workflows/validate` | Validate a SoftwareWorkflow, finite dependency order and expanded correction rounds; does not run it |
| GET | `/company-starters` | Built-in suggested team, seven instruction packages and three executable workflow templates; read-only, no installation/model call |
| POST | `/commands/resolve` | `{text:"/trace"}` → explicit role/skill/workflow/runtime/query resolution; never shell execution |

Company-scoped routes:

| Method | Suffix | Contract |
|---|---|---|
| GET | `/workbench` | Profile, projects, configurations, packages, workflows, runs, branch/message/meeting/consultation state, cloud routes and egress manifests |
| PUT | `/profile` | `{expectedVersion,description,departments,allowGitHubRelease,privacyMode,cloudPolicyVersion,maxCloudOutputTokens}`; Strict Local is default |
| POST | `/projects` | `{projectId,name,repositoryPath,readPaths,writePaths,dockerImage,build,test,expectedVersion?,remoteUrl?,baseBranch?}`; typed commands use `{name,executable,arguments,timeoutSeconds}` |
| GET | `/projects/{projectId}/inventory` | Safe committed base/file hashes/omissions and dependency inventory digest |
| PUT | `/agents/{agentId}/configuration` | `{expectedVersion,department,modelId,skillRefs,maxInputBytes,maxOutputTokens}`; skill refs use `id@version`, model null means Owner typed input |
| POST | `/skills` | Immutable `{skillId,version,name,instructions,roles,requestedTools}` metadata; no script execution or permission grant |
| POST | `/workflows` | Install a new validated immutable SoftwareWorkflow version |
| POST | `/starter-pack` | `{packId:"software-team",modelId?:string}` plus `Idempotency-Key`; installs missing agents/configs/skills/workflows in one guarded event batch, preserves existing customization, validates an optional installed local model, starts no run and grants no tools |
| POST | `/software-runs` | `{projectId,objective,assignments?,sourceIds?,workflowName?,workflowVersion?,releaseRequested?}` plus Idempotency-Key; pins safe base and all role contexts |
| GET | `/software-runs/{runId}` | Typed spec/plan, nodes, jobs, patches, receipts, report, grant, handoff packets/ACKs, feedback and release |
| POST | `/software-runs/{runId}/outputs/{nodeId}` | `{expectedSequence,output}` for a waiting-input BA, Leader or `dev-<taskId>`; strict schema, scopes and before hashes |
| POST | `/software-runs/{runId}/execution-preview` | `{expectedSequence}`; real sandbox preflight then a 10-minute manifest binding pinned image/base/plan/commands |
| POST | `/software-runs/{runId}/plan-approval` | `{expectedSequence,approved,manifestSha256}`; approval cannot bypass a missing/expired exact preview |
| POST | `/software-runs/{runId}/revision` | `{expectedSequence,stage:"plan"|"delivery",feedback}` at the matching Owner gate; fresh planning or scoped implementation/checks |
| POST | `/software-runs/{runId}/pause`, `/resume`, `/step`, `/retry`, `/cancel` | `{expectedSequence}`; one-step stops after one owned output; controls require Idempotency-Key |
| POST | `/software-runs/{runId}/jobs/{jobId}/reconcile-worktree` | `{expectedSequence}` at a blocked checkpoint; discard only the registered unaccepted failed Dev worktree; explicit retry is separate |
| GET | `/software-runs/{runId}/report` | Derived report plus real node/job/receipt/release state |
| POST | `/software-runs/{runId}/final-review` | `{expectedSequence,candidateSha256,acceptanceConfirmed,rightsConfirmed}`; all mandatory receipts must pass |
| GET | `/software-runs/{runId}/release-preview` | Passing candidate, scoped branch/remote/base, policy and target-manifest hash |
| POST | `/software-runs/{runId}/release` | `{expectedSequence,candidateSha256,acceptanceConfirmed,rightsConfirmed,pushRequested?,gitHubToken?,targetManifestSha256}`; create scoped commit, optionally verify push/open draft PR; token is never persisted |
| GET | `/software-runs/{runId}/export` | Exact accepted changed source files, patch manifests, report/receipts and SHA256SUMS ZIP |

Software states include waiting_input, queued, running, awaiting_plan_approval, paused, blocked, awaiting_final_approval, awaiting_release, completed and cancelled. Node status/typed output/tool receipt/Owner approval are separate. Exact sequence/hash conflicts return 409; schema failures return 400/422. Source text, models and packages cannot supply receipts or Owner authority. Jobs use leases/heartbeat; interrupted external operations require reconciliation and explicit retry. `/health` advertises the executable workflow version and optional release network boundary.

## Public collaboration, branches and model comparison 2.2

| Method | Company-scoped suffix | Contract |
|---|---|---|
| POST | `/interactions` | `{runId,type,fromAgentId,toAgentIds,summary,artifactRefs,citations,replyTo?,branchId?}`; reply must resolve in immutable branch history |
| POST | `/conversation-branches` | `{branchId,runId,name,parentBranchId,forkMessageId?}` plus Idempotency-Key; parent history is visible only through the fork boundary |
| POST | `/interactions/{interactionId}/respond` | `{agentId,roleId,branchId?}`; selected recipient's installed local route, immutable context, durable bounded consultation job; inherited questions can reply within their child branch |
| POST | `/interactions/{interactionId}/compare` | `{candidates:[{agentId,roleId},...],branchId?}`; 2–4 routes, one shared public context snapshot hash, distinct route/model payloads |
| GET | `/consultations` | Job state/input/usage; model replies are labelled model_proposal, not accepted decisions |
| POST | `/consultations/{jobId}/retry` | Explicit retry for failed/interrupted jobs, max two attempts |
| POST | `/meetings` | `{runId,agenda,participants}`; 2–8 company agents and Idempotency-Key |
| POST | `/meetings/{meetingId}/start` | `{expectedVersion,summary:"",actionItems:[],citations:[]}` |
| POST | `/meetings/{meetingId}/complete` | `{expectedVersion,summary,actionItems,citations}`; Owner-supplied public minutes saved as immutable source |
| POST | `/meetings/{meetingId}/summarize` | `{agentId,roleId}`; started participant's local model, only recorded matching public messages; minutes and reply commit atomically |

Meeting/report source artifacts and proposed memory survive backup/migration. No consultation or meeting changes canonical memory automatically. Receiver ACKs validate context identity/hash/version mechanically and are visible in trace; they do not assert model understanding.

## Controlled Cloud 2.2

| Method | Company-scoped suffix | Contract |
|---|---|---|
| PUT | `/cloud/providers/{providerId}` | `{expectedVersion,providerType,modelId,credentialRef,enabled}`; type is openai/anthropic/gemini, host/path/version are runtime-fixed |
| PUT | `/cloud/providers/{providerId}/credential` | `{secret}` → availability only; secret is held in process RAM and is never returned or persisted |
| DELETE | `/cloud/providers/{providerId}/credential` | Clear this company/provider type's RAM credential; an environment reference, if configured, remains host-managed |
| GET | `/cloud/providers/{providerId}/credential` | Availability/source only (`session-memory` or `environment-reference`), never the value |
| POST | `/interactions/{interactionId}/cloud-preview` | `{providerId,agentId,roleId,branchId?,excludedSourceIds?}` plus Idempotency-Key; local exact-payload manifest only, with optional retrieval exclusions and no network call; mandatory accepted memory cannot be silently omitted |
| POST | `/cloud/egress/{manifestId}/decision` | `{expectedVersion,payloadSha256,approved}` plus Idempotency-Key; rechecks privacy policy/provider version/hash/credential before queueing |
| POST | `/cloud/egress/{manifestId}/retry` | Retry the already approved exact payload once after failed/interrupted execution; changed policy/route requires a new preview |

Manifest states are `awaiting_approval`, `denied`, `queued`, `running`, `completed`, `failed` or `interrupted`. It contains the exact credential-free payload, fixed destination, provider/model/API snapshots, public thread/branch, context item provenance/classifications, source excerpts, estimated size/tokens, request hash, consent ID, attempts/lease, measured usage and response/metadata hashes. Restricted context cannot be previewed. Strict Local prevents preview and fences queued work. Output is a bounded `cloud_model_proposal:<providerId>` public interaction, not an accepted decision.

## Local setup, demo and team discussions 2.4

All routes retain the existing loopback/session/Origin requirements. Paths beginning with `/api/v1/setup` are runtime-scoped; the other paths below follow `/api/v1/companies/{companyId}`.

| Method | Path | Contract |
| --- | --- | --- |
| GET | `/api/v1/setup/status` | Runtime/storage/Git/local GGUF catalog and actual local Docker/image diagnostics; PATH-only CLI presence, not CLI authentication; no install or inference |
| POST | `/api/v1/setup/demo` | `{modelId?:string|null}`; validate selected installed model, create a marked managed Git fixture/company/project and missing starter configuration; preserve edits, no run/inference or implicit grants |
| POST | `/team-discussions` | `{runId,branchId,question,participants:[{agentId,roleId},...]}` plus Idempotency-Key; 2–4 distinct local agents, frozen bindings, first turn only queued initially; max two active sessions/company |
| POST | `/team-discussions/{discussionId}/cancel` | Persist cancellation and fence queued/running turns; cancelled sessions cannot retry failed turns |
| GET | `/software-runs/{runId}/external-handoff/{nodeId}` | ZIP with REQUEST.md, actual pinned context.json, schema.json, checkpoint.json; only current waiting_input BA/Leader/Dev jobs; does not invoke an external CLI |
| POST | `/cloud/providers/{providerId}/check` | Controlled Cloud only, enabled configured provider, ephemeral credential; fixed-host GET model listing, no workspace payload or text generation; returns availability/names and whether configured model was listed |

`workbench.teamDiscussions` projects each session's immutable participant routes, question/job links, status and maximum output budget. One public answer advances to the next participant with the recorded answer in branch history; completed sessions stop. Claim-time reconciliation recovers advancement if the process stopped after saving a reply. Failed/interrupted jobs retain the explicit consultation retry policy (maximum two attempts). Reply provenance stays `model_proposal`; conversation cannot grant tools or accept canonical memory. Importing external `result.json` uses the existing version-fenced `/outputs/{nodeId}` schema/patch validation and records `owner-supplied` provenance.

CLI initialization: `dotnet AICompanyOS.Runtime.dll --initialize-local [--model <installed-local-model>]` migrates to embedded SQLite with digest verification, creates/opens the managed demo and exits without starting an HTTP server or model inference. This is the command used by the setup bundle; it never supplies provider credentials.

## Guided connections, software cloud consent and updates 2.5

| Method | Path | Contract |
| --- | --- | --- |
| POST | `/api/v1/agent-models` | `{providerType,secret}`; explicit credential-only fixed-host model list; secret is not persisted |
| GET | `/companies/{company}/agent-connections` | Configuration/engine/model/provider references and key availability, never keys |
| POST | `/companies/{company}/agents/{agent}/connect` | `{expectedVersion,providerType,modelId,secret,allowCloud:true,engine:"api"|"claude-code"}`; explicitly enables Controlled Cloud, configures the employee and places key in RAM. Same-route key refills preserve provider/config versions |
| GET | `/companies/{company}/software-runs/{run}/jobs/{job}/cloud-preview` | Current `waiting_cloud_approval` job's pinned provider/engine and actual credential-free payload/hash/checkpoint |
| POST | `/companies/{company}/software-runs/{run}/jobs/{job}/cloud-approve` | `{expectedSequence,payloadSha256}`; exact payload/current provider/current policy/available credential required before queueing |
| GET | `/api/v1/updates/status` | Runtime build, managed-launcher availability and persisted update state; no network call |
| POST | `/api/v1/updates/check` | Fixed public repository stable release metadata; no install; bounded HTTPS and monotonic build numbers |
| POST | `/api/v1/updates/download` | Rechecks the inspected release, downloads/verifies all bytes, then stages a version; app/data stay unchanged |
| POST | `/api/v1/updates/apply` | `{tag,sha256}` must match the staged package; fences writes, rejects active jobs, requests graceful managed restart and returns before shutdown |

Company paths follow `/api/v1`; all existing cookie/Origin/rate-limit boundaries remain. Software role bindings optionally carry a frozen `cloud` snapshot; agent configurations add optional `providerId` and `engine` with backward-compatible defaults. New cloud inputs are not claimable until `waiting_cloud_approval` becomes `queued` through exact consent. New progressive-read context clears consent and releases its lease. Reconnect-after-restart does not silently replace a pinned route. Model proposals continue through the existing typed/patch validation and runtime checks.

Managed update protocol: installer sets `AICOS_INSTALL_ROOT`, `AICOS_INSTALL_PREFIX`, `AICOS_PYTHON`; the data root must equal `<prefix>/data`. Updater code comes from the installed bootstrap, not downloaded installer scripts. The runtime exits with code 42 only after an accepted apply; the parent snapshots the closed data store, atomically changes the active-version pointer and validates the new build through session + health. An unfinished activation journal or failed health rolls back both pointer and data snapshot. Stable GitHub assets are published only by the verified-master CI publish job. Unsupported runtime-major/protocol changes require a new bootstrap package.

The current Work Mode/preset/visual-editor contract is in [RELEASE_2.3.md](RELEASE_2.3.md); execution/privacy boundaries remain documented in [RELEASE_2.2.md](RELEASE_2.2.md). [RELEASE_2.0.md](RELEASE_2.0.md) remains historical.
