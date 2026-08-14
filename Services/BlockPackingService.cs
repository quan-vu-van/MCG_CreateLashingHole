using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRuntime = Autodesk.AutoCAD.Runtime;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Utilities;
using D = MCG_CreateLashingHole.Utilities.DiagLogger;

namespace MCG_CreateLashingHole.Services
{
    /// <summary>
    /// Rải lưới lỗ lashing trên tấm biên:
    ///   • Vẽ DUAL CIRCLE (innerCircle = lỗ thực / outerCircle = vùng clearance)
    ///   • Generate CHỈ tô ĐỎ lỗ va chạm (khớp VBA CheckAndHighlightConflicts) — KHÔNG dời lỗ;
    ///     việc dời 8-hướng là của bước LOCAL ADJUST sau đó
    ///   • Ghi kích thước CHỈ cho các lỗ bị điều chỉnh (adjusted holes)
    ///   • Effective center dùng polygon centroid (không phải midpoint bounding box)
    /// </summary>
    public class BlockPackingService
    {
        private const string LOG_PREFIX = "[BlockPacking]";

        private readonly CollisionEngineService _collision;
        private readonly GridEngineService      _gridEngine;

        private const double DIMSCALE  = 25.0;
        private const double TOLERANCE = 1.0;

        /// <summary>Số liệu chẩn đoán 1 lần vẽ — để flow báo ra command line</summary>
        public struct DrawStats
        {
            public int Total;      // tổng lỗ vẽ
            public int Collided;   // số điểm lưới va chạm ban đầu
            public int Relocated;  // số lỗ đã dời được ra vị trí an toàn
            public int Red;        // số lỗ không né được → tô đỏ
        }

        // Track vị trí đã điều chỉnh để tạo dimension (Gap #9)
        private readonly struct AdjustedHole
        {
            public readonly Point3d Planned;
            public readonly Point3d Actual;
            public AdjustedHole(Point3d planned, Point3d actual) { Planned = planned; Actual = actual; }
        }

        public BlockPackingService(CollisionEngineService collision)
        {
            _collision  = collision;
            _gridEngine = new GridEngineService(collision);
        }

        // ─────────────────────────────────────────────────────────────
        // API chính
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tạo lưới lỗ lashing. Trả về danh sách ObjectId của innerCircle (số lượng = số lỗ).
        /// </summary>
        public List<ObjectId> GenerateHoles(
            LashingInputParams p,
            Polyline           boundary,
            List<Entity>       structures,
            Transaction        tr,
            BlockTableRecord   space,
            Database           db)
        {
            D.Log("GenerateHoles START");
            double    radius = p.HoleDiameter / 2.0;
            Extents3d box    = boundary.GeometricExtents;
            D.Log($"  radius={radius}, box=({box.MinPoint.X:F0},{box.MinPoint.Y:F0})-({box.MaxPoint.X:F0},{box.MaxPoint.Y:F0})");

            D.Log("  EnsureLayerExists INNER_HOLE...");
            EnsureLayerExists(LashingInputParams.LAYER_INNER_HOLE,  db, tr);
            D.Log("  EnsureLayerExists OUTER_CLEAR...");
            EnsureLayerExists(LashingInputParams.LAYER_OUTER_CLEAR, db, tr);
            D.Log("  EnsureLayerExists DIMENSION...");
            EnsureLayerExists(LashingInputParams.LAYER_DIMENSION,   db, tr);
            D.Log("  All layers ensured");

            // Vùng cấm từ cấu kiện liền kề (nếu bật IsCheckAdjacent)
            var keepOutZones = new List<Line3d>();
            if (p.IsCheckAdjacent)
            {
                keepOutZones.AddRange(_collision.CreateVirtualKeepOutZones(
                    structures, box.MinPoint.Y, box.MaxPoint.Y, isVertical: true));
                keepOutZones.AddRange(_collision.CreateVirtualKeepOutZones(
                    structures, box.MinPoint.X, box.MaxPoint.X, isVertical: false));
                System.Diagnostics.Debug.WriteLine(
                    $"{LOG_PREFIX} Keep-out zones: {keepOutZones.Count} đường thẳng ảo.");
            }

            // PHASE 1 — Grid engine port trung thành từ VBA (GenerateCentralPoints):
            // seeded line-growth + retreat-and-gap. Điểm trả về đã né va chạm + trong boundary.
            var gridPoints = _gridEngine.GenerateGrid(p, boundary, structures, keepOutZones);

            // P1 cho dimension theo LocationMode (StarBoard = Trên-Trái, còn lại = Dưới-Trái)
            Point3d p1Auto = p.LocationMode == LashingLocationMode.StarBoard
                ? new Point3d(box.MinPoint.X, box.MaxPoint.Y, 0)
                : new Point3d(box.MinPoint.X, box.MinPoint.Y, 0);

            return DrawHolesWithDimensions(p, boundary, structures, keepOutZones, gridPoints, p1Auto,
                box.MinPoint.X, box.MaxPoint.X, box.MinPoint.Y, box.MaxPoint.Y, tr, space, db, out _);
        }

