# AI Company OS 2.4 · Bộ cài local và demo

Giải nén **toàn bộ** ZIP vào thư mục riêng, ví dụ `C:\AICompanyOS` hoặc `~/AICompanyOS`. Giữ `app/`, các launcher và `SHA256SUMS.json` cạnh nhau. Không chạy trực tiếp từ bên trong ZIP. Bộ cài dùng Python standard library và tải thành phần còn thiếu từ nguồn chính thức; cần Internet khi cài và tải model/image. Ứng dụng chạy trên `http://127.0.0.1:4173`.

## 1. Cài trên Windows

1. Dùng Windows có Docker Desktop/WSL2 được hỗ trợ, bật virtualization trong BIOS nếu Docker yêu cầu. Chạy bằng tài khoản thường; từng trình cài có thể hỏi quyền quản trị.
2. Mở `Setup-Windows.cmd`. Nếu máy chưa có Python, launcher thử cài Python 3.12 bằng `winget`. Nếu thiếu winget, cài **App Installer** của Microsoft hoặc Python từ python.org, rồi mở lại launcher.
3. Xác nhận cài đặt. Bộ cài kiểm tra/cài Git, .NET SDK 10, Node.js LTS >=22, Docker Desktop và Ollama; tải model mặc định `qwen2.5-coder:7b`, image `node:22-alpine`; khởi tạo SQLite nhúng và company demo.
4. Nếu có dòng **WAIT Docker**, mở Docker Desktop, hoàn tất điều khoản/first launch và chọn **Linux containers**. Nếu Windows yêu cầu WSL2 hoặc reboot, hoàn thành rồi chạy lại `Setup-Windows.cmd`. Script lưu tiến độ và giữ nguyên dữ liệu/demo đã sửa.
5. Khi tất cả kiểm tra đều **OK**, trình cài mở giao diện. Những lần sau dùng `Start-Windows.cmd`. Đóng cửa sổ runtime để dừng ứng dụng; dữ liệu vẫn còn.

Muốn cài thêm Codex, Gemini CLI và Copilot CLI, mở terminal trong thư mục giải nén:

```powershell
.\Setup-Windows.cmd --with-clis
```

Sau đó dùng `Open-Agent-Terminal.cmd` để có đúng PATH của Node/CLI riêng và đúng Ollama port. Đăng nhập bằng tài khoản của bạn ở bước 5 bên dưới. Bộ cài không đọc cookie hoặc kho credential từ các công cụ khác.

## 2. Cài trên Ubuntu

Dùng Ubuntu còn được Docker/.NET hỗ trợ, tài khoản thường có `sudo`, kiến trúc x64 hoặc arm64. Thư mục ứng dụng không nên nằm trên filesystem chỉ đọc. Chạy:

```bash
cd ~/AICompanyOS
bash setup-ubuntu.sh
```

Script chỉ yêu cầu sudo ở các bước cài package/service. Nếu Docker chưa cho tài khoản thường truy cập, có thể chủ động cấp quyền:

```bash
bash setup-ubuntu.sh --allow-docker-group
```

Nhóm `docker` có quyền điều khiển daemon; chỉ chọn trên máy bạn quản lý. **Đăng xuất và đăng nhập lại** sau thay đổi nhóm, rồi chạy lại setup. Runtime không tự dùng sudo hoặc chuyển sang chạy command trên host để vượt lỗi Docker.

Các lệnh thường dùng:

```bash
bash setup-ubuntu.sh --with-clis          # thêm các CLI tùy chọn
python3 setup-aicos.py --agent-shell      # terminal có PATH/environment đúng
python3 setup-aicos.py --launch           # mở lại ứng dụng sau khi cài xong
python3 setup-aicos.py --check            # chẩn đoán; không cài hoặc khởi động gì
python3 setup-aicos.py --plan             # xem danh sách thành phần; không thay đổi máy
```

Windows cũng nhận các flag tương tự qua `Setup-Windows.cmd`. `--prefix <đường-dẫn>` đổi thư mục cài/dữ liệu; dùng cùng prefix khi check, launch và mở agent shell. `--yes` bỏ câu hỏi của Python, không bỏ các điều khoản hay yêu cầu quyền của trình cài vendor. `--no-browser` chỉ cài/kiểm tra; `--launch --no-browser` chạy runtime trong terminal. `--no-model` tạo demo cho nhập output thủ công và giữ trạng thái WAIT model, không báo đã sẵn sàng tự động.

## 3. Các thành phần được setup

