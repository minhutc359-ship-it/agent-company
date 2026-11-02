# API contract: local workspace 1.0

Base: `http://127.0.0.1:4173/api/v1`. Bootstrap with `GET /api/session`; retain the HttpOnly session cookie. Every write requires exact local `Origin`. Workflow create/ACK/approval/pause/resume/cancel additionally require an identifier-shaped `Idempotency-Key` (1–100 ASCII letters, digits, `_`, `-`).

Errors use `{ "error": { "code": "...", "message": "...", "correlationId": "..." } }` where available. Framework binding/404/429 responses can have no JSON body. The UI handles both. Expected command conflicts return 409; an invalid ACK returns 422; unknown companies/records return 404. Integrity failures fail closed with 503; invalid backup input returns 400.

## Organization

| Method | Route | Behavior |
|---|---|---|
| GET | `/health` | Actual active storage, local Ollama opt-in, loopback-only provider transport, no external provider calls |
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
| GET | `/workflow-runs/{runId}/report` | Produced artifact IDs, model invocation/context/output/usage and true software execution evidence refs (empty until code executors exist) |
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
| POST | `/backups/export` | `{passphrase}` → encrypted binary `.aicos`; 12–256 characters, max 8 MiB before encryption |
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
