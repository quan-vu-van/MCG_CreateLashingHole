using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRuntime = Autodesk.AutoCAD.Runtime;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Utilities;

namespace MCG_CreateLashingHole.Services
{
    /// <summary>
    /// Phase 3 — Special Area Adjustment. Port TRUNG THÀNH VBA PerformSpecialAreaAdjustment_Phase3:
    ///   1. Chọn START + END hole (outer) + hướng phát triển P1/P2.
    ///   2. Xác định trục: 2 lỗ CÙNG Y → mọc các CỘT DỌC; cùng X → mọc các HÀNG NGANG.
    ///   3. seedHoles = tất cả outer trên đường của START, nằm giữa START↔END.
    ///   4. Với mỗi seed: XÓA lỗ cũ phía genDir → mọc lại 1 hàng lỗ mới (spacing + né va chạm)
    ///      ra tới biên (giao boundary − offset), kèm dimension liên tiếp.
    ///   5. Vẽ dual-circle cho điểm mới, tô ĐỎ nếu còn va chạm (để Local Adjustment xử lý tiếp).
    /// </summary>
    public class GridGenerationService
    {
        private const string LOG_PREFIX = "[GridGeneration]";
        private const double EPS_ALIGN  = 1e-3;   // dung sai canh chỉnh cùng hàng/cột
        private const double RANGE_PAD   = 0.1;    // nới biên [min,max] khi lọc seed (khớp VBA ±0.1)

        private readonly CollisionEngineService _collision;

        public GridGenerationService(CollisionEngineService collision)
        {
            _collision = collision;
        }