| Thành phần | Cách dùng trong chương trình |
| --- | --- |
| .NET SDK 10 | Chạy bản publish kèm sẵn và phục vụ thao tác .NET nếu cần; cài riêng trong `installed/.dotnet` khi thiếu |
| Git | Repo nguồn, snapshot commit và worktree cách ly |
| Node.js LTS >=22 | Các CLI tùy chọn; test demo thực thi bằng Node trong Docker |
| Docker local, Linux engine | Build/test có biên nhận thật, không có mạng, quyền theo scope Owner duyệt |
| Ollama local | Server riêng ở **127.0.0.1:11435**, `OLLAMA_NO_CLOUD=1`; không đổi server 11434 bạn đang dùng |
| Model GGUF | Mặc định `qwen2.5-coder:7b` (~4.7 GB tải model); phải có context tối thiểu 16K |
| SQLite | **Nhúng trong runtime**, tự tạo DB local qua migration có kiểm tra digest; không cần PostgreSQL/MySQL/Docker DB |
| Demo | Company `Demo · Agent Company`, 8 nhân viên, 7 skill, 3 workflow gợi ý, Git repo và test thật |

Để dùng model khác:

```bash
python3 setup-aicos.py --model qwen2.5-coder:7b
```

Đổi tên sang model GGUF local phù hợp bạn chọn. Kiểm tra model metadata chỉ xác nhận có model/context hỗ trợ; tốc độ, bộ nhớ và chất lượng JSON/patch còn tùy máy và model. Cần dư dung lượng cho model, SDK, Docker image và worktree. GPU không bắt buộc cho mọi máy nhưng CPU có thể chậm; cài driver GPU theo tài liệu vendor khi cần. Bộ cài không tự thay đổi driver/BIOS.

## 4. Xem demo agent hoạt động

1. Mở giao diện, chọn **Thiết lập AI & Demo**. Kiểm tra Git/Ollama/Docker/data; các mục còn thiếu phải được xử lý trước khi kỳ vọng chạy tự động toàn luồng.
2. Chọn model đã cài. Có thể dùng cùng một model cho BA/Leader/Dev hoặc chọn model riêng trong **Studio → Nhân viên**. Model là nguồn AI; BA, Leader, Dev, QA là vai trò. Chỉ các nhân viên được đánh dấu mới đổi route. Cấu hình đã được run cũ chụp lại không tự thay đổi.
3. Bấm **Tạo / mở project demo**. Repo demo nằm trong `installed/data/demo/validation-project`. Test `[AC-001]` ban đầu cố ý lỗi: validator chưa từ chối chuỗi rỗng/khoảng trắng. Setup không tự sửa code hoặc tạo báo cáo PASS.
4. Bấm **Giao việc demo**, xem objective được điền sẵn và các assignment, rồi tạo workflow. Chỉ BA/Leader/Dev gọi model để tạo artifact/patch; Build/Tester/SOC/QA/SubPM dùng logic/biên nhận runtime tương ứng.
5. Trong **Work Mode**, xem tiến trình và hội thoại công khai. Duyệt phạm vi sau kế hoạch: đúng thư mục `src/`, `tests/`, `docs/`, image và command. Docker phải thực sự sẵn sàng.
6. Dev tạo patch trong worktree riêng, build/test chạy thật. Nếu có lỗi, vòng sửa có giới hạn; báo cáo cuối giữ cả bằng chứng lỗi và lần chạy lại. Model không được tự nhận một command đã PASS.
7. Đọc báo cáo và nghiệm thu. Có thể xuất candidate; commit/push là quyết định riêng của Owner. Demo không tự push GitHub.

Trong tab **Hội thoại**, bấm **Hội ý tự động**, chọn 2–4 nhân viên local và câu hỏi. Hệ thống gọi lần lượt: người sau nhận câu hỏi và các trả lời công khai trước, mỗi người một lượt, tối đa hai lần thử có kiểm soát. Có nút dừng; không tự cấp tools, tự duyệt quyết định hay tạo vòng trò chuyện vô hạn. Luồng BA → Leader → Dev cũng đã tự chuyển giao artifact theo workflow sau các bước được duyệt.

Để xem lại demo đã chỉnh sửa, bấm tạo/mở lần nữa; chương trình giữ nguyên file, project và route tùy chỉnh. Muốn baseline mới, sao lưu dữ liệu và tạo một project khác; setup không âm thầm reset repo.

## 5. ChatGPT, Codex, Gemini, Copilot và API key

| Lựa chọn | Setup / đăng nhập | Mức tích hợp hiện có |
| --- | --- | --- |
| Ollama | Bộ cài tải model; không cần tài khoản cloud | Worker tự động cho software workflow và hội ý local |
| Codex / ChatGPT | Terminal: `codex login`, kiểm tra bằng `codex login status` | Xuất gói handoff rồi nhập JSON đã review; không phải worker CLI tự động |
| Gemini CLI | Terminal: `gemini`, chọn phương thức đăng nhập theo hướng dẫn | Handoff và nhập JSON |
| GitHub Copilot CLI | Terminal: `copilot`, sau đó `/login`; cần tài khoản/quyền Copilot phù hợp | Handoff và nhập JSON |
| OpenAI / Gemini / Anthropic API | Company policy → Controlled Cloud; Studio → provider/model → nạp API key vào RAM → kiểm tra model | Tham vấn hội thoại, từng payload được Owner preview/duyệt; chưa dùng cho BA/Leader/Dev tự động |

