---
name: build-and-test
description: Build the CAD plugin/addin project and report results. Use after writing new code to verify compilation.
disable-model-invocation: true
---

Build project và báo cáo kết quả chi tiết.

## Bước thực hiện

1. Chạy build:
```
dotnet build -c Debug
```

2. Phân tích kết quả:
   - **Build thành công**: liệt kê warnings nếu có, nhắc nhở test trong Inventor/AutoCAD
   - **Build thất bại**: phân tích từng lỗi, đề xuất fix cụ thể theo thứ tự ưu tiên

3. Nếu build thành công, nhắc user test checklist:

### Inventor AddIn
```
[ ] File .addin đã được copy đến %APPDATA%\Autodesk\Inventor 2023\Addins\?
[ ] File .dll đã được copy đến cùng thư mục?
[ ] Mở Inventor → Tools → Add-In Manager → "Symbol Replacer" xuất hiện?
[ ] Mở một file .idw → Tab "Custom Tools" hiện trên ribbon?
[ ] Click button → DockableWindow/Palette mở ra?
[ ] Kiểm tra DebugView hoặc VS Output để xem log khởi động
```

### AutoCAD Plugin
```
[ ] Thư mục .bundle đã được copy đến %APPDATA%\Autodesk\ApplicationPlugins\?
[ ] Mở AutoCAD → gõ lệnh NETLOAD nếu cần load thủ công
[ ] Gõ tên lệnh đã đăng ký → lệnh chạy được?
[ ] Palette/PaletteSet hiện đúng vị trí?
[ ] Kiểm tra log trong DebugView
```

4. Cập nhật SESSION_LOG.md với kết quả build:
   - Thêm dòng trạng thái build vào phần "Trạng thái từng file"
   - Ghi lại lỗi nếu có để tham khảo sau
