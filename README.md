# AI Company OS

A local-first personal agent workspace based on specification 2.2. The current **1.0 Local Static Delivery edition** completes a bounded BA → Leader → Dev → Tester/SOC/QA → SubPM → Owner workflow for passive static websites and documents, with optional Ollama inference, real validator receipts and an approved ZIP export. It is **not the complete autonomous agent product** described in the specification.

The original design documents, concept images and HTML prototype remain reference material. See [the 1.0 contract and release notes](docs/RELEASE_1.0.md), [local AI behavior](docs/LOCAL_INFERENCE.md) and [the historical durable-workspace review](docs/IMPLEMENTATION_REVIEW.md).

## What works

- Multiple isolated companies, seven versioned role contracts, and agents with multiple roles. Assigning roles never grants tools or Owner authority.
- Two explicit modes: a deterministic scaffold, or local AI BA → hashed handoff → Owner-mediated Leader ACK → local AI plan → Owner approval. Agent bindings and accepted memory are pinned when a run starts.
- Durable pause/resume/cancel, optimistic checkpoint checks, idempotent commands and event replay after process restart. AI jobs add leases/heartbeats, fenced completion, a two-attempt cap and explicit retry after an interrupted process.
- Explicit delivery preparation after plan approval; Owner-supplied or local-model static file generation, three real in-process validation gates, revision review, isolated preview and final Owner-approved ZIP export with checksums/receipts. No generated scripts or repository/shell execution.
- File Store and SQLite with the same event contract. Switching backend stages a new generation, verifies all event counts/digests, then atomically switches one profile pointer.
- Versioned text sources, accent-insensitive lexical search with exact source revision/hash/UTF-16 citations, and Owner-reviewed canonical memory. Imported text never authorizes actions.
- Optional installed-model discovery, structured output validation, pinned model/input/output digests and provider-reported token usage. No tools are offered to the model.
- Same-origin SSE, a consistent workspace snapshot, event-driven Office projection with client A* movement, reduced motion, visual pause and an accessible agent list.
- AES-256-GCM encrypted backup, checksum validation and restore into an empty data directory.

## Run locally

Install the .NET 10 SDK. Restore packages once, then start:

```sh
dotnet restore src/AICompanyOS.Runtime --locked-mode
dotnet run --project src/AICompanyOS.Runtime --no-restore
```

Open **http://127.0.0.1:4173**. Use this exact address; alternate hosts are rejected. No cloud account or frontend build is required. Package restore and development tooling may need Internet access; the running workspace has no cloud provider, telemetry or external asset calls. Local inference contacts only the explicitly configured loopback Ollama server when requested or when a previously queued job resumes.

Data lives outside the repository under the platform local application data directory, in `AICompanyOS`. Set `AICOS_DATA_DIR` before starting to choose another local directory. The first launch creates Software Delivery Studio. Existing company event logs from the initial runtime are registered without rewriting their workflow history.

The UI uses a per-process HttpOnly, SameSite Strict session cookie. API writes require `Origin: http://127.0.0.1:4173`; workflow commands also require an `Idempotency-Key`. Reload the UI after a runtime restart to establish its new session.

## Use local AI

Start Ollama with cloud features disabled and install a model appropriate to your hardware. Model downloads are a separate, explicit operation; this app never pulls weights or substitutes a cloud model.

```sh
OLLAMA_NO_CLOUD=1 ollama serve
```

