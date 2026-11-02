# Local security and privacy boundary

This milestone is a single-owner application running on `127.0.0.1:4173`. The local session intentionally has access to every registered company in the profile. Company scoping prevents accidental cross-company retrieval; it is not a multi-user authorization system.

## Controls implemented

- Loopback bind, exact Host and Origin checks, cross-site bootstrap rejection, HttpOnly/SameSite Strict per-process session and no CORS enablement.
- Same-origin static assets and SSE; CSP forbids inline scripts/styles and framing. Imported source text is escaped, not rendered as executable HTML or instructions.
- Every API company route checks the registry before reading a journal. IDs and source citation ranges are validated. Agent role changes never grant tools or Owner approval authority.
- API fixed-window limiting and one concurrent backup cryptographic operation. Body and individual source/field limits bound memory use for this milestone.
- Single writer per data root. Child symlinks are rejected. File batches write a same-directory temporary file, flush, then rename; SQLite batches commit in a transaction with WAL and synchronous FULL.
- Hash-chained new events. A non-newline tail from the old writer is quarantined before recovery; malformed or modified committed records fail verification. Old unhashed prefixes are reported as legacy, not certified retroactively.
- Backup AEAD authenticates ciphertext with a random salt/nonce and a standard password KDF. Restore stages a new generation and refuses to overwrite an existing profile.

## What these controls do not establish

Active File/SQLite data is **not encrypted at rest**. Use a private data directory and OS disk encryption appropriate to the device. Existing directories retain their OS permissions; the app makes a newly created data root owner-only on Unix. Provider credentials can use an ephemeral process-memory vault or an `AICOS_PROVIDER_*` environment reference. There is no OS keychain/Secret Service integration or encrypted persistent credential vault in this release; RAM/environment values are readable by sufficiently privileged same-user processes and disappear or remain according to their host process/environment lifecycle.

The credential scanner recognizes common patterns and can have false positives and false negatives. Review input before import. It is not a proof that a source contains no secret. Nothing in an imported source can authorize an action.

A local process with the same OS privileges can read process memory or rewrite application files. Event hashes are integrity checks, not external signatures: an attacker able to rewrite the whole store and recompute hashes is outside this protection. Removing a complete log suffix is not detectable without an independently trusted head/checkpoint. Older unhashed data cannot be independently authenticated.

File Store batch rename is tested on the local Linux filesystem. Directory fsync, network-drive semantics and hardware power-loss behavior are not established. Temporary files from an interrupted atomic write are not replayed. A failure after an external side effect would require reconciliation; the software executor records prepared worktree/release operations and requires explicit reconciliation when their outcome is uncertain. Inference leases fence late results after pause/cancel/restart; cancelling a client HTTP request does not prove Ollama has stopped computing. A model call may compute more than once after explicit retry, while only the owned, current attempt may commit output.

Backups use PBKDF2-HMAC-SHA256 (600,000 iterations) and AES-256-GCM. A forgotten passphrase cannot be recovered. Backup encryption does not guarantee removal of plaintext from managed process memory, swap, filesystem snapshots or OS backups. Export captures the complete profile under one writer lock, then encrypts outside that lock. Do not include real secrets in test fixtures.

## Network status

The runtime has an opt-in local Ollama client and an optional Controlled Cloud gateway. There is no remote DB, telemetry, update checker or automatic cloud fallback. Local inference remains default. Explicit Owner-approved GitHub release is a separate optional network capability. A previously queued local-model job can resume after restart; interrupted local/cloud work requires explicit retry. The Ollama transport disables proxy use and redirects, accepts only numeric IPv4 loopback, filters remote/cloud models and rechecks the pinned digest/GGUF/capabilities/context metadata. Configure the trusted Ollama server with cloud features disabled. Metadata checks cannot prove the behavior of a compromised local server.

UI fonts/graphics/scripts are local. The browser integration fixture recorded zero non-loopback requests. This is not an OS-wide packet-capture or firewall conformance report. SDK/NuGet/npm/browser installation can contact package services during development; that tooling is separate from runtime behavior.

Controlled Cloud supports only runtime-fixed HTTPS destinations for OpenAI Responses, Anthropic Messages and Gemini Interactions. The HTTP client disables redirects and proxies. A provider configuration alone never calls the network. Strict Local blocks preparation and dispatch. Controlled Cloud requires a locally persisted exact credential-free payload, provenance/classification/size/retention manifest, matching request hash, explicit Owner consent, current provider/policy versions and a credential available only at dispatch. Revoking policy fences queued work but cannot recall bytes already accepted by the provider.

