# Hướng dẫn sử dụng và các bước còn lại của AI Company OS

Tài liệu này áp dụng cho **Living Studio 2.3** trên nhánh `feat/company-software-workflow`. Luồng phần mềm local đã có triển khai và kiểm thử tích hợp; chưa thể gọi là hoàn thành 100% toàn bộ spec. Các ảnh concept trong work package là tài liệu thiết kế. Xem [RELEASE_2.3](RELEASE_2.3.md) cho Work Mode, đồ họa và Studio mới; [RELEASE_2.2](RELEASE_2.2.md) giữ hợp đồng thực thi/quyền riêng tư.

## 1. UI hiện tại so với ảnh mẫu

| Hạng mục | Trạng thái thực tế | Phần còn lại |
|---|---|---|
| Văn phòng isometric | Đã gắn 8 nhân vật chibi và 8 loại nội thất vào cảnh nhiều lớp, có ánh sáng đêm/ngày | Là concept mới được triển khai bằng asset thật; không phải bản sao từng pixel của ảnh mẫu |
| Animation | Có A*, tránh va chạm, chuyển động 2D đi/làm việc/thở, hội thoại, họp/handoff theo trạng thái | Dùng chuyển động lập trình trên sprite 2D; chưa phải nhân vật 3D hoặc sprite nhiều khung/hướng nhìn |
| Tương tác | Chọn nhân viên, inspector, theo dõi camera, mở trao đổi từ bubble; có dừng hoạt ảnh, reduced motion và board-only | Đã có Work Mode với tiến độ/board cạnh văn phòng, task/artifact phía dưới, pan/zoom/focus và phím tắt |
| Độ dễ đọc | Tiêu đề gọn hơn; nội dung chính ưu tiên tên nhân viên, nhiệm vụ, tên nhánh, câu trả lời và trạng thái | Cần nghiệm thu cỡ chữ, tương phản và mật độ thông tin trên thiết bị thật |
| Mã kỹ thuật | ID/hash/key bộ nhớ nằm trong mục mở rộng; tên model và đường dẫn cần thiết vẫn hiện khi cấu hình hoặc duyệt phạm vi | API key là trường mật khẩu và không được đưa vào inspector, log hay event |
| Workflow Studio | Có kéo node, nối phụ thuộc, form chỉnh, undo/redo, 3 mẫu sẵn, JSON import/export và lưu phiên bản thực thi | Hỗ trợ 13 node phần mềm; chưa thực thi node/plugin tùy ý |

**Kết luận về hình ảnh:** bản 2.3 đã dùng bộ chibi/nội thất mới trong runtime và bố cục Work Mode thật. Phong cách đã tiến gần hướng concept; không tuyên bố giống hệt `AI_Company_OS_Living_Office_Illustration_v2.2.png`. [Art direction và provenance](ART_DIRECTION.md) ghi rõ asset, prompt và cách tạo hoạt ảnh.

Đổi ánh sáng đêm/ngày, phóng to rồi kéo camera, chọn nhân viên để theo dõi hoặc bật **Tập trung** để mở rộng không gian. **Chỉ xem bảng** và reduced motion giúp giảm chuyển động. Asset được đóng gói local, không tải từ CDN.

## 2. Cài và mở ứng dụng

Chuẩn bị .NET 10 SDK và Git. Muốn thực thi build/test cần Docker trên máy local, sử dụng Linux containers. Muốn AI tự tạo spec/plan/patch cần Ollama và model GGUF completion đã cài, có context tối thiểu 16K; chất lượng đầu ra còn phụ thuộc model và tài nguyên máy.

Clone nhánh triển khai lần đầu:

```bash
git clone --branch feat/company-software-workflow https://github.com/minhutc359-ship-it/agent-company.git
cd agent-company
dotnet restore tests/AICompanyOS.Runtime.Tests --locked-mode
dotnet run --project src/AICompanyOS.Runtime
```

Mở **http://127.0.0.1:4173**. Nếu đã có checkout sạch trên đúng nhánh, dùng `git pull --ff-only` để cập nhật trước khi chạy. Không bỏ thay đổi riêng chỉ để cập nhật.

Docker image phải được bạn cài trước. Với repository demo Node đi kèm:

```bash
docker pull node:22-alpine
docker image inspect node:22-alpine
```

Runtime không tự pull image, không chạy lệnh build/test trực tiếp trên host và không giả lập PASS khi thiếu Docker. Container không có mạng; dependency cần có sẵn trong image phù hợp hoặc snapshot được hỗ trợ. Không dùng lệnh cài dependency cần Internet làm bước đầu của test.

