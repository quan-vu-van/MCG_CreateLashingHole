using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SDS.Models;
using SDS.Utilities;

namespace SDS.Services
{
    /// <summary>
    /// Dịch vụ chính: rải lưới lỗ lashing trên tấm biên, né cấu kiện,
    /// và ghi chú kích thước (chỉ dimension nếu spacing bị điều chỉnh).
    /// </summary>
    public class BlockPackingService
    {
        private readonly CollisionEngineService _collision;
        private const double DIMSCALE = 25.0;
        private const double TOLERANCE = 1.0;
        private const int MAX_ADJUST_ITERATIONS = 20;

        public BlockPackingService(CollisionEngineService collision)
        {
            _collision = collision;
        }

        // ─────────────────────────────────────────────────────────────
        // API chính
        // ─────────────────────────────────────────────────────────────

        public List<ObjectId> GenerateHoles(
            LashingInputParams p,
            Polyline boundary,
            List<Entity> structures,
            Transaction tr,
            BlockTableRecord space,
            Database db)
        {
            double radius = p.HoleDiameter / 2.0;
            Extents3d box = boundary.GeometricExtents;

            // Vùng cấm từ cấu kiện liền kề (nếu bật)
            var keepOutZones = new List<Line3d>();
            if (p.IsCheckAdjacent)
            {
                keepOutZones.AddRange(_collision.CreateVirtualKeepOutZones(
                    structures, box.MinPoint.Y, box.MaxPoint.Y, isVertical: true));
                keepOutZones.AddRange(_collision.CreateVirtualKeepOutZones(
                    structures, box.MinPoint.X, box.MaxPoint.X, isVertical: false));
            }

            // Tạo danh sách điểm lưới theo mode
            var gridPoints = BuildGridPoints(p, box);

            var createdIds = new List<ObjectId>();
            var finalCenters = new List<Point3d>();

            foreach (Point3d candidate in gridPoints)
            {
                if (!AutoCADGeometryHelper.IsInsidePolyline(candidate, boundary))
                    continue;

                Point3d finalPt = AdjustForCollisions(candidate, p.ClearanceRadius, structures, keepOutZones, boundary);

                if (!AutoCADGeometryHelper.IsInsidePolyline(finalPt, boundary))
                    continue;

                ObjectId id = DrawCircle(finalPt, radius, p.HoleLayer, tr, space);
                createdIds.Add(id);
                finalCenters.Add(finalPt);
            }

            // Ghi chú kích thước cho các hole đã bị điều chỉnh
            if (finalCenters.Count > 1)
            {
                db.Dimscale = DIMSCALE;
                EnsureLayerExists(p.DimLayer, db, tr);
                AddDimensions(finalCenters, p, box, tr, space, db);
            }

            return createdIds;
        }

        // ─────────────────────────────────────────────────────────────
        // Tạo lưới điểm theo LocationMode
        // ─────────────────────────────────────────────────────────────

        private List<Point3d> BuildGridPoints(LashingInputParams p, Extents3d box)
        {
            var pts = new List<Point3d>();
            double w = box.MaxPoint.X - box.MinPoint.X;
            double h = box.MaxPoint.Y - box.MinPoint.Y;

            switch (p.LocationMode)
            {
                case LashingLocationMode.PortSide:
                    // P1 = góc Dưới-Trái → phát triển lên trên và sang phải
                    for (double y = box.MinPoint.Y + p.OffsetY; y <= box.MaxPoint.Y - p.OffsetY / 2; y += p.SpacingY)
                        for (double x = box.MinPoint.X + p.OffsetX; x <= box.MaxPoint.X - p.OffsetX / 2; x += p.SpacingX)
                            pts.Add(new Point3d(x, y, 0));
                    break;

                case LashingLocationMode.StarBoard:
                    // P1 = góc Trên-Trái → phát triển xuống dưới và sang phải
                    for (double y = box.MaxPoint.Y - p.OffsetY; y >= box.MinPoint.Y + p.OffsetY / 2; y -= p.SpacingY)
                        for (double x = box.MinPoint.X + p.OffsetX; x <= box.MaxPoint.X - p.OffsetX / 2; x += p.SpacingX)
                            pts.Add(new Point3d(x, y, 0));
                    break;

                case LashingLocationMode.Center:
                    // Tâm panel → rải đối xứng ra 4 phía
                    int nX = Math.Max(1, (int)Math.Floor((w - 2 * p.OffsetX) / p.SpacingX) + 1);
                    int nY = Math.Max(1, (int)Math.Floor((h - 2 * p.OffsetY) / p.SpacingY) + 1);
                    double cx = (box.MinPoint.X + box.MaxPoint.X) / 2.0;
                    double cy = (box.MinPoint.Y + box.MaxPoint.Y) / 2.0;
                    double startX = cx - (nX - 1) / 2.0 * p.SpacingX;
                    double startY = cy - (nY - 1) / 2.0 * p.SpacingY;
                    for (int row = 0; row < nY; row++)
                        for (int col = 0; col < nX; col++)
                            pts.Add(new Point3d(startX + col * p.SpacingX, startY + row * p.SpacingY, 0));
                    break;
            }

            return pts;
        }

        // ─────────────────────────────────────────────────────────────
        // Điều chỉnh vị trí hole tránh va chạm (lặp tối đa MAX_ADJUST_ITERATIONS)
        // ─────────────────────────────────────────────────────────────

