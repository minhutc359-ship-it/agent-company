# Work handoff — AI Company OS 2.2

**Mục tiêu:** Tiếp tục triển khai một ứng dụng Local-first Personal Multi-Agent Workspace từ bộ tài liệu v2.2, có Living Agent Office nơi agent đi lại, trao đổi và phản ánh workflow thực.

## Tệp cần chuyển vào ChatGPT Work

1. `AI_Company_OS_Spec_v2.2.md` — nguồn đặc tả đầy đủ; ưu tiên quy định v2.2 (mục 52–62) khi có khác biệt.
2. `AI_Company_OS_Living_Office_Illustration_v2.2.png` — minh họa mục tiêu UX, không coi là screenshot production.
3. `AI_Company_OS_UI_Prototype_v2.2.html` — prototype tĩnh, thao tác mô phỏng 9 handoffs có avatar SVG đi lại và bảng đối thoại.
4. `Software_Delivery_Workflow_Example.yaml` — flow BA → Leader → Dev → Tester/SOC/QA → SubPM.
5. `AI_Company_OS_Office_Live_v2.2.png` — screenshot kiểm thử demo.

## Prompt khởi động trong ChatGPT Work

> Tiếp tục dự án AI Company OS 2.2 theo tài liệu đính kèm. Trước tiên đọc toàn bộ Spec v2.2 và Work handoff; không đánh đồng prototype/screenshot với code production. Hãy xây dựng backlog và triển khai vertical slice chạy thật trên máy người dùng: local runtime, File Store/SQLite, roles/skills/models, workflow BA → Leader → Dev → Tester/SOC/QA → SubPM, durable checkpoints, context snapshots, handoff ACK, report, Owner approval, và Office View là projection từ execution event stream. Giao diện Office có nhân viên đi lại theo lối đi, trò chuyện, họp và chuyển giao artifact; không hiển thị hidden chain-of-thought, không giả trạng thái `verified`. Ưu tiên test, security và local privacy boundary. Nếu kết nối repo được cấp quyền, hãy kiểm tra repo trước khi sửa, chạy tests, báo rõ các file đã đổi, kết quả kiểm chứng và điều chưa hoàn thành. Không push/deploy khi chưa có yêu cầu cụ thể hoặc quyền tương ứng. Cập nhật tiến độ trực tiếp trong lượt làm việc.

## Tiêu chí bàn giao cho giai đoạn đầu

- Chạy ứng dụng ở `127.0.0.1` trên desktop, không cần cloud account.
- Cho phép Owner chọn File Store hoặc SQLite; khi đổi backend phải migrate có checksum.
- Agent/Role/Skill/Model tách riêng, gán nhiều role vào cùng một agent.
- Một workflow có thể pause/restart và khôi phục checkpoint không double-execute.
- Agent-to-agent handoff có packet ID/hash, source refs và ACK.
- Office View nhận event thật, di chuyển/avatar/bubble/board đồng bộ, animation và runtime state phân biệt.
- Không outbound requests trong Strict Local; có cảnh báo/consent cho cloud API.
- Có test tích hợp, audit log, evidence của tester/SOC/QA và SubPM report.

## Trạng thái hiện tại

**Đã có:** bản đặc tả Markdown v2.2, hình minh họa concept, prototype HTML tương tác với trạng thái giả lập. Đã thử thao tác di chuyển, log và reset trên Chromium bằng nội dung trang; không ghi nhận lỗi JavaScript trong lượt thử.

**Chưa có:** agent runtime, local API/backend, DB thực, model/provider integration, A* collision/pathfinding, SignalR, persistence, security scanner, deploy pipeline. Không tuyên bố đã triển khai các chức năng này.
