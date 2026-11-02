# AI Company OS 1.0 — Local Static Delivery

1.0 is the first complete **bounded local delivery workflow**, built on the 0.2 durable workspace and 0.3 local-model runtime. It turns an approved plan into a passive static document/site, records real validator receipts, requires separate final human acceptance and exports exact files plus evidence. It is not full acceptance of the much broader product specification 2.2.

## Supported contract

| Capability | 1.0 behavior | Boundary |
|---|---|---|
| BA/Leader | Deterministic scaffold or explicitly selected installed Ollama model; hashed handoff and Owner plan decision | AI conclusions remain proposals |
| Delivery scope | Owner explicitly prepares `static-delivery/1.0.0`, pins five role bindings and 1–12 literal acceptance criteria | Plan approval alone never queues Dev |
| Dev | Owner supplies a JSON file manifest or separately authorizes the run's pinned local model to generate one | No arbitrary commands, repository reads/writes, tools, scripts, binaries, images or deployment |
| Files | `index.html` required; up to eight root-level `.html/.css/.txt/.md/.json` files, 48 KiB UTF-8 total, 32,768 characters per file | Safe lowercase names; no paths, traversal, duplicate entries or overwrites |
| Tester | Executes bounded XHTML parsing, required document/head/body/title/heading checks, JSON parsing and exact UTF-8/manifest checksums | No browser compatibility claim, application build or JavaScript execution |
| SOC | Executes a fail-closed passive-markup allowlist and CSS/credential rules; malformed markup, DTDs, processing instructions, XML comments/CDATA, active elements/attributes and external resource references block | Static versioned policy, not a comprehensive secret/CVE scanner or penetration test |
| QA | Executes Owner-defined exact-string criteria against specified files | Literal matches do not establish meaning, completeness or ownership |
| SubPM | Deterministically aggregates receipts for success and failure, with reporter identity, artifact hash, outstanding human review and limits | No model prose is promoted into test evidence |
| Owner gate | Separate acceptance and rights/provenance confirmations bind the current artifact hash and checkpoint | No automatic legal clearance; failed gates cannot be overridden |
| Preview/export | Passing artifact can preview in an opaque-origin iframe plus response CSP sandbox. Accepted artifact exports ZIP with files, report and checksums | Preview shows `index.html` with local CSS inlined; no scripts, images, network, forms or embedding |

HTML files must be well-formed, lowercase XHTML without a DOCTYPE, with non-void tags explicitly closed. The allowed static vocabulary is implemented in `DeliveryArtifacts.cs`; style blocks and local stylesheet links are supported. CSS at-rules (including media/font/import rules), escape sequences, URL/image functions and active constructs are intentionally unsupported. Plain text and Markdown are stored/exported as text, not rendered or executed in the workspace. This conservative contract makes the supported format explicit instead of trying to sandbox arbitrary generated applications.

## Complete user journey

1. Create a company/run and choose scaffold or local AI. Complete BA → handoff ACK → Leader and review the plan.
2. Click **Chuẩn bị sản phẩm bàn giao**. Choose manual files or an explicitly authorized local Dev invocation, assign each delivery role and enter criteria such as `index.html | Keep data local.`.
3. For manual mode, use **Nhập file sản phẩm** and edit the example JSON. For local mode, the durable worker generates an `ArtifactDraft` with a distinct structured schema and 4,096 output-token cap. The model receives the actual Leader output, approved plan, accepted memory, pinned sources and Owner criteria, without shell/Git/network permissions.
4. All three static validators run on exact produced bytes. The event transaction commits the artifact, every check receipt and the SubPM summary together. A model statement such as “tests passed” cannot produce a receipt.
5. Inspect file digests, individual checks, failures and the isolated preview. A failed check blocks both preview and final approval. Reject a passing candidate if its meaning or content rights are not acceptable; corrected candidates get new versions and new receipts.
6. Owner confirms actual acceptance and content rights. Only that exact approved artifact can be exported as ZIP. Export records no publication or deployment.