        private Point3d AdjustForCollisions(
            Point3d pt,
            double clearance,
            List<Entity> structures,
            List<Line3d> keepOutZones,
            Polyline boundary)
        {
            Point3d current = pt;

            for (int iter = 0; iter < MAX_ADJUST_ITERATIONS; iter++)
            {
                bool adjusted = false;

                // Kiểm tra va chạm với từng cấu kiện
                foreach (var structure in structures)
                {
                    var col = _collision.ClassifyCollision(current, clearance, structure);
                    if (!col.CollisionOccurred) continue;

                    switch (col.CollisionType)
                    {
                        case LashingCollisionType.Vertical:
                            // Cấu kiện đứng → dịch X ra khỏi vùng va chạm
                            current = new Point3d(current.X + col.DeltaX / 2.0 + TOLERANCE, current.Y, 0);
                            break;
                        case LashingCollisionType.Horizontal:
                            // Cấu kiện ngang → dịch Y
                            current = new Point3d(current.X, current.Y + col.DeltaY / 2.0 + TOLERANCE, 0);
                            break;
                        case LashingCollisionType.Complex:
                            // Góc phức → dịch cả hai
                            current = new Point3d(
                                current.X + col.DeltaX / 2.0 + TOLERANCE,
                                current.Y + col.DeltaY / 2.0 + TOLERANCE, 0);
                            break;
                    }
                    adjusted = true;
                    break; // Restart sau mỗi lần điều chỉnh
                }

                // Kiểm tra vùng cấm liền kề
                if (!adjusted)
                {
                    foreach (var zone in keepOutZones)
                    {
                        double dist = CollisionEngineService.DistanceToLine(current, zone);
                        if (dist < clearance)
                        {
                            // Dịch vuông góc với zone ra xa
                            Vector3d toPoint = (current - zone.PointOnLine);
                            Vector3d perp = toPoint - zone.Direction * zone.Direction.DotProduct(toPoint);
                            if (perp.Length > 1e-9)
                            {
                                double pushDist = clearance - dist + TOLERANCE;
                                current = current + perp.GetNormal() * pushDist;
                            }
                            adjusted = true;
                            break;
                        }
                    }
                }

                if (!adjusted) break;
            }

            return current;
        }

        // ─────────────────────────────────────────────────────────────
        // Vẽ circle vào Model Space
        // ─────────────────────────────────────────────────────────────

        private static ObjectId DrawCircle(Point3d center, double radius, string layer, Transaction tr, BlockTableRecord space)
        {
            var circle = new Circle(center, Vector3d.ZAxis, radius);
            circle.Layer = layer;
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);
            return circle.ObjectId;
        }

        // ─────────────────────────────────────────────────────────────
        // Thêm dimension chain — chỉ ghi chú khoảng cách bị điều chỉnh
        // (khoảng cách khớp chính xác SpacingX/Y được bỏ qua)
        // ─────────────────────────────────────────────────────────────

        private void AddDimensions(
            List<Point3d> centers,
            LashingInputParams p,
            Extents3d box,
            Transaction tr,
            BlockTableRecord space,
            Database db)
        {
            // Nhóm theo hàng (Y tương đương) và cột (X tương đương)
            const double GROUP_TOLERANCE = 5.0;

            // Chiều ngang: nhóm theo Y, sort theo X từng hàng
            var rows = centers
                .GroupBy(c => Math.Round(c.Y / GROUP_TOLERANCE) * GROUP_TOLERANCE)
                .OrderBy(g => g.Key);

            double dimLineY = box.MinPoint.Y - 200 * (DIMSCALE / 25.0);
            foreach (var row in rows)
            {
                var sorted = row.OrderBy(c => c.X).ToList();
                AddChainDims(sorted, p.SpacingX, dimLineY, isHorizontal: true, p.DimLayer, tr, space, db);
                dimLineY -= 150 * (DIMSCALE / 25.0); // stagger nếu nhiều hàng
            }

            // Chiều dọc: nhóm theo X, sort theo Y từng cột
            var cols = centers
                .GroupBy(c => Math.Round(c.X / GROUP_TOLERANCE) * GROUP_TOLERANCE)
                .OrderBy(g => g.Key);

            double dimLineX = box.MaxPoint.X + 200 * (DIMSCALE / 25.0);
            foreach (var col in cols)
            {
                var sorted = col.OrderBy(c => c.Y).ToList();
                AddChainDims(sorted, p.SpacingY, dimLineX, isHorizontal: false, p.DimLayer, tr, space, db);
                dimLineX += 150 * (DIMSCALE / 25.0);
            }
        }

        private void AddChainDims(
            List<Point3d> pts,
            double standardSpacing,
            double dimLineCoord,
            bool isHorizontal,
            string dimLayer,
            Transaction tr,
            BlockTableRecord space,
            Database db)
        {
            if (pts.Count < 2) return;

            for (int i = 0; i < pts.Count - 1; i++)
            {
                Point3d p1 = pts[i];
                Point3d p2 = pts[i + 1];
                double dist = isHorizontal
                    ? Math.Abs(p2.X - p1.X)
                    : Math.Abs(p2.Y - p1.Y);

                // Bỏ qua khoảng cách chuẩn (không cần ghi chú)
                if (Math.Abs(dist - standardSpacing) < TOLERANCE) continue;

                Point3d dimPt = isHorizontal
                    ? new Point3d((p1.X + p2.X) / 2.0, dimLineCoord, 0)
                    : new Point3d(dimLineCoord, (p1.Y + p2.Y) / 2.0, 0);

                var dim = new AlignedDimension(p1, p2, dimPt, string.Empty, db.Dimstyle);
                dim.Layer = dimLayer;
                dim.Dimscale = DIMSCALE;
                space.AppendEntity(dim);
                tr.AddNewlyCreatedDBObject(dim, true);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Đảm bảo layer dimension tồn tại
        // ─────────────────────────────────────────────────────────────

        private static void EnsureLayerExists(string layerName, Database db, Transaction tr)
        {
            var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null || lt.Has(layerName)) return;

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = layerName };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }
}
