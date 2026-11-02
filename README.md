# AI Company OS

**2.5 Guided Workspace & Managed Updates** adds an AI setup guide, resumable Windows/Ubuntu setup bundle, real Git demo project, bounded agent-to-agent local discussions and explicit external CLI handoff to the Living Studio workspace: multi-company UI, versioned software delivery, isolated Git worktrees, Docker evidence, Owner gates, Living Office, branch-aware conversations, hybrid local knowledge and an optional exact-payload Controlled Cloud gateway.

The supported local software flow is **BA → Leader → Owner scope approval → parallel Dev tasks → Build → Tester/SOC/QA → corrective rounds → SubPM → Owner acceptance → source export or explicit Git release**. Model output never substitutes for a command receipt. The original static-document workflow remains available under **Luồng tĩnh**.

See the [2.5 guided/update release](docs/RELEASE_2.5.md), [2.4 setup/demo release](docs/RELEASE_2.4.md), [2.3 Living Studio release](docs/RELEASE_2.3.md), [2.2 execution/privacy acceptance matrix](docs/RELEASE_2.2.md), [API](docs/API.md), [security boundary](docs/SECURITY.md), and [original specification](AI_Company_OS_2.2_Work_Package/AI_Company_OS_Spec_v2.2.md). Unsupported or externally unverified items are listed explicitly; the runtime does not simulate them.

**Tiếng Việt:** [Cài toàn bộ thành phần, đăng nhập AI/CLI và chạy demo](setup/README.vi.md) · [Hướng dẫn chi tiết và phạm vi còn thiếu so với spec](docs/HUONG_DAN_SU_DUNG.vi.md).

## Full setup bundle

