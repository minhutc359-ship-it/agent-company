# Local AI milestone 0.3

The BA/Leader behavior below remains available in 1.0. [Local Static Delivery](RELEASE_1.0.md) adds an explicitly authorized Dev artifact schema with a 4,096 output-token cap and real static-document validators; it does not add shell or repository tools.

This milestone adds a working HTTP provider adapter and persistent two-role inference queue to the 0.2 workspace. The app can execute an installed Ollama model when the Owner explicitly selects **local_model**. It does not download weights, add cloud credentials, execute code or declare a software test/security gate passed.

## End-to-end behavior

1. Owner selects installed model, BA/Leader assignments, objective and up to eight source IDs from the current company.
2. The runtime pins model name/digest, agent bindings, role/skill versions, accepted memory versions and immutable source excerpts. Local AI source IDs must resolve; the scaffold mode still accepts reference labels.
3. A BA invocation is queued durably. One background worker claims it with a unique lease and attempt number, verifies the model, dispatches structured JSON and records the validated public brief and usage.
4. BA completion atomically records an output receipt, spec and handoff. The packet's spec digest includes a reference to the exact BA output digest. It waits for an Owner-mediated ACK.
5. ACK queues Leader using the pinned inputs and the actual BA output, with no implicit tool grants. Leader completion creates the plan from its deliverables and waits for Owner approval.
6. Owner approval records the decision and ends this plan slice. No Dev process, Git write, test command or release is triggered.

The normalized model output is `RoleBrief`: nonempty `summary`, bounded `deliverables`, and bounded `questions`. BA is prompted to identify testable requirements/acceptance criteria; Leader is prompted to plan bounded implementation/verification steps. This is not a complete workflow DSL compiler or the full RequirementSpec/TaskGraph contract from spec 2.2.

## Input, source fidelity and budgets

- A run pins all accepted canonical records and the selected source revisions. Stale canonical source references block a new AI run until reviewed. Registry edits and later memory changes do not silently alter an existing run.
- Each selected source initially contributes its first at most 2,000 UTF-16 characters, with revision/hash/exact range. These are excerpts, not a full-document RAG system.
- The serialized context limit is 10,000 bytes. Only source excerpts may shrink further to fit; their ranges are recomputed and `budgetWarnings` explains the shortening. Objective, accepted records and prior role output remain intact. If mandatory content still exceeds the budget, enqueue/ACK fails with a conflict rather than dropping constraints.
- Requests set `num_ctx=16384`, `num_predict=2048`, structured schema, no tools and `think=false`. The selected model must support the request; failure does not change privacy mode or route.
- Response bodies are limited to 256 KiB and public structured output to bounded fields. Incomplete/length-terminated responses, unexpected tool calls, unknown/duplicate schema fields, malformed usage and known credential patterns are rejected. Credential scanning runs on decoded output too.
- Token counts are provider-reported for the retained result; missing counts remain unknown. Failed/interrupted requests can have unknown usage, and the UI does not claim total token/cost accounting for every attempt. Local inference is not automatically assigned a USD cost of zero.

Input/output hashes are audit checks, not proof that a model reasoned correctly. Prompt policy is part of the versioned application implementation; an input digest mismatch after an incompatible upgrade blocks dispatch and requires a reviewed new run. Model output stays an unverified proposal until human review, even after schema validation.

## Lease, pause/cancel and crash behavior

The worker runs one invocation at a time. A claim increments its persisted attempt count, records worker/lease identity and expires after 30 seconds. Heartbeats every five seconds extend an owned live lease. Provider metadata calls have a 10-second timeout; chat has a 90-second timeout; the worker has a 120-second ceiling.

Every completion must still own the current unexpired lease. Duplicate, expired, paused, cancelled or replaced attempts cannot append outputs. Pause immediately invalidates a running lease; the worker cancels its client request cooperatively. Resume of an interrupted active step can use the remaining attempt. Cancel is terminal and cancels queued invocations too. HTTP cancellation does not guarantee that the Ollama process has stopped computing.

After process restart, queued jobs can resume. Jobs that were running under the previous worker become interrupted/blocked with `RESTART_REQUIRES_RETRY`; they are not blindly regenerated. Expired leases become blocked with `LEASE_EXPIRED`. Explicit retry keeps the same pinned input/model and requires a current checkpoint and idempotency key.

There are at most **two attempts per role**, including the initial call. Repeated pause/resume and failure retries cannot bypass that cap. Inference may compute twice after a retry; only a current owned attempt can commit. No exactly-once external side-effect guarantee is made.

## Local privacy boundary

Default endpoint: `http://127.0.0.1:11434/`; optional `AICOS_OLLAMA_URL` must be another dedicated numeric IPv4 loopback HTTP origin. Remote hosts, DNS names, URL credentials, query/fragment/path overrides, the app port, proxy use and redirects are denied.

Only installed GGUF entries without remote/cloud metadata are selectable. Before each generation, catalog digest, local format, completion capability and declared context capacity are checked again. Source text is sent as untrusted user data, separate from system instructions. No API key or tool is supplied. Provider `thinking` metadata is discarded; the app retains only the public structured brief and safe usage fields.

Configure Ollama itself with cloud features disabled (`OLLAMA_NO_CLOUD=1`, then restart the server). The local server is a trust boundary: the app cannot prove a compromised server's actual network behavior or inspect its OS firewall. No automatic fallback, model pull, remote embedding or cloud account is used.

## Validation

- Existing regression and 114 storage/domain assertions remain passing.
- 81 inference assertions cover File/SQLite parity, pinned role/source context, model/output validation, late/duplicate completion, pause/cancel, retry caps, orphan/expired lease recovery, packing/citation fidelity, escaped credential rejection and cooperative request cancellation.
- 75 HTTP/Chromium assertions use a loopback Ollama protocol fixture, including killing the runtime with SIGKILL during an actual in-flight HTTP generation, then testing recovery and explicit retry. Three Office navigation tests also pass.
- UI tests cover model discovery, BA → ACK → Leader → approval, context/output/usage display, escaped model output and UTF-8 file import. Browser requests stay on the workspace origin; provider requests are server-to-loopback calls.

These tests execute the real adapter, queue and HTTP/UI boundaries against controlled responses. **No real model weights are installed in the verification environment.** Model quality, GPU/RAM behavior, Ollama hardware integration and OS-wide packet capture still require device testing. No Tester/SOC/QA software gate is implemented by asking a model to return PASS.

## Primary API references

- [Ollama chat](https://docs.ollama.com/api/chat)
- [Ollama model catalog](https://docs.ollama.com/api/tags)
- [Official API types: local/remote metadata](https://github.com/ollama/ollama/blob/main/api/types.go)
- [Official OpenAPI contract](https://github.com/ollama/ollama/blob/main/docs/openapi.yaml)
- [Disable cloud features](https://docs.ollama.com/faq#how-do-i-disable-ollamas-cloud-features)
