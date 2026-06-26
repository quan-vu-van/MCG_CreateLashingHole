# 🚢 MCG_CreateLashingHole

> Template chuẩn hóa dành cho việc phát triển các công cụ trên nền tảng **AutoCAD .NET API**.  
> Bộ khung này được xây dựng dựa trên kiến trúc **Module hóa** và nguyên lý **Clean Code**, giúp bóc tách dữ liệu hình học (MTO) chính xác và nhanh chóng.

---

## 📌 Tên chuẩn & Vị trí dự án

| Nền tảng | Tên Project | Thư mục |
|---|---|---|
| AutoCAD | `MCG_*` | `C:\CustomTools\Autocad` |


---

## 🏗️ Cấu trúc dự án

Dự án được phân chia thành các **Module độc lập** để dễ bảo trì và mở rộng:

```
MCG_CreateLashingHole/
├── Commands/       # Đăng ký lệnh và quản lý vòng đời Palette
├── Models/         # Định nghĩa đối tượng (POCO), tách biệt với AutoCAD
├── Services/       # Logic xử lý chính (ExtractionService)
├── Views/          # Giao diện WPF tích hợp AutoCAD PaletteSet
└── Utilities/      # Hàm dùng chung (COG, đổi đơn vị, tọa độ WCS)
```

### Chi tiết từng thư mục

**📁 Commands** — Nơi đăng ký lệnh `[CommandMethod]` và quản lý Palette theo Singleton.
Tiền tố lệnh bắt buộc: `MCG_` (ví dụ: `MCG_AdvanceBoundary`)

**📁 Models** — Các lớp định nghĩa đối tượng thuần (POCO).
Tách biệt hoàn toàn với thư viện AutoCAD, không phụ thuộc bên ngoài.

**📁 Services** — Trái tim của Plugin.
Chứa `ExtractionService` thực hiện toàn bộ logic bóc tách dữ liệu.

**📁 Views** — Giao diện người dùng WPF tích hợp vào AutoCAD PaletteSet.

**📁 Utilities** — Các hàm tiện ích dùng chung:
tính COG, đổi đơn vị, định dạng tọa độ WCS.

> ⚠️ **Không chỉnh sửa file `MCG_CreateLashingHole.csproj`**

---

## 🚀 Bắt đầu nhanh

### Yêu cầu
- Cài **Git**: https://git-scm.com
- Cài **VS Code**: https://code.visualstudio.com
- Cài **GitHub Desktop** *(khuyến nghị)*: https://desktop.github.com

### Clone dự án về máy
```bash
git clone <url-repo>
cd MCG_*
```

---

## 🔄 Quy trình làm việc hàng ngày

### Thuật ngữ cần nhớ
| Thuật ngữ | Ý nghĩa |
|---|---|
| **Pull** (Checkout) | Kéo code mới nhất từ GitHub về máy |
| **Commit** (Checkin) | Lưu thay đổi kèm ghi chú nội dung |
| **Push / Sync** | Đẩy commit lên GitHub |
| **Branch** | Nhánh làm việc riêng, tách khỏi `main` |
| **Merge** | Gộp nhánh vào `main` sau khi Admin duyệt |

---

### ✅ B1 — Trước khi làm việc: PULL về trước
Luôn đồng bộ code mới nhất trước khi bắt đầu:
```bash
git pull
```
> Hoặc nhấn **"Fetch origin" → "Pull"** trong GitHub Desktop

---

### ✅ B2 — Khi kết thúc phiên làm: COMMIT & SYNC
Lưu và đẩy thay đổi lên hệ thống:
```bash
git add .
git commit -m "Module1: mô tả ngắn nội dung thay đổi"
git push
```
> ⚠️ **Commit bắt buộc phải có ghi chú** — mô tả rõ mình đã thay đổi gì

---

## 🌿 Quy tắc Branch

Không ai được **commit thẳng lên nhánh `main`**.  
Mỗi thành viên làm việc trên **branch riêng**:

```
main              ← Bản chính thức, do Admin quản lý
└── feature/ten-module    ← Branch của từng thành viên
```

### Quy trình duyệt code
1. Thành viên làm việc trên branch riêng → Push lên GitHub
2. Tạo **Pull Request** trên GitHub
3. **Admin review** → Merge vào `main` nếu OK, hoặc yêu cầu sửa lại

---

## ⚠️ Xử lý Conflict

**Conflict xảy ra khi:** 2 người cùng sửa 1 đoạn code và đẩy lên hệ thống.

### Phòng tránh
- Phân công **phạm vi rõ ràng** — mỗi người phụ trách Module riêng
- Không tự ý sửa code của Module khác

### Khi có Conflict
- GitHub sẽ báo Conflict trên Pull Request
- **Admin quyết định** giữ phiên bản nào hoặc gộp lại

### Trường hợp đặc biệt — Sửa function dùng chung
Khi nhiều Module cùng dùng 1 function (ví dụ `function_a`) và cần tối ưu:

```
Module1 ──┐
Module2 ──┼──► function_a  (Utilities)
Module3 ──┘
```

> Tạo **branch riêng** để sửa → tạo Pull Request → Admin review và merge.  
> Tránh sửa thẳng trên `main` vì có thể ảnh hưởng tất cả Module.

---

## 📋 Quy tắc chung

| Quy tắc | Chi tiết |
|---|---|
| Pull trước khi làm | Bắt buộc mỗi ngày |
| Làm trên branch riêng | Không commit thẳng lên `main` |
| Commit có ghi chú | Mô tả rõ thay đổi gì |
| Không sửa `.csproj` | Giữ nguyên cấu hình dự án |
| Không đụng Module khác | Trừ khi được Admin cho phép |