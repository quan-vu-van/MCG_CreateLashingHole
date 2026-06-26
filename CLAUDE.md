# CLAUDE.md — MCG_CreateLashingHole

# AutoCAD 2023 | C# | .NET Framework 4.8 | WPF | VS Code

> File này được Claude Code tự động đọc mỗi khi mở project.
> Toàn bộ team đều áp dụng. Không cần nhắc lại. Không được bỏ qua bất kỳ mục nào.

---
## 1. Khởi động phiên làm việc — Đọc theo thứ tự
1. CLAUDE.md
2. CONTEXT.md
3. SESSION_LOG.md
4. .claude/PALETTE_GUIDE.md  ← thêm dòng này

Sau khi đọc xong, **báo cáo ngắn**:
- Đang ở Phase nào / task nào
- File nào cần làm tiếp theo
- Có vấn đề tồn đọng không

---

## 2. Thông tin dự án

```
Tên project   : MCG_CreateLashingHole
Mục tiêu      : Plugin AutoCAD .NET API để bóc tách dữ liệu hình học (MTO)
Thư mục chuẩn : C:\CustomTools\Autocad\MCG_CreateLashingHole
Runtime        : .NET Framework 4.8, x64
UI             : WPF + AutoCAD PaletteSet (Singleton)
API DLLs       : C:\Program Files\Autodesk\AutoCAD 2023\acmgd.dll
                 C:\Program Files\Autodesk\AutoCAD 2023\acdbmgd.dll
                 C:\Program Files\Autodesk\AutoCAD 2023\accoremgd.dll
Output         : AutoCAD Plugin (.dll + .bundle)
Bundle folder  : %APPDATA%\Autodesk\ApplicationPlugins\
Build          : dotnet build -c Debug (chạy với quyền Administrator)
```

> ⚠️ **Hạn chế chỉnh sửa `MCG_CreateLashingHole.csproj`** — chỉ sửa khi có lý do rõ ràng (đổi `PluginName`, thêm `PackageReference`, đổi target framework). Không chạm các field không hiểu rõ.

---

## 3. Namespace — BẮT BUỘC TUÂN THỦ

```
MCG_CreateLashingHole                                    ← Root
├── MCG_CreateLashingHole.Commands                       ← Đăng ký lệnh CommandMethod
│   ├── MCG_CreateLashingHole.Commands.DetailDesign
│   ├── MCG_CreateLashingHole.Commands.FittingManagement
│   ├── MCG_CreateLashingHole.Commands.PanelData
│   ├── MCG_CreateLashingHole.Commands.TableOfContent
│   └── MCG_CreateLashingHole.Commands.Weight
├── MCG_CreateLashingHole.Models                         ← Data objects thuần, không import AutoCAD
│   ├── MCG_CreateLashingHole.Models.DetailDesign
│   ├── MCG_CreateLashingHole.Models.FittingManagement
│   ├── MCG_CreateLashingHole.Models.PanelData
│   ├── MCG_CreateLashingHole.Models.TableOfContent
│   └── MCG_CreateLashingHole.Models.Weight
├── MCG_CreateLashingHole.Services                       ← Business logic, luôn có Interface
│   ├── MCG_CreateLashingHole.Services.DetailDesign
│   ├── MCG_CreateLashingHole.Services.FittingManagement
│   ├── MCG_CreateLashingHole.Services.PanelData
│   ├── MCG_CreateLashingHole.Services.TableOfContent
│   └── MCG_CreateLashingHole.Services.Weight
├── MCG_CreateLashingHole.Views                          ← WPF XAML + code-behind tối thiểu
│   ├── MCG_CreateLashingHole.Views.DetailDesign
│   ├── MCG_CreateLashingHole.Views.FittingManagement
│   ├── MCG_CreateLashingHole.Views.PanelData
│   ├── MCG_CreateLashingHole.Views.TableOfContent
│   └── MCG_CreateLashingHole.Views.Weight
└── MCG_CreateLashingHole.Utilities                      ← Hàm dùng chung toàn project
```

**Quy tắc namespace theo vị trí file:**
```
Services/DetailDesign/DetailExtractionService.cs     → namespace MCG_CreateLashingHole.Services.DetailDesign
Models/FittingManagement/FittingData.cs              → namespace MCG_CreateLashingHole.Models.FittingManagement
Views/PanelData/PanelDataView.xaml.cs                → namespace MCG_CreateLashingHole.Views.PanelData
Services/TableOfContent/TableGeneratorService.cs     → namespace MCG_CreateLashingHole.Services.TableOfContent
Models/Weight/WeightCalculationResult.cs             → namespace MCG_CreateLashingHole.Models.Weight
Utilities/CoordinateHelper.cs                        → namespace MCG_CreateLashingHole.Utilities
```