The gateway sets stateless request controls where available, validates bounded structured public output and stores only response/metadata hashes plus measured usage and the public proposal. Provider processing, abuse monitoring, logs, regional handling, feature-specific storage and Zero Data Retention eligibility remain outside the runtime and must be verified for the actual account. The deterministic adapter tests use local HTTP handlers; they do not make paid calls or certify provider terms. Secret-pattern scanning is incomplete, so the exact-payload review remains mandatory.


## Static delivery boundary (1.0)

Delivery preparation is an explicit per-run grant to produce a bounded text package, not a shell, filesystem or repository permission. Candidate paths are restricted root-level names and never passed to a filesystem API. File bytes live in the existing event journal. ZIP entries are generated under `files/`; path traversal, case collisions, duplicate names, executable extensions and oversized bodies are rejected. No process is launched and no generated script runs.

HTML uses a bounded XML reader with DTD prohibited and resolver disabled, then a passive tag/attribute/namespace policy. XML comments, CDATA, processing instructions and self-closed non-void tags are rejected to reduce XML-to-HTML parsing ambiguities. CSS policy rejects imports/at-rules, backslash escapes, URL/image functions and active constructs after linear comment/whitespace normalization. This is a conservative supported format, not a general HTML sanitizer. JSON and text remain text; Markdown is not rendered in the application.

Only a passing artifact may reach the preview. The iframe has an empty sandbox attribute; its response enforces a separate sandbox CSP, `default-src 'none'`, scripts/connect/images disabled, no forms/base/object, and embedding restricted to this origin. Checked local CSS is inlined for `index.html`; other links are not a full-site preview navigator. The main application's CSP remains restrictive.

Tester/SOC/QA receipts name the exact artifact digest, pinned agent, recognized static tool/version and hash of executed check results. They cannot be submitted through an API and are not inferred from model text. Shared Dev/reviewer identities are labelled self-reviewed. Passing static checks do not mean a general application was built/tested, that dependencies were audited, or that content rights are known. Owner must separately attest meaning/acceptance and provenance/rights before exact-hash ZIP export. Failed checks cannot be overridden by approval. Active storage remains plaintext, with a 64 MiB plaintext / 128 MiB envelope backup bound.


## Software execution boundary (2.0)

Owner-approved manifests bind project base, task scopes, model/skill bindings, image digest and typed commands. Source code never grants permissions. Git plumbing reads committed UTF-8 blobs without checkout filters/hooks, creates managed detached worktrees with `--no-checkout`, and stages exact accepted bytes with hash-object/update-index. It does not execute project hooks, shell command interpolation or generated code on the host. Same-user filesystem/config races are outside this local application's isolation guarantee; use repositories and data directories controlled by the Owner.

Generated build/test code runs only in an explicitly local Linux Docker engine, network none, no host mounts, unprivileged user, dropped capabilities, no-new-privileges, read-only root, bounded tmpfs/CPU/memory/PID/timeout/output and digest-pinned preinstalled image. The Docker engine is a trusted local dependency with significant OS authority; container isolation is not proof against kernel/engine vulnerabilities or malicious images. The application never auto-pulls an image. Missing/unverifiable execution yields NOT_RUN/BLOCK with no host fallback. A dependency-bearing project must provide its runtime dependencies offline and fresh reviewed inventory-bound audit/license evidence.

Receipts bind job/attempt/role/agent/candidate/command/image/log hash. Static SOC rules are a bounded scan, not a complete vulnerability database or penetration test. Semantic acceptance, copyright and asset provenance require Owner review. Imported audit evidence is labelled as such, not presented as a runtime network scan. Model output, public messages, skill metadata and proposed memory cannot satisfy Owner gates or grant tools.

Git release requires accepted passing bytes and a separate exact-target manifest. It creates only the run's scoped branch. Optional push uses a credential-free GitHub HTTPS destination, verifies the base ancestry and exact remote SHA, refuses to overwrite differing remote content and opens a draft PR without merge/deploy. The token is accepted only by the explicit release request, kept in process memory and passed to the child Git client through scoped environment configuration, never command arguments, prompts or persisted events. Same-user processes can still inspect process memory/environment. Policy is rechecked before dispatch; revocation cannot recall an already in-flight network request. Ambiguous success must be reconciled against the scoped ref and existing PR, not blindly replayed.

Browser outbound-request fixtures cover the UI. Docker network-none and local-provider validation are separate tested boundaries. Real host firewall/packet capture, user-device Docker Desktop, real model weights and a live remote release remain additional acceptance tasks.
