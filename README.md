# Scam With Your Friends: Mod tiếng Việt

Mod cho game **Scam With Your Friends** (bản 0.1.89) để gọi điện bằng tiếng Việt:

- **AI trả lời tiếng Việt** qua [Vilao](https://api.vilao.ai) (thay cho OpenRouter), ép JSON mode, tự sửa câu trả lời lỗi, câu đệm tiếng Việt, nâng thời gian chờ AI 15 s → 45 s.
- **Nhận giọng nói tiếng Việt** bằng Whisper large-v3-turbo chạy GPU (Vulkan), tắt flash attention (chậm 60× trên bản whisper.cpp này), nạp model thẳng từ ổ đĩa (đỡ ~0,6 GB RAM), rút ngắn thời gian chốt câu.
- **Giọng người gọi tiếng Việt**: [VieNeu-TTS](https://github.com/pnnbao97/VieNeu-TTS) (tùy chọn, 25 giọng, chọn giọng nam/nữ theo nhân vật) hoặc [Piper](https://github.com/rhasspy/piper) (nhẹ, luôn có, làm dự phòng).
- **Sửa lỗi chạy game**: PlayerPrefs / UnityWebRequest bị strip, interface camera thiếu `TargetCamera`, màn hình hàng đợi người gọi bị treo.

Repo **không chứa file game hay model** nào. Bộ cài vá ngay trên DLL gốc của người chơi (kiểm tra SHA256 trước khi vá) và tải model từ nguồn gốc lúc cài.

## Cài cho người chơi

1. Lấy `SWYF-VietMod.zip` (mục Releases, hoặc tự build như bên dưới) rồi giải nén.
2. Tắt game, bấm đúp `CAI-DAT.bat`. Bộ cài tự tìm game; không thấy thì hỏi thư mục chứa `Scam With Your Friends.exe`.
3. Trả lời có cài VieNeu không (Y/N).
4. Vào game, chọn **Tiếng Việt** ở mục ngôn ngữ cuộc gọi (trong cài đặt, hoặc nút 文A khi đang gọi).

Hướng dẫn đầy đủ (yêu cầu máy, mẹo chống crash, gỡ mod): [kit/HUONG-DAN.txt](kit/HUONG-DAN.txt).

- AI chạy trên máy **chủ phòng**, nên chỉ host cần nhập API key Vilao trong game. Người vào phòng không cần key.
- Gỡ mod: `GO-CAI-DAT.bat` trả mọi file gốc (`*.swyf-orig`) về chỗ cũ.

## Build từ mã nguồn

Cần .NET SDK (chỉ dùng trình biên dịch Roslyn của nó) và thư mục `Managed` của game, vì `SwyfCompat.dll` được biên dịch với chính DLL của game:

```powershell
.\build.ps1 -Managed "D:\Games\Scam With Your Friends\Scam With Your Friends_Data\Managed"
```

Kết quả: `dist\SWYF-VietMod.zip`.

## Cấu trúc

| Thư mục | Nội dung |
|---|---|
| `src/compat/` | `SwyfCompat.dll`, được game nạp: cầu nối Vilao, sửa câu trả lời, PlayerPrefs, Whisper GPU, Piper, VieNeu (HTTP stream) |
| `src/patcher/` | `SwyfPatcher.exe` (.NET Framework 4 + Mono.Cecil): vá IL của 5 DLL game |
| `kit/` | Bộ cài: `CAI-DAT.bat`, `GO-CAI-DAT.bat`, `HUONG-DAN.txt`, `mod/install.ps1`, `uninstall.ps1`, `swyf_launch.py` |

| DLL của game | Bản vá |
|---|---|
| `Assembly-CSharp.dll` | endpoint/model Vilao đọc từ `swyf-vilao.json`, JSON mode, sửa câu trả lời, câu đệm tiếng Việt, PlayerPrefs/UnityWebRequest compat, `TargetCamera`, ngân sách AI 45 s, hàng đợi người gọi |
| `Kolkata.Speech.dll` | hồ sơ phát âm `vi` (số, chữ cái, ký hiệu, màu sắc) |
| `Kolkata.MossTtsNano.dll` | câu tiếng Việt đi qua VieNeu / Piper |
| `com.whisper.unity.dll` | GPU bật, flash attention tắt, nạp model native từ ổ đĩa |
| `ScriptsAssDef.dll` | thời gian chốt câu ≤ 0,8 s, cập nhật tạm ≤ 0,5 s |

## Ghi công

- [Mono.Cecil](https://github.com/jbevain/cecil) (MIT): đóng gói kèm `SwyfPatcher.exe`
- [Piper](https://github.com/rhasspy/piper) (MIT), giọng [vi_VN-vais1000-medium](https://huggingface.co/rhasspy/piper-voices) (CC BY 4.0)
- [whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) large-v3-turbo (MIT)
- [VieNeu-TTS](https://github.com/pnnbao97/VieNeu-TTS) (Apache-2.0)
- [uv](https://github.com/astral-sh/uv) (MIT / Apache-2.0)

Đây là mod do người chơi làm, không liên quan tới nhà phát triển game.
