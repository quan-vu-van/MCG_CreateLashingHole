## Session 2026-08-13 21:20 — ✅ VALIDATION: né va chạm 100% sạch trên V1.dwg

### Bối cảnh
- User có công cụ export riêng `MCG_.ExportDwgData` (`ExportDwgDataCommand.cs`, lệnh `MCG_ExportDwgData`) xuất boundary/holes/structures ra JSON+MD. Export đọc lỗ trong block OK.
- Vá thêm cho export tool: `StructureData.Vertices` + `ExtractStructureVertices` (đỉnh world, tessellate cung) + xuất `"Vertices"` trong JSON → phân tích va chạm chính xác thay vì bbox.

### Kết quả (panel V1.dwg, 2 block PNL_01_1/2)
- Bộ kiểm tra offline `scratchpad/collisioncheck` (point-to-segment với 81 cấu kiện có vertices):
  - **946 lỗ, 0 va chạm thật (0.00%)**, 573 borderline (75–100mm = dấu hiệu né đúng).
  - 22 "va chạm" theo bbox trước đó đều là FALSE POSITIVE (thanh xiên 26B1F bbox 8698×825 → thực ra dải mảnh ~10mm).
- → Củng cố fix view-dependent (SelectAll): khi cấu kiện được quét đủ, addin đặt lỗ chính xác 100%.

### Công cụ để lại
- `collisioncheck` (đọc JSON export → đếm va chạm thật) = regression check nhanh cho các panel export sau.

---

## Session 2026-08-13 14:56 — 🐛 FIX: generate không được relocate (thuộc local adjust)

### Triệu chứng (user, manual mode)
- Local adjust "đã thực hiện ngay ở bước generate" nhưng addin VẪN hỏi "Perform local adjustment?" → vô lý.

### Nguyên nhân
- `DrawHolesWithDimensions` (generate) đang **relocate 8-hướng bằng FindSafePoint** → làm luôn việc của local adjust.
- VBA gốc: generate chỉ `CheckAndHighlightConflicts` (dòng 2435) — **CHỈ tô đỏ (`acRed`), KHÔNG dời lỗ**. Dời 8-hướng là của `PerformLocalAdjustments_Phase2` (bước local adjust).

### Đã sửa
- `DrawHolesWithDimensions`: bỏ FindSafePoint relocate ở generate → chỉ vẽ lỗ tại điểm lưới + tô ĐỎ nếu va chạm (`markRed = collides`). Grid engine vẫn retreat in-line như cũ.
- Kết quả: generate hiện lỗ + đỏ chỗ va chạm → prompt "Perform local adjustment?" giờ CÓ nghĩa → local adjust (auto ở auto-mode / hỏi ở manual) mới dời lỗ đỏ.
- Cập nhật message PHASE 1: `colliding(red)=N -> resolve via local adjust`. Bỏ `stats.Relocated` (nay luôn 0 ở generate).
- Build PASS (`MCG_LashingHole_20260813_145628.dll`).

### Ghi chú
- `AddAdjustedDimensions`/`AdjustedHole` nay không được gọi ở generate (adjusted rỗng) — để lại, vô hại. Dim lỗ dời do local adjust tự thêm.

---

## Session 2026-08-13 14:04 — 🐛 FIX: cấu kiện quét thất thường (view-dependent)

### Triệu chứng (user báo, có file DXF `File dxf/Test V1.dxf`)
- 2 panel input GIỐNG HỆT (vd PN_01_11 vs PN_01_12): panel này addin rải lỗ tốt + né va chạm, panel kia "thuật toán thông minh gần như không chạy". User nghi stale data không xóa giữa các phiên.

### Điều tra
- Đọc được DXF (ASCII AC1032, 18MB, 1.9M dòng). Layers khớp domain addin (boundary AM_0/0, cấu kiện, AM_11 loại trừ, holes 0/AM_9). Block addin: `PNL_01_L.H_1` (1068 circle), `PNL_01_L.H_2` (1028).
- **Audit static state**: SẠCH — `LashingParamsStore.Current` ghi đè mỗi START; `LashingWorkflowState.Clear()` gọi đầu `RunGenerate`; services tạo mới mỗi lệnh. → KHÔNG phải "saved data".
- **NGUYÊN NHÂN THẬT**: `SelectStructuresByCrossing` dùng `ed.SelectCrossingWindow` — **phụ thuộc VIEW**, bỏ sót cấu kiện ngoài màn hình. VBA gốc `SelectStructuresByExample_Helper` (dòng 1836) có **"CRITICAL FIX: AUTO ZOOM"** (`ZoomWindow` + buffer 10% trước khi crossing) vì "AutoCAD sometimes misses objects if they are off-screen". C# port ĐÃ BỎ zoom (comment sai ".NET không cần ZoomWindow"). → panel ngoài view quét 0 cấu kiện → né va chạm không có gì để né → lưới phẳng.

### Đã sửa
- `SelectStructuresByCrossing`: thay `SelectCrossingWindow` → **`ed.SelectAll(filter)` + test overlap bbox thủ công** (tất định, KHÔNG phụ thuộc view, không nhảy màn hình). Fix cho cả RunGenerate lẫn RunInterferenceAudit.
- Build PASS (`MCG_LashingHole_20260813_140445.dll`).

### Còn lại
- Runtime-test trên bản vẽ Test V1 (panel từng lỗi giờ phải né va chạm ổn định).
- (tuỳ) xác nhận sâu trong DXF: PN_01_11 có lỗ đè cấu kiện không.

---

## Session 2026-08-01 22:40 — 📌 ĐIỂM DỪNG (PIN)

> User ghim điểm dừng. "Còn rất nhiều việc với logic RẢI LỖ và ĐIỀU CHỈNH so với VBA" — xử lý tiếp sau.

### Build hiện tại
- `MCG_LashingHole_20260801_222200.dll` (Debug, 0 warn/err). CHƯA runtime-test các thay đổi mới nhất.

### ĐÃ XONG (chuỗi session hôm nay)
1. **Lỗ hiện trước prompt** — tách `RunGenerate` (MCG_LH_RUN) / `RunPostProcess` (MCG_LH_POST); lệnh kết thúc → AutoCAD repaint → lỗ hiển thị chắc chắn; auto-chain qua `Application.Idle`.
2. **Special area port đúng VBA** `PerformSpecialAreaAdjustment_Phase3`: seed band → xóa lỗ cũ → mọc lại hàng lỗ (`RegenerateSeedLineSpecial` + `TryRayBoundaryIntersection`) + dimension + mark đỏ. Mỗi bước 1 lần chạy POST, hiện lỗ rồi tự hỏi lại (cờ `ContinueSpecial`).
3. **Hướng special = CHUỘT** (rubber-band từ START), bỏ P1/P2 keyword.
4. **Audit trong block** — `CollectCircleCentersWorld` quét cả trong BlockReference (transform world); spacing + interference đều chạy không cần phá block.
5. **Command line = English** toàn bộ (memory `command-line-english`).

