using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SDS.Models;

namespace SDS.Services
{
    /// <summary>
    /// Toàn bộ phép toán giao cắt được tính trực tiếp trong RAM qua Circle ảo,
    /// không đẩy vào Database → không tạo thực thể rác trên bản vẽ.
    /// </summary>
    public class CollisionEngineService
    {
        // Tỉ lệ lọc cấu kiện liền kề (ADJACENT_STRUCTURE_FILTER_RATIO = 300)
        private const double ADJACENT_FILTER_RATIO = 300.0;

        /// <summary>
        /// Phân loại hướng va chạm dựa vào tỉ lệ Delta X / Delta Y hình học.
        /// </summary>
        public LashingCollisionResult ClassifyCollision(Point3d center, double radius, Entity structure)
        {
            var result = new LashingCollisionResult();
            var pts = new Point3dCollection();

            using (var virtualCircle = new Circle(center, Vector3d.ZAxis, radius))
            {
                try
                {
                    virtualCircle.IntersectWith(structure, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                }
                catch
                {
                    return result; // cấu kiện không hỗ trợ giao cắt → bỏ qua
                }
            }

            if (pts.Count < 2) return result;

            result.CollisionOccurred = true;
            result.DeltaX = Math.Abs(pts[0].X - pts[1].X);
            result.DeltaY = Math.Abs(pts[0].Y - pts[1].Y);

            if (result.DeltaY > result.DeltaX * 5.0 + 0.000001)
                result.CollisionType = LashingCollisionType.Vertical;
            else if (result.DeltaX > result.DeltaY * 5.0 + 0.000001)
                result.CollisionType = LashingCollisionType.Horizontal;
            else
                result.CollisionType = LashingCollisionType.Complex;

            return result;
        }

        /// <summary>
        /// Tạo vùng cấm ảo (keep-out zone) hoàn toàn trong RAM từ danh sách cấu kiện liền kề.
        /// isVertical = true  → lọc cấu kiện đứng (dy >> dx), tạo đường giới hạn theo X.
        /// isVertical = false → lọc cấu kiện ngang (dx >> dy), tạo đường giới hạn theo Y.
        /// </summary>
        public List<Line3d> CreateVirtualKeepOutZones(
            IEnumerable<Entity> adjStructures,
            double boundMin,
            double boundMax,
            bool isVertical)
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
                    // Cấu kiện đứng → tạo 2 đường thẳng đứng tại MinX và MaxX
                    zones.Add(new Line3d(
                        new Point3d(box.MinPoint.X, boundMin, 0),
                        new Point3d(box.MinPoint.X, boundMax, 0)));
                    zones.Add(new Line3d(
                        new Point3d(box.MaxPoint.X, boundMin, 0),
                        new Point3d(box.MaxPoint.X, boundMax, 0)));
                }
                else if (!isVertical && dx > ADJACENT_FILTER_RATIO * dy)
                {
                    // Cấu kiện ngang → tạo 2 đường nằm ngang tại MinY và MaxY
                    zones.Add(new Line3d(
                        new Point3d(boundMin, box.MinPoint.Y, 0),
                        new Point3d(boundMax, box.MinPoint.Y, 0)));
                    zones.Add(new Line3d(
                        new Point3d(boundMin, box.MaxPoint.Y, 0),
                        new Point3d(boundMax, box.MaxPoint.Y, 0)));
                }
            }

            return zones;
        }

        /// <summary>
        /// Tính khoảng cách vuông góc từ điểm đến đường thẳng vô hạn (Line3d).
        /// </summary>
        public static double DistanceToLine(Point3d pt, Line3d line)
        {
            Vector3d toPoint = pt - line.PointOnLine;
            return toPoint.CrossProduct(line.Direction).Length;
        }
    }
}