        /// <summary>
        /// Vẽ dual-circle + tô đỏ lỗ không né được + dimension (adjusted + continuous)
        /// cho danh sách điểm lưới ĐÃ TÍNH SẴN — dùng bởi GenerateHoles và flow MCG_LH_RUN.
        /// </summary>
        public List<ObjectId> DrawHolesWithDimensions(
            LashingInputParams p,
            Polyline           boundary,
            List<Entity>       structures,
            IList<Line3d>      keepOutZones,
            List<Point3d>      gridPoints,
            Point3d            dimOrigin,
            double rectMinX, double rectMaxX, double rectMinY, double rectMaxY,
            Transaction        tr,
            BlockTableRecord   space,
            Database           db,
            out DrawStats      stats)
        {
            stats = new DrawStats();
            double radius = p.HoleDiameter / 2.0;
            EnsureLayerExists(LashingInputParams.LAYER_INNER_HOLE,  db, tr);
            EnsureLayerExists(LashingInputParams.LAYER_OUTER_CLEAR, db, tr);
            EnsureLayerExists(LashingInputParams.LAYER_DIMENSION,   db, tr);
            if (keepOutZones == null) keepOutZones = new List<Line3d>();

            var innerIds    = new List<ObjectId>();
            var adjusted    = new List<AdjustedHole>();
            var drawnPoints = new List<Point3d>();

            foreach (Point3d candidate in gridPoints)
            {
                if (!AutoCADGeometryHelper.IsInsidePolylineOrEdge(candidate, boundary))
                    continue;

                // Kiểm tra va chạm tại vị trí lưới
                var  col      = _collision.GetWorstCollision(candidate, p.ClearanceRadius, structures);
                bool collides = col.CollisionOccurred ||
                                HasKeepOutCollision(candidate, p.ClearanceRadius, keepOutZones);

                // VBA: bước GENERATE chỉ VẼ lỗ + TÔ ĐỎ lỗ va chạm (CheckAndHighlightConflicts — chỉ đổi
                // màu, KHÔNG dời lỗ). Việc DỜI lỗ va chạm (8-hướng) là nhiệm vụ của bước LOCAL ADJUST
                // phía sau (PerformLocalAdjustments_Phase2). KHÔNG relocate ở generate — nếu không,
                // local adjust thành thừa và prompt "Perform local adjustment?" trở nên vô nghĩa.
                Point3d finalPt = candidate;
                bool    markRed = collides;
                if (collides) { stats.Collided++; stats.Red++; }

                // Vẽ DUAL CIRCLE — inner ByLayer; outer tô đỏ nếu va chạm không né được
                ObjectId innerId = DrawCircle(finalPt, radius, LashingInputParams.LAYER_INNER_HOLE,
                    tr, space, db, markRed: false);
                DrawCircle(finalPt, p.ClearanceRadius, LashingInputParams.LAYER_OUTER_CLEAR,
                    tr, space, db, markRed: markRed);
                innerIds.Add(innerId);
                drawnPoints.Add(finalPt);

                // Dimension chỉ cho lỗ đã dịch (không phải lỗ đỏ đứng yên)
                if (!markRed && finalPt.DistanceTo(candidate) > TOLERANCE)
                    adjusted.Add(new AdjustedHole(candidate, finalPt));
            }
            stats.Total = innerIds.Count;

            // FULL DIMENSIONING — chuỗi dim liên tục theo hàng/cột dài nhất, mốc = dimOrigin (P1)
            AddContinuousDimensions(drawnPoints, dimOrigin,
                rectMinX, rectMaxX, rectMinY, rectMaxY,
                LashingInputParams.LAYER_DIMENSION, tr, space, db);

            // Gap #9: Dimension CHỈ cho các lỗ bị điều chỉnh
            // db.Dimscale không set ở đây — gây reactor callback crash trong transaction.
            // dim.Dimscale được set trực tiếp trên entity trong AddAdjustedDimensions.
            if (adjusted.Count > 0)
            {
                AddAdjustedDimensions(adjusted, LashingInputParams.LAYER_DIMENSION, tr, space, db);
                System.Diagnostics.Debug.WriteLine(
                    $"{LOG_PREFIX} Thêm {adjusted.Count} kích thước lỗ điều chỉnh.");
            }

            System.Diagnostics.Debug.WriteLine(
                $"{LOG_PREFIX} GenerateHoles THÀNH CÔNG: {innerIds.Count} lỗ, {adjusted.Count} điều chỉnh.");
            return innerIds;
        }

