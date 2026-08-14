using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcRuntime = Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Utilities;

namespace MCG_CreateLashingHole.Services
{
    /// <summary>
    /// Trạng thái tạm giữ giữa lệnh generate (MCG_LH_RUN) và hậu xử lý (MCG_LH_POST).
    /// Flow bị tách 2 lệnh để lỗ hiện ra màn hình TRƯỚC khi hỏi special area:
    /// lệnh generate kết thúc → AutoCAD repaint toàn bộ → lỗ chắc chắn hiển thị →
    /// MCG_LH_POST (tự chạy qua Application.Idle) mới hỏi các bước hiệu chỉnh.
    /// </summary>
    public static class LashingWorkflowState
    {
        /// <summary>Có phiên generate vừa xong đang chờ hậu xử lý không</summary>
        public static bool HasPending { get; set; }
        /// <summary>Boundary polyline đã chọn</summary>
        public static ObjectId BoundaryId { get; set; }
        /// <summary>Danh sách cấu kiện đã quét trong boundary</summary>
        public static List<ObjectId> StructureIds { get; set; } = new List<ObjectId>();
        /// <summary>Keep-out zones ảo của panel liền kề (RAM)</summary>
        public static List<Line3d> KeepOut { get; set; } = new List<Line3d>();

        /// <summary>Góc tham chiếu P1 (dùng cho hướng phát triển special area)</summary>
        public static Point3d P1 { get; set; }
        /// <summary>Góc tham chiếu P2</summary>
        public static Point3d P2 { get; set; }

        /// <summary>
        /// Cờ báo POST cần chạy lại để hỏi tiếp special area. Sau MỖI lần điều chỉnh, lệnh POST
        /// kết thúc (để AutoCAD repaint lỗ mới) rồi tự chạy lại qua Idle — user luôn thấy kết quả
        /// trước khi quyết định lần kế. Tắt cờ khi user chọn "No" (chuyển sang local adjust + block).
        /// </summary>
        public static bool ContinueSpecial { get; set; }

        /// <summary>Lưu state cuối bước generate, đánh dấu chờ hậu xử lý</summary>
        public static void Set(ObjectId boundary, List<ObjectId> structures, List<Line3d> keepOut,
            Point3d p1, Point3d p2)
        {
            BoundaryId   = boundary;
            StructureIds = structures ?? new List<ObjectId>();
            KeepOut      = keepOut ?? new List<Line3d>();
            P1           = p1;
            P2           = p2;
            HasPending   = true;
        }

        /// <summary>Xóa state sau khi hậu xử lý xong (hoặc khi bắt đầu phiên mới)</summary>
        public static void Clear()
        {
            HasPending   = false;
            BoundaryId   = ObjectId.Null;
            StructureIds    = new List<ObjectId>();
            KeepOut         = new List<Line3d>();
            P1              = Point3d.Origin;
            P2              = Point3d.Origin;
            ContinueSpecial = false;
        }
    }

    /// <summary>
    /// Flow tuần tự dẫn dắt qua command line — port trung thành VBA CFS_CreateLashingHole:
    /// palette chỉ nhập tham số + bấm START; mọi tương tác tiếp theo (chọn boundary,
    /// structures, adjacent V/H/N, P1/P2, special area Y/N loop, local adjust, tên block)
    /// diễn ra tại command line qua Editor prompts.
    /// Chạy trong command context (MCG_LH_RUN) — document đã lock, editor prompt hợp lệ.
    /// </summary>
    public class LashingWorkflowService
    {
        private const string LOG_PREFIX = "[LashingWorkflow]";

        private readonly CollisionEngineService   _collision;
        private readonly BlockPackingService      _packing;
        private readonly GridEngineService        _gridEngine;
        private readonly GridGenerationService    _fillGap;
        private readonly InterferenceAuditService _interference;

        /// <summary>Khởi tạo LashingWorkflowService cùng bộ services lõi</summary>
        public LashingWorkflowService()
        {
            _collision    = new CollisionEngineService();
            _packing      = new BlockPackingService(_collision);
            _gridEngine   = new GridEngineService(_collision);
            _fillGap      = new GridGenerationService(_collision);
            _interference = new InterferenceAuditService();
        }