---

## 4. Quy tắc đặt tên

| Loại | Quy tắc | Ví dụ |
|---|---|---|
| Class | PascalCase | `BoundaryExtractor` |
| Interface | I + PascalCase | `IExtractionService` |
| Method | PascalCase | `GetBoundaryPoints()` |
| Property | PascalCase | `PointCount` |
| Variable | camelCase | `pointList` |
| Hằng số | UPPER_SNAKE_CASE | `MAX_RETRY_COUNT` |
| Lệnh CAD | `MCG_` + PascalCase | `MCG_AdvanceBoundary` |
| Module folder | TenModule (PascalCase) | `DetailDesign`, `Weight` |

---

## 5. Quy tắc ngôn ngữ

| Loại nội dung | Ngôn ngữ |
|---|---|
| Tên class, method, variable, property | **English** |
| UI text (button, label, tooltip) | **English** |
| XML doc comment `/// <summary>` | **Tiếng Việt** |
| Inline comment `//` | **Tiếng Việt** |
| Log message | **English** |
| Error message hiển thị cho user | **Tiếng Việt** |

---

## 6. Kiến trúc — MVC + SOLID

```
Commands   → Chỉ đăng ký lệnh và gọi Service. KHÔNG chứa logic.
Models     → Data objects thuần. KHÔNG import AutoCAD namespace.
Services   → Toàn bộ business logic. Luôn có Interface (IXxxService).
Views      → WPF XAML + ViewModel. Code-behind tối thiểu.
Utilities  → Hàm dùng chung (COG, đổi đơn vị, tọa độ WCS).
```

**Nguyên lý bắt buộc:**
- **SRP**: mỗi class chỉ 1 trách nhiệm
- **OCP**: mở rộng qua interface, không sửa class đã ổn định
- **DIP**: phụ thuộc abstraction (interface), không phụ thuộc implementation
- **DI**: Dependency Injection thủ công tại entry point

---

## 7. Logging — Bắt buộc mọi class

```csharp
// Khai báo ở đầu mỗi class
private const string LOG_PREFIX = "[TênClass]";

// Pattern chuẩn cho mọi method quan trọng
public void DoSomething()
{
    Debug.WriteLine($"{LOG_PREFIX} Bắt đầu [tên hành động]...");
    try
    {
        // ... code ...
        Debug.WriteLine($"{LOG_PREFIX} [Tên hành động] THÀNH CÔNG.");
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"{LOG_PREFIX} LỖI: {ex.Message}");
        Debug.WriteLine($"{LOG_PREFIX} Stack trace:\n{ex.StackTrace}");
        throw; // KHÔNG swallow exception ở Service/Controller
    }
}
```

> Xem log qua: VS Code Output window hoặc DebugView (Sysinternals)

---

## 8. Error Handling

```
Tầng Service    → Log + throw (để Controller xử lý)
Tầng Controller → Log + chuyển thành thông báo user-friendly
Tầng View       → Hiển thị thông báo, không crash
```

> ⚠️ **KHÔNG BAO GIỜ** bắt exception rồi bỏ qua (swallow) ở Service/Controller

---

## 9. AutoCAD API — Patterns chuẩn

### Đăng ký lệnh
```csharp
[CommandMethod("MCG_TenLenh", CommandFlags.Modal)]
public void MyCommand()
{
    var doc = Application.DocumentManager.MdiActiveDocument;
    var db  = doc.Database;
    var ed  = doc.Editor;

    Debug.WriteLine($"{LOG_PREFIX} Bắt đầu lệnh MCG_TenLenh...");

    using (var tr = db.TransactionManager.StartTransaction())
    {
        try
        {
            // ... thao tác với entities ...
            tr.Commit();
            Debug.WriteLine($"{LOG_PREFIX} Lệnh hoàn thành THÀNH CÔNG.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{LOG_PREFIX} LỖI: {ex.Message}");
            ed.WriteMessage($"\nLỗi: {ex.Message}");
        }
    }
}
```

