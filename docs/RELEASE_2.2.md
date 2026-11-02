# 2.2 Company OS: supported contract and acceptance

This release closes the practical end-to-end gaps around the 2.0 software workbench while preserving fail-closed local execution. It implements the locally testable product flow from the 2.2 specification; it does not claim external provider retention terms, model quality, signed desktop distribution or every ecosystem adapter have been certified.

## Requirement mapping

| Requirement | Implemented behavior | Evidence / limit |
|---|---|---|
| Company UI | Multiple isolated companies, profile/departments/privacy policy, projects, agents, tasks, conversations, knowledge, providers and Living Office | Chromium exercises UI → API → append-only store → refreshed UI; single local Owner, not multi-user RBAC |
| Roles/models/skills | N:M roles, local route/budgets, immutable skill/workflow versions, pinned run bindings | Skills are bounded instructions/metadata; no arbitrary script/plugin execution or permission grant |
| Software workflow | BA → Leader → Owner → parallel Dev → Build/Tester/SOC/QA → bounded correction → SubPM → Owner → export/release | Thirteen typed executable node schemas; arbitrary third-party node kinds are rejected rather than pretending to run |
| Execution evidence | Scoped Git worktrees, exact patches, Linux Docker network-none build/test, findings and receipts | UTF-8 text patches only; missing Docker/image/dependencies produce NOT_RUN/BLOCK with no host fallback |
| Recovery | Leases, heartbeat, pause/resume/step/retry/cancel, File/SQLite parity, encrypted backup/restore and worktree reconciliation | External ambiguous effects require explicit reconciliation; active workspace is not encrypted at rest |
| Collaboration | Public messages, immutable parent/message forks, branch-local replies, meetings/minutes and local model proposals | Transcript branches never auto-merge; curated artifacts/Owner decisions provide merge semantics |
| Model comparison | Two to four agent/role routes receive one byte-identical public context snapshot hash | Route/model payloads remain distinct; results are proposals and no automatic winner is selected |
| Knowledge ingestion | TXT/MD/JSON/CSV/TSV/YAML/HTML/DOCX/XLSX/text-layer PDF, parser/version/hash/classification metadata | Offline bounded parsers; no macro execution; encrypted/image-only PDF and OCR are rejected/not run |
| Hybrid retrieval | Accent-insensitive lexical score plus deterministic 256-dimensional local token/trigram feature vector | Exact revision/range citations; honest `local-hybrid-v1` label, not a neural embedding or ANN claim |
| Controlled Cloud | Strict Local default; fixed OpenAI/Anthropic/Gemini endpoints; exact payload/provenance/classification/retention preview; Owner hash consent; durable worker/retry/audit | Credentials are session RAM or approved `AICOS_PROVIDER_*` refs and never persisted; live accounts/retention/ZDR require provider-side acceptance |
| Git release | Exact accepted candidate, scoped local branch/commit; optional create-only GitHub branch and draft PR | Separate policy/target confirmation/ephemeral token; never merge/deploy |
| Living Office | Event-derived status, handoff/meeting/public-message behavior, A* reservations, follow, reduced motion and board-only mode | Visualization is not canonical state; 25-agent headless check is not a user-device benchmark |

The office currently uses simple SVG scenery and avatars; it does **not** reproduce the detailed chibi concept illustration. Everyday UI shows human names, objectives and branch names; complete identifiers and hashes remain available in explicit technical disclosures. Scope, destination, commands and exact cloud payload remain inspectable before approval. Workflow Studio currently edits validated versioned JSON, rather than providing the full drag-and-drop palette from the specification. Cloud routes currently serve public conversation proposals, not BA/Leader/Dev software execution. See the [Vietnamese setup and remaining-work guide](HUONG_DAN_SU_DUNG.vi.md).

## Controlled Cloud invariant

Every outbound model call follows:

`public message → local context assembly → fixed provider payload → persisted manifest/hash → Owner decision → policy/provider/credential recheck → durable lease → HTTPS call → validated public proposal + response metadata hash`

- Strict Local blocks preview and dispatch. Returning to Strict Local fences approved-but-unsent jobs.
- Privacy-mode/output-budget changes advance the cloud policy version automatically. Provider or classification upgrades invalidate unsent consent. Optional source exclusions are recorded in the manifest and cannot remove mandatory accepted constraints.
- Provider host/path/API version are selected by runtime policy, never supplied as an arbitrary URL.
- OpenAI uses Responses with `store:false`; Anthropic uses stateless Messages with a pinned API version; Gemini uses Interactions with `store:false`. These request flags do not establish account-wide Zero Data Retention.
- `restricted` context is denied. `confidential` context is shown in the sensitive-label manifest and always needs per-payload approval, like every cloud call in this release.
- Secret-pattern hits block payload creation. This is defense in depth, not proof that arbitrary content contains no secret.
- Provider credentials are resolved only immediately before dispatch. They are absent from events, payload previews, logs, migrations, exports and encrypted backups.
- Output must satisfy the bounded public-reply JSON contract and may cite only an exact source span included in the approved payload.

