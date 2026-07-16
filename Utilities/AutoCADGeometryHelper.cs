using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace MCG_CreateLashingHole.Utilities
{
    public static class AutoCADGeometryHelper
    {
        /// <summary>
        /// Kiểm tra điểm có nằm bên trong Polyline không (ray-casting).
        /// Hỗ trợ cả Closed và Open polyline (VBA: ALLOW OPEN POLYLINE).
        /// </summary>
        public static bool IsInsidePolylineOrEdge(Point3d pt, Polyline poly)
        {
            int count = poly.NumberOfVertices;
            if (count < 3) return false;

            bool inside = false;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Point2d pi = poly.GetPoint2dAt(i);
                Point2d pj = poly.GetPoint2dAt(j);

                if (((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                    pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                    inside = !inside;
            }

            // Nếu open polyline: cũng kiểm tra cạnh đóng ảo (last → first)
            if (!poly.Closed)
            {
                Point2d p0 = poly.GetPoint2dAt(0);
                Point2d pn = poly.GetPoint2dAt(count - 1);
                if (((p0.Y > pt.Y) != (pn.Y > pt.Y)) &&
                    pt.X < (p0.X - pn.X) * (pt.Y - pn.Y) / (p0.Y - pn.Y) + pn.X)
                    inside = !inside;
            }

            return inside;
        }

        /// <summary>
        /// Tính tâm hình học (centroid) của Polyline bằng công thức Shoelace.
        /// Dùng cho LocationMode.Center thay vì midpoint bounding box.
        /// </summary>
        public static Point3d GetPolygonCentroid(Polyline poly)
        {
            int n = poly.NumberOfVertices;
            double area = 0, cx = 0, cy = 0;

            for (int i = 0; i < n; i++)
            {
                Point2d p1 = poly.GetPoint2dAt(i);
                Point2d p2 = poly.GetPoint2dAt((i + 1) % n);
                double cross = p1.X * p2.Y - p2.X * p1.Y;
                area += cross;
                cx   += (p1.X + p2.X) * cross;
                cy   += (p1.Y + p2.Y) * cross;
            }

            area /= 2.0;
            if (Math.Abs(area) < 1e-9)
            {
                // Fallback: midpoint bounding box khi polyline suy biến
                var ext = poly.GeometricExtents;
                return new Point3d(
                    (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                    (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0, 0);
            }

            cx /= 6.0 * area;
            cy /= 6.0 * area;
            return new Point3d(cx, cy, 0);
        }

        /// <summary>
        /// Zoom viewport đến vùng Extents3d + margin.
        /// PHẢI gọi NGOÀI mọi Transaction đang mở (editor op không được trong transaction).
        /// </summary>
        public static void ZoomToBoundary(Editor ed, Extents3d box, double marginMm = 500.0)
        {
            try
            {
                var    view   = ed.GetCurrentView();
                double w      = box.MaxPoint.X - box.MinPoint.X + 2 * marginMm;
                double h      = box.MaxPoint.Y - box.MinPoint.Y + 2 * marginMm;
                double aspect = view.Width / view.Height;
                if (w / h < aspect) w = h * aspect;
                else                h = w / aspect;

                view.Width       = w;
                view.Height      = h;
                view.CenterPoint = new Point2d(
                    (box.MinPoint.X + box.MaxPoint.X) / 2.0,
                    (box.MinPoint.Y + box.MaxPoint.Y) / 2.0);
                ed.SetCurrentView(view);
            }
            catch { /* Non-critical */ }
        }

        /// <summary>
        /// Overload nhận Polyline — đọc GeometricExtents rồi delegate sang overload Extents3d.
        /// PHẢI gọi NGOÀI mọi Transaction đang mở.
        /// </summary>
        public static void ZoomToBoundary(Editor ed, Polyline boundary, double marginMm = 500.0)
        {
            try { ZoomToBoundary(ed, boundary.GeometricExtents, marginMm); }
            catch { }
        }

        /// <summary>Lấy bounding box an toàn từ Polyline.</summary>
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

        /// <summary>Trả về 4 góc hình chữ nhật từ 2 điểm đường chéo.</summary>
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