Download `AICompanyOS-Full-Setup-2.5.zip` from the latest green branch [Actions run](https://github.com/minhutc359-ship-it/agent-company/actions), or build it using [these commands](setup/README.vi.md#7-build-lại-bộ-cài-từ-source). Extract all files, then run `Setup-Windows.cmd` on Windows or `bash setup-ubuntu.sh` on Ubuntu. The installer handles missing .NET/Git/Node/Docker/Ollama components, a dedicated local model server, embedded SQLite and the demo. It reports WAIT until the real prerequisites are present; Docker first launch/reboot, account login and hardware capacity remain host-specific. Vendor dependencies/model/image require network during setup. The runtime itself does not download them.

Open **Thiết lập AI & Demo** in Company to review diagnostics and local routes, create/open the separate demo, or see cloud/API and external CLI instructions. Ollama runs local jobs/discussions. The one-screen **Kết nối AI** form binds Claude/OpenAI/Gemini API keys or an installed Claude Code CLI to each employee. BA/Leader/Dev cloud steps require exact-payload approval and retain typed output/worktree/Docker gates. Codex/ChatGPT, Gemini CLI and Copilot CLI retain reviewed handoff export/import. A next-step card and optional 15-second idle coach show the current Owner action. Managed installations can check/download/upgrade in the app, with closed-store snapshots and startup-failure rollback. Stable update releases are published only after a master push passes all required CI jobs. See [2.5](docs/RELEASE_2.5.md).

To publish and run the current source with update supervision (close the old runtime first):

```bash
python setup/setup-aicos.py --source --launch --cloud-only
```

.NET SDK 10 is required; the launcher can install missing components. Omit `--cloud-only` for a configured local Ollama environment. Direct `dotnet run` also works, but in-app installation requires the managed launcher.

## Run locally

Install .NET 10 SDK and Git. For actual code execution, install a local Docker engine using **Linux containers**, then explicitly install a trusted image suitable for the project:

```bash
docker pull node:22-alpine
dotnet restore tests/AICompanyOS.Runtime.Tests --locked-mode
dotnet run --project src/AICompanyOS.Runtime
```

Open **http://127.0.0.1:4173**. The app never downloads images or models. Without Docker, project setup, planning, conversations and manual typed inputs work; execution approval is blocked and no build/test PASS is invented.

For local AI, install the chosen GGUF completion model in a trusted Ollama server with cloud features disabled. The default endpoint is `http://127.0.0.1:11434/`; `AICOS_OLLAMA_URL` accepts numeric IPv4 loopback HTTP only. The app rechecks the installed model digest and at least 16K context capacity before each invocation. Configure each agent under **Company → Agents & Skills**. An empty model route means **Owner-supplied typed JSON**, not simulated inference.

Optional environment variables:

| Variable | Purpose |
|---|---|
| `AICOS_DATA_DIR` | Private local data directory; default `AICompanyOS` under the OS local application-data directory |
| `AICOS_OLLAMA_URL` | Trusted local Ollama endpoint |
| `AICOS_DOCKER_ENDPOINT` | Explicit local Unix `.sock` or Windows Docker named pipe; remote TCP is rejected |
| `AICOS_PROVIDER_*` | Optional provider credential referenced by a configured Controlled Cloud route, for example `env:AICOS_PROVIDER_OPENAI`; the value is never persisted |

## Use Company Workbench

1. Add a project using the absolute root of a committed local Git repository. Set readable/writable paths and typed build/test argument arrays. Intake reads the committed base, including no uncommitted edits.
2. Install versioned skill instructions and configure department, model and budgets for each agent. Multiple agents may hold Dev; one agent may hold several roles.
3. Create a software run, select source revisions and assignments. All inputs, accepted memory, agents, skills, model routes, workflow and repository base are pinned.
4. Review the BA requirements and Leader task graph. **Yêu cầu sửa** sends explicit feedback into a new Leader input.
5. Preview and approve the exact execution manifest, including image digest, source base, write scopes and commands. This grants isolated worktree/container operations for this run.
6. Inspect task outputs, immutable handoff packets/receiver ACKs and actual receipts. Independent Dev tasks use separate worktrees. The source checkout and HEAD stay unchanged.
7. A failing actionable test/code check can trigger up to two automatic correction rounds. Missing infrastructure or audit evidence escalates to Owner. Pause/resume/one-step/retry/cancel preserve durable checkpoints; stale workers cannot commit results.
8. Review the passing candidate, diff/patch, test logs and provenance. Owner delivery revisions produce another candidate and fresh checks within the approved scopes and run budget.
9. Confirm semantic acceptance and rights. Download the patch/source ZIP or separately preview an explicit scoped Git commit. Optional GitHub push opens a **draft PR**, requires company policy, exact target confirmation and an ephemeral token, and never merges/deploys.

**Work Mode** combines a locally bundled chibi office with actual workflow progress, tasks, evidence, approvals and a public live board. New companies can install the editable suggested eight-member team, seven role skills and three workflow presets; existing configurations are preserved. **Workflow Studio** supports node dragging/keyboard movement, dependency ports/forms, undo/redo, validation, immutable version saving and JSON import/export for the thirteen executable software nodes. See the [2.3 release](docs/RELEASE_2.3.md) and [art direction/provenance](docs/ART_DIRECTION.md).

**Conversations & Meetings** supports public messages, immutable branch forks, local-model replies, 2–4 route comparisons over one context snapshot, meeting participants and saved minutes. Minutes and SubPM reports are source artifacts; proposed memory only becomes canonical after Owner review in Knowledge. Office movement and bubbles project saved execution/public-message events. Visual pause, board-only mode, keyboard selection, follow camera and reduced motion are independent from the worker.

**Knowledge** imports bounded TXT/Markdown/JSON/CSV/TSV/YAML/HTML/DOCX/XLSX and text-layer PDF files entirely offline. HTML active content is stripped; macros, unsafe archives, encrypted PDFs and detected credentials are rejected. Parsed text, parser version and the original file hash are retained; keep original binary documents separately. Search combines Unicode/accent-insensitive lexical scoring with a deterministic local feature vector and returns exact source revision/range citations with highlighted source inspection. OCR and neural embeddings remain optional extensions.

**Controlled Cloud** is off by default. Under Company policy, select `controlled_cloud`, configure one of the fixed OpenAI Responses, Anthropic Messages or Gemini Interactions routes, then load the credential into the session RAM vault or reference an `AICOS_PROVIDER_*` environment variable. A public message can produce an exact payload preview with endpoint, provenance, classifications, size, retention caveat and SHA-256. Only the exact Owner-approved manifest is queued. Switching to Strict Local, changing provider version or changing policy version fences unsent work. Provider output is labelled `cloud_model_proposal`; it never auto-writes canonical memory. Provider-specific retention/ZDR settings remain an account-level responsibility.

The command bar resolves `/status`, `/trace`, `/budget`, `/spec`, `/plan`, `/cook`, `/test`, `/check-policy`, `/report`, `/handoff @agent`, `/run software-delivery`, `/pause`, `/resume`, `/step`, `/retry` and `/cancel`. Role/skill commands inspect the current pinned node; they do not bypass prerequisites or dispatch extra tools. Slash commands never become shell text.

## Small executable example

```bash
node examples/create-demo-repository.mjs /absolute/path/aicos-validation-demo
```

The script creates a new Git fixture with a deliberately failing validation rule, an actual Node test and documentation. It refuses an existing destination and prints a project configuration to copy into Company. Create a second Dev agent for parallel code/docs tasks, or let the Leader assign both tasks to the default Dev. Use an installed local model, or supply the typed spec/plan/patch shown in each node's input inspector. Production model quality depends on the chosen weights; test fixtures do not prove that quality.

Dependencies must already be present in the selected image or safe snapshot: execution containers have no network. For external dependencies, import and Owner-review the inventory-matching audit/license evidence described in [RELEASE_2.2.md](docs/RELEASE_2.2.md#dependency-evidence). An unknown license or unavailable audit blocks the mandatory gates.

## Verify and package

```bash
dotnet build tests/AICompanyOS.Runtime.Tests -c Release --no-restore
dotnet run --project tests/AICompanyOS.Runtime.Tests -c Release --no-build
npm ci
npm test
npx playwright install --with-deps chromium
npm run test:browser
dotnet publish src/AICompanyOS.Runtime -c Release --no-restore --self-contained false -o artifacts/runtime
```

For required actual Docker integration on Linux, explicitly install `node:22-alpine` and set `AICOS_REQUIRE_SANDBOX=1` for both domain and browser checks. Missing Docker then fails the checks. Without that setting the local suite clearly reports its container phase as NOT RUN. Fixtures supply deterministic model JSON over real loopback HTTP; actual build/test phases execute the repository code in Docker.

[GitHub Actions](https://github.com/minhutc359-ship-it/agent-company/actions) runs Linux/Windows domain checks, runtime packaging, Chromium UI checks and required real-container Linux scenarios, including failing test → correction → passing gates → Owner → scoped commit. Browser screenshots/results are uploaded as CI evidence, not embedded release data.

## Data and recovery

Switch File Store/SQLite in **Dữ liệu & sao lưu** with checksum verification. Encrypted backups contain up to 64 MiB plaintext, with a 128 MiB envelope limit, using AES-256-GCM and PBKDF2. They preserve all company relationships and source/receipt/version history. Worktree directories and external repository history are not copied into the backup; keep the source repository separately.

Restore into an empty data directory, supplying the passphrase interactively on standard input:

```bash
AICOS_DATA_DIR=/absolute/path/new-profile dotnet run --project src/AICompanyOS.Runtime -- --restore-backup /absolute/path/backup.aicos
```

Interrupted running jobs require explicit retry. An incomplete unaccepted Dev worktree can be reconciled from its blocked node inspector; only its registered managed worktree is removed. Accepted artifacts, source HEAD and unrelated worktrees are retained. Ambiguous remote Git failures require explicit release reconciliation/retry against the exact scoped ref.

Historical documents: [1.0 static delivery](docs/RELEASE_1.0.md), [2.0 software workbench](docs/RELEASE_2.0.md), [local inference](docs/LOCAL_INFERENCE.md), [baseline implementation review](docs/IMPLEMENTATION_REVIEW.md). Concept images/prototype are reference material, not runtime evidence.
