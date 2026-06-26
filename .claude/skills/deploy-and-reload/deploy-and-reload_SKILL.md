---
name: deploy-and-reload
description: Build code, deploy DLL to Inventor Addins folder, restart Inventor automatically. Use after finishing new code to test in Inventor.
disable-model-invocation: true
allowed-tools: Bash
---

Tự động build, deploy và restart Inventor để test addin mới.
Không cần PowerShell, không cần quyền admin.

## Thực hiện

Chạy file deploy.bat:

```
deploy.bat
```

## Đọc kết quả và xử lý

### Build thất bại → phân tích lỗi
Đọc output, tìm dòng `error CS`:
- `CS0246` — thiếu reference DLL → kiểm tra `<InventorBinPath>` trong `.csproj`
- `CS1061` — method không tồn tại → sai tên API
- `CS0103` — biến/class không tìm thấy → thiếu `using` hoặc sai namespace
Sau khi fix → chạy lại `/deploy-and-reload`

### Build thành công nhưng DLL không được copy
Chạy thủ công trong terminal:
```
copy "bin\Debug\net48\SymbolReplacer.dll" "%APPDATA%\Autodesk\Inventor 2023\Addins\"
copy "SymbolReplacer.addin" "%APPDATA%\Autodesk\Inventor 2023\Addins\"
```

### Deploy thành công
Nhắc user kiểm tra trong Inventor vừa mở:
```
[ ] Tools → Add-In Manager → "Symbol Replacer" xuất hiện?
[ ] Mở file .idw → Tab "Custom Tools" có trên ribbon?
[ ] Click button → Palette mở ra?
[ ] DebugView hiện dòng: [SymbolReplacerAddin] ===== Addin kích hoạt THÀNH CÔNG =====
```

## Sau khi user test xong

Hỏi kết quả rồi cập nhật SESSION_LOG.md:
- Pass → đánh dấu phase hoàn thành, xác định bước tiếp theo
- Fail → ghi lại log lỗi từ DebugView, phân tích nguyên nhân, fix
