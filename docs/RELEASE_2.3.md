# 2.3 Living Studio, Work Mode and visual workflow editing

This release adds original chibi character/decor atlases to the running office, a combined Work Mode, an editable suggested team and a visual editor for the supported software workflow. It preserves the [2.2 execution and privacy contract](RELEASE_2.2.md).

## Implemented user flow

1. Create a company with **Cài đội ngũ, skill và workflow gợi ý** checked, optionally discover and select an installed local model in its expandable model selector; or use **Bộ đội gợi ý** in Work Mode / **Thiết lập đội ngũ** in Agents & Skills for an existing company.
2. Preview the eight suggested members, role purposes and three workflow templates. Choose an installed local model or configure models later. Installation adds missing records only and preserves existing agent identities, roles, model routes, budgets and skills.
3. Edit each agent's model, department, budgets and skill references in Agents & Skills; change role assignments in Nhân sự & vai trò. Use **Tạo phiên bản sửa** to create a new skill instruction version, then explicitly select that version in the relevant agent configuration.
4. In **Workflows & Tasks → Workflow Studio**, start from a preset or saved version. Drag nodes, connect an output port to an input port, or edit dependencies in the selected node's form. Node selection/connection and movement also support keyboard navigation. Undo/redo, reset arrangement, zoom, JSON import/export, budgets, retry counts and correction limits are available.
5. Validate before saving. Missing mandatory gates, unsupported kinds, cycles, invalid budgets and immutable-version conflicts are rejected by the backend. Save creates a new executable definition version; choose that version when creating a run. Running seeds keep their pinned definition.
6. Work Mode combines the actual office, execution progress, current tasks, saved results, pending decisions and a public conversation board. The composer persists messages through the company API; an unsent draft survives realtime rerendering. Sending a public message does not automatically invoke a model.

## Suggested team and templates

The starter pack is `software-team@1.0.0`. It installs seven versioned instruction packages covering BA, Leader, Dev, Tester, SOC, QA and SubPM; the eighth suggested member is a second Dev for independent tasks. The existing seven default identities are reused. Installation does not rename or overwrite them, start a run, enable cloud, set a remote endpoint or grant tools. Agent and workflow changes remain separate from project execution/Owner approvals.

| Template | Behavior |
|---|---|
| Phần mềm · song song | `software-delivery@2.3.0`: two jobs concurrently, up to two corrective rounds, 30-minute and 40,000-output-token run limits |
| Phần mềm · kiểm tra tuần tự | `software-reviewed@1.0.0`: Tester → SOC → QA additional dependencies; two jobs concurrently, 45 minutes, 60,000 output tokens |
| Phần mềm · tập trung | `software-focused@1.0.0`: one job concurrently, two attempts/job, one corrective round, 20 minutes, 24,000 output tokens |

These are adjustable starting points, not model-quality guarantees. Tester/SOC/QA runtime evidence remains distinct from skill instructions or generated prose. The visual editor supports the thirteen executable software contract nodes; it does not claim arbitrary Prompt/Tool/MCP/plugin execution.

Starter installation uses one guarded event batch after conflict checks. Idempotency keys bind the exact request; command replay returns its original result. Reinstalling with a fresh key skips existing entities/configurations. File and SQLite tests cover preservation, replay, isolation, invalid model rejection and restart.

## Graphics and animation

- Original generated assets: eight role-themed chibi characters and eight reusable furniture/decor props, each in a transparent 4×2 PNG atlas. Both are bundled locally; there is no remote image host, font or asset CDN.
- The native isometric scene composes the atlases with floor, walls, windows, signage and room lighting. Props and agents are depth-sorted; decorative props are at the perimeter, while desk/meeting footprints retain navigation obstacles.
- A* and tile reservations drive movement. Procedural 2D animation adds walking sway/bob, breathing, working motion, conversation indicators and handoff trails from persisted states. This is not a rigged 3D model, video, or a frame-by-frame multidirectional sprite animation.
- Night/day lighting, zoom, pan after zoom, camera reset, follow, department filter, focus view, board-only view and reduced motion are available. Visual pause and hidden views pause presentation; they do not manufacture workflow progress or stop execution.
- Work Mode shortcuts: Ctrl/Cmd+K opens Knowledge search, Ctrl/Cmd+N opens a public message, Ctrl/Cmd+Shift+N opens a new software run, Space on the page background toggles visual pause, and Escape exits focus/resets the camera. Modal and text-input editing retain their native keyboard behavior.
- Progress counts are derived from stored node status and rendered with a native progress control under the existing strict CSP. Identifiers/hashes stay in technical disclosures.

Artwork provenance, prompts, runtime file paths and art direction are documented in [ART_DIRECTION.md](ART_DIRECTION.md). The runtime now uses this artwork directly; the older concept illustration remains a design reference rather than a screenshot of shipped behavior.

## Persistence and verification boundaries

Execution definitions, starter entities, agent configuration, skills and messages persist in the company event store and its backup. Per-company lighting/board-only choices and designer node positions are optional browser display preferences; they do not alter execution definitions and are not part of the event-store backup. Unsaved designer definitions remain modal state until saved/exported.

Verification covers UI → API → persisted definition → pinned software run, invalid connection rejection, actual pointer dragging, skill version editing, starter installation, live-board updates/draft retention, reload, camera controls, focus, mobile width, CSP errors, local assets and the existing actual Docker flow in Linux CI. Model responses are protocol fixtures. Real model quality, accessibility on target devices, OS-wide privacy measurement and full production distribution still require separate acceptance.

MCP/A2A/plugins, generic additional node kinds, neural embeddings/ANN, OCR, binary/deletion patches, OS keychain/active-workspace encryption and signed installers/auto-update retain their earlier explicit boundaries.
