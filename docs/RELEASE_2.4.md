# 2.4 · Local Setup & Team Demo

People configuring an AI company previously had to infer the difference between role assignments, local models, cloud API credentials and separate CLI logins, and build their own fixture to see execution. This release provides explicit setup choices and a real managed demo, while preserving the existing software gates.

## Delivered

- Company **Thiết lập AI & Demo**: .NET/storage/Git/Ollama catalog/Docker diagnostics, selected-agent model application preserving other configuration, cloud credential/model-list checks, external CLI login/handoff instructions and demo creation/opening.
- Windows/Ubuntu setup ZIP builder and resumable Python stdlib installer: missing Git, private .NET SDK/Node LTS, local Docker Linux prerequisites, dedicated Ollama `127.0.0.1:11435` with cloud disabled, selected model, explicit `node:22-alpine` pull, verified File → SQLite initialization and a demo. Local READ-ONLY `--plan`/`--check`, progress state without secrets, integrity manifest, start/agent-terminal launchers and Vietnamese guide included. Optional CLI npm packages never auto-login.
- Real demo Git repository with an intentionally failing Node acceptance test, editable source/docs, a separate eight-member starter company/project and prefilled objective. Reinstallation preserves source/configuration and never starts inference or claims a passing test.
- Bounded local team discussion: 2–4 distinct configured participants, frozen role/model/skill budgets, actual previous public answers in later context, durable turn advancement and recovery, at most two attempts/person and two active sessions/company, cancellation with lease fencing. The chain stops after one answer/person; no new tools, accepted memory or approvals arise from conversation.
- External handoff ZIP for waiting BA/Leader/Dev nodes: actual pinned input, schema, request and checkpoint; file import into the existing version-fenced typed-output flow. External output remains Owner-supplied; the runtime does not silently run CLIs or reuse their credentials.
- Office layout: work clusters, native chairs, department rugs, walking aisle and perimeter decoration; stable avatar identity across active role changes, cell-inset atlas crops, motion-facing presentation and interpolated depth ordering.

## Verification and limits

Domain tests cover both File/SQLite stores, actual Git demo creation, preserved edits/routes, complete handoff bytes, discussion order/history/pins/termination/cancel/retry and fixed-endpoint credential-only cloud model listing. Installer tests cover read-only modes, archive traversal/links, checksums, real read-only SQLite inspection, private environment and download origins. Chromium stories exercise setup → persisted selected route → separate demo → external result import → local Leader continuation, and automatic local discussions, alongside the existing Work Mode/Studio/office/mobile stories. CI requires actual Docker execution on Linux and also builds/tests/packages on Windows.

Model responses in automated acceptance tests are HTTP protocol fixtures. Real model quality/performance, live provider accounts/quotas, clean-machine vendor installer/UAC/WSL2/GPU/reboot behavior and signed distribution are not certified by those tests. The bundle downloads prerequisites during setup; it is not an offline or signed installer. READY means the checked local prerequisites exist, not that arbitrary projects/models/accounts will always succeed.

Local Ollama remains the autonomous software/discussion provider. OpenAI/Gemini/Anthropic cloud remains exact-preview-approved conversation proposal only. Codex/ChatGPT, Gemini CLI and Copilot CLI use manual reviewed handoff/import; direct unattended CLI adapters are not included. Characters remain procedural 2D presentation of existing raster atlases rather than 3D/multidirectional frame animations.

See [full setup guide](../setup/README.vi.md), [API additions](API.md#local-setup-demo-and-team-discussions-24), [prior boundaries](RELEASE_2.2.md), [visual baseline](RELEASE_2.3.md), and [remaining spec work](HUONG_DAN_SU_DUNG.vi.md).
