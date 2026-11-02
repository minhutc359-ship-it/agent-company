# API contract: local workspace 1.0

Base: `http://127.0.0.1:4173/api/v1`. Bootstrap with `GET /api/session`; retain the HttpOnly session cookie. Every write requires exact local `Origin`. Workflow create/ACK/approval/pause/resume/cancel additionally require an identifier-shaped `Idempotency-Key` (1–100 ASCII letters, digits, `_`, `-`).

Errors use `{ "error": { "code": "...", "message": "...", "correlationId": "..." } }` where available. Framework binding/404/429 responses can have no JSON body. The UI handles both. Expected command conflicts return 409; an invalid ACK returns 422; unknown companies/records return 404. Integrity failures fail closed with 503; invalid backup input returns 400.

## Organization

| Method | Route | Behavior |
|---|---|---|
| GET | `/health` | Actual active storage, local Ollama opt-in, loopback-only provider transport, no cloud-model calls; separate explicit GitHub-release capability |
| GET/POST | `/companies` | List/create isolated local company; create body `{companyId,name}`; ID is the natural idempotency key; case-only aliases are rejected for portable file paths |
| GET | `/providers/local/models` | Explicitly query installed local GGUF models; cloud/remote entries are filtered |
| GET | `/roles`, `/skills` | Built-in immutable versioned contracts/metadata |
| GET/POST | `/companies/{companyId}/agents` | Agent creation `{agentId,name,roleIds}`; no tool grant fields are accepted |
| PUT | `/companies/{companyId}/agents/{agentId}/roles` | `{roleIds,expectedVersion}`; stale version → 409 |
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
| GET/POST | `/sources` | Import `{sourceId,title,content,expectedRevision?}`; exact retry deduplicates; changed content creates a new immutable revision |
| GET | `/sources/{sourceId}/revisions/{revision}` | Exact original source text/hash |
| GET | `/knowledge/search?q=...` | Accent-insensitive lexical matching of current source revisions, top 10; explicitly not semantic search |
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
| POST | `/commands/resolve` | `{text:"/trace"}` → explicit role/skill/workflow/runtime/query resolution; never shell execution |

Company-scoped routes:

| Method | Suffix | Contract |
|---|---|---|
| GET | `/workbench` | Profile, projects, configurations, instruction packages, workflow versions, software runs, interactions, meetings and consultation jobs |
| PUT | `/profile` | `{expectedVersion,description,departments,allowGitHubRelease}`; policy defaults to remote release disabled |
| POST | `/projects` | `{projectId,name,repositoryPath,readPaths,writePaths,dockerImage,build,test,expectedVersion?,remoteUrl?,baseBranch?}`; typed commands use `{name,executable,arguments,timeoutSeconds}` |
| GET | `/projects/{projectId}/inventory` | Safe committed base/file hashes/omissions and dependency inventory digest |
| PUT | `/agents/{agentId}/configuration` | `{expectedVersion,department,modelId,skillRefs,maxInputBytes,maxOutputTokens}`; skill refs use `id@version`, model null means Owner typed input |
| POST | `/skills` | Immutable `{skillId,version,name,instructions,roles,requestedTools}` metadata; no script execution or permission grant |
| POST | `/workflows` | Install a new validated immutable SoftwareWorkflow version |
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

## Public collaboration and meetings 2.0

| Method | Company-scoped suffix | Contract |
|---|---|---|
| POST | `/interactions` | `{runId,type,fromAgentId,toAgentIds,summary,artifactRefs,citations,replyTo?}`; run or lobby scope, valid company participants/artifacts; explicit Owner-authored origin |
| POST | `/interactions/{interactionId}/respond` | `{agentId,roleId}`; selected recipient's installed local route, immutable context, durable bounded consultation job |
| GET | `/consultations` | Job state/input/usage; model replies are labelled model_proposal, not accepted decisions |
| POST | `/consultations/{jobId}/retry` | Explicit retry for failed/interrupted jobs, max two attempts |
| POST | `/meetings` | `{runId,agenda,participants}`; 2–8 company agents and Idempotency-Key |
| POST | `/meetings/{meetingId}/start` | `{expectedVersion,summary:"",actionItems:[],citations:[]}` |
| POST | `/meetings/{meetingId}/complete` | `{expectedVersion,summary,actionItems,citations}`; Owner-supplied public minutes saved as immutable source |
| POST | `/meetings/{meetingId}/summarize` | `{agentId,roleId}`; started participant's local model, only recorded matching public messages; minutes and reply commit atomically |

Meeting/report source artifacts and proposed memory survive backup/migration. No consultation or meeting changes canonical memory automatically. Receiver ACKs validate context identity/hash/version mechanically and are visible in trace; they do not assert model understanding. The 2.0 supported node schemas, command semantics, size bounds and remaining capabilities are defined in [RELEASE_2.0.md](RELEASE_2.0.md).