Khởi động Ollama với cloud bị tắt (`OLLAMA_NO_CLOUD=1`, khởi động lại Ollama sau khi cấu hình). Endpoint mặc định là `http://127.0.0.1:11434/`; cấu hình endpoint khác phải là HTTP IPv4 loopback. Ứng dụng không tải model. Không cấu hình tên model cloud vào route local để mong tự fallback.

Nếu chưa có model, vẫn có thể thiết lập project và nhập output JSON bằng tay khi node chờ input. Đây là chế độ bạn cung cấp kết quả, không phải AI đã tự hoàn thành công việc.

## 3. Chạy thử trọn luồng bằng project nhỏ

### Bước 1 — Tạo repository demo

Chạy từ thư mục `agent-company`, chọn một đích tuyệt đối chưa tồn tại:

```bash
node examples/create-demo-repository.mjs /absolute/path/aicos-validation-demo
```

Thay `/absolute/path/` bằng đường dẫn trên máy của bạn. Script tạo Git repository, file kiểm tra đầu vào và test cố ý thất bại với chuỗi rỗng; sau đó in cấu hình để bạn đối chiếu. Trên Windows, truyền đường dẫn tuyệt đối kiểu `C:/Projects/aicos-validation-demo`.

### Bước 2 — Tạo Company và cấu hình nhân viên

1. Dùng nút **+** để tạo company; giữ dấu chọn **Cài đội ngũ, skill và workflow gợi ý** để có sẵn 8 nhân viên, 7 skill và 3 mẫu. Company đang có dùng **Bộ đội gợi ý**; cấu hình hiện có được giữ nguyên.
2. Vào **Company → Agents & Skills**, chọn BA, Leader và Dev. Bấm **Đọc danh sách model local**, chọn đúng model đã cài rồi lưu. Cấu hình áp dụng cho run mới.
3. Dùng **Sửa tên & vai trò** để đổi tên/gán nhiều vai trò. Skill có nút **Tạo phiên bản sửa**; sau khi lưu, chọn phiên bản mới trong cấu hình nhân viên. Với demo nhỏ, có thể đặt input budget **10.000 byte**, output budget **4.096 token** trong giới hạn form. Các số này là trần của request, không chứng minh toàn bộ repository vừa context.
4. Tester/SOC/QA trong luồng phần mềm có bộ kiểm tra thực thi thật; không cần yêu cầu model viết “PASS”. Skill package là instructions có phiên bản, không tự cấp quyền shell hay plugin.
5. Nếu muốn hai task Dev chạy song song, tạo nhân viên Dev thứ hai trong **Nhân sự & vai trò**, gán vai trò Dev và cấu hình route. Một nhân viên Dev vẫn có thể xử lý nhiều task theo điều phối.

### Bước 3 — Thêm project

Trong **Company → Thêm project**, điền:

| Trường | Giá trị cho demo |
|---|---|
| Project ID | `validation-demo` — mã ngắn dễ nhớ |
| Tên project | `Demo kiểm tra dữ liệu` |
| Repository | Đường dẫn tuyệt đối vừa tạo |
| Read scopes | `*` cho demo này; thu hẹp theo nhu cầu với repo thật |
| Write scopes | `src/, tests/, docs/` |
| Docker image | `node:22-alpine` đã cài |
| Base branch | `master` theo script demo |
| GitHub remote | Để trống cho lần thử local đầu tiên |

Build command:

```json
{"name":"build","executable":"node","arguments":["--check","src/validator.cjs"],"timeoutSeconds":30}
```

Test command:

```json
{"name":"test","executable":"node","arguments":["--test","tests/validator.test.cjs"],"timeoutSeconds":30}
```

Đọc phạm vi rồi đánh dấu cho phép đọc file đã commit. Intake dùng commit đã chụp; sửa chưa commit không tự đi vào run. Dùng **Xem inventory** để kiểm tra file được đọc và file bị bỏ qua.

### Bước 4 — Giao việc và duyệt kế hoạch

1. Bấm **Giao việc mới**, chọn project và người phụ trách từng vai trò.
2. Mục tiêu ví dụ: “Sửa validator để từ chối chuỗi rỗng và chuỗi chỉ có khoảng trắng, chấp nhận chuỗi hợp lệ; bổ sung test và cập nhật tài liệu. Chỉ sửa src/, tests/, docs/.”
3. Nếu muốn tạo commit sau nghiệm thu, đánh dấu cho phép đề nghị release trên nhánh riêng. Việc này chưa cho phép push.
4. Chờ BA tạo yêu cầu, Leader tạo task graph. Mở node để kiểm tra input/output; dùng **Yêu cầu sửa** nếu thiếu tiêu chí hoặc scope quá rộng.
5. Bấm **Xem scope & duyệt kế hoạch**. Đối chiếu repository, file/path được phép ghi, lệnh build/test, image và toàn bộ manifest trong mục chi tiết trước khi duyệt.

