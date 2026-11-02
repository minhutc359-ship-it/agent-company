# Rà soát triển khai AI Company OS 2.2

Bản hiện tại là [2.2 Company OS](RELEASE_2.2.md). Phần bên dưới giữ lại như báo cáo lịch sử 0.2; không dùng backlog này để suy ra trạng thái hiện tại.

Ngày kiểm tra: **02/11/2026**. Baseline: `a803439`, “Initial local-first AI Company OS runtime”. Tài liệu này ghi nhận mốc **0.2 durable workspace**, chưa phải nghiệm thu toàn bộ sản phẩm production.

Cập nhật 0.3 về Ollama và worker được ghi riêng tại [LOCAL_INFERENCE.md](LOCAL_INFERENCE.md). Backlog dưới đây là snapshot tại 0.2; local inference/lease đã được triển khai bước đầu trong 0.3.

Đã đọc đặc tả 62 mục, Work Handoff, Office addendum và workflow YAML; đối chiếu với runtime, test và prototype. Mục 38–62 được ưu tiên khi các đề xuất cũ khác nhau. Giữ modular monolith .NET 10, không chuyển sang framework agent khác hay microservices.

## Những thiếu hụt quan trọng ở baseline

1. UI chỉ có một company cố định; BA và Leader là identity hard-code, chưa có registry N:M.
2. Một command ghi nhiều dòng riêng lẻ, phải sửa phần thiếu khi bị gián đoạn; chưa có hash chain, SQLite hoặc migration.
3. Chưa có pause/resume/cancel, kiểm tra checkpoint stale hoặc snapshot UI nhất quán.
4. Nhãn nguồn chưa được giải quyết nhưng lại nằm trong `EvidenceRefs` của report. Nay tách nhãn tham chiếu khỏi evidence thực thi.
5. Không có Office live, source revision, citation retrieval, reviewed memory, backup hay CI.
6. Spec/plan là scaffolding cố định. Đây là giới hạn cần giữ minh bạch, không đổi nhãn thành AI execution.

## Ma trận yêu cầu và kết quả

| Nhóm trong spec | Triển khai ở mốc này | Giới hạn còn lại |
|---|---|---|
| Company isolation, §25/38 | Registry bền vững; HTTP chỉ mở company đã đăng ký; dữ liệu tìm kiếm/trích dẫn tách scope | Một local Owner quản lý mọi company; chưa có SaaS/multi-user ACL |
| Agent/Role/Skill, §39/41/46 | 7 role contract v1.0.0, nhiều role/agent, optimistic version, bindings chụp trong event tạo run | Skill chỉ là metadata built-in; chưa có package installer/model routes |
| Storage, §40 | Atomic File Store batch; SQLite WAL/FULL; chuỗi hash; single writer; backend migration có parity digest | File Store rewrite toàn log mỗi batch; chưa tối ưu log lớn/compaction |
| Workflow durability, §43 | Pause/resume/cancel với checkpoint và idempotency; giữ cùng gate sau restart | Chưa có job leases/heartbeat hay external side effects để phục hồi |
| Handoff, §44/55 | Packet ràng buộc spec digest, destination, snapshot, canonical records; ACK đúng receiver/hash; state pin | Chưa có model-to-model inference hoặc token-aware context packing |
| Knowledge, §27 | Immutable revision/hash; lexical search; citation kiểm tra company/revision/range; propose/review/canonical versions | Không embedding/FTS5 ranking/reranker; semantic relevance do Owner đối chiếu |
| Report, §41/43 | Report lấy artifact/checkpoint thật; không giả receipts từ nhãn nguồn | Chưa có Dev, Tester/SOC/QA evidence để kết luận pass |
| Office, §52–60 | SVG isometric local; A* tránh furniture/occupied cell; SSE cursor; inspector/list; visual pause riêng; reduced motion | Chưa có meetings, sprite atlas hoặc benchmark 25 avatar; đơn giản hóa thành một mover tại một thời điểm |
| Backup/security, §28/40 | Authenticated encrypted archive, restore mới, session/Origin/Host/CSP/no-store, child-symlink rejection | Chưa có vault, mã hóa active DB, firewall audit hoặc signed installer |

## Thay đổi kỹ thuật

