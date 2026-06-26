---
name: update-session
description: Save current session progress to SESSION_LOG.md. Use before ending a session or when approaching context limit.
disable-model-invocation: true
---

Lưu tiến độ session hiện tại vào SESSION_LOG.md.

## Bước thực hiện

1. **Thu thập thông tin session hiện tại**:
   - Phase đang làm là gì?
   - Files nào đã được tạo hoặc sửa?
   - Có phát hiện quirk API nào không?
   - Có thay đổi quyết định kỹ thuật nào so với CONTEXT.md không?
   - Vấn đề nào chưa giải quyết?
   - Bước tiếp theo cụ thể là gì?

2. **Cập nhật SESSION_LOG.md**:
   - Cập nhật mục "⚡ Trạng thái hiện tại" với thông tin mới nhất
   - Cập nhật mục "Làm ngay tiếp theo" với bước cụ thể
   - Cập nhật bảng "Trạng thái từng file"
   - Thêm entry mới vào "🗒️ Session Log Chi Tiết" (thêm lên ĐẦU)
   - Cập nhật Phase Checklist nếu có phase hoàn thành

3. **Nếu có quirk API mới phát hiện**:
   - Thêm vào bảng "Vấn đề đã biết / Quirks của API" trong CONTEXT.md

4. **Xác nhận** bằng cách in ra tóm tắt những gì đã ghi vào SESSION_LOG.md.

## Lưu ý quan trọng

- Bước tiếp theo phải **cụ thể**: tên file, tên method, lý do
- Không ghi chung chung như "tiếp tục Phase 2"
- Ghi đủ context để session sau không cần đọc lại toàn bộ conversation
