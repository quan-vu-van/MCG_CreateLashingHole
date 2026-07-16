using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Utilities;

namespace MCG_CreateLashingHole.Services
{
    /// <summary>
    /// Toàn bộ phép toán giao cắt tính trực tiếp trong RAM qua Circle ảo,
    /// không đẩy vào Database → không tạo thực thể rác trên bản vẽ.
    /// Bao gồm 8-direction safe point search từ VBA Phase 2 Local Adjustment.
    /// </summary>
    public class CollisionEngineService
    {
        private const string LOG_PREFIX = "[CollisionEngine]";

        // Tỉ lệ lọc cấu kiện liền kề (ADJACENT_STRUCTURE_FILTER_RATIO = 300)
        private const double ADJACENT_FILTER_RATIO = 300.0;

        // Tham số 8-direction scan
        private const double SEARCH_STEP_MM = 10.0;
        private const int    MAX_SEARCH_STEPS = 150;

        // 8 hướng: Cardinal trước (ưu tiên cao), Diagonal sau
        // Priority Vertical  → Right/Left đầu tiên
        // Priority Horizontal → Up/Down đầu tiên
        // Priority Complex   → xen kẽ đều
        private static readonly (double dx, double dy)[] DIRS_VERTICAL = {
            ( 1,  0), (-1,  0),           // Right, Left (primary)
            ( 0,  1), ( 0, -1),           // Up, Down
            ( 1,  1), (-1,  1), ( 1, -1), (-1, -1)  // Diagonals
        };

        private static readonly (double dx, double dy)[] DIRS_HORIZONTAL = {
            ( 0,  1), ( 0, -1),           // Up, Down (primary)
            ( 1,  0), (-1,  0),           // Right, Left
            ( 1,  1), (-1,  1), ( 1, -1), (-1, -1)  // Diagonals
        };

        private static readonly (double dx, double dy)[] DIRS_COMPLEX = {
            ( 1,  0), (-1,  0), ( 0,  1), ( 0, -1),
            ( 1,  1), (-1,  1), ( 1, -1), (-1, -1)
        };

        // ─────────────────────────────────────────────────────────────
        // Core: Phân loại va chạm tại 1 điểm
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Phân loại va chạm bằng GetClosestPointTo — KHÔNG tạo database object.
        /// Tuyệt đối không dùng new Circle() + IntersectWith() vì transient object
        /// thiếu database context → AutoCAD native code đọc null pointer → FATAL ERROR.
        ///
        /// Phân loại với GetClosestPointTo (NGƯỢC logic IntersectWith):
        ///   DeltaX >> DeltaY → cấu kiện nằm theo chiều đứng (web plate dọc X)   → Vertical
        ///   DeltaY >> DeltaX → cấu kiện nằm theo chiều ngang (stiffener dọc Y)  → Horizontal
        /// </summary>
        public LashingCollisionResult ClassifyCollision(Point3d center, double radius, Entity structure)
        {
            var result = new LashingCollisionResult();

            if (structure is Curve curve)
            {
                // ✅ GetClosestPointTo — pure geometric query, safe inside transaction
                try
                {
                    Point3d closestPt = curve.GetClosestPointTo(center, false);
                    double  dist      = center.DistanceTo(closestPt);
                    if (dist >= radius) return result;

                    result.CollisionOccurred = true;
                    double deltaX = Math.Abs(center.X - closestPt.X);
                    double deltaY = Math.Abs(center.Y - closestPt.Y);
                    result.DeltaX = deltaX;
                    result.DeltaY = deltaY;

                    // DeltaX lớn = cấu kiện nằm sang trái/phải = cấu kiện đứng → Vertical
                    // DeltaY lớn = cấu kiện nằm trên/dưới     = cấu kiện ngang → Horizontal
                    if (deltaX > deltaY * 5.0 + 1e-6)
                        result.CollisionType = LashingCollisionType.Vertical;
                    else if (deltaY > deltaX * 5.0 + 1e-6)
                        result.CollisionType = LashingCollisionType.Horizontal;
                    else
                        result.CollisionType = LashingCollisionType.Complex;
                }
                catch { /* Curve suy biến hoặc không hỗ trợ → bỏ qua */ }
            }
            else
            {
                // Fallback: bounding box distance cho entity không phải Curve (Block, Hatch…)
                try
                {
                    Extents3d bbox     = structure.GeometricExtents;
                    double    closestX = Math.Max(bbox.MinPoint.X, Math.Min(center.X, bbox.MaxPoint.X));
                    double    closestY = Math.Max(bbox.MinPoint.Y, Math.Min(center.Y, bbox.MaxPoint.Y));
                    double    dist     = Math.Sqrt(Math.Pow(center.X - closestX, 2) +
                                                   Math.Pow(center.Y - closestY, 2));
                    if (dist < radius)
                    {
                        result.CollisionOccurred = true;
                        result.DeltaX            = Math.Abs(center.X - closestX);
                        result.DeltaY            = Math.Abs(center.Y - closestY);
                        result.CollisionType     = LashingCollisionType.Complex;
                    }
                }
                catch { }
            }

            return result;
        }

        /// <summary>Trả về va chạm đầu tiên phát hiện được (worst-first).</summary>
        public LashingCollisionResult GetWorstCollision(Point3d pt, double clearance,
            IEnumerable<Entity> structures)
        {
            foreach (var s in structures)
            {
                var r = ClassifyCollision(pt, clearance, s);
                if (r.CollisionOccurred) return r;
            }
            return new LashingCollisionResult();
        }

