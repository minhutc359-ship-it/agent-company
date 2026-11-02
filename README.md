# AI Company OS

Local-first personal multi-agent workspace based on the v2.2 specification. This repository is an active implementation; the supplied prototype and design documents remain reference material, not proof of runtime behavior.

## Current milestone

The first vertical slice is a .NET 10 local runtime with an append-only, per-company File Store and a small same-origin web UI. It demonstrates a deterministic BA → Leader handoff with a content hash, receiver acknowledgement, an Owner approval gate, replayable events, and a report endpoint. It makes no provider or tool calls. The generated spec and plan are scaffolding records, not AI output and not verified engineering artifacts.

The runtime binds only to `127.0.0.1:4173`. Its session cookie is HttpOnly and SameSite Strict; write endpoints require the same-origin header. Data is stored outside the repository under the platform local application data directory. Set `AICOS_DATA_DIR` to choose another local path.

## Run

```sh
dotnet run --project src/AICompanyOS.Runtime
```

Open http://127.0.0.1:4173. No cloud account, frontend package installation, or outbound provider call is required.

## Validate

```sh
dotnet build src/AICompanyOS.Runtime/AICompanyOS.Runtime.csproj
dotnet run --project tests/AICompanyOS.Runtime.Tests
```

## Delivery backlog

- [x] Local loopback runtime and local-only UI shell.
- [x] Append-only File Store event log; workflow state is rebuilt by replay.
- [x] BA → Leader packet hash, ACK idempotency, and Owner approval gate.
- [ ] Add formal company/agent/role/skill contracts and enforce per-company authorization.
- [ ] Add SQLite adapter with storage parity fixtures and checksum-verified File Store ↔ SQLite migration.
- [ ] Add resumable workflow checkpoints, leases, and crash-tail recovery tests.
- [ ] Add evidence-backed Tester/SOC/QA gates and SubPM report from actual receipts.
- [ ] Add provider interfaces and explicit cloud egress preview/consent; keep Strict Local default.
- [ ] Add event replay/SSE and Office projection from persisted events.
- [ ] Add source ingestion, citation retrieval, and reviewed canonical memory.
- [ ] Add backup/restore, audit hardening, and adversarial privacy/security tests.

See `AI_Company_OS_2.2_Work_Package/AI_Company_OS_Spec_v2.2.md` and `AI_Company_OS_2.2_Work_Package/AI_Company_OS_Work_Handoff_v2.2.md` for the source requirements and acceptance gates.