- `Storage.cs`: journal adapter, validation/hash chain, atomic local writes và SQLite transaction. `FileEventStore` giữ tên public cũ để test/client C# hiện tại tiếp tục dùng; backend active có thể là File hoặc SQLite.
- `StorageManagement.cs`: kiểm tra manifest, freeze writer, shadow generation, count/hash parity, pointer switch. `storage.json` giữ nguồn/digest/thời gian chuyển; các generation trước vẫn tồn tại và không được ghi tiếp.
- `Organization.cs`: company, agent, versioned role/skill catalog; gán role không hợp nhất quyền công cụ.
- `Knowledge.cs`: nguồn có phiên bản, scanner mẫu credential phổ biến, tìm lexical có citation, proposal và review. Source stale chặn acceptance mới; quyết định đã duyệt vẫn giữ lịch sử và UI cảnh báo nguồn mới.
- `Backups.cs`: PBKDF2-HMAC-SHA256 600.000 vòng, salt ngẫu nhiên 16 byte, AES-256-GCM nonce 12 byte/tag 16 byte. Không có mật khẩu backup trong event log.
- `OfficeProjection.cs`: một snapshot lấy dưới cùng lock, cùng event cursor cho workflow/Office/knowledge; không gán trạng thái coding/testing khi chưa có worker.
- `Program.cs`: API company-scoped, structured errors, request limits, rate/concurrency limits, SSE replay và CLI restore.
- `wwwroot/`: giao diện tiếng Việt, responsive; không inline JS/CSS, CDN, font tải mạng hoặc browser storage làm canonical state.

## Kết quả kiểm tra

| Lớp | Kết quả kiểm tra local |
|---|---|
| Build .NET 10 | Thành công, 0 warning/0 error |
| Regression baseline | Create/ACK/approval idempotency, wrong hash/receiver, restart, torn tail, sequence cursor và company scope đều pass |
| Domain/storage mở rộng | 114 assertions pass trên File và SQLite: N:M role, version pin, memory/citation, race review, lifecycle, migration, encrypted restore, legacy log, null citation, SQLite row tampering, culture-independent digest, case-only company alias và symlink escape |
| Client navigation | 3 tests pass: đường đi hợp lệ, tránh furniture/occupied target và từ chối route không hợp lệ |
| HTTP + Chromium | 45 assertions pass: auth/Origin/Host, SSE replay, thao tác UI, reload checkpoint, reviewed memory, migration, backup và restart tiến trình thật |
| UI/network | Desktop 1440×1080 và mobile 390×844; không có lỗi JavaScript/console hoặc horizontal overflow trong flow kiểm tra; 0 request browser ra ngoài origin loopback |

Môi trường local: Ubuntu 24.04 x64, .NET SDK 10.0.403/runtime 10.0.14, Node 24.22.1, Playwright 1.62.1 và headless Chromium 153.0.8010.0. Dùng browser binary local thay cho download mặc định không truy cập được trong môi trường kiểm tra. CI dùng browser do Playwright cài. Windows CI, thiết bị thật, audit firewall, power-cut durability và số liệu performance chưa được kiểm chứng trong lượt này.

Các assertion theo fixture không chứng minh “100% an toàn”, “exactly-once external effects”, context fidelity qua nhiều model hay production readiness. Chưa có provider/tool call trong runtime để đo những tiêu chí đó.

## Remaining product backlog

Thứ tự phụ thuộc được đề xuất để đi từ mốc hiện tại đến sản phẩm đầy đủ:

| Ưu tiên | Công việc | Gate nghiệm thu |
|---|---|---|
| P0-1 | Local model adapter, model capability registry, typed invocation, time/token caps | Inference thật có snapshot/input hash/output provenance; malformed output bị chặn; không cấp tool grant từ model |
| P0-2 | Durable worker: leases, heartbeat, bounded retries, reconciliation và cancellation | Kill/restart giữa step không double-apply; side effect không rõ trạng thái phải chờ đối chiếu |
| P0-3 | Read-only repo tools rồi isolated worktree + command sandbox có grants/preview | Path/network/process limits được test; ghi receipt với command/exit code/log/diff digest |
| P0-4 | Dev → Tester/SOC/QA → SubPM pipeline và typed artifact validators | Mandatory gates có evidence thật; critical finding/unknown provenance chặn release; report được tạo cả khi fail |
| P0-5 | Controlled Cloud egress gateway + exact payload consent + vault | Manifest/hash/destination khớp; credentials không vào prompts/logs; Strict Local không thể fallback sang cloud |
| P1-1 | Workflow compiler/DAG, pinned node schemas/skill packages | Validate dependency/cycle/bounded loop, runOnFailure, skipped_satisfied và workflow versioning |
| P1-2 | Conversation/branches, context packing, FTS5/hybrid retrieval và golden corpus | Citation/fidelity/privacy/token metrics đo trên corpus Việt/Anh; source stale/conflict/denied classification không bị bỏ qua |
| P1-3 | Streaming export/restore lớn, index rebuild, crash/corruption fault injection | Restore giữ mọi relationships, backup vượt 8 MiB; kiểm tra power-cut trên filesystem mục tiêu |
| P2 | Meeting interactions, richer sprites, 25-agent performance, signed installers và auto-update opt-in | Office phản ánh worker events, collision/perf/reduced-motion pass; packaging/privacy review trên từng OS |

Không thể tuyên bố hoàn tất spec 2.2 trước khi các P0 có evidence thực thi và được nghiệm thu. Không cần triển khai external PostgreSQL, A2A hay cloud deployment để hoàn thành local-first core.