        /// <summary>Kiểm tra nhanh: điểm có va chạm với bất kỳ cấu kiện / keep-out zone nào không?</summary>
        public bool HasAnyCollision(Point3d pt, double clearance,
            IList<Entity> structures, IList<Line3d> keepOutZones)
        {
            foreach (var s in structures)
                if (ClassifyCollision(pt, clearance, s).CollisionOccurred) return true;

            foreach (var z in keepOutZones)
                if (DistanceToLine(pt, z) < clearance) return true;

            return false;
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #2: 8-Direction Safe Point Search
        // Tương đương VBA: FindSafeVerticalPoint + FindSafeHorizontalPoint
        //                  + FindSafeComplex8DirectionPoint
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tìm điểm an toàn gần nhất bằng cách quét 8 hướng từ gần đến xa.
        /// Ưu tiên hướng dựa theo loại va chạm ban đầu (khớp logic VBA).
        /// Trả về origin nếu không tìm được điểm nào trong boundary.
        /// </summary>
        public Point3d FindSafePoint(
            Point3d             origin,
            double              clearance,
            IList<Entity>       structures,
            IList<Line3d>       keepOutZones,
            Polyline            boundary,
            LashingCollisionType collisionType = LashingCollisionType.Complex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"{LOG_PREFIX} FindSafePoint từ ({origin.X:F0},{origin.Y:F0}), loại: {collisionType}");

            var dirs = GetPriorityDirs(collisionType);

            Point3d best    = origin;
            double  bestDist = double.MaxValue;

            foreach (var (dx, dy) in dirs)
            {
                double len = Math.Sqrt(dx * dx + dy * dy);
                double nx  = dx / len;
                double ny  = dy / len;

                for (int step = 1; step <= MAX_SEARCH_STEPS; step++)
                {
                    double dist      = step * SEARCH_STEP_MM;
                    var    candidate = new Point3d(origin.X + nx * dist, origin.Y + ny * dist, 0);

                    // Ra ngoài boundary → dừng hướng này
                    if (!AutoCADGeometryHelper.IsInsidePolylineOrEdge(candidate, boundary))
                        break;

                    // Tìm thấy điểm an toàn trong hướng này
                    if (!HasAnyCollision(candidate, clearance, structures, keepOutZones))
                    {
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best     = candidate;
                        }
                        System.Diagnostics.Debug.WriteLine(
                            $"{LOG_PREFIX}   Hướng ({dx:F0},{dy:F0}): điểm an toàn tại dist={dist:F0}mm");
                        break; // Đủ rồi, không cần quét xa hơn hướng này
                    }
                }
            }

            if (bestDist < double.MaxValue)
                System.Diagnostics.Debug.WriteLine(
                    $"{LOG_PREFIX} FindSafePoint THÀNH CÔNG: ({best.X:F0},{best.Y:F0}), dist={bestDist:F0}mm");
            else
                System.Diagnostics.Debug.WriteLine(
                    $"{LOG_PREFIX} FindSafePoint KHÔNG TÌM ĐƯỢC vị trí an toàn → giữ nguyên origin");

            return best;
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #3 (helper): Keep-Out Zones cho Adjacent Panel
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tạo vùng cấm ảo hoàn toàn trong RAM từ danh sách cấu kiện liền kề.
        /// isVertical = true  → lọc cấu kiện đứng (dy >> dx), tạo đường giới hạn theo X.
        /// isVertical = false → lọc cấu kiện ngang (dx >> dy), tạo đường giới hạn theo Y.
        /// </summary>
        public List<Line3d> CreateVirtualKeepOutZones(
            IEnumerable<Entity> adjStructures,
            double              boundMin,
            double              boundMax,
            bool                isVertical)
        {
            var zones = new List<Line3d>();

            foreach (var ent in adjStructures)
            {
                if (!(ent is Polyline poly)) continue;

                Extents3d box;
                try { box = poly.GeometricExtents; }
                catch { continue; }

                double dx = Math.Abs(box.MaxPoint.X - box.MinPoint.X);
                double dy = Math.Abs(box.MaxPoint.Y - box.MinPoint.Y);

                if (isVertical && dy > ADJACENT_FILTER_RATIO * dx)
                {
                    zones.Add(new Line3d(new Point3d(box.MinPoint.X, boundMin, 0),
                                         new Point3d(box.MinPoint.X, boundMax, 0)));
                    zones.Add(new Line3d(new Point3d(box.MaxPoint.X, boundMin, 0),
                                         new Point3d(box.MaxPoint.X, boundMax, 0)));
                }
                else if (!isVertical && dx > ADJACENT_FILTER_RATIO * dy)
                {
                    zones.Add(new Line3d(new Point3d(boundMin, box.MinPoint.Y, 0),
                                         new Point3d(boundMax, box.MinPoint.Y, 0)));
                    zones.Add(new Line3d(new Point3d(boundMin, box.MaxPoint.Y, 0),
                                         new Point3d(boundMax, box.MaxPoint.Y, 0)));
                }
            }

            return zones;
        }

        /// <summary>Khoảng cách vuông góc từ điểm đến đường thẳng vô hạn (Line3d).</summary>
        public static double DistanceToLine(Point3d pt, Line3d line)
        {
            Vector3d toPoint = pt - line.PointOnLine;
            return toPoint.CrossProduct(line.Direction).Length;
        }

        // ─────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────

        private static (double dx, double dy)[] GetPriorityDirs(LashingCollisionType type)
        {
            switch (type)
            {
                case LashingCollisionType.Vertical:   return DIRS_VERTICAL;
                case LashingCollisionType.Horizontal: return DIRS_HORIZONTAL;
                default:                              return DIRS_COMPLEX;
            }
        }
    }
}