### Transaction — Bắt buộc khi đọc/ghi database
```csharp
using (var tr = db.TransactionManager.StartTransaction())
{
    try
    {
        // ... thao tác ...
        tr.Commit();
    }
    catch (Exception ex)
    {
        // Transaction tự rollback khi dispose
        Debug.WriteLine($"{LOG_PREFIX} Transaction ABORT: {ex.Message}");
        throw;
    }
}
```

### Selection filter
```csharp
var filterList = new TypedValue[]
{
    new TypedValue((int)DxfCode.Start, "INSERT"), // Block reference
};
var filter = new SelectionFilter(filterList);
var result = doc.Editor.GetSelection(filter);

if (result.Status == PromptStatus.OK)
{
    Debug.WriteLine($"{LOG_PREFIX} User chọn {result.Value.Count} đối tượng.");
}
```

### PaletteSet — Singleton pattern (1 Module — 4 Tabs)

> Quyết định kiến trúc đã được chốt. Không thảo luận lại.

- Toàn plugin dùng **DUY NHẤT 1 PaletteSet** title `"Fitting Management"`, GUID cố định, không bao giờ thay đổi
- `PaletteManager.cs` là Singleton nằm trong `Commands/`
- 4 tab cùng 1 PaletteSet: **Fitting Handle / Project Config / Template / Block Utilities**
- Mỗi tab là 1 UserControl độc lập (Service riêng, chia sẻ styles qua `FittingStyles.xaml`)
- **Cấm** tạo PaletteSet thứ 2 ở bất kỳ file nào khác
- **Thứ tự tab KHÔNG đổi sau deploy** — AutoCAD nhớ index theo GUID

```
Commands/PaletteManager.cs                              ← Singleton duy nhất
Views/FittingManagement/FittingStyles.xaml              ← ResourceDictionary chung
Views/FittingManagement/FittingHandleView.xaml          ← Tab 1: BOM + Balloon
Views/FittingManagement/ProjectConfigView.xaml          ← Tab 2: Open Library
Views/FittingManagement/TemplateView.xaml               ← Tab 3: Import .idw + Open Library
Views/FittingManagement/BlockUtilitiesView.xaml         ← Tab 4: 6 block utility buttons
```

```csharp
// Commands/PaletteManager.cs
private static readonly Guid PaletteGuid = new Guid("2b80cfe9-c560-49d6-8a09-9d636260fcf2");

private void Initialize()
{
    // 1. Khởi tạo cơ bản với GUID
    _paletteSet = new PaletteSet("Fitting Management", PaletteGuid);

    // 2. Nạp nội dung — PHẢI thực hiện TRƯỚC khi set Dock/Size
    _paletteSet.AddVisual("Fitting Handle",  new FittingHandleView());
    _paletteSet.AddVisual("Project Config",  new ProjectConfigView());
    _paletteSet.AddVisual("Template",        new TemplateView());
    _paletteSet.AddVisual("Block Utilities", new BlockUtilitiesView());

    // 3. Thiết lập kích thước và khả năng neo — SAU AddVisual
    _paletteSet.DockEnabled = DockSides.Right | DockSides.Left;
    _paletteSet.Size = new System.Drawing.Size(400, 600);
    _paletteSet.KeepFocus = true;
}

// 4. HIỂN THỊ — bắt buộc visible trước khi AutoCAD cho phép gán Dock
public void Show() { _paletteSet.Visible = true; }
public void Hide() { _paletteSet.Visible = false; }
```

**Thứ tự khởi tạo PaletteSet — BẮT BUỘC:**
```
1. new PaletteSet(name, guid)     ← Tạo shell
2. AddVisual(...)                  ← Nạp nội dung TRƯỚC
3. DockEnabled / Size / KeepFocus ← Set thuộc tính SAU
4. Visible = true                  ← Hiển thị CUỐI CÙNG (AutoCAD mới cho phép dock)
```

**Focus về bản vẽ sau khi click button trên Palette — BẮT BUỘC:**
```csharp
// Khi user click button trên PaletteSet, focus vẫn nằm ở Palette.
// Phải gọi dòng này để trả focus về bản vẽ (cần thiết cho pick, select, v.v.)
Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
```
> Luôn gọi `SetFocusToDwgView()` trong event handler của button khi hành động tiếp theo
> cần tương tác với bản vẽ (pick point, select entity, zoom, v.v.)

**Lệnh CAD:**
- `MCG_Fitting` → hiển thị PaletteSet (đăng ký trong `Commands/FittingManagement/FittingManagementCommand.cs`)
- `MCG_Fitting_Hide` → ẩn PaletteSet (đăng ký trong `Commands/PaletteManager.cs`)