        // ═════════════════════════════════════════════════════════════
        // FLOW CHÍNH — MCG_LH_RUN
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// PHẦN 1 (MCG_LH_RUN): boundary → structures → adjacent → P1/P2 → sinh lưới + vẽ lỗ + dimension.
        /// KẾT THÚC tại đây để AutoCAD repaint (lỗ hiển thị chắc chắn); special area / local adjust /
        /// đóng block chuyển sang MCG_LH_POST (auto-chain qua Application.Idle).
        /// </summary>
        public void RunGenerate()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;
            var p  = LashingParamsStore.Current ?? new LashingInputParams();

            LashingWorkflowState.Clear(); // reset state phiên trước

            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} === START GENERATE ===");
            ed.WriteMessage("\n=== MCG LASHING HOLE — CREATE FLOW ===");

            // ── Step 2: Chọn boundary (luôn pick trên màn hình như VBA) ──
            ObjectId boundaryId = PromptBoundary(ed, db);
            if (boundaryId.IsNull) { ed.WriteMessage("\nOperation cancelled."); return; }
            ed.WriteMessage("\n-> Boundary selected successfully.");

            Extents3d box;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var b = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                box = b.GeometricExtents;
                tr.Commit();
            }

            // ── Step 3: Tự quét cấu kiện trong bbox (crossing, lọc INSERT + AM_11) ──
            ed.WriteMessage("\nStep 3: Auto-selecting structures inside boundary...");
            var structureIds = SelectStructuresByCrossing(ed, db, boundaryId, box);
            ed.WriteMessage(structureIds.Count == 0
                ? "\nNo structures found. Collision check will be skipped."
                : $"\n-> Auto-selected {structureIds.Count} objects.");

            // ── Step 3.5: Adjacent panel keep-out (V/H/N) ──
            var keepOut = new List<Line3d>();
            if (p.IsCheckAdjacent && structureIds.Count > 0)
            {
                string vh = Keyword(ed, "\nCheck adjacent panel?", "No", "Vertical", "Horizontal", "No");
                if (vh == null) { ed.WriteMessage("\nOperation cancelled."); return; }
                if (vh != "No")
                {
                    keepOut = PromptAdjacentZones(ed, db, box, vh == "Vertical");
                    ed.WriteMessage(keepOut.Count == 0
                        ? "\nNo valid adjacent structures found."
                        : $"\n-> {keepOut.Count} virtual keep-out lines created.");
                }
            }

            // ── Step 4: Xác định P1/P2 ──
            Point3d p1, p2;
            if (p.IsAutomaticMode)
            {
                (p1, p2) = GetSmartP1P2(db, boundaryId, box, p.LocationMode);
                ed.WriteMessage($"\nSmart boundary ({p.LocationMode}, long-edge 1500mm): " +
                    $"P1({p1.X:F0},{p1.Y:F0}) - P2({p2.X:F0},{p2.Y:F0})");
            }
            else
            {
                var pr1 = ed.GetPoint(new PromptPointOptions("\n1. Pick first corner P1:"));
                if (pr1.Status != PromptStatus.OK) { ed.WriteMessage("\nOperation cancelled."); return; }
                var pr2 = ed.GetCorner(new PromptCornerOptions("\n2. Pick opposite corner P2:", pr1.Value));
                if (pr2.Status != PromptStatus.OK) { ed.WriteMessage("\nOperation cancelled."); return; }
                p1 = new Point3d(pr1.Value.X, pr1.Value.Y, 0);
                p2 = new Point3d(pr2.Value.X, pr2.Value.Y, 0);
            }

            double rectMinX = Math.Min(p1.X, p2.X), rectMaxX = Math.Max(p1.X, p2.X);
            double rectMinY = Math.Min(p1.Y, p2.Y), rectMaxY = Math.Max(p1.Y, p2.Y);
            var actualCenter = new Point3d((rectMinX + rectMaxX) / 2.0, (rectMinY + rectMaxY) / 2.0, 0);

            // ── Step 4.1: Điểm bắt đầu rải (Auto → Center như VBA) ──
            string startOption;
            if (p.IsAutomaticMode)
            {
                startOption = "Center";
                ed.WriteMessage("\nAuto mode: start point = Center.");
            }
            else
            {
                startOption = Keyword(ed, "\nSelect hole generation start point", "Center", "P1", "P2", "Center");
                if (startOption == null) { ed.WriteMessage("\nOperation cancelled."); return; }
            }

            // ── Step 4.2: Effective center (manual + P1/P2: cho phép pick tay) ──
            Point3d effCenter        = actualCenter;
            bool    skipCenterAdjust = false;
            if (!p.IsAutomaticMode && (startOption == "P1" || startOption == "P2"))
            {
                string pick = Keyword(ed, "\nManually select effective center point?", "No", "Yes", "No");
                if (pick == "Yes")
                {
                    while (true)
                    {
                        var pr = ed.GetPoint(new PromptPointOptions("\nSelect effective center point:"));
                        if (pr.Status != PromptStatus.OK) break; // Esc → dùng tâm hình học

                        var cand = new Point3d(pr.Value.X, pr.Value.Y, 0);
                        bool ok  = false;
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            var b       = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                            var structs = OpenStructures(tr, structureIds);
                            if (!AutoCADGeometryHelper.IsInsidePolylineOrEdge(cand, b))
                                ed.WriteMessage("\nPoint is outside the boundary. Pick again.");
                            else if (_collision.HasAnyCollision(cand, p.ClearanceRadius, structs, keepOut))
                                ed.WriteMessage("\nPoint collides with a structure. Pick again.");
                            else ok = true;
                            tr.Commit();
                        }
                        if (ok) { effCenter = cand; skipCenterAdjust = true; break; }
                    }
                }
            }

            // ── PHASE 1: Sinh lưới + vẽ + dimension ──
            ed.WriteMessage("\nPHASE 1: Generating initial point grid...");
            List<ObjectId> innerIds;
            BlockPackingService.DrawStats stats;
            int gridCount;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var boundary = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                    var structs  = OpenStructures(tr, structureIds);
                    var bt       = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var space    = (BlockTableRecord)tr.GetObject(
                                       bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Auto + Center mode: dùng polygon centroid làm effective center (cải tiến giữ lại)
                    if (p.IsAutomaticMode && p.LocationMode == LashingLocationMode.Center)
                        effCenter = AutoCADGeometryHelper.GetPolygonCentroid(boundary);

                    var grid = _gridEngine.GenerateGrid(p, boundary, structs, keepOut,
                        p1, p2, startOption, effCenter, skipCenterAdjust);
                    gridCount = grid.Count;

                    innerIds = _packing.DrawHolesWithDimensions(p, boundary, structs, keepOut,
                        grid, p1, rectMinX, rectMaxX, rectMinY, rectMaxY, tr, space, db, out stats);
                    tr.Commit();
                }
                catch
                {
                    tr.Abort();
                    throw;
                }
            }
            if (innerIds.Count == 0)
            {
                ed.WriteMessage("\nPHASE 1: Unable to generate points.");
                return;
            }
            // Chẩn đoán tránh va chạm — báo số liệu để kiểm chứng logic đang chạy
            ed.WriteMessage(
                $"\nPHASE 1 done: {innerIds.Count} hole(s). " +
                $"[structures={structureIds.Count}, grid={gridCount}, " +
                $"colliding(red)={stats.Red} -> resolve via local adjust]");
            if (structureIds.Count == 0)
                ed.WriteMessage("\n! No structures selected -> nothing to avoid.");

            // Lỗ đã commit vào database. KẾT THÚC lệnh generate tại đây: khi lệnh trả về
            // "Command:" AutoCAD repaint toàn bộ nên lỗ + dimension CHẮC CHẮN hiển thị.
            // (Regen/UpdateScreen GIỮA một lệnh modal không flush được graphics — đó là lý do
            //  trước đây lỗ hiện muộn.) Special area / local adjust / đóng block chuyển sang
            // lệnh MCG_LH_POST, tự động chạy sau khi màn hình đã vẽ xong lỗ.
            LashingWorkflowState.Set(boundaryId, structureIds, keepOut, p1, p2);
            ed.WriteMessage("\n-> Holes displayed. Continuing to special-area / adjustment...");
            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} === GENERATE DONE, chờ POST ===");
        }

        // ═════════════════════════════════════════════════════════════
        // PHẦN 2 — MCG_LH_POST (lệnh riêng: lỗ đã hiển thị trước khi hỏi)
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// PHẦN 2 (MCG_LH_POST): special area loop → local adjust → đóng block.
        /// Chạy ở lệnh riêng để lỗ đã được AutoCAD vẽ ra màn hình TRƯỚC khi hỏi user —
        /// giải quyết triệt để "lỗ hiện muộn". Đọc state từ LashingWorkflowState.
        /// </summary>
        public void RunPostProcess()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;
            var p  = LashingParamsStore.Current ?? new LashingInputParams();

            if (!LashingWorkflowState.HasPending)
            {
                ed.WriteMessage("\nNo pending lashing session. Please click START first.");
                return;
            }
            ObjectId       boundaryId   = LashingWorkflowState.BoundaryId;
            List<ObjectId> structureIds = LashingWorkflowState.StructureIds ?? new List<ObjectId>();
            List<Line3d>   keepOut      = LashingWorkflowState.KeepOut ?? new List<Line3d>();
            if (boundaryId.IsNull || boundaryId.IsErased)
            {
                LashingWorkflowState.Clear();
                ed.WriteMessage("\nBoundary is no longer valid. Post-processing cancelled.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} === START POST PROCESS ===");

            // ── PHASE 2: MỘT bước special area mỗi lần chạy POST ──
            // Sau mỗi lần điều chỉnh, KẾT THÚC lệnh (AutoCAD repaint → user thấy lỗ mới) rồi
            // tự chạy lại POST qua Idle để hỏi tiếp — luôn thấy kết quả trước khi quyết định.
            // Chọn "No" → tắt cờ, chuyển sang local adjust + đóng block.
            string ans = Keyword(ed, "\nPerform special area adjustment?", "No", "Yes", "No");
            if (ans == "Yes")
            {
                ObjectId h1 = PromptCircle(ed, "\nSelect START hole (outer circle):");
                ObjectId h2 = h1.IsNull ? ObjectId.Null
                                        : PromptCircle(ed, "\nSelect END hole (outer circle):");

                if (!h1.IsNull && !h2.IsNull && h2 != h1)
                {
                    // Đọc tâm 2 lỗ để xác định trục dải + base cho pick hướng
                    Point3d startC, endC;
                    using (var rtr = db.TransactionManager.StartTransaction())
                    {
                        startC = ((Circle)rtr.GetObject(h1, OpenMode.ForRead)).Center;
                        endC   = ((Circle)rtr.GetObject(h2, OpenMode.ForRead)).Center;
                        rtr.Commit();
                    }
                    // Dải cùng Y → mọc CỘT DỌC (trục Y); cùng X → mọc HÀNG NGANG (trục X)
                    bool isVerticalRegen = Math.Abs(startC.Y - endC.Y) < 1e-3;

                    // HƯỚNG điều chỉnh theo CHUỘT — rubber-band từ lỗ START, user click về phía muốn mọc
                    var ppo = new PromptPointOptions(
                        "\nClick a point to indicate growth direction:")
                    { UseBasePoint = true, BasePoint = startC, AllowNone = false };
                    var pr = ed.GetPoint(ppo);

                    if (pr.Status == PromptStatus.OK)
                    {
                        int genDir = isVerticalRegen
                            ? Math.Sign(pr.Value.Y - startC.Y)
                            : Math.Sign(pr.Value.X - startC.X);
                        if (genDir == 0) genDir = 1;

                        int createdCount = 0;
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            try
                            {
                                var c1       = (Circle)tr.GetObject(h1, OpenMode.ForRead);
                                var c2       = (Circle)tr.GetObject(h2, OpenMode.ForRead);
                                var boundary = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                                var structs  = OpenStructures(tr, structureIds);
                                var bt       = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                                var space    = (BlockTableRecord)tr.GetObject(
                                                   bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                                var created = _fillGap.RegenerateSpecialArea(
                                    c1, c2, genDir, boundary, p, tr, space, _gridEngine, structs, keepOut);
                                createdCount = created.Count;
                                tr.Commit();
                            }
                            catch (Exception ex)
                            {
                                tr.Abort();
                                ed.WriteMessage($"\nError: {ex.Message}");
                            }
                        }
                        ed.WriteMessage($"\n-> Special area regenerated: {createdCount} new hole(s).");
                    }
                }

                // Giữ phiên, KẾT THÚC lệnh → repaint lỗ mới → tự hỏi lại (Idle re-chain POST)
                LashingWorkflowState.ContinueSpecial = true;
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Special step done, re-chain POST.");
                return;
            }

            // ans == "No" → dừng vòng special, tiếp tục local adjust + đóng block
            LashingWorkflowState.ContinueSpecial = false;

            // ── PHASE 3: Local adjustment (Auto → tự chạy như VBA) ──
            string doAdj = p.IsAutomaticMode
                ? "Yes"
                : Keyword(ed, "\nPerform local adjustment for colliding holes?", "No", "Yes", "No");
            if (doAdj == "Yes" && (structureIds.Count > 0 || keepOut.Count > 0))
            {
                int moved;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var boundary = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                        var structs  = OpenStructures(tr, structureIds);
                        var bt       = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var space    = (BlockTableRecord)tr.GetObject(
                                           bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        moved = LocalAdjustRedHoles(p, boundary, structs, keepOut, tr, space, db);
                        tr.Commit();
                    }
                    catch
                    {
                        tr.Abort();
                        throw;
                    }
                }
                ed.WriteMessage($"\nLocal adjustment: {moved} hole(s) relocated.");
                ed.Regen(); ed.UpdateScreen();
            }

            // ── BLOCK PACKING: hỏi tên qua command line (Esc = bỏ qua) ──
            string baseName = (string.IsNullOrWhiteSpace(p.PanelName) ? "PNL" : p.PanelName.Trim()) + "_L.H";
            string packed   = null;
            while (true)
            {
                var pso = new PromptStringOptions("\nBlock name (Esc to skip packing)")
                {
                    DefaultValue    = baseName,
                    UseDefaultValue = true,
                    AllowSpaces     = false
                };
                var rs = ed.GetString(pso);
                if (rs.Status != PromptStatus.OK) { ed.WriteMessage("\nBlock packing skipped."); break; }
                string name = string.IsNullOrWhiteSpace(rs.StringResult) ? baseName : rs.StringResult.Trim();

                bool exists;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    exists = bt.Has(name);
                    tr.Commit();
                }
                if (exists)
                {
                    ed.WriteMessage($"\nBlock '{name}' already exists. Choose another name.");
                    baseName = name + "_2";
                    continue;
                }

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var boundary = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                        var bt       = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var space    = (BlockTableRecord)tr.GetObject(
                                           bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        packed = _packing.PackIntoBlock(p, boundary, tr, db, space, name);
                        tr.Commit();
                    }
                    catch (Exception ex)
                    {
                        tr.Abort();
                        ed.WriteMessage($"\nBlock packing error: {ex.Message}");
                    }
                }
                break;
            }

            ed.WriteMessage(packed != null
                ? $"\nBlock '{packed}' created successfully. Done."
                : "\nDone.");
            LashingWorkflowState.Clear();
            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} === END POST PROCESS ===");
        }

        // ═════════════════════════════════════════════════════════════
        // AUDIT FLOWS — MCG_LH_AUDIT / MCG_LH_INTERFERE
        // ═════════════════════════════════════════════════════════════

        /// <summary>Audit spacing: chọn boundary → báo cáo bước lưới lệch chuẩn ra command line</summary>
        public void RunSpacingAudit()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;
            var p  = LashingParamsStore.Current ?? new LashingInputParams();

            ObjectId boundaryId = PromptBoundary(ed, db);
            if (boundaryId.IsNull) { ed.WriteMessage("\nOperation cancelled."); return; }

            string report;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var boundary = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                var bt       = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms       = (BlockTableRecord)tr.GetObject(
                                   bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // Quét inner hole CẢ trong block (không cần phá block)
                var centers = AutoCADGeometryHelper.CollectCircleCentersWorld(
                    tr, ms, boundary, LashingInputParams.LAYER_INNER_HOLE, p.HoleDiameter / 2.0, 0.5);
                report = BuildAuditReport(centers, p);
                tr.Commit();
            }
            ed.WriteMessage($"\n[AUDIT]\n{report}");
        }

        /// <summary>Audit interference: chọn boundary → tự quét cấu kiện → chèn cloud mark tại lỗ va chạm</summary>
        public void RunInterferenceAudit()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;
            var p  = LashingParamsStore.Current ?? new LashingInputParams();

            ObjectId boundaryId = PromptBoundary(ed, db);
            if (boundaryId.IsNull) { ed.WriteMessage("\nOperation cancelled."); return; }

            Extents3d box;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var b = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                box = b.GeometricExtents;
                tr.Commit();
            }
            var structureIds = SelectStructuresByCrossing(ed, db, boundaryId, box);
            if (structureIds.Count == 0)
            {
                ed.WriteMessage("\nNo structures found inside boundary. Audit skipped.");
                return;
            }

            string message;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var boundary = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                    var structs  = OpenStructures(tr, structureIds);
                    var bt       = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var space    = (BlockTableRecord)tr.GetObject(
                                       bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    _interference.RunAudit(p, boundary, structs, tr, space, db, out message);
                    tr.Commit();
                }
                catch
                {
                    tr.Abort();
                    throw;
                }
            }
            ed.WriteMessage($"\n[AUDIT] {message}");
        }

        // ═════════════════════════════════════════════════════════════
        // LOCAL ADJUSTMENT — dịch các lỗ ĐỎ (port PerformLocalAdjustments_Phase2)
        // ═════════════════════════════════════════════════════════════

        /// <summary>Quét outer circle màu đỏ trong boundary, tìm vị trí an toàn gần nhất, dịch cả cặp inner/outer + reset màu + ghi dimension</summary>
        private int LocalAdjustRedHoles(
            LashingInputParams p, Polyline boundary, List<Entity> structures,
            IList<Line3d> keepOut, Transaction tr, BlockTableRecord space, Database db)
        {
            int    moved  = 0;
            double rOuter = p.ClearanceRadius;
            double rInner = p.HoleDiameter / 2.0;
            var circleClass = AcRuntime.RXObject.GetClass(typeof(Circle));

            // Gom outer đỏ + toàn bộ inner (map theo tâm) — proxy-safe
            var redOuters = new List<Circle>();
            var inners    = new List<Circle>();
            foreach (ObjectId id in space)
            {
                if (!id.IsValid || id.IsErased || !id.ObjectClass.IsDerivedFrom(circleClass)) continue;
                Circle c;
                try { c = tr.GetObject(id, OpenMode.ForRead) as Circle; }
                catch { continue; }
                if (c == null || c.IsErased) continue;

                if (c.Layer == LashingInputParams.LAYER_OUTER_CLEAR &&
                    Math.Abs(c.Radius - rOuter) < 0.5 && c.ColorIndex == 1 &&
                    AutoCADGeometryHelper.IsInsidePolylineOrEdge(c.Center, boundary))
                    redOuters.Add(c);
                else if (c.Layer == LashingInputParams.LAYER_INNER_HOLE &&
                         Math.Abs(c.Radius - rInner) < 0.5)
                    inners.Add(c);
            }

            foreach (var outer in redOuters)
            {
                Point3d origin = outer.Center;
                var col = _collision.GetWorstCollision(origin, rOuter, structures);
                Point3d target = _collision.FindSafePoint(
                    origin, rOuter, structures, keepOut, boundary, col.CollisionType);

                if (target.DistanceTo(origin) < 0.5) continue; // không tìm được → giữ đỏ
                if (!AutoCADGeometryHelper.IsInsidePolylineOrEdge(target, boundary)) continue;

                var move = Matrix3d.Displacement(target - origin);
                var ow = (Circle)tr.GetObject(outer.ObjectId, OpenMode.ForWrite);
                ow.TransformBy(move);
                ow.ColorIndex = 256; // ByLayer — lỗ đã an toàn

                var inner = inners.FirstOrDefault(c =>
                    Math.Abs(c.Center.X - origin.X) < 0.001 &&
                    Math.Abs(c.Center.Y - origin.Y) < 0.001);
                if (inner != null)
                    ((Circle)tr.GetObject(inner.ObjectId, OpenMode.ForWrite)).TransformBy(move);

                // Dimension vị trí cũ → mới (tương đương AddDimensionForAdjustedHole_Local)
                try
                {
                    var dimPt = new Point3d((origin.X + target.X) / 2.0,
                                            Math.Min(origin.Y, target.Y) - 150.0, 0);
                    var dim = new AlignedDimension(origin, target, dimPt, string.Empty, db.Dimstyle)
                    {
                        Layer    = LashingInputParams.LAYER_DIMENSION,
                        Dimscale = 25.0
                    };
                    space.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                }
                catch { /* Dimstyle chưa sẵn sàng → bỏ qua dim, không chặn việc dịch lỗ */ }
                moved++;
            }
            return moved;
        }

        // ═════════════════════════════════════════════════════════════
        // EDITOR PROMPT HELPERS
        // ═════════════════════════════════════════════════════════════

        /// <summary>Chọn boundary trên màn hình: LWPolyline layer 0/Mechanical-AM_0, diện tích > 20m² (khớp VBA SelectBoundaryPolyline_Helper)</summary>
        private static ObjectId PromptBoundary(Editor ed, Database db)
        {
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start,     "LWPOLYLINE"),
                new TypedValue((int)DxfCode.LayerName, "0,Mechanical-AM_0"),
            });
            var opts = new PromptSelectionOptions
            {
                MessageForAdding = "\n>>> Select Boundary (Polyline, layer 0/Mechanical-AM_0, >20m2):"
            };
            var res = ed.GetSelection(opts, filter);
            if (res.Status != PromptStatus.OK) return ObjectId.Null;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in res.Value)
                {
                    try
                    {
                        if (tr.GetObject(so.ObjectId, OpenMode.ForRead) is Polyline pl &&
                            Math.Round(pl.Area / 1_000_000.0) > 20)
                        {
                            tr.Commit();
                            return so.ObjectId;
                        }
                    }
                    catch { }
                }
                tr.Commit();
            }
            ed.WriteMessage("\nSelected object is too small (< 20m2) or on the wrong layer!");
            return ObjectId.Null;
        }

        /// <summary>
        /// Tự quét cấu kiện overlap bbox boundary, lọc bỏ INSERT + layer AM_11 (khớp VBA SelectStructuresByExample_Helper).
        /// Dùng SelectAll (quét TOÀN BỘ database theo filter) + test overlap bbox thủ công — KẾT QUẢ TẤT ĐỊNH,
        /// KHÔNG phụ thuộc view. (SelectCrossingWindow bỏ sót đối tượng NGOÀI MÀN HÌNH; VBA phải ZoomWindow
        /// trước khi crossing để né lỗi này — chính là nguyên nhân "thuật toán thông minh" chạy thất thường
        /// giữa các panel có input giống hệt: panel nào tình cờ nằm trong view thì né va chạm, panel ngoài
        /// view thì quét ra 0 cấu kiện → rải lỗ lưới phẳng.)
        /// </summary>
        private static List<ObjectId> SelectStructuresByCrossing(
            Editor ed, Database db, ObjectId boundaryId, Extents3d box)
        {
            var fv = new[]
            {
                new TypedValue((int)DxfCode.Operator,  "<AND"),
                new TypedValue((int)DxfCode.Operator,  "<NOT"),
                new TypedValue((int)DxfCode.Start,     "INSERT"),
                new TypedValue((int)DxfCode.Operator,  "NOT>"),
                new TypedValue((int)DxfCode.Operator,  "<NOT"),
                new TypedValue((int)DxfCode.LayerName, "Mechanical-AM_11"),
                new TypedValue((int)DxfCode.Operator,  "NOT>"),
                new TypedValue((int)DxfCode.Operator,  "AND>"),
            };

            // SelectAll: quét toàn bộ model space theo filter — KHÔNG phụ thuộc view (khác SelectCrossingWindow)
            var res = ed.SelectAll(new SelectionFilter(fv));
            if (res.Status != PromptStatus.OK) return new List<ObjectId>();

            var curveClass = AcRuntime.RXObject.GetClass(typeof(Curve));
            var result     = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in res.Value.GetObjectIds())
                {
                    if (id == boundaryId || !id.IsValid || id.IsErased ||
                        !id.ObjectClass.IsDerivedFrom(curveClass)) continue;

                    Entity ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                    catch { continue; }
                    if (ent == null) continue;

                    Extents3d e;
                    try { e = ent.GeometricExtents; }
                    catch { continue; } // entity không có extents hợp lệ → bỏ

                    // Overlap bbox 2D với boundary (crossing = có giao vùng)
                    if (e.MaxPoint.X >= box.MinPoint.X && e.MinPoint.X <= box.MaxPoint.X &&
                        e.MaxPoint.Y >= box.MinPoint.Y && e.MinPoint.Y <= box.MaxPoint.Y)
                        result.Add(id);
                }
                tr.Commit();
            }
            return result;
        }

        /// <summary>Chọn cấu kiện panel liền kề trên màn hình → dựng keep-out zones ảo trong RAM</summary>
        private List<Line3d> PromptAdjacentZones(Editor ed, Database db, Extents3d box, bool vertical)
        {
            ed.WriteMessage(vertical
                ? "\nSelect VERTICAL structures (LWPolyline) of adjacent panel..."
                : "\nSelect HORIZONTAL structures (LWPolyline) of adjacent panel...");
            var res = ed.GetSelection(
                new PromptSelectionOptions { MessageForAdding = "\nSelect adjacent panel structures:" },
                new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
            if (res.Status != PromptStatus.OK) return new List<Line3d>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ents = new List<Entity>();
                foreach (var id in res.Value.GetObjectIds())
                {
                    try { if (tr.GetObject(id, OpenMode.ForRead) is Entity e) ents.Add(e); }
                    catch { }
                }
                var zones = vertical
                    ? _collision.CreateVirtualKeepOutZones(ents, box.MinPoint.Y, box.MaxPoint.Y, true)
                    : _collision.CreateVirtualKeepOutZones(ents, box.MinPoint.X, box.MaxPoint.X, false);
                tr.Commit();
                return zones;
            }
        }

        /// <summary>Prompt keyword với default; trả về null nếu user Esc</summary>
        private static string Keyword(Editor ed, string message, string def, params string[] keywords)
        {
            var opts = new PromptKeywordOptions(message) { AllowNone = true };
            foreach (var k in keywords) opts.Keywords.Add(k);
            opts.Keywords.Default = def;
            var r = ed.GetKeywords(opts);
            if (r.Status == PromptStatus.OK)   return r.StringResult;
            if (r.Status == PromptStatus.None) return def;
            return null;
        }

        /// <summary>Pick 1 Circle trên màn hình; trả ObjectId.Null nếu hủy</summary>
        private static ObjectId PromptCircle(Editor ed, string message)
        {
            var peo = new PromptEntityOptions(message);
            peo.SetRejectMessage("\nObject must be a Circle.");
            peo.AddAllowedClass(typeof(Circle), false);
            var r = ed.GetEntity(peo);
            return r.Status == PromptStatus.OK ? r.ObjectId : ObjectId.Null;
        }

        /// <summary>Mở danh sách entity từ ObjectId (đã pre-filter Curve lúc chọn)</summary>
        private static List<Entity> OpenStructures(Transaction tr, List<ObjectId> ids)
        {
            var list = new List<Entity>();
            foreach (var id in ids)
            {
                if (!id.IsValid || id.IsErased) continue;
                try { if (tr.GetObject(id, OpenMode.ForRead) is Entity e) list.Add(e); }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// Suy P1/P2 theo nguyên tắc CẠNH DÀI 1500mm của VBA (GetSmartRectangularPointsFromPolyline):
        /// mở boundary, lấy mép từ các cạnh thẳng dài (fallback bbox), gán góc theo LocationMode
        /// (StarBoard: P1 Trên-Trái; PortSide/Center: P1 Dưới-Trái).
        /// </summary>
        private static (Point3d, Point3d) GetSmartP1P2(
            Database db, ObjectId boundaryId, Extents3d box, LashingLocationMode mode)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var pl = (Polyline)tr.GetObject(boundaryId, OpenMode.ForRead);
                AutoCADGeometryHelper.GetSmartRectEdges(pl, box.MinPoint, box.MaxPoint,
                    out double minX, out double maxX, out double minY, out double maxY);
                tr.Commit();

                return mode == LashingLocationMode.StarBoard
                    ? (new Point3d(minX, maxY, 0), new Point3d(maxX, minY, 0))
                    : (new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
            }
        }

        /// <summary>Báo cáo spacing lệch chuẩn theo nhóm hàng/cột (tâm lỗ world, đã gồm lỗ trong block)</summary>
        private static string BuildAuditReport(List<Point3d> holes, LashingInputParams p)
        {
            if (holes.Count == 0)
                return $"AUDIT: No holes on '{LashingInputParams.LAYER_INNER_HOLE}' inside boundary.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"AUDIT RESULT: {holes.Count} hole(s) found on '{LashingInputParams.LAYER_INNER_HOLE}'.");
            if (holes.Count < 2)
            {
                sb.Append("  -> Not enough holes to check spacing.");
                return sb.ToString();
            }

            const double GROUP_TOL = 5.0;
            int irregular = 0;

            var rows = holes.GroupBy(c => Math.Round(c.Y / GROUP_TOL) * GROUP_TOL);
            foreach (var row in rows)
            {
                var sorted = row.OrderBy(c => c.X).ToList();
                for (int i = 1; i < sorted.Count; i++)
                    if (Math.Abs(Math.Abs(sorted[i].X - sorted[i - 1].X) - p.SpacingX) > GROUP_TOL)
                        irregular++;
            }
            var cols = holes.GroupBy(c => Math.Round(c.X / GROUP_TOL) * GROUP_TOL);
            foreach (var col in cols)
            {
                var sorted = col.OrderBy(c => c.Y).ToList();
                for (int i = 1; i < sorted.Count; i++)
                    if (Math.Abs(Math.Abs(sorted[i].Y - sorted[i - 1].Y) - p.SpacingY) > GROUP_TOL)
                        irregular++;
            }

            sb.Append(irregular > 0
                ? $"  -> {irregular} adjusted spacing(s) detected (non-standard)."
                : "  -> All spacings match standard. OK.");
            return sb.ToString();
        }
    }
}