`examples/static-delivery.json` is an editable offline fixture, not an AI output or a complete fulfillment of an arbitrary objective.

## State, budgets and failure handling

The existing plan slice remains compatible. Delivery adds:

`approved → awaiting_delivery_input` (manual) or `queued → running` (local Dev), then `awaiting_delivery_approval | delivery_blocked → delivered | delivery_revision_requested`.

Manual corrections can be submitted only from input, failed-check or requested-revision checkpoints. There are at most **three artifact versions** per run, and at most **two model attempts** per role including the initial attempt. Owner criteria/bindings are immutable after preparation; changed scope requires a new run. Accepted artifacts cannot be replaced. Pause/cancel and expected-sequence/idempotency validation apply to delivery commands too. A cancelled run cannot export.

Dev uses the same durable leases, heartbeats, cancellation, pinned input hash and explicit restart retry as BA/Leader. Interrupted generation does not create an artifact. Validation tools have no external effects: their outputs commit as one atomic journal batch; restart cannot leave a “PASS” without its corresponding file snapshot. No exactly-once external-side-effect claim is made.

Receipts include role/agent identity, static tool/version, exact artifact digest, executed checks, start/end times, exit code and a hash of the check log. Shared Dev/reviewer identities are explicitly `selfReviewed`; identities do not isolate processes. The trusted in-process validators execute independently of model text.

Only passing receipts for the recognized tool/version and pinned reviewer identity qualify. Export rechecks file/manifest digests and receipt bindings. Reports distinguish provider-reported usage, generated file provenance, executed static checks and human approval. Neither static checks nor human approval prove all requirements of an arbitrary software application.

## Verification and release use

Run the commands in README. The test suite covers both storage backends, failing security/acceptance checks, immutable revisions, stale approvals, missing rights confirmations, forged/mismatched receipts, late model completion, three-version exhaustion, model restart/retry, checksum-verified migration and encrypted backup/restore. Browser tests traverse both manual and HTTP-model Dev delivery, isolated preview, revision request, explicit final approval, ZIP download, mobile layout and restart persistence. Provider responses are controlled loopback protocol fixtures; real weights/model quality/GPU/RAM and device behavior still require hardware testing.

Local release verification: .NET 10 Release build and runtime-dependent publish succeeded with zero warnings/errors; the baseline regression, 114 storage/domain assertions, 81 inference assertions, 134 delivery assertions and three Office navigation tests passed. The published runtime passed 94 API/Chromium assertions, including actual process restart, manual/local-model delivery, sandboxed preview, final approval and ZIP download. Browser traffic remained on the local workspace origin. Test environment: Ubuntu x64, .NET SDK 10.0.403/runtime 10.0.14, Node 24.22.1, Playwright 1.62.1, Chromium 153.0.8010.0. These counts exclude any remote CI result.

CI builds/tests Linux and Windows, runs Chromium on Linux and packages runtime-dependent outputs for both systems. Those packages require the .NET 10 runtime and are unsigned; source users can run with the .NET 10 SDK. CI status must be inspected after pushing; local passing checks are not remote CI results.

## Deferred beyond this edition

Arbitrary repository mutation, shell/process isolation, actual application builds/test commands, dependency-vulnerability databases, general workflow/DAG execution, cloud providers/egress consent, conversation branches, semantic search, signed installers and 25-avatar performance acceptance remain outside 1.0. Existing versioned organization, reviewed memory, lexical citations, Office projection, File/SQLite migration and encrypted backup remain available.

Source requirements and concept/prototype material are preserved unchanged. Future broader executors must introduce their own explicit grants, sandbox boundary, real command receipts and supported release gates before claiming software execution.

## Implementation references

- [Microsoft XML settings and parser limits](https://learn.microsoft.com/en-us/dotnet/api/system.xml.xmlreadersettings?view=net-10.0)
- [W3C Content Security Policy, sandbox and frame ancestors](https://www.w3.org/TR/CSP/)
- [Ollama protocol and local trust boundary](LOCAL_INFERENCE.md)
