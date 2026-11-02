# 2.0 Company Workbench: supported contract and acceptance

Historical release contract. See [2.2](RELEASE_2.2.md) for current capabilities and remaining limits.

This release extends 1.0 with the core local software-delivery flow from spec §§38–60. It does not certify every optional mode or production acceptance target in the complete specification.

## Requirement mapping

| Requirement | Implemented behavior | Evidence / limit |
|---|---|---|
| Company/department/project UI | Live Company home, projects, counts, departments, policy, agent inspector | Chromium browser → API → event-store checks; local single Owner |
| Roles/models/skills | N:M role membership, per-agent local route and budgets, immutable instruction packages and workflow versions | Pinned in every run; packages do not run scripts or grant tools |
| BA and Leader | Strict RequirementSpec and bounded TaskGraph; AC coverage and dependency validation; Owner plan feedback | Real Ollama HTTP adapter tested with deterministic protocol replies; real weights require user acceptance |
| Handoff | Immutable identity/input/spec/plan/candidate/memory/source/permission hash; receiver ACK before invocation | ACK is the runtime context receiver's mechanical validation, not a claim of model comprehension |
| Workflow | Versioned software DAG, cycle/schema validation, bounded correction loops, conditional release, failure reports | Supports the 13 defined software node schemas and budget/dependency tuning; rejects arbitrary additional schemas |
| Parallel Dev | Scoped full-text patches in independent real Git worktrees; dependency integration and exact before hashes | Original checkout/HEAD untouched; conflicts block instead of silently overwriting |
| Execution | Local Linux Docker, pinned preinstalled image, network none, read-only root, unprivileged user, CPU/RAM/PID/time/output bounds | No generated code runs on host; actual sandbox fixture is required on Linux CI |
| Tester/SOC/QA | Actual configured build/test commands; static source rules; imported dependency evidence; license/provenance and criterion mapping | Exit/log/image/command/candidate/attempt-bound receipts; automatic checks cannot prove complete semantic/security/legal correctness |
| SubPM / memory | Evidence-derived success/failure report, source-cited proposed memory | No automatic canonical write; Owner review is separate |
| Owner controls | Plan and delivery revisions, pause/resume/step/retry/cancel, exact-hash acceptance/rights review | Durable leases/heartbeat, bounded attempts/time/output reservations; late results fenced |
| Recovery / portability | File/SQLite migration, restart, encrypted backup/restore, failed managed-worktree reconciliation | Hash parity tested; power-cut/filesystem fault behavior and large-log compaction are not certified |
| Git release | Explicit scoped local commit; optional create-only GitHub branch plus draft PR, exact ref verification | Ephemeral token only, separate company policy; no merge/deploy; provider-side remote release needs an account smoke test |
| Collaboration / Office | Public consult/question/answer/inform/review/escalation/status; local replies; meetings/minutes; multi-agent A*, reservations, follow, reduced motion | Saved records drive animation; no hidden reasoning; 25-agent headless rendering/navigation fixture is not a user-device performance guarantee |
| Context continuity | Accepted constraints retained, ranked partial inventory, complete file reads, progressive read requests, source offsets | Lexical selection only; no embeddings/vector/graph index or automatic context-fidelity score |

## Execution limits

- Repository intake: committed safe UTF-8 files only, at most 2 MiB/2,000 files; 256 KiB per intake file. Hidden paths, known secret/generated paths, symlinks, submodules and binary files are omitted. Omitted base files cannot be overwritten as new files.
- Patches: 1–24 text files, 128 KiB/file, 512 KiB total; exact before hashes, immutable approved scopes; file deletion and binary assets are not supported.
- Task graph: at most 8 tasks, 1–4 concurrent jobs; up to 3 job attempts, 2 automatic correction rounds, 6 Owner revision requests; run time/output budgets remain binding across all revisions.
- Agent context: 4,000–10,000 UTF-8 bytes; output 256–4,096 tokens. Full mandatory constraints that cannot fit block execution. Inventory omissions are explicitly labelled; editing an existing unread model-context file is denied. A complete requested file must fit the context budget.
- Docker commands: typed node/npm/dotnet/python3 argv; 1–300 seconds; fixed network-none sandbox with 1 GiB RAM, 2 CPUs, 128 processes and bounded writable tmpfs/logs. Images/dependencies are prepared by the Owner outside the app. Compiled binaries are ephemeral; source/patch/check receipts are exported.
- Backup: 64 MiB plaintext / 128 MiB encrypted envelope; in-memory crypto, not streaming unlimited export. Existing v1 envelopes remain readable. External Git source history and temporary worktree directories are not part of the archive.

## Dependency evidence

Open the project's repository inventory to obtain `dependencyInventorySha256`. Import a source with ID **`dependency-audit-<projectId>`**, create an accepted **decision** citing that exact source revision, and select the source when creating a run. Evidence must be at most 30 days old and match the exact candidate inventory. A changed dependency inventory requires a new reviewed run/source; it cannot reuse stale clearance.

```json
{
  "inventorySha256": "sha256:<hash shown by inventory>",
  "generatedAt": "2026-11-02T00:00:00Z",
  "tool": "name of the actual audit tool",
  "toolVersion": "actual version",
  "findings": [],
  "licenses": [
    { "name": "dependency-name", "version": "resolved-version", "license": "MIT", "source": "reviewed provenance reference" }
  ]
}
```

`findings` use `{id,severity,message,path?,criterionId?}` and critical/high/medium/low/info severities. Unknown/malformed/unreviewed/stale evidence and UNKNOWN/NOASSERTION/UNLICENSED license identifiers cannot clear the gates. Imported evidence is explicitly labelled Owner-reviewed input, not a live vulnerability-database query. Nothing here establishes legal clearance.

## Verification

- Existing durability, inference, static delivery and HTTP/browser suites remain active.
- New domain checks exercise typed graph validation, exact scopes, real Git worktrees, handoff ACKs, single-step/manual inputs, Owner feedback, stale-worker fencing, partial-worktree reconciliation, audit/license decisions, failure reporting, proposed memory, File/SQLite parity, restart and authenticated backup above the previous 8 MiB ceiling.
- Company browser checks exercise actual forms/API persistence, local model protocol calls, invalid output rejection, conversations, minutes, command trace, mobile/board-only modes, zero external browser requests and 25-agent rendering.
- Linux CI requires a real Docker engine and explicitly installed Node image for actual parallel Dev → failing Node test → correction → passing build/test/SOC/QA → Owner → scoped commit, both at domain level and through Company UI. The model is a clearly identified fixture; commands and Git operations are real.
- Windows checks validate the portable domain/store paths and publish a runtime-dependent package. Actual Windows-hosted Docker Desktop, real Ollama weights, user hardware and a live GitHub-token release are separate acceptance work.

## Outside this release

Controlled Cloud providers/exact-payload egress consent/vault, arbitrary tool plugins or workflow node kinds, vector/hybrid retrieval, general branching conversation UI, PDF/Office ingestion/OCR, binary/deletion patches, automatic dependency downloads, signed installers, auto-update and full device/privacy/performance qualification remain outside this implementation. The app has no cloud-model fallback. Complete specification 2.2 acceptance still requires those explicitly selected product extensions and real-device/model validation.