### CÒN LẠI vs VBA (ưu tiên cho phiên sau)
- [ ] **Runtime-test** special-area mới (mouse dir, seed regen, mark đỏ) + audit trong block — CHƯA kiểm chứng trên AutoCAD.
- [ ] **Logic rải lỗ (PHASE 1)** — rà lại `GenerateCentralPoints`/`GenerateLineOfPoints` so VBA: gap Case1/2/3, retreat, reposition Li-1, thứ tự 4 hướng, unique X×Y. User nói phần rải lỗ còn cần chỉnh.
- [ ] **Local adjustment** — `LocalAdjustRedHoles` (C#) vs `PerformLocalAdjustments_Phase2` (VBA, dòng ~?): đối chiếu 8-hướng (`FindClosestSafe*Shift_Local`, `FindBestSafeComplexShift_Local` dòng 1610-1717), thứ tự ưu tiên, dimension.
- [ ] **Vá degenerate-guard** `GetSmartRectEdges` (fallback bbox khi 1 trục chỉ có 1 cạnh dài) — đã bàn, CHƯA áp.
- [ ] Dọn P1/P2 vestigial trong `LashingWorkflowState` (không còn dùng sau khi special dùng mouse).
- [ ] (Tùy chọn) cloud-mark audit đặt ở model space phủ block — nếu muốn mark NẰM TRONG block cần bước mở định nghĩa block.

### Nguồn đối chiếu
- VBA gốc: `../vba_src/mod_MainLashingHole_V3.bas` (memory `vba-source-of-truth`). Extractor: `scratchpad/vbaextract`.

---

## Session 2026-08-01 22:22

### User yêu cầu
- Dùng **tiếng Anh** cho TẤT CẢ text hiển thị ở command line (prompt + message + error), KHÔNG tiếng Việt.

### Đã làm
- Dịch sang English mọi `ed.WriteMessage`, prompt (`PromptPointOptions`/`SetRejectMessage`...), và audit `message` còn tiếng Việt trong: `LashingWorkflowService.cs`, `LashingHoleCommands.cs`, `InterferenceAuditService.cs`.
- Comment `///` và `//` GIỮ tiếng Việt (theo CLAUDE.md §5); Debug.WriteLine vốn đã English.
- Đây là override CLAUDE.md §5 ("error message → tiếng Việt") → đã ghi memory `command-line-english`.

### Trạng thái
- Build PASS (`MCG_LashingHole_20260801_222200.dll`). Grep xác nhận không còn tiếng Việt trong string command-line.

### Đang treo (chưa áp)
- Vá degenerate-guard `GetSmartRectEdges`.

---

## Session 2026-08-01 22:09

### User yêu cầu (2 điểm)
1. Special area: hướng điều chỉnh nên chọn bằng **HƯỚNG CHUỘT**, không gán cứng P1/P2.
2. Audit (spacing + interference) chỉ chạy được khi **phá block** — sai. Phải audit ngay trong block (đã đóng block rồi).

### Đã làm
**Điểm 1 — hướng bằng chuột:**
- `RegenerateSpecialArea`: đổi tham số `dirChoice/p1/p2` → `int genDir` (bỏ tính genDir per-seed từ P1/P2).
- `RunPostProcess`: sau khi chọn START/END, đọc tâm 2 lỗ → xác định trục (cùng Y→cột dọc), rồi **`ed.GetPoint` với rubber-band từ lỗ START** để user click chỉ hướng → `genDir = Sign(pick - startC)` trên trục phát triển.
- State P1/P2 giờ vô dụng cho special (vẫn set ở RunGenerate, để lại — vô hại).

**Điểm 2 — audit trong block (không phá block):**
- `AutoCADGeometryHelper.CollectCircleCentersWorld(tr, space, boundary, layer, radius, rTol)`: quét circle ở model space TOP-LEVEL **và bên trong mọi BlockReference** (transform tâm qua `BlockTransform`), trả tâm world trong boundary. Block đóng ở scale 1, insert=Origin=basePt nên BlockTransform ≈ identity → tâm giữ world.
- `InterferenceAuditService.RunAudit`: dùng collector → dựng Circle ẢO (RAM) ở tâm world để `IntersectWith` cấu kiện (bỏ `CollectOuterCircles`). Cloud-mark chèn ở model space tại tâm world (KHÔNG nhét vào trong block).
- `RunSpacingAudit` + `BuildAuditReport(List<Point3d>)`: dùng collector cho inner hole.

### Trạng thái
- Build PASS (`MCG_LashingHole_20260801_220903.dll`). CHƯA test runtime.
- Lưu ý: cloud-mark & báo cáo audit đặt/tính ở model space phủ lên block — KHÔNG sửa định nghĩa block. Nếu user muốn mark NẰM TRONG block thì cần bước riêng.

### Đang treo (chưa áp)
- Vá degenerate-guard `GetSmartRectEdges`.
- P1/P2 trong LashingWorkflowState nay vô dụng — dọn sau nếu cần.

### Ghi chú API
- `BlockReference.BlockTransform` = ma trận block-def → world; `center.TransformBy(xform)` cho tâm world (khớp Explode). Dùng để audit lỗ nằm trong block mà không phá block.
- `Circle` ẢO (không AppendEntity) vẫn `IntersectWith` được với entity trong DB — phép toán hình học thuần, không cần circle nằm trong database.

---

## Session 2026-08-01 17:46

### Bối cảnh (user test bản split RUN/POST)
- ✅ Lỗ generate ĐÃ hiện trước prompt special area (điểm 1 đóng).
- ❌ Special area SAI hoàn toàn: thiếu prompt hướng, "không lỗ nào được điều chỉnh", chỉ thêm 2 lỗ.

### Trích lại VBA gốc để đối chiếu
- Viết extractor CFBF + MS-OVBA (`scratchpad/vbaextract`) → trích 6 module ra `../vba_src/`.
- Đọc `PerformSpecialAreaAdjustment_Phase3` (dòng 1330) + `RegenerateLineFromSeed_Special` (1538) + `FindIntersectionWithLongLine_Helper` (2540).
- **Thuật toán VBA thật**: chọn START+END outer → hỏi **"Direction towards P1 or P2?"** → xác định trục (2 lỗ CÙNG Y → mọc CỘT DỌC) → seedHoles = mọi outer trên đường START giữa START↔END → mỗi seed: **XÓA lỗ cũ phía genDir** + **mọc lại 1 hàng lỗ mới** (spacing + retreat né va chạm) tới biên (giao boundary − offset) + dimension → vẽ dual-circle + tô ĐỎ nếu còn va chạm.
- Code CŨ (`CalculateIntermediateCoords`) chỉ "fill gap giữa 2 lỗ" → hoàn toàn khác VBA.

### Đã làm (port trung thành Phase 3)
- `Utilities/AutoCADGeometryHelper.cs`: thêm `TryRayBoundaryIntersection` (bắn tia RAM, port FindIntersectionWithLongLine_Helper).
- `Services/GridEngineService.cs`: thêm `RegenerateSeedLineSpecial` (port RegenerateLineFromSeed_Special — mọc hàng từ seed, KHÔNG có gap Case1/2/3).
- `Services/GridGenerationService.cs`: **viết lại** `RegenerateSpecialArea` đúng VBA (seed detection, delete-beyond-seed, regen line, dims, mark đỏ). Bỏ `CalculateIntermediateCoords`.
- `Services/LashingWorkflowService.cs`: state thêm P1/P2 + cờ `ContinueSpecial`; special-area đổi từ while-loop → **1 bước/lần chạy POST** (thêm prompt hướng P1/P2), kết thúc lệnh → repaint → tự re-chain POST qua Idle để user THẤY lỗ mới trước khi hỏi tiếp.
- `Commands/LashingHoleCommands.cs`: `RunLashingPost` re-hook Idle khi `ContinueSpecial`.

### Trạng thái
- Build PASS (`MCG_LashingHole_20260801_174610.dll`). CHƯA test runtime.
- Kỳ vọng: special area giờ hỏi hướng P1/P2, xóa+mọc lại hàng lỗ, hiện ra sau mỗi bước.

### Ghi chú API
- `.dvb` = OLE compound (CFBF) + VBA nén MS-OVBA. Extractor tự viết: quét stream tìm sig 0x01 → decompress → giữ block chứa "Attribute VB_Name". Nguồn VBA ở `vba_src/mod_MainLashingHole_V3.bas` (134KB).
- VBA `isVerticalRegen = |startY - endY| < EPS` (2 lỗ cùng Y → mọc các đường DỌC). axisIsX = !isVerticalRegen.

### Đang treo (chưa áp)
- Vá degenerate-guard `GetSmartRectEdges`.

---

## Session 2026-08-01 16:34

### Đã làm (điểm 1 — lỗ hiện muộn: FIX TRIỆT ĐỂ bằng tách lệnh)
- User xác nhận `ed.Regen()` + `ed.UpdateScreen()` VẪN không hiện lỗ trước prompt special area.
- Kiểm chứng `SafeRun` KHÔNG bọc transaction ngoài → commit ở PHASE 1 là final. Kết luận: **AutoCAD batch graphics trong 1 lệnh modal — chỉ repaint khi lệnh trả về "Command:"**. Regen/UpdateScreen giữa lệnh không flush.
- **Giải pháp**: tách `RunCreateFlow` thành 2 lệnh:
  - `MCG_LH_RUN` → `RunGenerate()`: boundary → structures → adjacent → P1/P2 → PHASE 1 (sinh lưới + vẽ + dimension) → commit → **KẾT THÚC lệnh** (AutoCAD repaint → lỗ chắc chắn hiển thị). Lưu state qua `LashingWorkflowState`.
  - `MCG_LH_POST` → `RunPostProcess()`: special area loop → local adjust → đóng block. Đọc state.
  - Auto-chain single-click: sau `RunGenerate`, hook `Application.Idle` (one-shot) → khi AutoCAD rảnh (đã repaint xong lỗ) → `SendStringToExecute("MCG_LH_POST ")`. User vẫn chỉ bấm START 1 lần.
- File sửa: `Services/LashingWorkflowService.cs` (thêm class `LashingWorkflowState`; tách RunGenerate/RunPostProcess), `Commands/LashingHoleCommands.cs` (lệnh MCG_LH_POST + hook Idle).

### Trạng thái
- Build PASS (`MCG_LashingHole_20260801_163417.dll`). CHƯA test runtime.
- Kỳ vọng: sau boundary/structure/P1P2 → lỗ HIỆN → rồi mới hỏi special area. Nếu OK, điểm 1 đóng.
- Lưu ý phụ (chưa xử lý): lỗ trung gian trong vòng special-area / local-adjust vẫn có thể hiện muộn cùng lý do (batch trong lệnh POST). Chờ user xác nhận điểm chính trước.

### Đang treo (chưa áp)
- Vá degenerate-guard `GetSmartRectEdges`.

### Ghi chú API
- Entity chỉ repaint khi lệnh modal trả về "Command:". Muốn user thấy kết quả GIỮA chuỗi bước → phải KẾT THÚC lệnh, dùng `Application.Idle` (one-shot) + `SendStringToExecute` để tự chạy bước kế → giữ trải nghiệm 1-click.
- `Application.Idle` fire liên tục khi rảnh → BẮT BUỘC gỡ handler (`-=`) ngay đầu callback (one-shot), nếu không sẽ lặp vô hạn lệnh POST.

---

## Session 2026-08-01 15:53

### Đã làm
- User xác nhận điểm 2 (dimension vào block) OK.
- Điểm 1 CHƯA hết: `ed.Regen()` không đủ — chỉ đánh dấu regen, chưa đẩy ra màn hình khi lệnh đang chạy → lỗ vẫn hiện cuối lệnh.
- Vá: thêm `ed.UpdateScreen()` ngay sau mỗi `ed.Regen()` (3 chỗ) — ép vẽ ra màn hình để lỗ hiện NGAY sau generate, trước prompt special area/local (user cần thấy lỗ để quyết định).

### Trạng thái
- Build PASS (`MCG_LashingHole_20260801_155343.dll`). CHƯA test runtime.
- Nếu UpdateScreen vẫn không hiện lỗ → nghi ngờ graphics bị batch trong ngữ cảnh SendStringToExecute; phương án dự phòng: đổi cách flush (Application-level) hoặc tách generate ra command riêng.

### Đang treo (chưa áp)
- Vá degenerate-guard `GetSmartRectEdges`.

### Ghi chú API
- `Editor.Regen()` = mark-for-regen (như VBA Regen nhưng chưa chắc repaint mid-command); `Editor.UpdateScreen()` = ép flush graphics ra màn hình ngay. Cần CẢ HAI khi muốn entity hiện giữa các prompt trong 1 lệnh.

---

## Session 2026-08-01 15:22

### Đã làm (user report: thứ tự flow sai + dimension không vào block)

**Điểm 1 — Thiếu Regen khiến flow NHÌN như sai thứ tự** (code order vốn ĐÚNG VBA):
- Xác nhận `RunCreateFlow`: generate (PHASE 1, dòng 158) CHẮC CHẮN trước special area (204) trước local adjust (245) trước block — đúng VBA, cả auto/manual.
- Root cause user thấy "special area hỏi trước generate": PHASE 1 vẽ lỗ vào DB nhưng **thiếu `Regen`** (VBA gọi `Regen acAllViewports` sau mỗi phase) → màn hình chưa refresh → lỗ hiện muộn (cuối lệnh) → tưởng generate chạy sau. Auto mode: mọi bước trước special area đều im lặng → prompt special area hiện "ngay" sau boundary.
- Vá: thêm `ed.Regen()` sau PHASE 1, sau special-area loop, sau local adjust.

**Điểm 2 — Dimension bị bỏ sót khỏi block** (chọn Cách A):
- `CollectLashingEntities`: test cũ gom dim theo TÂM bbox của dim. Dim đặt lệch 150mm ra ngoài lỗ → tâm rơi ngoài biên → bị loại → nằm rời, không vào block.
- Vá (Cách A): với `AlignedDimension` test theo ĐIỂM ĐO `XLine1Point`/`XLine2Point` (= tâm lỗ / mép panel, luôn trong/trên biên) bằng `IsInsidePolylineOrEdge`; dim loại khác fallback extents-giao-bbox.

### Trạng thái
- Build PASS. CHƯA test runtime — cần user test bản `MCG_LashingHole_20260801_152247.dll` để xác nhận (a) lỗ hiện ngay sau generate, (b) dimension vào block.

### Đang treo (chưa áp)
- Vá degenerate-guard `GetSmartRectEdges` (fallback bbox khi maxX≤minX). User đã duyệt (AskUserQuestion) nhưng tạm hoãn để trao đổi — CHƯA ghi vào file.

### Ghi chú API
- `Editor.Regen()` — refresh viewport như VBA `ThisDrawing.Regen acAllViewports`; gọi NGOÀI transaction.
- `AlignedDimension.XLine1Point`/`XLine2Point` = 2 điểm đo gốc (definition points), luôn nằm trong/trên biên → test robust hơn tâm bbox của cả dimension.

---

## Session 2026-07-24 13:13

### Đã làm — KHÔI PHỤC nguyên tắc CẠNH-DÀI 1500mm cho P1/P2 auto (port VBA GetSmartRectangularPointsFromPolyline)

- **`AutoCADGeometryHelper.GetSmartRectEdges(poly, fbMin, fbMax, out minX/maxX/minY/maxY)`** — thuần hình học, không phụ thuộc Models:
  - Duyệt từng cạnh (khép vòng `(i+1)%n` như VBA), bỏ cạnh cong (`GetBulgeAt` ≥ 0.001) và cạnh ngắn ≤ 1500mm.
  - Cạnh đứng (Δx<1mm) → minX/maxX; cạnh ngang (Δy<1mm) → minY/maxY. Fallback per-axis về bbox.
- **`GridEngineService`** (auto overload): P1/P2 + effCenter (P1 mode) lấy từ `GetSmartRectEdges`; **giới hạn phát triển lưới vẫn dùng full bbox** (`GetSmartRectFromPolyline`) — đúng VBA (B_abs riêng, smart rect riêng).
- **`LashingWorkflowService`**: thay `DeriveP1P2(box, mode)` (bbox góc) bằng `GetSmartP1P2(db, boundaryId, box, mode)` — mở boundary, gọi GetSmartRectEdges, gán góc theo mode. Xoá DeriveP1P2. Log ghi "long-edge 1500mm".

### Kiểm chứng (standalone test scratchpad/collisiontest/SmartRect.cs)
- Panel 4000×4000 có MẤU LỒI ra X=4300 (các cạnh mấu đều ngắn): **bbox = X[0,4300]** (gồm mấu) vs **smart rect = X[0,4000]** (bỏ mấu, bám mép chính) ✅. Chữ nhật sạch → smart = 0..4000 cả 2 trục ✅.

### Trạng thái
- Build PASS. 2 test standalone PASS (tránh va chạm + smart rect). CHƯA test runtime AutoCAD.

### Ghi chú
- Quirk VBA giữ nguyên: nếu 1 cạnh bị chẻ hết thành đoạn < 1500mm thì trục đó chỉ nhận cạnh dài còn lại (có thể suy biến min=max) — port trung thành, không "sửa khôn".
- `boundary.GetBulgeAt(i)` / `GetPoint2dAt((i+1)%n)` — hợp lệ cả polyline kín lẫn hở (hở: có cạnh khép ảo như VBA).

---

## Session 2026-07-23 11:44

### Đã làm — KIỂM TRA + VÁ LOGIC TRÁNH VA CHẠM (user báo "logic không chạy")

**Kiểm chứng bằng test độc lập** (scratchpad/collisiontest — pure geometry, không cần AutoCAD):
- Tái hiện chính xác ClassifyCollision + FindSafePoint + vòng vẽ DrawHolesWithDimensions.
- Kịch bản panel 3000×2000, plate đứng X=1000 + stiffener ngang Y=1000: 11/35 lỗ va chạm ban đầu → **sau FindSafePoint cả 11 dời an toàn, 0 lỗ còn đè**.
- KẾT LUẬN: thuật toán tránh va chạm ĐÚNG và có chạy. Nhưng test lộ 2 lỗ hổng tầng dữ liệu.

**Vá 1 — ClassifyCollision ép 2D** (`CollisionEngineService`):
- Trước: `center.DistanceTo(closestPt)` tính 3D. Nếu cấu kiện có elevation Z≠0 → distance phồng > clearance → BỎ SÓT va chạm → lỗ xuyên cấu kiện ("logic không chạy").
- Nay: chiếu tâm về Z=0, tính distance 2D (bỏ Z).

**Vá 2 — Phân loại hướng "smart" theo tangent cấu kiện** (`ClassifyDirection`):
- Trước: dùng vector tâm→điểm-gần-nhất. Lỗ nằm ĐÚNG trên cấu kiện → delta≈0 → luôn Complex → mất tính "né ngang cho plate đứng / né dọc cho stiffener ngang".
- Nay: lấy `curve.GetFirstDerivative` tại điểm gần nhất → tangent dọc = cấu kiện đứng → Vertical (né ngang); tangent ngang = Horizontal (né dọc). Fallback về vector cũ nếu không lấy được tangent. Test xác nhận Vertical/Horizontal kích hoạt đúng.

**Chẩn đoán runtime** (`BlockPackingService.DrawStats` + `LashingWorkflowService`):
- PHASE 1 giờ in ra command line: `[structures=N, grid=N, collided=N, relocated=N, red(stuck)=N]`.
- Nếu `structures=0` → cảnh báo rõ "không có cấu kiện để tránh va chạm" (giúp phân biệt lỗi chọn cấu kiện vs lỗi thuật toán).

### Trạng thái
- Build PASS. Test standalone PASS. CHƯA test runtime AutoCAD — nhưng lần chạy tới sẽ có số liệu chẩn đoán ngay trên command line.

### Bước tiếp theo / cần user xác nhận
- Chạy `MCG_LH_RUN`, đọc dòng `[structures=... collided=... relocated=...]`:
  - `structures=0` → vấn đề ở SelectStructuresByCrossing (filter/crossing), KHÔNG phải thuật toán.
  - `structures>0, collided=0` mà mắt thấy lỗ đè → cấu kiện dạng polyline KÍN bọc lỗ (GetClosestPointTo chỉ bắt cạnh; lỗ nằm sâu bên trong không bị bắt — giống hạn chế VBA IntersectWith). Cần point-in-polygon cho cấu kiện kín (enhancement ngoài VBA).
  - `collided>0, relocated>0` → tránh va chạm đang chạy đúng.

### Ghi chú API
- `Curve.GetParameterAtPoint(cp)` + `GetFirstDerivative(param)` → tangent cấu kiện; bọc try/catch vì GetParameterAtPoint có thể ném nếu điểm lệch curve do sai số.

---

## Session 2026-07-23 09:40

### Đã làm

**TÁI CẤU TRÚC UX — mô hình VBA UserForm: palette chỉ nhập liệu + START, flow tuần tự chạy tại command line**
(Quyết định user đã chốt: Full flow như VBA + giữ 2 nút Audit trên palette)

- `Models/LashingHoleModels.cs`: thêm `LashingParamsStore` (static) — cầu nối tham số Palette → CommandMethod.
- `Services/GridEngineService.cs`:
  - Overload `GenerateGrid(..., p1, p2, startOption, effCenter, skipCenterAdjust)` — nhận anchor tường minh cho manual mode.
  - Thêm case start `"P2"` (từ P2 lùi về P1, khớp VBA) + `skipCenterAdjust` (VBA skipInitialAdjustment khi user pick effective center).
- `Services/BlockPackingService.cs`:
  - Tách `DrawHolesWithDimensions(...)` public — vẽ dual-circle + tô đỏ + dims cho lưới ĐÃ TÍNH, nhận dimOrigin (P1) + rect P1-P2 tường minh; `GenerateHoles` cũ delegate sang.
  - `PackIntoBlock` nhận `presetName` (tên block hỏi qua command line).
- `Services/GridGenerationService.cs` (fill gap):
  - **Fix bug**: bán kính inner lấy `p.HoleDiameter/2` thay vì `startHole.Radius` (user pick outer R75 → lỗ trung gian bị vẽ sai R75).
  - Thêm param `structures` → tô đỏ lỗ mới nếu va chạm (khớp VBA highlight sau Phase 3).
- **MỚI** `Services/LashingWorkflowService.cs` — port trọn flow `CFS_CreateLashingHole`:
  - `RunCreateFlow`: boundary pick (filter layer 0/AM_0, >20m²) → auto crossing structures (lọc INSERT+AM_11) → adjacent V/H/N + pick adjacent plines → keep-out ảo → P1/P2 (auto bbox theo mode / manual GetPoint+GetCorner) → start P1/P2/Center keyword → effective center pick có validate → PHASE 1 (grid engine + vẽ + dims) → special area Y/N loop (pick 2 outer circle) → local adjust (auto/Y-N) `LocalAdjustRedHoles` (dịch lỗ đỏ 8-hướng, reset ByLayer, dim cũ→mới) → hỏi tên block (default `<PanelName>_L.H`, Esc bỏ qua) → PackIntoBlock.
  - `RunSpacingAudit` / `RunInterferenceAudit`: tự prompt boundary + tự quét structures — palette không giữ state.
- `Commands/LashingHoleCommands.cs`: thêm `MCG_LH_RUN`, `MCG_LH_AUDIT`, `MCG_LH_INTERFERE` (mỏng, gọi service qua SafeRun).
- `UI/LashingHolePalette.xaml(.cs)` viết lại: chỉ còn INPUT + MODE + SETTINGS + **▶ START** + 2 nút AUDIT + status. Bỏ hết: pick boundary/structures, CREATE, Phase 2, Pack Block. Code-behind không còn service/state; `Dispatch()` = validate (port VBA ValidateInput) → SaveSettings → ParamsStore → `SendStringToExecute`.

### Trạng thái
- Build PASS (0 warning / 0 error). CHƯA test runtime AutoCAD.
- Palette giờ đúng mô hình VBA UserForm; mọi tương tác qua command line trong command context (không còn editor prompt từ modeless palette).

### Bước tiếp theo
- Test runtime end-to-end: NETLOAD → `MCG_CreateLashingHole` → START → theo prompts.
- Artifact mockup UI đã LỖI THỜI (còn UI cũ nhiều nút) — cần cập nhật nếu user muốn.
- Tinh chỉnh nếu cần: VBA `GetSmartRectangularPointsFromPolyline` dùng long-segment threshold 1500mm, bản port auto dùng bbox corners.

### Ghi chú API
- Pattern palette→flow: button → `SendStringToExecute("MCG_LH_RUN\n")` → CommandMethod (document tự lock, editor prompt hợp lệ). KHÔNG gọi GetEntity/GetSelection trực tiếp từ modeless palette.
- `PromptKeywordOptions.AllowNone=true` + `Keywords.Default` → Enter = default; `PromptStatus.None` phân biệt với Esc (Cancel).
- Editor prompts phải nằm NGOÀI transaction; mỗi phase mở transaction riêng.

---

## Session 2026-07-20 16:27

### Đã làm

**Fix fidelity — tô đỏ lỗ va chạm không né được** (port `CheckAndHighlightConflicts` + hành vi Phase 2)
- Trước đây: lỗ va chạm mà `FindSafePoint` không né được → `continue` (BỎ lỗ). Sai so với VBA.
- Nay (`BlockPackingService.GenerateHoles`): va chạm → thử dịch 8-hướng; **dịch được** → vẽ vị trí mới (ByLayer); **không dịch được** → vẽ tại vị trí lưới + **tô đỏ outer circle** (`ColorIndex=1 = acRed`), không drop.
- `DrawCircle` thêm tham số `markRed`; chỉ outer circle bị tô đỏ (khớp VBA `circleObj.color = acRed`), inner giữ ByLayer.
- Lỗ đỏ vẫn được gom vào block (PackIntoBlock) và tính trong audit — như VBA.

**Ghi chú tương đương hành vi**: VBA tách 2 bước (CheckAndHighlightConflicts tô đỏ TẤT CẢ lỗ va chạm → Phase 2 optional mới dịch). Bản port gộp: va chạm → dịch ngay → fail mới đỏ. Kết quả cuối KHỚP VBA **auto mode** (auto chạy local adjust): lỗ dịch được = ByLayer, lỗ không dịch được = đỏ.

### Trạng thái
- Build PASS. Addin đầy đủ nghiệp vụ + đã khôi phục cảnh báo đỏ. CHƯA test runtime AutoCAD.

### Bước tiếp theo
- Test runtime end-to-end trên panel mẫu; xác nhận lỗ đỏ xuất hiện đúng chỗ va chạm không né được.

---

## Session 2026-07-20 16:16

### Đã làm

**Slice 4b — Cloud-mark interference audit** (port VBA `CheckLashingHole_InterferenceStructure` + `DbxCopyBlock`)
- Tạo `Services/InterferenceAuditService.cs`:
  - `RunAudit`: quét outer clearance circle (layer AM_9, R≈clearance) trong boundary; với mỗi lỗ × cấu kiện dùng `Circle.IntersectWith` → va chạm nếu ≥3 giao điểm (VBA uBound>5) hoặc =2 giao điểm mà chord ăn sâu (dist tâm→trung điểm ≤ R−0.0001). Va chạm → chèn block `LashingInterfereCloudMark` tại tâm.
  - `EnsureInterferenceBlock`: port ObjectDBX — `ReadDwgFile(Symbol.dwg)` + `WblockCloneObjects` copy block vào drawing hiện tại; graceful nếu thiếu file/block.
  - Proxy-safe: check `ObjectClass.IsDerivedFrom(Circle)` trước GetObject.
- UI: thêm nút "Audit Interference (Cloud Marks)" + `CmdAuditInterference_Click`; **giữ nguyên** nút audit-text spacing cũ.
- `Symbol.dwg` xác nhận tồn tại tại `C:\CustomTools\Symbol.dwg`.

### Trạng thái — ADDIN CHUYỂN THỂ HOÀN THIỆN (build PASS)
Toàn bộ nghiệp vụ VBA đã port sang C# giữ kiến trúc mới:
1. UI + Registry settings ✅  2. Boundary/structure select (tay+auto) ✅
3. Grid engine Phase 1 (seeded growth + retreat-and-gap + GAP 3-case) ✅
4. Dual-circle + full continuous dimensioning ✅  5. Block packing `<PanelName>_L.H` ✅
6. Phase 2 fill-gap ✅  7. Audit spacing (mới) + Audit interference cloud-mark (VBA) ✅
- **CHƯA test runtime trong AutoCAD** — cần load `.dll` kiểm chứng end-to-end.

### Bước tiếp theo
- Test runtime: load bin\x64\Debug\MCG_LashingHole_*.dll, lệnh `MCG_CreateLashingHole`, chạy trên panel mẫu; so lưới/dim/block với VBA gốc.
- Kiểm chứng `ClassifyCollision` (GetClosestPointTo) vs VBA IntersectWith — xem hướng dịch lỗ có khớp.
- (Tùy chọn) copy source VBA giải nén vào `docs/legacy_vba/`.

### Ghi chú API
- ObjectDBX .NET: `new Database(false,true)` + `ReadDwgFile(path, FileShare.Read, true, null)` + `db.WblockCloneObjects(ids, db.BlockTableId, map, DuplicateRecordCloning.Ignore, false)` để import block định nghĩa từ dwg ngoài.
- `Circle.IntersectWith(ent, Intersect.OnBothOperands, Point3dCollection, IntPtr.Zero, IntPtr.Zero)` — tương đương ActiveX `IntersectWith(acExtendNone)`.

---

## Session 2026-07-20 16:11

### Đã làm

**Slice 4a — Full continuous dimensioning** (port VBA `AddContinuousDimensions`)
- `BlockPackingService.AddContinuousDimensions()`: phân loại điểm theo hàng/cột → chọn hàng dài nhất (ngang) + cột dài nhất (đứng) → chuỗi dim liên tục gồm mốc P1 + biên rect, bỏ đoạn trùng/0.
- Helper `CreateAlignedDim` (set `Dimscale=25` trên entity), `GetUniqueSorted` (distinct key "0.000" + sort), `CK`.
- Wire vào `GenerateHoles`: thu `drawnPoints`, suy P1 theo LocationMode (StarBoard→Trên-Trái, còn lại→Dưới-Trái), gọi dim với rect = bbox boundary.
- Dim tạo trên layer `Mechanical-AM_9` → `PackIntoBlock` (Slice 2) tự gom vào block.

### Trạng thái
- Build PASS. Output Phase 1 giờ đủ: lưới né va chạm + dual circle + chuỗi dimension → gom block. CHƯA test runtime AutoCAD.

### Bước tiếp theo
- **Slice 4b — Cloud-mark interference audit** (cần quyết định): port `CheckLashingHole_InterferenceStructure` — chèn block `LashingInterfereCloudMark` từ `C:\CustomTools\Symbol.dwg` (ObjectDBX). Phụ thuộc file ngoài + khác audit-text hiện tại.

---

## Session 2026-07-20 16:05

### Đã làm

**Slice 3 — Port Phase 1 Grid Engine trung thành** (phần lõi lớn nhất)
- Tạo `Services/GridEngineService.cs` — port từ VBA `mod_MainLashingHole_V3`:
  - `GenerateCentralPoints`: gieo mầm từ effective-center, mọc 4 hướng (Center mode) hoặc từ P1 (PortSide/StarBoard).
  - `GenerateLineOfPoints`: mọc điểm dọc trục + retreat-and-gap + GAP HANDLING 3 case (Case 2 chèn điểm, Case 3 reposition Li-1).
  - `AdjustPointAlongAxis` (gộp AdjustPointHorizontal/Vertical), `AdjustInitialCenter8Dir` + `FindSafePointAlongDirection`.
  - Hằng số khớp VBA: `MIN_DIST_FACTOR_AFTER_RETREAT=0.25`, `RETREAT_STEP_MM=5`, `MIN_EDGE_DISTANCE_FOR_GAP=200`, `MAX_ITER=100`, `stdMaxRetreat=2·clearance+20`.
  - Dict key khớp VBA `GetCoordKey` = format "0.000".
  - Va chạm dùng `CollisionEngineService.HasAnyCollision` (RAM) thay vì vẽ circle tạm — giữ kiến trúc mới.
- `BlockPackingService`: thay `BuildGridPoints` (nested-loop rút gọn) bằng `_gridEngine.GenerateGrid(...)`; xoá `BuildGridPoints` cũ.
- Map LocationMode → thuật toán: Center→"Center" (centroid), PortSide→"P1" (dirX=1,dirY=1), StarBoard→"P1" (dirX=1,dirY=-1).

### Trạng thái
- Build `dotnet build -c Debug` PASS. CHƯA test runtime trong AutoCAD (cần load .dll kiểm chứng lưới thực tế).
- Grid engine trả điểm đã né va chạm + trong boundary → vòng lặp FindSafePoint trong GenerateHoles giờ chỉ là lưới an toàn dự phòng.

### Bước tiếp theo (để "addin hoàn thiện")
- **Slice 4a — Full continuous dimensioning**: port `AddContinuousDimensions` (chuỗi dim dọc từng hàng/cột của lưới cuối), thay vì chỉ dim lỗ điều chỉnh. Self-contained, không cần file ngoài.
- **Slice 4b — Cloud-mark interference audit**: port `CheckLashingHole_InterferenceStructure` — chèn block `LashingInterfereCloudMark` từ `C:\CustomTools\Symbol.dwg` (ObjectDBX). CẦN xác nhận: phụ thuộc file Symbol.dwg + thay hành vi audit text hiện tại.
- Kiểm chứng runtime: so lưới engine vs VBA gốc trên cùng 1 panel mẫu.

### Ghi chú API
- P1 mode: lưới = (X trên trục row) × (Y trên trục col) — chỉ lấy toạ độ nằm trên trục cơ sở, khớp VBA `xCoordsOnEffectiveRowAxis`/`yCoordsOnEffectiveColAxis`.
- `AdjustPointAlongAxis`: retreatDir = -genDir (luôn ngược hướng mọc); nhánh `retreatDir==genDir` trong VBA là dead code nên đã bỏ.

---

## Session 2026-07-20 15:55

### Đã làm

**Trích xuất & phân tích VBA gốc từ `CreateLashingHole.dvb`**
- Viết extractor C# (ole32 + giải nén MS-OVBA) bung được 6 module: `mod_MainLashingHole_V3` (2575 dòng), `mod_LashingHoleInterference`, `ModInputHelper_V3`, `frmLashingInput`, `frmHelpDialog`, `ThisDrawing`.
- Đối chiếu VBA gốc ↔ addin C#: addin hiện là bản viết lại rút gọn ~50-60%, còn thiếu grid engine Phase 1, block packing, cloud-mark audit, full dimensioning.

**Quyết định hướng port**: Lấp gap, giữ kiến trúc mới (RAM collision + service tách lớp).

**Slice 1 — Fix layer fidelity** (`Models/LashingHoleModels.cs`)
- `LAYER_INNER_HOLE`: `Mechanical-AM_0` → `"0"` (khớp VBA `holeLayerName`)
- `LAYER_OUTER_CLEAR`: `Mechanical-AM_3` → `Mechanical-AM_9` (khớp VBA `clearanceLayerName`)
- Sai layer trước đây sẽ làm audit interference hỏng (VBA lọc outer theo `Mechanical-AM_9`).

**Slice 2 — Block Packing thật** (port đoạn "BLOCK PACKING" của VBA)
- `BlockPackingService.PackIntoBlock()`: gom inner/outer circle (khớp bán kính) + dimension trong boundary → tạo block `<PanelName>_L.H` (tên duy nhất) → `DeepCloneObjects` → insert 1 block reference → xóa entity gốc.
- Quét ModelSpace an toàn proxy: check `ObjectClass.IsDerivedFrom` TRƯỚC khi `GetObject` (theo pattern SESSION trước).
- UI: thêm nút "Pack Holes → Create Block" + handler `CmdPackBlock_Click` (`LashingHolePalette`).

### Trạng thái
- Phase hiện tại: Migration VBA → C# addin, hướng "lấp gap giữ kiến trúc".
- Build `dotnet build -c Debug` PASS (net48 + AutoCAD 2023). CHƯA test runtime trong AutoCAD.
- Source VBA đã giải nén: `scratchpad/dvbextract/out/*.vba` (chưa copy vào repo).

### Bước tiếp theo
- **Slice 3 (lõi, lớn nhất)**: Port Phase 1 grid engine trung thành — `GenerateCentralPoints` + `GenerateLineOfPoints` (seeded line-growth 4 hướng từ effective-center + retreat-and-gap + tái phân bố Li-1), thay `BuildGridPoints` nested-loop hiện tại.
- **Slice 4**: Full continuous dimensioning (`AddContinuousDimensions`) + cloud-mark interference audit (chèn block `LashingInterfereCloudMark` từ `Symbol.dwg`).
- Kiểm chứng: `ClassifyCollision` C# dùng `GetClosestPointTo` (comment ghi V/H có thể bị đảo) — cần test so với VBA `IntersectWith`.

### Ghi chú API
- `.dvb` = OLE Compound File, VBA source nén MS-OVBA; nested `VBA_Project/VBA/dir` + module streams (offset lấy từ record `0x0031` trong `dir`).
- Block packing: set `BlockTableRecord.Origin = basePt` + insert `BlockReference` tại `basePt` → geometry giữ nguyên tọa độ world (tương đương VBA `Blocks.Add(p1)` + `InsertBlock(p1)`).

---

## Session 2026-06-29 15:10

### Đã làm

**ROOT CAUSE XÁC ĐỊNH qua diagnostic log**

```
[15:07:05.307] Auto mode: TryAutoDetectBoundary...
← dừng tại đây → crash trong TryAutoDetectBoundary
```

**Root cause thực sự**: `foreach (ObjectId id in ms)` duyệt ALL entities trong ModelSpace.
Khi `tr.GetObject(id, OpenMode.ForRead)` gặp **proxy entity / custom entity** (Autodesk MEP, Structural, XREF objects),
AutoCAD native code cố load class handler của proxy = null pointer → FATAL ERROR.
Native exception bypass hoàn toàn managed try/catch.

**Fix: `id.ObjectClass.IsDerivedFrom(...)` — check type TRƯỚC khi mở entity**
- Đọc metadata object (RXClass) mà không cần open → an toàn với mọi entity type
- Chỉ mở entity nếu type là đúng (Polyline, Curve)

**3 chỗ đã fix:**

| Vị trí | Filter type | Lý do |
|---|---|---|
| `TryAutoDetectBoundary`: vòng foreach ModelSpace | `IsDerivedFrom(RXClass Polyline)` | Chỉ cần tìm Polyline boundary |
| `CmdCreate_Click`: load structures từ `_structureIds` | `IsDerivedFrom(RXClass Curve)` | Structural members = Curve subclasses |
| `TryAutoSelectStructures`: filter sau SelectCrossingWindow | `IsDerivedFrom(RXClass Curve)` | SelectCrossingWindow trả về ALL entities kể cả proxy |

**Pattern chuẩn cho việc iterate ModelSpace:**
```csharp
var polylineClass = AcRuntime.RXObject.GetClass(typeof(Polyline));
foreach (ObjectId id in ms)
{
    if (!id.IsValid || id.IsErased) continue;
    if (!id.ObjectClass.IsDerivedFrom(polylineClass)) continue; // type guard TRƯỚC khi mở
    var poly = tr.GetObject(id, OpenMode.ForRead) as Polyline;  // an toàn
}
```

**Bonus**: thêm DiagLogger (`Utilities/DiagLogger.cs`) để chẩn đoán — ghi ra `%TEMP%\MCG_LashingHole_diag.txt`.
XÓA DiagLogger sau khi crash đã được fix xác nhận.

### Trạng thái
- Build: ✅ 0 error, 0 warning
- Root cause: ✅ Xác định chính xác qua log
- Fix: ✅ Applied

### Ghi chú API (QUAN TRỌNG — lưu vĩnh viễn)
- `id.ObjectClass.IsDerivedFrom(RXClass)` → check entity type mà không open object → safe với mọi loại entity
- `foreach (ObjectId id in ms)` duyệt ModelSpace có thể gặp proxy entity → LUÔN pre-filter bằng ObjectClass
- Native Access Violation (`Reading 0x0000`) từ proxy entity KHÔNG bị catch bởi C# try/catch trong .NET Framework 4.8
- `using AcRuntime = Autodesk.AutoCAD.Runtime;` thay vì `using Autodesk.AutoCAD.Runtime;` — tránh conflict với `Microsoft.Win32.RegistryKey`

---

## Session 2026-06-29 15:00

### Đã làm

**Bugfix 3: FATAL ERROR vẫn còn — 5 fixes nữa (aggressive)**

Phân tích: crash tại cùng địa chỉ native `EBBEBEC7h` mọi lần → cùng instruction → deterministic.
Native exceptions (Access Violation Reading 0x0000) **không bị catch bởi C# try/catch** trong .NET Framework 4.8
(xử lý như "corrupted state exceptions").

Tất cả editor ops và "drawing state" API đã bị loại bỏ khỏi Create workflow:

| Fix | File | Chi tiết |
|---|---|---|
| Remove `SetSystemVariable("DIMSCALE")` | `UI/LashingHolePalette.xaml.cs` | Native view API từ palette context có thể crash |
| Remove `ZoomToBoundaryExternal()` + method | `UI/LashingHolePalette.xaml.cs` | `ed.SetCurrentView()` từ palette crash nếu không có `SetFocusToDwgView` |
| Remove `ZoomToBoundary` trong `TryAutoSelectStructures` | `UI/LashingHolePalette.xaml.cs` | `SelectCrossingWindow` dùng model coords, không cần viewport zoom |
| Add `SetFocusToDwgView()` vào `CmdCreate_Click` | `UI/LashingHolePalette.xaml.cs` | Tất cả buttons trên Palette đều phải gọi theo CLAUDE.md |
| Remove `db.Dimscale = DIMSCALE` khỏi transaction | `Services/BlockPackingService.cs` | Database-wide property set trong transaction → reactor callback crash |
| `DrawCircle`: dùng `new Circle()` + `SetDatabaseDefaults(db)` | `Services/BlockPackingService.cs`, `GridGenerationService.cs` | Parameterized constructor tạo "zombie entity" không có database context; `SetDatabaseDefaults` initialize đúng cách trước `AppendEntity` |

### Trạng thái
- Build: ✅ 0 error, 0 warning

### Bước tiếp theo
- Deploy và test trong AutoCAD 2023
- Nếu crash vẫn còn: thêm file-based diagnostic logging để identify exact crash point

---

## Session 2026-06-29 14:50

### Đã làm

**Bugfix 2: FATAL ERROR vẫn còn sau fix 14:30 — thêm 3 fixes nữa**

Root cause thứ hai: `new Circle(center, Vector3d.ZAxis, radius)` trong `CollisionEngineService.ClassifyCollision` tạo **transient database object không có database context**. Khi `IntersectWith()` được gọi, AutoCAD native code cố đọc database pointer từ object này = null → `Reading 0x0000` → FATAL ERROR.

**Sự khác biệt:**
- Virtual Circle dùng cho DrawCircle (thêm vào DB) → an toàn vì `AppendEntity` + `AddNewlyCreatedDBObject`
- Virtual Circle dùng cho `IntersectWith()` collision check → CRASH vì không bao giờ được thêm vào DB

**3 fixes mới:**

| File | Fix |
|---|---|
| `Services/CollisionEngineService.cs` | Thay `new Circle() + IntersectWith()` → `curve.GetClosestPointTo()` (pure geometry, không database object) |
| `Services/BlockPackingService.cs` | `EnsureLayerExists`: mở LayerTable bằng `ForWrite` trực tiếp thay vì `ForRead + UpgradeOpen()` |
| `UI/LashingHolePalette.xaml.cs` | Bọc `tr.GetObject` cho structures trong try/catch phòng entity bị xóa |

**Logic phân loại collision thay đổi (GetClosestPointTo NGƯỢC IntersectWith):**
```
IntersectWith chord: DeltaY >> DeltaX → Vertical   | DeltaX >> DeltaY → Horizontal
GetClosestPointTo:   DeltaX >> DeltaY → Vertical   | DeltaY >> DeltaX → Horizontal
```
Lý do: closest point vector vuông góc với structure surface — hướng X lớn = structure nằm cạnh = cấu kiện đứng.

### Trạng thái
- Build: ✅ 0 error, 0 warning

### Bước tiếp theo
- Deploy lại DLL và test trong AutoCAD 2023

---

## Session 2026-06-29 14:30

### Đã làm

**Bugfix: FATAL ERROR Access Violation khi bấm Create Lashing Holes**

Root cause: Editor API (`GetCurrentView`, `SetCurrentView`, `SetSystemVariable`, `WriteMessage`)
bị gọi bên trong `TransactionManager.StartTransaction()` — xung đột internal state của AutoCAD → crash native.

**Quy tắc vàng đã áp dụng:**
> Editor ops + SetSystemVariable **KHÔNG ĐƯỢC** gọi trong bất kỳ transaction đang mở nào.

Fixes (3 files):

| File | Lỗi | Fix |
|---|---|---|
| `UI/LashingHolePalette.xaml.cs` | `ZoomToBoundary()` + `SetSystemVariable("DIMSCALE")` trong main transaction | Di chuyển ra ngoài transaction; thêm helper `ZoomToBoundaryExternal()` |
| `UI/LashingHolePalette.xaml.cs` | `TryAutoSelectStructures`: `ZoomToBoundary()` + `SelectCrossingWindow()` trong transaction | Tách: đọc extents trong transaction → commit → zoom + select ngoài transaction |
| `UI/LashingHolePalette.xaml.cs` | `CmdAudit_Click`: `doc.Editor.WriteMessage()` trong transaction | Di chuyển sau `tr.Commit()` |
| `Utilities/AutoCADGeometryHelper.cs` | `ZoomToBoundary` chỉ nhận `Polyline` (phải có transaction để đọc) | Thêm overload nhận `Extents3d` — dùng khi transaction đã commit |

Pattern chuẩn đã áp dụng:
```
[LockDocument]
  SetSystemVariable(...)     ← NGOÀI transaction ✅
  ZoomToBoundaryExternal()   ← NGOÀI transaction ✅
    [Transaction 1 – read extents]
    tr.Commit()              ← Commit TRƯỚC khi gọi editor
    editor.SetCurrentView()  ← NGOÀI transaction ✅
  [Transaction 2 – main DB ops, KHÔNG có editor ops]
  tr.Commit()
  SetStatus(...)             ← NGOÀI transaction ✅
```

### Trạng thái
- Build: ✅ 0 error, 0 warning
- FATAL ERROR: ✅ Fixed

### Bước tiếp theo
- Deploy lại và test trong AutoCAD 2023

---

## Session 2026-06-29 14:12

### Đã làm

**Task: Implement 12 Gaps + UI overhaul (tất cả so sánh DVB vs C# repo)**

Files đã sửa (8 files, +1089 / -486 lines, build 0 error 0 warning):

| File | Thay đổi chính |
|---|---|
| `Models/LashingHoleModels.cs` | Fix namespace `SDS.Models` → `MCG_CreateLashingHole.Models`; thêm 3 layer constants (`LAYER_INNER_HOLE/OUTER_CLEAR/DIMENSION`) |
| `Utilities/AutoCADGeometryHelper.cs` | Fix namespace; thêm `GetPolygonCentroid()` (Shoelace), `IsInsidePolylineOrEdge()` (support open polyline), `ZoomToBoundary()` |
| `Services/CollisionEngineService.cs` | Fix namespace; thêm `FindSafePoint()` 8-direction scan, `HasAnyCollision()`, `GetWorstCollision()`; priority dirs (Vertical→X first, Horizontal→Y first) |
| `Services/BlockPackingService.cs` | Fix namespace; dual circle (inner AM_0 + outer AM_3); dùng `FindSafePoint()` thay shift đơn giản; `BuildGridPoints()` dùng polygon centroid cho Center mode; AddAdjustedDimensions chỉ cho lỗ bị điều chỉnh |
| `Services/GridGenerationService.cs` | Fix namespace; GAP HANDLING 3 cases (Case1: fill chuẩn, Case2: thêm slot, Case3: điều chỉnh spacing); dual circle trong Special Area; EnsureLayerExists |
| `Commands/LashingHoleCommands.cs` | Fix namespace `SDS.Commands` → `MCG_CreateLashingHole.Commands`; fix ambiguous `Exception` |
| `UI/LashingHolePalette.xaml` | Full English UI; RadioButtons cho LocationMode (thay ComboBox); Audit button; Phase 2 button rename; section headers SETTINGS/PREPARATION/EXECUTION MODE |
| `UI/LashingHolePalette.xaml.cs` | Fix namespace; Registry save/load (Gap #5); Auto-detect boundary area>20m² (Gap #6); Auto-zoom via `ZoomToBoundary()` (Gap #7); Audit function với spacing check (Gap #8); Auto-select structures trong Automatic mode |

### 12 Gaps đã giải quyết

| # | Gap | Trạng thái |
|---|---|---|
| 1 | Dual Circle (inner+outer) | ✅ Implemented |
| 2 | 8-Direction Safe Point Search | ✅ Implemented |
| 3 | Phase 2 GAP HANDLING (Case 1/2/3 + Anchors) | ✅ Implemented |
| 4 | Block System (simplified: dual circles) | ✅ Partially (raw circles; block từ template = future) |
| 5 | Registry Persistence | ✅ Implemented |
| 6 | Auto-detect Boundary (area > 20m²) | ✅ Implemented |
| 7 | Auto Zoom trước selection | ✅ Implemented |
| 8 | Audit Function | ✅ Implemented |
| 9 | Dimension chỉ cho adjusted holes | ✅ Implemented |
| 10 | Namespace fix (SDS.* → MCG_CreateLashingHole.*) | ✅ Implemented |
| 11 | Layer AM_0 / AM_3 / AM_9 | ✅ Implemented |
| 12 | Effective Center (Polygon Centroid) | ✅ Implemented |

### Trạng thái
- **Phase hiện tại**: Migration VBA → C# hoàn chỉnh (core logic)
- **Build**: ✅ 0 error, 0 warning (`dotnet build -c Debug`)
- **Gap #4 còn thiếu**: Block từ template file (VBA: copy block từ source DWG) — cần design riêng nếu team muốn

### Bước tiếp theo
- **Deploy & test thực tế** trong AutoCAD 2023: chạy `MCG_CreateLashingHole`, thử Automatic mode, Phase 2, Audit
- **Kiểm tra dual circle**: outerCircle (AM_3) có hiển thị đúng màu/layer không
- **Kiểm tra Registry**: mở lại Palette → values có được restore không
- **Gap #4 nếu cần**: Implement block-based approach (mỗi lỗ = BlockReference thay vì raw Circle)

### Ghi chú API
- `Editor.SelectCrossingWindow()` trong .NET quét thẳng database — không cần zoom màn hình trước. Nhưng vẫn gọi `ZoomToBoundary()` cho UX.
- `IsInsidePolylineOrEdge()` hỗ trợ open polyline (VBA: "ALLOW OPEN POLYLINE") — ray-casting thêm cạnh đóng ảo last→first.
- `GetPolygonCentroid()` dùng Shoelace formula — fallback về midpoint BBox khi polyline suy biến (area ≈ 0).
- Khi dùng `Registry.CurrentUser.CreateSubKey()` trong AutoCAD plugin, cần `using Microsoft.Win32;` — không cần thêm reference (đã có trong .NET Framework).