## Knowledge and document limits

- Upload: 8 MiB raw; 262,144 UTF-16 characters after parsing.
- DOCX/XLSX are opened as bounded ZIP/XML containers with entry/path/ratio limits; macro-enabled formats are rejected.
- PDF support is a conservative text-layer parser with bounded Flate expansion. It is not a full layout engine. Encrypted or image-only PDFs require an external, explicitly reviewed local OCR step before import.
- Hybrid vector features are derived projections and can be rebuilt from source events. They run locally and make typo/related-token matching more useful, but do not imply semantic-model quality.
- All results retain source ID, revision, content hash and exact UTF-16 range. Owner-reviewed memory remains separate from raw/imported and model-proposed content.
- File imports retain a separate SHA-256 of the original file bytes alongside the parsed-text hash. The original binary file itself is not kept in the event journal; retain it separately when it is required for an evidence archive.

## Execution limits

- Repository intake: committed safe UTF-8 files only, at most 2 MiB/2,000 files and 256 KiB/file. Hidden/secret/generated/symlink/submodule/binary paths are omitted and cannot be overwritten as text.
- Patch: 1–24 UTF-8 files, 128 KiB/file and 512 KiB total, exact before hashes, immutable task scopes. File deletion and binary asset mutation are not supported in this executable contract.
- Task graph: at most 8 tasks, 1–4 concurrent jobs, up to 3 attempts, 2 automatic correction rounds and 6 Owner revisions.
- Agent context: 4,000–10,000 UTF-8 bytes; output 256–4,096 tokens. Mandatory constraints that do not fit block the call.
- Cloud context: 10,000 UTF-8 bytes for the complete serialized request and company output budget 256–4,096 tokens. Local comparison packing measures the complete route-specific payload for every candidate. Estimated input tokens are labelled as an estimate; measured usage is stored when returned.
- Containers: typed node/npm/dotnet/python3 argv, 1–300 seconds, network none, 1 GiB RAM, 2 CPUs, 128 processes, bounded tmpfs/output and a preinstalled digest-pinned image.
- Backup: 64 MiB plaintext / 128 MiB envelope. External repositories, model weights, provider secrets and temporary worktrees are not included.

## Dependency evidence

Open the project inventory to obtain `dependencyInventorySha256`. Import `dependency-audit-<projectId>`, accept a source-cited decision, and select it for a run. Evidence must be at most 30 days old and match the exact candidate inventory.

```json
{
  "inventorySha256": "sha256:<inventory hash>",
  "generatedAt": "2026-11-02T00:00:00Z",
  "tool": "actual audit tool",
  "toolVersion": "actual version",
  "findings": [],
  "licenses": [
    { "name": "dependency", "version": "resolved version", "license": "MIT", "source": "reviewed provenance" }
  ]
}
```

Unknown/malformed/stale evidence and UNKNOWN/NOASSERTION/UNLICENSED identifiers cannot clear the gate. The runtime does not silently download dependencies or contact vulnerability/license services from the execution container.

## Verification

- Domain suites cover durability, upgrade compatibility, ingestion/retrieval, cloud policy, local inference, static delivery and software execution in both File and SQLite modes.
- Cloud adapter fixtures use real HTTP request objects with fixed host/path/auth/version assertions and current response shapes, but no live provider account or paid request.
- Browser suites exercise company settings, branch creation, same-snapshot comparison, exact cloud preview/denial, collaboration, meeting minutes, source-backed state, responsive UI and browser outbound-request detection.
- Linux CI requires an actual Docker engine and preinstalled image for failing test → correction → passing gates → Owner → scoped Git commit. A protocol fixture supplies model JSON; build/test/Git operations are real.
- Windows validates portable domain/storage behavior and runtime packaging. Live model weights, real provider accounts, remote GitHub push, Docker Desktop, signed installers and user-device performance/privacy tests remain external acceptance.

## Explicit remaining boundaries

The following specification extensions are intentionally not represented as complete: arbitrary MCP/A2A/tool-plugin execution, neural embedding/ANN packaging, local OCR/image captioning, binary/deletion patches, automatic dependency downloads, OS keychain integration, SQLCipher active-data encryption, signed desktop installers, auto-update, live provider retention/ZDR qualification and full device accessibility/performance/privacy certification. The runtime fails closed or labels these as not run; it does not claim them through UI text.

## Provider contracts checked for this release

- [OpenAI Responses and stateless storage controls](https://developers.openai.com/api/docs/guides/migrate-to-responses)
- [OpenAI structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs)
- [Anthropic API authentication](https://platform.claude.com/docs/en/api/overview) and [API version 2026-10-21](https://platform.claude.com/docs/en/api/versioning)
- [Gemini Interactions API](https://ai.google.dev/api/interactions-api)
