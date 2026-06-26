---
name: audit-project
description: Audit project files on disk vs CONTEXT.md architecture. Use when resuming after a broken session or to check project completeness.
disable-model-invocation: true
---

Kiểm tra toàn bộ trạng thái project thực tế trên disk.
Dùng khi session bị đứt hoặc muốn kiểm tra tình trạng project.

## Bước thực hiện

1. **Đọc kiến trúc mong muốn** từ CONTEXT.md (mục "Kiến trúc thư mục")

2. **Liệt kê files thực tế** trên disk:
   - Dùng lệnh: `find . -name "*.cs" -o -name "*.xaml" -o -name "*.csproj" | sort`
   - Loại trừ: `bin/`, `obj/`, `.git/`

3. **So sánh** và tạo bảng kết quả:

| File trong CONTEXT.md | Tồn tại? | Trạng thái ước tính |
|---|---|---|
| `Models/XModel.cs` | ✅ / ❌ | Complete / Partial / Unknown |

4. **Đánh giá từng file tồn tại**:
   - File có đủ các method/class chính không?
   - Có TODO comment chưa hoàn thành không?
   - Có compile error rõ ràng không?

5. **Xác định Phase hiện tại**:
   - Phase nào đã hoàn thành?
   - Phase nào đang dang dở?
   - File nào cần làm tiếp?

6. **Cập nhật SESSION_LOG.md** với kết quả audit:
   - Cập nhật bảng "Trạng thái từng file"
   - Cập nhật "⚡ Trạng thái hiện tại"
   - Ghi rõ "Làm ngay tiếp theo"

7. **Báo cáo tóm tắt** cho user:
   - Bao nhiêu file đã xong / đang làm / chưa làm
   - Phase hiện tại là gì
   - Đề xuất bước tiếp theo

## Lưu ý

- KHÔNG viết code trong skill này
- Chỉ audit và báo cáo
- Sau khi audit xong, hỏi user có muốn tiếp tục không