### Bước 5 — Kiểm tra kết quả thực thi

Luồng là **BA → Leader → Owner duyệt phạm vi → Dev → Build → Tester/SOC/QA → vòng sửa nếu có → SubPM → Owner nghiệm thu**.

- Xem task outputs, patch, log và receipts trong node inspector. Test fixture dùng để kiểm thử runtime không chứng minh model thật luôn viết đúng code.
- Dev dùng worktree riêng, không sửa trực tiếp checkout/HEAD gốc. Task chỉ được thay đổi phạm vi đã duyệt.
- Test lỗi có thể dẫn đến tối đa hai vòng sửa tự động theo workflow hiện tại. Không nên mong mọi run demo đều đi qua một lần lỗi: model tốt có thể sửa đúng ngay.
- Thiếu image/dependency/audit evidence hoặc JSON sai phải hiện trạng thái bị chặn, không được coi là hoàn tất. Mở chi tiết lỗi để xử lý nguyên nhân trước khi Retry.
- **Dừng hoạt ảnh** chỉ dừng hình ảnh. **Tạm dừng** ở workflow mới tác động điều phối. Nếu runtime khởi động lại giữa job đang chạy, kiểm tra trạng thái rồi chủ động Retry theo giới hạn.

### Bước 6 — Nghiệm thu, xuất và release

1. Khi chờ nghiệm thu, đối chiếu diff/patch, test logs, findings và báo cáo SubPM; chạy thử nghiệp vụ thật nếu cần.
2. Nếu chưa đúng, dùng **Yêu cầu sửa** để tạo candidate mới và bằng chứng kiểm tra mới.
3. **Owner duyệt kết quả** yêu cầu xác nhận nội dung đạt và quyền sử dụng; duyệt kế hoạch trước đó không thay cho bước này.
4. Tải gói source/patch và receipts; hoặc duyệt release để tạo Git commit trên nhánh riêng nếu run đã yêu cầu release.
5. Nếu cần push: cấu hình GitHub remote, bật quyền GitHub release trong Company policy, xem đúng repository/nhánh/candidate, đánh dấu push và cung cấp token cho thao tác đó. Luồng mở draft PR; không tự merge hoặc deploy.

