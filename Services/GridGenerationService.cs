using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SDS.Models;
using SDS.Utilities;

namespace SDS.Services
{
    /// <summary>
    /// Phase 3 — Tái tạo lỗ lashing trong khu vực đặc biệt giữa 2 hole đã có.
    /// Logic: User chọn hole đầu và hole cuối → tool tính khoảng trống và
    /// điền holes trung gian theo spacing, với auto-detect hướng đối xứng.
    /// </summary>
    public class GridGenerationService
    {
        private readonly CollisionEngineService _collision;

        public GridGenerationService(CollisionEngineService collision)
        {
            _collision = collision;
        }

        /// <summary>
        /// Điền holes vào khoảng trống giữa startHole và endHole.
        /// </summary>
        public List<ObjectId> RegenerateSpecialArea(
            Circle startHole,
            Circle endHole,
            Polyline boundary,
            double spacing,
            double offset,
            bool isVertical,
            LashingInputParams p,
            Transaction tr,
            BlockTableRecord space)
        {
            var created = new List<ObjectId>();
            double radius = startHole.Radius;

            Point3d startCenter = startHole.Center;
            Point3d endCenter = endHole.Center;

            // 1. Xác định hướng phát triển đối xứng từ midpoint so với tâm panel
            Point3d midPt = new Point3d(
                (startCenter.X + endCenter.X) / 2.0,
                (startCenter.Y + endCenter.Y) / 2.0, 0);

            Extents3d panelBox = boundary.GeometricExtents;
            Point3d panelCenter = new Point3d(
                (panelBox.MinPoint.X + panelBox.MaxPoint.X) / 2.0,
                (panelBox.MinPoint.Y + panelBox.MaxPoint.Y) / 2.0, 0);

            // genDir: +1 nếu midpoint nằm về phía dương so với tâm, -1 nếu ngược lại
            int genDir = isVertical
                ? (midPt.Y >= panelCenter.Y ? 1 : -1)
                : (midPt.X >= panelCenter.X ? 1 : -1);

            // 2. Tính số holes cần tạo trong khoảng trống
            double gap = isVertical
                ? Math.Abs(endCenter.Y - startCenter.Y)
                : Math.Abs(endCenter.X - startCenter.X);

            int nHoles = (int)Math.Round(gap / spacing) - 1;
            if (nHoles <= 0) return created;

            double baseCoordFixed = isVertical ? startCenter.X : startCenter.Y;
            double baseCoordMoving = isVertical
                ? Math.Min(startCenter.Y, endCenter.Y)
                : Math.Min(startCenter.X, endCenter.X);

            // 3. Tạo các hole trung gian
            for (int i = 1; i <= nHoles; i++)
            {
                double movingCoord = baseCoordMoving + i * spacing;
                Point3d candidate = isVertical
                    ? new Point3d(baseCoordFixed, movingCoord, 0)
                    : new Point3d(movingCoord, baseCoordFixed, 0);

                if (!AutoCADGeometryHelper.IsInsidePolyline(candidate, boundary))
                    continue;

                var circle = new Circle(candidate, Vector3d.ZAxis, radius);
                circle.Layer = p.HoleLayer;
                space.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);
                created.Add(circle.ObjectId);
            }

            return created;
        }
    }
}