---

## 10. WPF — Patterns chuẩn

### ViewModel (MVVM)
```csharp
public class MyViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    private string _statusText = "Ready";
    /// <summary>Trạng thái hiển thị trên status bar</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusText = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }
}
```

### Thread safety — Cập nhật UI từ background thread
```csharp
Application.Current.Dispatcher.Invoke(() =>
{
    StatusText = "Đang xử lý...";
});
```

### Màu sắc chuẩn (Dark theme — AutoCAD)
```xml
<Color x:Key="BackgroundColor">#FF1E1E1E</Color>
<Color x:Key="SurfaceColor">#FF2D2D2D</Color>
<Color x:Key="AccentColor">#FF0078D4</Color>
<Color x:Key="BorderColor">#FF404040</Color>
<Color x:Key="TextPrimaryColor">#FFEEEEEE</Color>
<Color x:Key="TextSecondaryColor">#FF999999</Color>
<FontFamily x:Key="UIFont">Segoe UI</FontFamily>
```

---

## 11. Cấu trúc file mẫu (_Template)

Mỗi layer có folder `_Template/` chứa file mẫu. Khi tạo Module mới, **copy từ _Template**, không tạo từ đầu.

```csharp
// File: Services/Module1/TenService.cs
namespace MCG_CreateLashingHole.Services.Module1
{
    /// <summary>
    /// TODO: Mô tả class này làm gì
    /// </summary>
    public class TenService : ITenService
    {
        #region Fields
        private const string LOG_PREFIX = "[TenService]";
        // TODO: Khai báo biến ở đây
        #endregion

        #region Constructor
        /// <summary>Khởi tạo TenService</summary>
        public TenService()
        {
            Debug.WriteLine($"{LOG_PREFIX} Khởi tạo.");
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// TODO: Mô tả method này làm gì
        /// </summary>
        public void Execute()
        {
            Debug.WriteLine($"{LOG_PREFIX} Bắt đầu Execute...");
            try
            {
                // TODO: Viết logic ở đây
                Debug.WriteLine($"{LOG_PREFIX} Execute THÀNH CÔNG.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{LOG_PREFIX} LỖI: {ex.Message}");
                throw;
            }
        }
        #endregion
    }
}
```

---

## 12. Checklist trước khi viết code

```
[ ] Đọc SESSION_LOG.md — phase và bước tiếp theo là gì?
[ ] File cần tạo đã tồn tại chưa? Không tạo lại nếu đã có.
[ ] Namespace đúng theo vị trí file chưa?
[ ] Có Interface nếu tạo Service mới chưa?
[ ] Có LOG_PREFIX ở đầu class chưa?
[ ] Có log đầu/cuối mọi method quan trọng chưa?
[ ] Có Transaction khi đọc/ghi AutoCAD database chưa?
[ ] Comment tiếng Việt đầy đủ chưa?
[ ] Models có import AutoCAD namespace không? (KHÔNG được phép)
[ ] Commands có chứa logic không? (KHÔNG được phép)
```

---

## 13. Kết thúc task — Bắt buộc

Sau khi hoàn thành **BẤT KỲ** task nào, **tự động** cập nhật `SESSION_LOG.md`:

- THÊM session mới vào ĐẦU FILE SESSION_LOG.md
- KHÔNG xóa session cũ
- Số thứ tự SESSION tăng dần
- Khi đọc lại: chỉ cần đọc session mới nhất (đầu file)

```markdown
## Session [ngày giờ]
### Đã làm
- [Liệt kê file đã tạo/sửa]
### Trạng thái
- Phase hiện tại: ...
### Bước tiếp theo
- File: ... | Method: ... | Mục tiêu: ...
### Ghi chú API
- [Quirk hoặc phát hiện mới về AutoCAD API nếu có]
```

> Dấu hiệu "task xong": tạo/sửa xong file, fix xong lỗi, hoàn thành phase, user confirm "OK" hoặc "done".

## 14. Audit dự án
Khi được yêu cầu audit, chạy tuần tự 6 hạng mục trên.
KHÔNG tự sửa trước khi user xác nhận.
Kết quả audit lưu vào SESSION_LOG.md.

## 15. Lưu session nhanh
Khi user nhắn bất kỳ nội dung nào chứa từ khóa:
"lưu session", "save session", "hết token", "tạm dừng"
→ Lập tức chạy lưu SESSION_LOG.md theo format mục 13.
Không hỏi lại. Không chờ xác nhận.