Đối với repository có dependency ngoài, đọc mục [Dependency evidence](RELEASE_2.2.md#dependency-evidence). Cần evidence khớp inventory, được Owner review; audit không có hoặc license chưa rõ có thể chặn gate bắt buộc.

### Chỉnh workflow trước khi giao việc

Trong **Workflows & Tasks → Workflow Studio**, chọn một trong 3 mẫu hoặc phiên bản đã lưu. Kéo node để sắp xếp; dùng cổng ra → cổng vào hoặc checkbox phụ thuộc để thêm liên kết. Các phụ thuộc bắt buộc được giữ. Chọn cổng chất lượng để đổi số vòng sửa; chỉnh song song/lần thử/phút/token ở đầu form. **Kiểm tra workflow** sẽ phát hiện chu trình hoặc cấu hình sai; **Lưu phiên bản mới** lưu definition thực thi. Khi **Giao việc mới**, chọn đúng workflow/version vừa lưu. Run đang chạy không tự đổi theo editor. Có undo/redo và nhập/xuất JSON; vị trí node là tùy chọn hiển thị trên trình duyệt.

## 4. Hội thoại, kiến thức và cloud

**Hội thoại:** trong **Conversations & Meetings**, chọn công việc/nhánh, gửi câu hỏi, gọi AI local hoặc so sánh 2–4 route đã cấu hình. So sánh dùng cùng snapshot ngữ cảnh, không tự chọn người thắng. Tạo nhánh bằng tên dễ nhớ; trả lời có liên kết về câu hỏi gốc. Mời họp rồi lưu biên bản bằng tay hoặc Leader local; biên bản trở thành nguồn tham khảo, chưa tự thành quyết định chính thức.

**Kiến thức:** import tài liệu với tên và phân loại rõ ràng, tìm kiếm, bấm **Xem nguồn / highlight** để kiểm tra đoạn trích rồi **Đề xuất bộ nhớ**. Owner duyệt mới đưa vào bộ nhớ chính thức. Tài liệu sửa sau khi tạo run không tự ghi đè snapshot cũ. PDF phải có text layer được parser hỗ trợ; bản scan cần OCR ngoài trước. Tìm kiếm hiện dùng lexical + vector đặc trưng cục bộ, chưa phải neural embedding.

**Cloud tùy chọn:**

1. **Company policy → Controlled Cloud**, lưu policy.
2. **Agents & Skills → Thêm cloud provider**: chọn OpenAI/Anthropic/Gemini, nhập đúng model được tài khoản của bạn hỗ trợ. Mã cấu hình tự tạo nằm trong **Cấu hình nâng cao**.
3. **Nạp credential vào RAM** bằng trường mật khẩu. Khóa mất khi runtime dừng; có thể dùng tham chiếu biến môi trường `AICOS_PROVIDER_*` nếu bạn cấu hình riêng.
4. Từ câu hỏi công khai chọn **Xem trước AI cloud**. Kiểm tra nơi nhận, tài liệu/phân loại, dung lượng, lưu ý lưu giữ và mở **Xem toàn bộ nội dung gửi đi** để đọc chính xác payload. Hash và provenance nằm trong **Thông tin kiểm chứng**.
5. Chỉ bấm duyệt khi đồng ý gửi đúng nội dung. Có thể từ chối; chuyển Strict Local hoặc đổi phiên bản policy sẽ chặn yêu cầu cũ chưa gửi. Kết quả chỉ là đề xuất công khai, cần review.

Cloud hiện phục vụ **tham vấn trong hội thoại**. Chưa có route cloud thay thế BA/Leader/Dev trong toàn bộ software workflow; cấu hình provider không làm run local tự đổi route. Không có cam kết zero retention chỉ từ nút bật Controlled Cloud; phải nghiệm thu cấu hình và điều kiện tài khoản provider riêng.

## 5. Cần phát triển thêm những gì?

| Ưu tiên | Công việc | Điều kiện đạt |
|---|---|---|
| P0 — Sử dụng thực tế | Chạy dự án nhỏ với model thật, Docker thật và dữ liệu thật trên máy đích | Có spec/plan/patch, receipts, nghiệm thu và export/commit; ghi rõ lỗi và cấu hình tái hiện |
| P0 — Hình ảnh và UX | Bản 2.3 đã có asset chibi/nội thất, Work Mode, bàn phím và pan/zoom; tiếp tục nghiệm thu trên thiết bị đích | So sánh screenshot và thao tác thực tế với ảnh mẫu; không chồng nhãn, tràn trang, mất bằng chứng hoặc gây khó đọc |
| P1 — Workflow Studio mở rộng | Editor 13 node đã có; node palette tổng quát và branch/join/tool bên ngoài cần hợp đồng runtime mới | Workflow lưu phiên bản, dry-run có ích, chạy/restart được và mọi node có bằng chứng đúng loại |
| P1 — Provider theo vai trò | Nếu cần cloud cho BA/Leader/Dev: thêm adapter typed-output cho từng role, consent/lease/budget/fencing toàn luồng | Mỗi payload được duyệt chính xác; không fallback âm thầm; kiểm tra schema/candidate như local |
| P1 — Knowledge | Neural embedding/ANN, OCR local, chất lượng parse/truy hồi trên bộ tài liệu chuẩn | Đánh giá tìm đúng nguồn và citation, giới hạn tài nguyên; không mất ràng buộc khi context đầy |
| P1 — Bảo vệ dữ liệu trên máy | OS keychain, mã hóa dữ liệu đang dùng, khóa khẩn cấp và chính sách khôi phục | Kiểm tra trên từng OS; backup có mã hóa hiện tại không thay cho mã hóa workspace đang mở |
| P1 — Đóng gói | Installer ký số, cập nhật có xác minh/rollback, cấu hình thiết bị rõ ràng | Cài, nâng cấp, gỡ và phục hồi trên máy sạch; bảo toàn dữ liệu |
| P2 — Tích hợp mở rộng | MCP, A2A, tool/plugin bên ngoài; broader DB adapter khi có nhu cầu; patch xóa file/binary | Quyền theo scope, tương thích phiên bản, audit và test lỗi; các adapter tùy chọn không bắt buộc cho luồng local hiện tại |
| Xuyên suốt | Đối chiếu từng acceptance ID trong spec và nghiệm thu accessibility, hiệu năng, crash/recovery, network/privacy, token/cost | Bằng chứng tái hiện được; giá trị chưa đo vẫn phải ghi chưa biết |

Nhiều mục trong bảng là **code/chức năng còn thiếu**, không thể hoàn tất chỉ bằng cài Docker hoặc thêm API key. Editor phần mềm, backup mã hóa và đồ họa 2D hiện có cũng không thay thế các mục mở rộng tương ứng. Nên nghiệm thu luồng local dùng được trước, sau đó hoàn thiện đồ họa và các phần mở rộng theo ưu tiên sản phẩm.

## 6. Checklist nghiệm thu trên máy của bạn

- [ ] Cập nhật đúng nhánh, chạy ứng dụng, tạo hai company; nguồn/hội thoại của company này không xuất hiện ở company kia.
- [ ] Demo local đi từ yêu cầu tới file đã kiểm tra và export/commit; đối chiếu nội dung file thay vì chỉ nhìn trạng thái xanh.
- [ ] Khi cố tình dùng image chưa cài hoặc model sai, run bị chặn với lỗi xem được, không tự chạy trên host hay chuyển cloud.
- [ ] Pause/resume, hủy, tắt/mở runtime giữa job; không có kết quả từ worker cũ ghi đè candidate đang được duyệt.
- [ ] Handoff, tham vấn và họp xuất hiện trên board kèm artifact/biên bản; dừng hoạt ảnh không làm mất tiến trình.
- [ ] UI mặc định đọc bằng tên; chỉ mở chi tiết khi cần ID/hash; có thể đọc payload đầy đủ trước khi gửi cloud.
- [ ] Nếu dùng cloud, thử từ chối trước; sau đó mới thử một payload ít nhạy cảm với tài khoản thật và kiểm tra usage/error/retention riêng.
- [ ] Xuất backup có mật khẩu và restore vào thư mục dữ liệu mới, đối chiếu công ty, nguồn, version, quan hệ và receipts. Giữ repository/worktree bên ngoài bằng cơ chế backup riêng.
- [ ] Kiểm tra bàn phím, màn hình hẹp, reduced motion và 25 nhân viên trên máy đích; kết quả headless CI không thay cho phép đo thiết bị thật.

Hướng dẫn restore và chạy bộ kiểm thử đầy đủ nằm trong [README](../README.md#data-and-recovery). CI hiện kiểm tra domain Linux/Windows và Chromium; Linux yêu cầu Docker thật. Model fixture là dữ liệu kiểm thử giao thức, không phải chứng nhận chất lượng model hoặc tài khoản cloud của bạn.

## 7. Cập nhật 2.4: setup AI, demo và hội ý

[Bộ hướng dẫn setup đầy đủ](../setup/README.vi.md) giải thích từng lựa chọn Ollama / API cloud / CLI, cách cài Windows và Ubuntu, DB SQLite nhúng, Docker và server Ollama riêng ở port 11435. Có launcher, kiểm tra OK/WAIT và chạy tiếp sau reboot/đăng nhập. PostgreSQL không phải dependency của chương trình hiện tại.

Trong Company, mở **Thiết lập AI & Demo**: chọn model local và đúng nhân viên cần áp dụng; không phải tạo API key cho BA/Dev vì đây là vai trò. Với Codex/Gemini/Copilot, dùng CLI ngoài app và nhập JSON qua gói handoff. Website ChatGPT/CLI login không tự cấu hình OpenAI API route. Cloud hiện phục vụ tham vấn có duyệt payload; việc đưa cloud vào worker BA/Leader/Dev vẫn thuộc phần mở rộng trong bảng trên.

**Tạo / mở project demo** tạo repo code/test thật cùng company riêng. **Giao việc demo** điền sẵn mục tiêu, sau đó Owner tạo run và duyệt phạm vi để xem workflow hoạt động. Test ban đầu cố ý thất bại; model phải sửa đúng hành vi và Docker tạo receipts thật. Chạy setup lần nữa giữ nguyên file/config đã sửa.

**Hội ý tự động** cho 2–4 nhân viên local lần lượt phản hồi và đọc trả lời trước, có giới hạn lượt/budget, retry có kiểm soát và nút dừng. Không tự cấp tools hoặc tự phê duyệt kết quả. Bàn giao công việc BA → Leader → Dev vẫn theo workflow đã chụp cấu hình.

Văn phòng 2.4 có cụm bàn, ghế, thảm khu vực, lối đi, decor ở viền phòng và sprite giữ nhận dạng khi agent đổi việc. Thứ tự lớp bám vị trí di chuyển; ảnh atlas được crop từng ô. Đây vẫn là hoạt ảnh 2D từ atlas, không phải model 3D hay bộ frame đa hướng. Bản đồ mới cần nghiệm thu trên máy đích cùng các tiêu chí ở phần 6.