        // ─────────────────────────────────────────────────────────────
        // BLOCK PACKING — port đoạn "BLOCK PACKING" trong VBA CFS_CreateLashingHole:
        // gom toàn bộ inner/outer circle + dimension nằm trong boundary → tạo block
        // "<PanelName>_L.H" → insert 1 block reference → xóa entity gốc.
        // Quét theo layer + bán kính để tự bắt cả lỗ do Phase 2 tạo thêm và tránh
        // gom nhầm circle lạ trên cùng layer.
        // Trả về tên block cuối cùng, hoặc null nếu không có entity nào để gom.
        // ─────────────────────────────────────────────────────────────
        public string PackIntoBlock(
            LashingInputParams p,
            Polyline           boundary,
            Transaction        tr,
            Database           db,
            BlockTableRecord   modelSpace,
            string             presetName = null)
        {
            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Bắt đầu PackIntoBlock...");

            // 1. Thu thập entity thuộc lưới lashing bên trong boundary
            var ids = CollectLashingEntities(p, boundary, tr, modelSpace);
            if (ids.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Không tìm thấy entity nào để gom block.");
                return null;
            }

            // 2. Điểm chèn = góc dưới-trái boundary.
            //    VBA dùng p1 làm cả base point của block lẫn điểm insert → net dịch = 0.
            //    Ta set Origin = basePt và insert tại basePt → geometry giữ nguyên tọa độ world.
            Extents3d box    = boundary.GeometricExtents;
            Point3d   basePt = box.MinPoint;

            // 3. Tên block duy nhất "<PanelName>_L.H"
            var    bt        = tr.GetObject(db.BlockTableId, OpenMode.ForWrite) as BlockTable;
            string baseName  = presetName ??
                ((string.IsNullOrWhiteSpace(p.PanelName) ? "PNL" : p.PanelName.Trim()) + "_L.H");
            string finalName = ResolveUniqueBlockName(bt, baseName);

            // 4. Tạo BlockTableRecord
            var      btr   = new BlockTableRecord { Name = finalName, Origin = basePt };
            ObjectId btrId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            // 5. Deep clone entity vào block record (tương đương ActiveX CopyObjects)
            var idColl = new ObjectIdCollection();
            foreach (var id in ids) idColl.Add(id);
            var mapping = new IdMapping();
            db.DeepCloneObjects(idColl, btrId, mapping, false);

            // 6. Insert 1 block reference tại basePt
            var bref = new BlockReference(basePt, btrId);
            modelSpace.AppendEntity(bref);
            tr.AddNewlyCreatedDBObject(bref, true);

            // 7. Xóa entity gốc (đã được clone vào block)
            foreach (var id in ids)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForWrite) is Entity e && !e.IsErased) e.Erase();
                }
                catch { /* bỏ qua entity không xóa được */ }
            }

            System.Diagnostics.Debug.WriteLine(
                $"{LOG_PREFIX} PackIntoBlock THÀNH CÔNG: block '{finalName}' gom {ids.Count} entity.");
            return finalName;
        }

        /// <summary>Thu thập inner/outer circle (khớp bán kính) + dimension nằm trong boundary.</summary>
        private static List<ObjectId> CollectLashingEntities(
            LashingInputParams p, Polyline boundary, Transaction tr, BlockTableRecord modelSpace)
        {
            var       result   = new List<ObjectId>();
            double    rInner   = p.HoleDiameter / 2.0;
            double    rOuter   = p.ClearanceRadius;
            Extents3d bbox      = boundary.GeometricExtents;
            const double R_TOL = 0.5; // dung sai bán kính (mm)

            // An toàn với proxy/custom entity: check RXClass TRƯỚC khi GetObject.
            // Native code crash (bypass managed try/catch) nếu open proxy entity — xem SESSION_LOG.
            var circleClass = AcRuntime.RXObject.GetClass(typeof(Circle));
            var dimClass    = AcRuntime.RXObject.GetClass(typeof(Dimension));

            foreach (ObjectId id in modelSpace)
            {
                if (!id.IsValid || id.IsErased) continue;
                bool isCircleType = id.ObjectClass.IsDerivedFrom(circleClass);
                bool isDimType    = id.ObjectClass.IsDerivedFrom(dimClass);
                if (!isCircleType && !isDimType) continue;

                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                catch { continue; }
                if (ent == null || ent.IsErased) continue;

                if (ent is Circle c)
                {
                    bool isInner = c.Layer == LashingInputParams.LAYER_INNER_HOLE  && Math.Abs(c.Radius - rInner) < R_TOL;
                    bool isOuter = c.Layer == LashingInputParams.LAYER_OUTER_CLEAR && Math.Abs(c.Radius - rOuter) < R_TOL;
                    if ((isInner || isOuter) && AutoCADGeometryHelper.IsInsidePolylineOrEdge(c.Center, boundary))
                        result.Add(id);
                }
                else if (ent is Dimension dim && dim.Layer == LashingInputParams.LAYER_DIMENSION)
                {
                    // Dim đặt LỆCH ra ngoài lỗ (offset 150mm) → tâm bbox của dim có thể nằm NGOÀI
                    // biên → test cũ loại nhầm, dim bị bỏ sót khỏi block. Nay test theo ĐIỂM ĐO
                    // (XLine1/XLine2 = tâm lỗ hoặc mép panel — luôn nằm trong/trên biên).
                    if (dim is AlignedDimension ad)
                    {
                        if (AutoCADGeometryHelper.IsInsidePolylineOrEdge(ad.XLine1Point, boundary) ||
                            AutoCADGeometryHelper.IsInsidePolylineOrEdge(ad.XLine2Point, boundary))
                            result.Add(id);
                    }
                    else
                    {
                        // Dim loại khác: fallback — extents giao với bbox biên (không chỉ tâm)
                        try
                        {
                            Extents3d de = dim.GeometricExtents;
                            if (de.MaxPoint.X >= bbox.MinPoint.X && de.MinPoint.X <= bbox.MaxPoint.X &&
                                de.MaxPoint.Y >= bbox.MinPoint.Y && de.MinPoint.Y <= bbox.MaxPoint.Y)
                                result.Add(id);
                        }
                        catch { }
                    }
                }
            }
            return result;
        }

        /// <summary>Trả về tên block chưa tồn tại: baseName, baseName_2, baseName_3, …</summary>
        private static string ResolveUniqueBlockName(BlockTable bt, string baseName)
        {
            if (!bt.Has(baseName)) return baseName;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + "_" + i;
                if (!bt.Has(candidate)) return candidate;
            }
            return baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #9: Dimension chỉ cho lỗ bị điều chỉnh
        // Tương đương VBA: AddDimensionForAdjustedHole_Local
        // ─────────────────────────────────────────────────────────────

        private static void AddAdjustedDimensions(
            IList<AdjustedHole> adjusted,
            string              dimLayer,
            Transaction         tr,
            BlockTableRecord    space,
            Database            db)
        {
            EnsureLayerExists(dimLayer, db, tr);

            foreach (var h in adjusted)
            {
                double dist = h.Planned.DistanceTo(h.Actual);
                if (dist < TOLERANCE) continue;

                // AlignedDimension từ vị trí lưới → vị trí thực tế
                Point3d dimPt = new Point3d(
                    (h.Planned.X + h.Actual.X) / 2.0,
                    Math.Min(h.Planned.Y, h.Actual.Y) - 150 * (DIMSCALE / 25.0), 0);

                try
                {
                    var dim = new AlignedDimension(h.Planned, h.Actual, dimPt,
                        string.Empty, db.Dimstyle);
                    dim.Layer    = dimLayer;
                    dim.Dimscale = DIMSCALE;
                    space.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                }
                catch { /* Bỏ qua nếu Dimstyle chưa sẵn sàng */ }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // FULL DIMENSIONING — port VBA AddContinuousDimensions
        // Chuỗi dim liên tục dọc hàng dài nhất (ngang) và cột dài nhất (đứng),
        // gồm mốc P1 + biên rect, bỏ đoạn trùng và đoạn 0.
        // ─────────────────────────────────────────────────────────────
        private static List<ObjectId> AddContinuousDimensions(
            List<Point3d> points, Point3d originPt,
            double rectMinX, double rectMaxX, double rectMinY, double rectMaxY,
            string dimLayer, Transaction tr, BlockTableRecord space, Database db)
        {
            var created = new List<ObjectId>();
            if (points == null || points.Count < 1) return created;

            EnsureLayerExists(dimLayer, db, tr);
            const double PLACEMENT_OFFSET = 150.0;

            // 1) Phân loại điểm theo hàng (Y) và cột (X)
            var rows = new Dictionary<string, List<Point3d>>();
            var cols = new Dictionary<string, List<Point3d>>();
            foreach (var pt in points)
            {
                string rk = CK(pt.Y), ck = CK(pt.X);
                if (!rows.ContainsKey(rk)) rows[rk] = new List<Point3d>();
                rows[rk].Add(pt);
                if (!cols.ContainsKey(ck)) cols[ck] = new List<Point3d>();
                cols[ck].Add(pt);
            }

            // 2) Tìm hàng/cột dài nhất
            string longestRowKey = null, longestColKey = null;
            int    maxRow = 0, maxCol = 0;
            foreach (var kv in rows) if (kv.Value.Count > maxRow) { maxRow = kv.Value.Count; longestRowKey = kv.Key; }
            foreach (var kv in cols) if (kv.Value.Count > maxCol) { maxCol = kv.Value.Count; longestColKey = kv.Key; }

            // --- HORIZONTAL DIMENSIONS (dọc hàng dài nhất) ---
            if (maxRow >= 1 && longestRowKey != null)
            {
                double longestRowY = rows[longestRowKey][0].Y;
                double dimLineY    = longestRowY + PLACEMENT_OFFSET;

                var xs = GetUniqueSorted(rows[longestRowKey].Select(pt => pt.X));
                if (xs.Count >= 1)
                {
                    // Hố gần P1 nhất
                    double closest = xs[0];
                    double best    = -1;
                    foreach (double x in xs)
                    {
                        double d = (x - originPt.X) * (x - originPt.X);
                        if (best < 0 || d < best) { best = d; closest = x; }
                    }

                    // Dim đầu: P1 → hố gần nhất
                    if (Math.Abs(originPt.X - closest) > 1e-6)
                        created.Add(CreateAlignedDim(
                            new Point3d(originPt.X, longestRowY, 0),
                            new Point3d(closest,    longestRowY, 0),
                            new Point3d((originPt.X + closest) / 2.0, dimLineY, 0),
                            dimLayer, tr, space, db));

                    // Chuỗi dim liên tục: [P1.X, rectMinX, rectMaxX] + xs
                    var chainSrc = new List<double> { originPt.X, rectMinX, rectMaxX };
                    chainSrc.AddRange(xs);
                    var chain = GetUniqueSorted(chainSrc);
                    for (int i = 0; i < chain.Count - 1; i++)
                    {
                        double a = chain[i], b = chain[i + 1];
                        bool dup = (Math.Abs(a - originPt.X) < 1e-6 && Math.Abs(b - closest) < 1e-6) ||
                                   (Math.Abs(b - originPt.X) < 1e-6 && Math.Abs(a - closest) < 1e-6);
                        if (!dup && Math.Abs(a - b) > 1e-6)
                            created.Add(CreateAlignedDim(
                                new Point3d(a, longestRowY, 0),
                                new Point3d(b, longestRowY, 0),
                                new Point3d((a + b) / 2.0, dimLineY, 0),
                                dimLayer, tr, space, db));
                    }
                }
            }

            // --- VERTICAL DIMENSIONS (dọc cột dài nhất) ---
            if (maxCol >= 1 && longestColKey != null)
            {
                double longestColX = cols[longestColKey][0].X;
                double dimLineX    = longestColX + PLACEMENT_OFFSET;

                var ys = GetUniqueSorted(cols[longestColKey].Select(pt => pt.Y));
                if (ys.Count >= 1)
                {
                    double closest = ys[0];
                    double best    = -1;
                    foreach (double y in ys)
                    {
                        double d = (y - originPt.Y) * (y - originPt.Y);
                        if (best < 0 || d < best) { best = d; closest = y; }
                    }

                    if (Math.Abs(originPt.Y - closest) > 1e-6)
                        created.Add(CreateAlignedDim(
                            new Point3d(longestColX, originPt.Y, 0),
                            new Point3d(longestColX, closest,    0),
                            new Point3d(dimLineX, (originPt.Y + closest) / 2.0, 0),
                            dimLayer, tr, space, db));

                    var chainSrc = new List<double> { originPt.Y, rectMinY, rectMaxY };
                    chainSrc.AddRange(ys);
                    var chain = GetUniqueSorted(chainSrc);
                    for (int i = 0; i < chain.Count - 1; i++)
                    {
                        double a = chain[i], b = chain[i + 1];
                        bool dup = (Math.Abs(a - originPt.Y) < 1e-6 && Math.Abs(b - closest) < 1e-6) ||
                                   (Math.Abs(b - originPt.Y) < 1e-6 && Math.Abs(a - closest) < 1e-6);
                        if (!dup && Math.Abs(a - b) > 1e-6)
                            created.Add(CreateAlignedDim(
                                new Point3d(longestColX, a, 0),
                                new Point3d(longestColX, b, 0),
                                new Point3d(dimLineX, (a + b) / 2.0, 0),
                                dimLayer, tr, space, db));
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} AddContinuousDimensions: {created.Count} dim.");
            return created;
        }

        private static ObjectId CreateAlignedDim(
            Point3d p1, Point3d p2, Point3d dimLinePt, string dimLayer,
            Transaction tr, BlockTableRecord space, Database db)
        {
            var dim = new AlignedDimension(p1, p2, dimLinePt, string.Empty, db.Dimstyle);
            dim.Layer    = dimLayer;
            dim.Dimscale = DIMSCALE; // set trên entity — tránh set db.Dimscale gây reactor crash
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);
            return dim.ObjectId;
        }

        /// <summary>Distinct theo key "0.000" + sort tăng dần (khớp VBA GetUniqueSortedValuesFromCollection).</summary>
        private static List<double> GetUniqueSorted(IEnumerable<double> values)
        {
            var map = new Dictionary<string, double>();
            foreach (double v in values)
            {
                string k = CK(v);
                if (!map.ContainsKey(k)) map[k] = v;
            }
            var list = map.Values.ToList();
            list.Sort();
            return list;
        }

        private static string CK(double c)
            => c.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        private static ObjectId DrawCircle(Point3d center, double radius, string layer,
            Transaction tr, BlockTableRecord space, Database db, bool markRed = false)
        {
            var circle = new Circle();
            circle.SetDatabaseDefaults(db);
            circle.Center = center;
            circle.Normal = Vector3d.ZAxis;
            circle.Radius = radius;
            circle.Layer  = layer;
            // markRed = lỗ va chạm không né được → tô đỏ (acRed = ColorIndex 1); còn lại ByLayer
            if (markRed)
                circle.ColorIndex = 1;
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);
            return circle.ObjectId;
        }

        private static bool HasKeepOutCollision(Point3d pt, double clearance, IList<Line3d> zones)
        {
            foreach (var z in zones)
                if (CollisionEngineService.DistanceToLine(pt, z) < clearance) return true;
            return false;
        }

        public static void EnsureLayerExists(string layerName, Database db, Transaction tr)
        {
            D.Log($"    EnsureLayerExists: GetObject LayerTable ForWrite for '{layerName}'...");
            var lt = tr.GetObject(db.LayerTableId, OpenMode.ForWrite) as LayerTable;
            D.Log($"    EnsureLayerExists: lt={lt != null}, Has={lt?.Has(layerName)}");
            if (lt == null || lt.Has(layerName)) { D.Log($"    EnsureLayerExists: '{layerName}' already exists or lt null, skip"); return; }
            D.Log($"    EnsureLayerExists: creating layer '{layerName}'...");
            var ltr = new LayerTableRecord { Name = layerName };
            D.Log($"    EnsureLayerExists: lt.Add...");
            lt.Add(ltr);
            D.Log($"    EnsureLayerExists: AddNewlyCreatedDBObject...");
            tr.AddNewlyCreatedDBObject(ltr, true);
            D.Log($"    EnsureLayerExists: '{layerName}' created OK");
        }
    }
}