        // ─────────────────────────────────────────────────────────────
        // API chính — PerformSpecialAreaAdjustment_Phase3
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Điều chỉnh khu vực đặc biệt: từ dải lỗ START↔END, mọc lại các đường lỗ theo hướng genDir
        /// (do user chỉ định bằng CHUỘT). Trả về ObjectId các inner-circle MỚI tạo.
        /// </summary>
        public List<ObjectId> RegenerateSpecialArea(
            Circle             startHole,
            Circle             endHole,
            int                genDir,      // +1 / -1 theo trục phát triển (từ hướng chuột)
            Polyline           boundary,
            LashingInputParams p,
            Transaction        tr,
            BlockTableRecord   space,
            GridEngineService  gridEngine,
            IList<Entity>      structures,
            IList<Line3d>      keepOut)
        {
            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Bắt đầu RegenerateSpecialArea (genDir={genDir})...");
            if (genDir == 0) genDir = 1;

            var created = new List<ObjectId>();
            var db      = space.Database;
            double rInner = p.HoleDiameter / 2.0;
            double rOuter = p.ClearanceRadius;

            BlockPackingService.EnsureLayerExists(LashingInputParams.LAYER_INNER_HOLE,  db, tr);
            BlockPackingService.EnsureLayerExists(LashingInputParams.LAYER_OUTER_CLEAR, db, tr);

            Point3d sC = startHole.Center, eC = endHole.Center;
            // Cùng Y (dải nằm ngang) → mọc các CỘT DỌC (varying = Y). Ngược lại → mọc HÀNG NGANG.
            bool isVerticalRegen = Math.Abs(sC.Y - eC.Y) < EPS_ALIGN;
            bool axisIsX = !isVerticalRegen;

            // bbox tuyệt đối (B_abs) làm giới hạn phát triển
            var (bmin, bmax) = AutoCADGeometryHelper.GetSmartRectFromPolyline(boundary);

            // Gom outer + inner hiện có (proxy-safe)
            var circleClass = AcRuntime.RXObject.GetClass(typeof(Circle));
            var outers = new List<Circle>();
            var inners = new List<Circle>();
            foreach (ObjectId id in space)
            {
                if (!id.IsValid || id.IsErased || !id.ObjectClass.IsDerivedFrom(circleClass)) continue;
                Circle c;
                try { c = tr.GetObject(id, OpenMode.ForRead) as Circle; }
                catch { continue; }
                if (c == null || c.IsErased) continue;

                if (c.Layer == LashingInputParams.LAYER_OUTER_CLEAR && Math.Abs(c.Radius - rOuter) < 0.5)
                    outers.Add(c);
                else if (c.Layer == LashingInputParams.LAYER_INNER_HOLE && Math.Abs(c.Radius - rInner) < 0.5)
                    inners.Add(c);
            }

            // seedHoles = outer trên đường cố định của START, trong [min,max] trục động
            var seeds = new List<Circle>();
            if (isVerticalRegen)
            {
                double minC = Math.Min(sC.X, eC.X), maxC = Math.Max(sC.X, eC.X);
                foreach (var c in outers)
                    if (Math.Abs(c.Center.Y - sC.Y) < EPS_ALIGN &&
                        c.Center.X >= minC - RANGE_PAD && c.Center.X <= maxC + RANGE_PAD)
                        seeds.Add(c);
            }
            else
            {
                double minC = Math.Min(sC.Y, eC.Y), maxC = Math.Max(sC.Y, eC.Y);
                foreach (var c in outers)
                    if (Math.Abs(c.Center.X - sC.X) < EPS_ALIGN &&
                        c.Center.Y >= minC - RANGE_PAD && c.Center.Y <= maxC + RANGE_PAD)
                        seeds.Add(c);
            }

            if (seeds.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Không tìm thấy seed hole nào — bỏ qua.");
                return created;
            }

            double spacing = isVerticalRegen ? p.SpacingY : p.SpacingX;
            double offset  = isVerticalRegen ? p.OffsetY  : p.OffsetX;
            double bMin    = isVerticalRegen ? bmin.Y : bmin.X;
            double bMax    = isVerticalRegen ? bmax.Y : bmax.X;

            var newPoints = new Dictionary<string, Point3d>();
            var seedKeys  = new HashSet<string>();
            foreach (var s in seeds) seedKeys.Add(Key(s.Center));

            foreach (var seed in seeds)
            {
                Point3d seedPt = seed.Center;

                // genDir do user chỉ định bằng chuột — chung cho mọi seed (cùng nằm trên 1 đường dải)

                // Xóa lỗ CŨ (outer+inner) trên đường vuông góc của seed, phía genDir (trừ chính seed)
                DeleteBeyondSeed(outers, seedPt, genDir, isVerticalRegen, tr, seed);
                DeleteBeyondSeed(inners, seedPt, genDir, isVerticalRegen, tr, null);

                // endAnchor = giao boundary theo hướng genDir, lùi vào offset
                Vector3d dir = isVerticalRegen ? new Vector3d(0, genDir, 0) : new Vector3d(genDir, 0, 0);
                if (!AutoCADGeometryHelper.TryRayBoundaryIntersection(seedPt, dir, boundary, out Point3d inter))
                    continue;
                Point3d endAnchor = isVerticalRegen
                    ? new Point3d(seedPt.X, inter.Y - genDir * offset, 0)
                    : new Point3d(inter.X - genDir * offset, seedPt.Y, 0);

                // Mọc lại hàng lỗ từ seed → endAnchor
                var linePts = gridEngine.RegenerateSeedLineSpecial(
                    p, boundary, structures, keepOut,
                    seedPt, endAnchor, spacing, genDir, axisIsX, bMin, bMax);

                foreach (var pt in linePts)
                {
                    string k = Key(pt);
                    if (!newPoints.ContainsKey(k)) newPoints[k] = pt;
                }

                // Dimension giữa các điểm liên tiếp trên đường này
                AddLineDimensions(linePts, seedPt, isVerticalRegen, tr, space, db);
            }

            // Vẽ dual-circle cho điểm MỚI (trừ seed đã có sẵn), tô đỏ nếu còn va chạm
            foreach (var kv in newPoints)
            {
                if (seedKeys.Contains(kv.Key)) continue;
                Point3d pt = kv.Value;

                ObjectId innerId = DrawCircle(pt, rInner, LashingInputParams.LAYER_INNER_HOLE, tr, space, db);
                DrawCircle(pt, rOuter, LashingInputParams.LAYER_OUTER_CLEAR, tr, space, db, out ObjectId outerId);

                if (structures != null && structures.Count > 0 &&
                    _collision.GetWorstCollision(pt, rOuter, structures).CollisionOccurred)
                {
                    var oc = (Circle)tr.GetObject(outerId, OpenMode.ForWrite);
                    oc.ColorIndex = 1;
                }

                created.Add(innerId);
            }

            System.Diagnostics.Debug.WriteLine(
                $"{LOG_PREFIX} RegenerateSpecialArea THÀNH CÔNG: {seeds.Count} seed, {created.Count} lỗ mới.");
            return created;
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        /// <summary>Xóa các circle trên đường vuông góc của seed (cùng trục cố định), nằm phía genDir so với seed.</summary>
        private static void DeleteBeyondSeed(
            List<Circle> circles, Point3d seedPt, int genDir, bool isVerticalRegen,
            Transaction tr, Circle exceptSeed)
        {
            for (int i = circles.Count - 1; i >= 0; i--)
            {
                var c = circles[i];
                if (c.IsErased) { circles.RemoveAt(i); continue; }
                if (exceptSeed != null && c.ObjectId == exceptSeed.ObjectId) continue;

                Point3d cc = c.Center;
                bool onLine, beyond;
                if (isVerticalRegen)
                {
                    // cùng cột X, Y vượt seed theo genDir
                    onLine = Math.Abs(cc.X - seedPt.X) < EPS_ALIGN;
                    beyond = genDir == 1 ? cc.Y > seedPt.Y + EPS_ALIGN : cc.Y < seedPt.Y - EPS_ALIGN;
                }
                else
                {
                    // cùng hàng Y, X vượt seed theo genDir
                    onLine = Math.Abs(cc.Y - seedPt.Y) < EPS_ALIGN;
                    beyond = genDir == 1 ? cc.X > seedPt.X + EPS_ALIGN : cc.X < seedPt.X - EPS_ALIGN;
                }

                if (onLine && beyond)
                {
                    try
                    {
                        var cw = (Circle)tr.GetObject(c.ObjectId, OpenMode.ForWrite);
                        cw.Erase();
                    }
                    catch { }
                    circles.RemoveAt(i);
                }
            }
        }

        /// <summary>Ghi dimension aligned giữa các điểm liên tiếp trên 1 đường (offset 150mm ra ngoài, Dimscale 25).</summary>
        private static void AddLineDimensions(
            List<Point3d> linePts, Point3d seedPt, bool isVerticalRegen,
            Transaction tr, BlockTableRecord space, Database db)
        {
            var vals = linePts
                .Select(pt => isVerticalRegen ? pt.Y : pt.X)
                .Distinct()
                .OrderBy(v => v)
                .ToList();
            if (vals.Count < 2) return;

            for (int i = 0; i < vals.Count - 1; i++)
            {
                double a = vals[i], b = vals[i + 1];
                if (Math.Abs(a - b) < EPS_ALIGN) continue;

                Point3d pt1, pt2, dLoc;
                if (isVerticalRegen)
                {
                    pt1  = new Point3d(seedPt.X, a, 0);
                    pt2  = new Point3d(seedPt.X, b, 0);
                    dLoc = new Point3d(seedPt.X + 150.0, (a + b) / 2.0, 0);
                }
                else
                {
                    pt1  = new Point3d(a, seedPt.Y, 0);
                    pt2  = new Point3d(b, seedPt.Y, 0);
                    dLoc = new Point3d((a + b) / 2.0, seedPt.Y + 150.0, 0);
                }

                try
                {
                    var dim = new AlignedDimension(pt1, pt2, dLoc, string.Empty, db.Dimstyle)
                    {
                        Layer    = LashingInputParams.LAYER_DIMENSION,
                        Dimscale = 25.0
                    };
                    space.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                }
                catch { /* Dimstyle chưa sẵn sàng → bỏ dim, không chặn việc vẽ lỗ */ }
            }
        }

        private static string Key(Point3d pt)
            => pt.X.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
               pt.Y.ToString("0.000", CultureInfo.InvariantCulture);

        private static ObjectId DrawCircle(Point3d center, double radius, string layer,
            Transaction tr, BlockTableRecord space, Database db)
            => DrawCircle(center, radius, layer, tr, space, db, out _);

        private static ObjectId DrawCircle(Point3d center, double radius, string layer,
            Transaction tr, BlockTableRecord space, Database db, out ObjectId id)
        {
            var circle = new Circle();
            circle.SetDatabaseDefaults(db);
            circle.Center = center;
            circle.Normal = Vector3d.ZAxis;
            circle.Radius = radius;
            circle.Layer  = layer;
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);
            id = circle.ObjectId;
            return id;
        }
    }
}
