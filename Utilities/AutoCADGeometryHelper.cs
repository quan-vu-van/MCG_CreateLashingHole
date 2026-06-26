using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace SDS.Utilities
{
    public static class AutoCADGeometryHelper
    {
        /// <summary>
        /// Kiểm tra điểm có nằm bên trong Polyline đóng không (ray-casting).
        /// Hoạt động chính xác với polyline tuyến tính (không có cung arc).
        /// </summary>
        public static bool IsInsidePolyline(Point3d pt, Polyline poly)
        {
            if (!poly.Closed) return false;

            int count = poly.NumberOfVertices;
            bool inside = false;

            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Point2d pi = poly.GetPoint2dAt(i);
                Point2d pj = poly.GetPoint2dAt(j);

                if (((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                    pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                    inside = !inside;
            }

            return inside;
        }

        /// <summary>
        /// Lấy 2 điểm góc đối diện (min và max) của bounding box từ Polyline.
        /// </summary>
        public static (Point3d min, Point3d max) GetSmartRectFromPolyline(Polyline poly)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            for (int i = 0; i < poly.NumberOfVertices; i++)
            {
                Point2d v = poly.GetPoint2dAt(i);
                if (v.X < minX) minX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.X > maxX) maxX = v.X;
                if (v.Y > maxY) maxY = v.Y;
            }

            return (new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
        }

        /// <summary>
        /// Trả về 4 góc của hình chữ nhật từ 2 điểm đường chéo.
        /// </summary>
        public static Point3d[] GetRectangularPoints(Point3d p1, Point3d p2)
        {
            return new[]
            {
                p1,
                new Point3d(p2.X, p1.Y, 0),
                p2,
                new Point3d(p1.X, p2.Y, 0)
            };
        }

        public static double DistanceBetweenPoints(Point3d p1, Point3d p2)
            => Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));
    }
}
