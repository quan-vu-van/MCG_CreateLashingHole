---
name: new-service
description: Scaffold a new Service with Interface following team standards. Usage: /new-service ServiceName "description"
disable-model-invocation: true
argument-hint: <ServiceName> "<short description>"
---

Tạo cặp Interface + Service class mới theo chuẩn team.

## Arguments
- `$0` — Tên Service (không có chữ "Service"), ví dụ: `Library`, `ThumbnailCache`
- `$1` — Mô tả ngắn (optional), ví dụ: `"Reads symbol definitions from library IDW file"`

## Bước thực hiện

Tạo 2 files theo chuẩn team cho Service tên **$0**:

### File 1: `Services/I$0Service.cs`

```csharp
using System;
using System.Collections.Generic;

namespace [Namespace].Services
{
    /// <summary>
    /// Interface cho $0Service.
    /// $1
    /// </summary>
    public interface I$0Service
    {
        // Điền các method signatures vào đây
        // Ví dụ:
        // List<ItemModel> GetAll();
        // void Initialize(string path);
    }
}
```

### File 2: `Services/$0Service.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace [Namespace].Services
{
    /// <summary>
    /// $1
    /// Implement I$0Service.
    /// </summary>
    public class $0Service : I$0Service
    {
        // ─── Hằng số log ──────────────────────────────────────────────
        private const string LOG_PREFIX = "[$0Service]";

        // ─── Fields ───────────────────────────────────────────────────
        // (inject dependencies qua constructor)

        // ─── Constructor ──────────────────────────────────────────────
        public $0Service()
        {
            // Khởi tạo $0Service
            Debug.WriteLine($"{LOG_PREFIX} Khởi tạo.");
        }

        // ─── Public methods (implement interface) ─────────────────────

        // TODO: implement các method từ I$0Service

        // ─── Private helpers ──────────────────────────────────────────
    }
}
```

Sau khi tạo file:
1. Thông báo tên 2 file đã tạo
2. Nhắc đăng ký vào Addin.Activate() theo DI pattern:
   ```csharp
   I$0Service $0_service = new $0Service();
   // Truyền vào Controller cần dùng
   ```
3. Cập nhật SESSION_LOG.md: thêm 2 file mới vào bảng trạng thái
