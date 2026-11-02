# AI Company OS 2.2 — Work-ready package

**Ngày:** 02/11/2026  
**Mục tiêu:** Bổ sung Living Agent Office có avatar đi lại, trò chuyện, họp, handoff, cùng workflow và persistent memory.

## Tệp

- `AI_Company_OS_Spec_v2.2.md` — full specification, gồm mục 52–62 thêm vào.
- `AI_Company_OS_Living_Agent_Office_Work_v2.2.docx` — bản minh họa 4 trang có 2 hình, dễ biên tập trong Word/Work.
- `AI_Company_OS_UI_Prototype_v2.2.html` — demo tương tác mô phỏng 9 handoffs (chạy offline, không có backend hay provider calls).
- `AI_Company_OS_Living_Office_Illustration_v2.2.png` — minh họa concept do AI tạo.
- `AI_Company_OS_Office_Live_v2.2.png` — screenshot kiểm tra demo.
- `AI_Company_OS_v2.2_Office_Addendum.md` — addendum mục 52–62 riêng.
- `Software_Delivery_Workflow_Example.yaml` — YAML flow cũ, ở mức design example.
- `AI_Company_OS_Work_Handoff_v2.2.md` — ngữ cảnh và chỉ dẫn để tiếp tục trong ChatGPT Work.

## Chuyển qua Work

Trong ChatGPT, chọn **Work** từ bộ chọn chế độ (hoặc mở một phiên Work) rồi đính kèm ZIP này. Đưa ra yêu cầu tiếp tục theo `AI_Company_OS_Work_Handoff_v2.2.md`. Cuộc trò chuyện hiện tại không tự đổi chế độ từ việc tạo file.

## Giới hạn

Đây là bộ thiết kế và prototype UI, không tuyên bố đã triển khai production agent runtime, local database, provider calls, A* pathfinding hoặc durable workflow. Minh họa là concept; app HTML chỉ mock events.
