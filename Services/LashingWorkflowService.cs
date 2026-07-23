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

        /// <summary>Chạy trọn flow tạo lỗ lashing như VBA: boundary → structures → adjacent → P1/P2 → Phase 1 → special area loop → local adjust → block packing</summary>
        public void RunCreateFlow()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;
            var p  = LashingParamsStore.Current ?? new LashingInputParams();

            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} === START CREATE FLOW ===");
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
                (p1, p2) = DeriveP1P2(box, p.LocationMode);
                ed.WriteMessage($"\nSmart boundary ({p.LocationMode}): " +
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
                                ed.WriteMessage("\nĐiểm nằm ngoài boundary. Chọn lại.");
                            else if (_collision.HasAnyCollision(cand, p.ClearanceRadius, structs, keepOut))
                                ed.WriteMessage("\nĐiểm va chạm với cấu kiện. Chọn lại.");
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

                    innerIds = _packing.DrawHolesWithDimensions(p, boundary, structs, keepOut,
                        grid, p1, rectMinX, rectMaxX, rectMinY, rectMaxY, tr, space, db);
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
            ed.WriteMessage($"\nPHASE 1 done: {innerIds.Count} hole(s) created.");

            // ── PHASE 2: Special area adjustment loop (Y/N như VBA) ──
            while (true)
            {
                string ans = Keyword(ed, "\nPerform special area adjustment?", "No", "Yes", "No");
                if (ans != "Yes") break;

                ObjectId h1 = PromptCircle(ed, "\nSelect START hole (outer circle):");
                if (h1.IsNull) continue;
                ObjectId h2 = PromptCircle(ed, "\nSelect END hole (outer circle):");
                if (h2.IsNull || h2 == h1) continue;

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

                        bool   isVertical = Math.Abs(c1.Center.Y - c2.Center.Y) >
                                            Math.Abs(c1.Center.X - c2.Center.X);
                        double spacing    = isVertical ? p.SpacingY : p.SpacingX;
                        double offset     = isVertical ? p.OffsetY  : p.OffsetX;

                        var created = _fillGap.RegenerateSpecialArea(
                            c1, c2, boundary, spacing, offset, isVertical, p, tr, space, structs);
                        tr.Commit();
                        ed.WriteMessage($"\n-> {created.Count} intermediate hole(s) added.");
                    }
                    catch (Exception ex)
                    {
                        tr.Abort();
                        ed.WriteMessage($"\nLỗi: {ex.Message}");
                    }
                }
            }

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
                    ed.WriteMessage($"\nBlock '{name}' đã tồn tại. Chọn tên khác.");
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
                        ed.WriteMessage($"\nLỗi đóng block: {ex.Message}");
                    }
                }
                break;
            }

            ed.WriteMessage(packed != null
                ? $"\nBlock '{packed}' created successfully. Done."
                : "\nDone.");
            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} === END CREATE FLOW ===");
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

                var circleClass = AcRuntime.RXObject.GetClass(typeof(Circle));
                var holes = new List<Circle>();
                foreach (ObjectId id in ms)
                {
                    if (!id.IsValid || id.IsErased || !id.ObjectClass.IsDerivedFrom(circleClass)) continue;
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is Circle c
                            && c.Layer == LashingInputParams.LAYER_INNER_HOLE
                            && AutoCADGeometryHelper.IsInsidePolylineOrEdge(c.Center, boundary))
                            holes.Add(c);
                    }
                    catch { }
                }
                report = BuildAuditReport(holes, p);
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
            ed.WriteMessage("\nĐối tượng đã chọn quá nhỏ (< 20m²) hoặc sai layer!");
            return ObjectId.Null;
        }

        /// <summary>Tự quét cấu kiện bằng crossing window trên bbox, lọc bỏ INSERT + layer AM_11 (khớp VBA SelectStructuresByExample_Helper — .NET không cần ZoomWindow)</summary>
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
            var res = ed.SelectCrossingWindow(box.MinPoint, box.MaxPoint, new SelectionFilter(fv));
            if (res.Status != PromptStatus.OK) return new List<ObjectId>();

            var curveClass = AcRuntime.RXObject.GetClass(typeof(Curve));
            return res.Value.GetObjectIds()
                .Where(id => id != boundaryId && id.IsValid && !id.IsErased &&
                             id.ObjectClass.IsDerivedFrom(curveClass))
                .ToList();
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
            peo.SetRejectMessage("\nĐối tượng phải là Circle.");
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

        /// <summary>Suy P1/P2 từ bbox theo LocationMode (StarBoard: P1 Trên-Trái; còn lại: P1 Dưới-Trái)</summary>
        private static (Point3d, Point3d) DeriveP1P2(Extents3d box, LashingLocationMode mode)
        {
            return mode == LashingLocationMode.StarBoard
                ? (new Point3d(box.MinPoint.X, box.MaxPoint.Y, 0),
                   new Point3d(box.MaxPoint.X, box.MinPoint.Y, 0))
                : (new Point3d(box.MinPoint.X, box.MinPoint.Y, 0),
                   new Point3d(box.MaxPoint.X, box.MaxPoint.Y, 0));
        }

        /// <summary>Báo cáo spacing lệch chuẩn theo nhóm hàng/cột (chuyển từ palette sang)</summary>
        private static string BuildAuditReport(List<Circle> holes, LashingInputParams p)
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

            var rows = holes.GroupBy(c => Math.Round(c.Center.Y / GROUP_TOL) * GROUP_TOL);
            foreach (var row in rows)
            {
                var sorted = row.OrderBy(c => c.Center.X).ToList();
                for (int i = 1; i < sorted.Count; i++)
                    if (Math.Abs(Math.Abs(sorted[i].Center.X - sorted[i - 1].Center.X) - p.SpacingX) > GROUP_TOL)
                        irregular++;
            }
            var cols = holes.GroupBy(c => Math.Round(c.Center.X / GROUP_TOL) * GROUP_TOL);
            foreach (var col in cols)
            {
                var sorted = col.OrderBy(c => c.Center.Y).ToList();
                for (int i = 1; i < sorted.Count; i++)
                    if (Math.Abs(Math.Abs(sorted[i].Center.Y - sorted[i - 1].Center.Y) - p.SpacingY) > GROUP_TOL)
                        irregular++;
            }

            sb.Append(irregular > 0
                ? $"  -> {irregular} adjusted spacing(s) detected (non-standard)."
                : "  -> All spacings match standard. OK.");
            return sb.ToString();
        }
    }
}