Đăng nhập ChatGPT/Codex, gói thuê bao và API credential là các lựa chọn xác thực khác nhau; không tự suy ra quyền API chỉ từ việc đã đăng nhập website. Quyền model, quota, billing và chính sách tổ chức do tài khoản/provider quyết định. Nút kiểm tra API chỉ gọi danh sách model, không gửi nội dung workspace và không chứng minh khả năng sinh đúng schema. Credential RAM mất khi runtime dừng; dùng credential riêng phù hợp thay vì chép credential của CLI vào app.

**Handoff ra CLI/ChatGPT:** đặt model của vai trò trống **trước khi tạo run** → node chờ output → **Dùng AI/CLI bên ngoài** → xem context thật rồi tải `agent-handoff.zip`. Giải nén, mở CLI đã đăng nhập tại thư mục gói và yêu cầu đọc `REQUEST.md`, `context.json`, `schema.json`, tạo `result.json`. Với ChatGPT có thể đính kèm các file này sau khi xem nội dung sẽ gửi. Không cho CLI tự chạy command/push; runtime tiếp nhận JSON/patch có kiểm tra rồi thực thi trong sandbox đã duyệt. Nhập `result.json` về đúng node còn đang chờ. Kết quả được ghi là **Owner cung cấp**, không gắn nhãn đã chạy adapter CLI. Checkpoint trong gói giúp đối chiếu run/node, không thay thế việc review.

## 6. Dữ liệu, khởi động lại và xử lý lỗi

- `installed/data/`: DB/event journal, cấu hình company, demo, worktrees; dùng **Dữ liệu & sao lưu** để xuất backup mã hóa.
- `installed/models/`: model của Ollama riêng; `installed/setup-state.json`: tiến độ và tên model, không chứa API key.
- `installed/ollama.log`: log server riêng. Khởi động app bằng launcher để giữ đúng PATH và model endpoint.
- Nếu port 11435 bị một server không do bộ cài tạo chiếm, setup dừng thay vì đổi cấu hình hoặc kill server đó. Tắt server xung đột trước khi chạy lại.
- `WAIT dotnet10/git/node22plus`: kiểm tra quyền cài, Internet, antivirus và chạy lại. SDK dùng nguồn dot.net; Node archive có kiểm tra SHA256 từ Node.js.
- `WAIT docker_linux/demo_image`: mở Docker Desktop Linux engine, hoặc kiểm tra service/quyền nhóm Docker trên Ubuntu. `node:22-alpine` phải có trên daemon local. Không dùng daemon remote.
- `WAIT ollama_local/model`: xem `ollama.log`, dung lượng/driver; mở agent terminal và thử `ollama list`, `ollama pull <model>`. Phải dùng đúng port 11435 của bộ cài.
- `WAIT sqlite`: chạy lại bước setup để khởi tạo; nếu journal báo lỗi integrity thì giữ bản gốc và dùng backup, không xóa DB để che lỗi.
- Test/JSON/model thất bại: đọc lỗi node và evidence; đổi model hoặc sửa input có review. READY của bộ cài là kiểm tra prerequisites, **không phải chứng nhận mọi model/tài khoản hoặc mọi project đều chạy thành công**.
- Các installer của vendor có thể yêu cầu chấp nhận điều khoản, reboot hoặc đăng nhập. Không thể tự hoàn tất các bước đó thay người dùng. Chạy lại cùng setup sau khi hoàn thành.

## 7. Build lại bộ cài từ source

```bash
dotnet restore src/AICompanyOS.Runtime --locked-mode
dotnet publish src/AICompanyOS.Runtime -c Release --no-restore --self-contained false -o artifacts/runtime
python3 setup/build-bundle.py --runtime artifacts/runtime --output artifacts/AICompanyOS-Full-Setup-2.4.zip
```

ZIP có runtime, UI/assets, SQLite native dependencies và launcher. SDK/model/image được tải đúng nguồn trong quá trình setup; đây không phải bộ cài offline. Checksum kèm gói phát hiện file thiếu/hỏng sau giải nén, không phải chữ ký số của publisher.

Nguồn chính thức để đối chiếu: [Codex auth](https://learn.chatgpt.com/docs/auth), [Gemini CLI auth](https://geminicli.com/docs/get-started/authentication/), [Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli), [.NET installer](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script), [Ollama Linux](https://docs.ollama.com/linux), [Ollama Windows](https://docs.ollama.com/windows), [model](https://ollama.com/library/qwen2.5-coder:7b), [Docker Ubuntu](https://docs.docker.com/engine/install/ubuntu/).