For an existing Ollama service, configure that service and restart it as explained in the [Ollama FAQ](https://docs.ollama.com/faq#how-do-i-disable-ollamas-cloud-features). In **Yêu cầu mới**, select **AI local · Ollama**, click **Tải danh sách model đã cài**, choose a model/source excerpts and create the run. After BA completes, ACK the handoff to queue Leader. Owner approval remains an explicit separate action.

The default provider is `http://127.0.0.1:11434/`. `AICOS_OLLAMA_URL` may select another dedicated numeric IPv4 loopback port. DNS names, credentials, remote URLs, redirects, proxies and the workspace port are rejected. GGUF locality, a pinned digest, completion capability and at least a 16k model context declaration are checked before generation. Unsupported models fail visibly; there is no automatic fallback.

See [local inference, budgets and recovery](docs/LOCAL_INFERENCE.md) for the exact scope and verification limits.

## Deliver a static site/document

After approving a plan, click **Chuẩn bị sản phẩm bàn giao**. Assign Dev/Tester/SOC/QA/SubPM, enter literal file criteria and explicitly authorize manual files or local-model Dev generation. Plan approval alone does not create files.

For a complete offline example, keep manual mode and the default `index.html | Tài liệu dự án` criterion. Click **Nhập file sản phẩm**, edit the JSON sample (or use [examples/static-delivery.json](examples/static-delivery.json)) and submit. Inspect the three check receipts and the isolated preview, then either request a new version or review/confirm acceptance and content rights. Only the accepted exact artifact can export **ZIP sản phẩm và receipts**.

This edition supports up to eight safe text files, 48 KiB total, with `index.html` as passive XHTML. Static checks cover markup/JSON, passive-content policy and Owner literal criteria. They do not establish application correctness or legal clearance. See [the exact supported format, states and limits](docs/RELEASE_1.0.md).

## Back up and restore

In **Dữ liệu & sao lưu**, export an encrypted `.aicos` file and keep its passphrase separately. The UI can authenticate/decrypt a backup and verify its event digest without restoring it. The current backup limit is 8 MiB of plaintext event data before encryption; individual source imports are limited to 65,536 UTF-16 characters.

Stop any runtime using the destination, choose a **new empty directory**, and restore from a local terminal:

```sh
AICOS_DATA_DIR="$HOME/.local/share/AICompanyOS-restored" \
  dotnet run --project src/AICompanyOS.Runtime --no-restore -- \
  --restore-backup /path/to/backup.aicos
```

Enter the passphrase at the prompt; it is not echoed in an interactive terminal. Start the app with the same `AICOS_DATA_DIR` to open the restored workspace. Restore refuses to overwrite existing data. The destination path is local; there is no remote backup service or account recovery for a lost passphrase.

File/SQLite storage is plaintext at rest. Backup encryption does not encrypt the active workspace. See [the security boundary](docs/SECURITY.md).

## Verify

```sh
dotnet restore tests/AICompanyOS.Runtime.Tests --locked-mode
dotnet build tests/AICompanyOS.Runtime.Tests -c Release --no-restore
dotnet run --project tests/AICompanyOS.Runtime.Tests -c Release --no-build
```

Optional UI quality checks use Node.js 20+; npm is needed only for testing:

```sh
npm ci
npm test
npx playwright install chromium
npm run test:browser
```

The browser test starts and stops its own runtime with temporary data. Port 4173 must be free. It does not use your normal data directory. `npm run test:api` runs just the HTTP/restart checks and does not need a browser binary. Browser evidence is written to a temporary directory or `AICOS_QA_OUTPUT_DIR`; it is not release content.

CI is configured for Linux/Windows domain tests, Linux Chromium checks and runtime-dependent packages for both OSes. CI results must be confirmed after the branch is pushed; configuration alone is not a passing remote run.

## Still required for the full product

The static delivery workflow is implemented with actual static validator receipts. Arbitrary repository/shell Dev execution, a process sandbox, application build/test and dependency-security gates, a general workflow compiler, cloud egress consent, conversation branching, semantic/vector retrieval and signed installers remain future work. Skills are versioned built-in contract metadata, not executable packages. Approving a scaffold plan never starts implementation or grants shell/network/Git permissions.

See [the prioritized backlog](docs/IMPLEMENTATION_REVIEW.md#remaining-product-backlog), [API details](docs/API.md), and the source requirements in `AI_Company_OS_2.2_Work_Package/AI_Company_OS_Spec_v2.2.md` and `AI_Company_OS_Work_Handoff_v2.2.md`.
