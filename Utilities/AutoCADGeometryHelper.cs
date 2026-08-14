using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcRuntime = Autodesk.AutoCAD.Runtime;

namespace MCG_CreateLashingHole.Utilities
{
    public static class AutoCADGeometryHelper
    {
        /// <summary>
        /// Thu thập tâm (world) các circle khớp layer + bán kính, QUÉT CẢ bên trong block reference
        /// (KHÔNG cần phá block). Circle nằm trong block: tâm được transform qua BlockTransform.
        /// Chỉ nhận điểm nằm trong boundary nếu boundary != null.
        /// </summary>
        public static List<Point3d> CollectCircleCentersWorld(
            Transaction tr, BlockTableRecord space, Polyline boundary,
            string layer, double radius, double rTol)
        {
            var result      = new List<Point3d>();
            var circleClass = AcRuntime.RXObject.GetClass(typeof(Circle));
            var brefClass   = AcRuntime.RXObject.GetClass(typeof(BlockReference));

            foreach (ObjectId id in space)
            {
                if (!id.IsValid || id.IsErased) continue;

                // Circle ở model space (chưa đóng block)
                if (id.ObjectClass.IsDerivedFrom(circleClass))
                {
                    Circle c;
                    try { c = tr.GetObject(id, OpenMode.ForRead) as Circle; }
                    catch { continue; }
                    if (c == null || c.IsErased) continue;
                    if (c.Layer == layer && Math.Abs(c.Radius - radius) < rTol &&
                        (boundary == null || IsInsidePolylineOrEdge(c.Center, boundary)))
                        result.Add(c.Center);
                }
                // Circle nằm TRONG block reference → transform tâm về world
                else if (id.ObjectClass.IsDerivedFrom(brefClass))
                {
                    BlockReference br;
                    try { br = tr.GetObject(id, OpenMode.ForRead) as BlockReference; }
                    catch { continue; }
                    if (br == null || br.IsErased) continue;

                    Matrix3d xform = br.BlockTransform;
                    BlockTableRecord btr;
                    try { btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord; }
                    catch { continue; }
                    if (btr == null) continue;

                    foreach (ObjectId eid in btr)
                    {
                        if (!eid.IsValid || eid.IsErased || !eid.ObjectClass.IsDerivedFrom(circleClass)) continue;
                        Circle c;
                        try { c = tr.GetObject(eid, OpenMode.ForRead) as Circle; }
                        catch { continue; }
                        if (c == null || c.IsErased) continue;
                        if (c.Layer == layer && Math.Abs(c.Radius - radius) < rTol)
                        {
                            Point3d wc = c.Center.TransformBy(xform);
                            if (boundary == null || IsInsidePolylineOrEdge(wc, boundary))
                                result.Add(wc);
                        }
                    }
                }
            }
            return result;
        }

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
        /// Bắn tia từ basePt theo hướng dir, tìm giao GẦN NHẤT (khác basePt) với boundary polyline.
        /// Port FindIntersectionWithLongLine_Helper của VBA — dùng Line ảo trong RAM (KHÔNG thêm vào DB).
        /// Trả false nếu không có giao điểm hợp lệ.
        /// </summary>
        public static bool TryRayBoundaryIntersection(Point3d basePt, Vector3d dir, Polyline boundary, out Point3d hit)
        {
            hit = basePt;
            var far = new Point3d(basePt.X + dir.X * 1_000_000.0,
                                  basePt.Y + dir.Y * 1_000_000.0, 0);
            using (var ray = new Line(basePt, far))
            {
                var pts = new Point3dCollection();
                try { ray.IntersectWith(boundary, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero); }
                catch { return false; }

                double bestSq = -1.0;
                foreach (Point3d pt in pts)
                {
                    double dsq = (pt.X - basePt.X) * (pt.X - basePt.X) +
                                 (pt.Y - basePt.Y) * (pt.Y - basePt.Y);
                    if (dsq < 1e-6) continue; // bỏ giao trùng basePt
                    if (bestSq < 0 || dsq < bestSq) { bestSq = dsq; hit = new Point3d(pt.X, pt.Y, 0); }
                }
                return bestSq >= 0;
            }
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

        /// <summary>
        /// Xác định mép hình chữ nhật tham chiếu theo nguyên tắc CẠNH DÀI của VBA
        /// (GetSmartRectangularPointsFromPolyline): chỉ các cạnh thẳng > 1500mm mới định nghĩa mép:
        ///   cạnh đứng  (Δx &lt; 1mm) → minX/maxX (mép trái/phải)
        ///   cạnh ngang (Δy &lt; 1mm) → minY/maxY (mép trên/dưới)
        /// Bỏ qua cạnh cong (bulge) và cạnh ngắn (vết khía/bậc nhỏ).
        /// Fallback per-axis về bounding box (fbMin/fbMax) nếu trục không có cạnh dài nào.
        /// Trung thành VBA: luôn khép vòng (i+1) mod n khi duyệt cạnh.
        /// </summary>
        public static void GetSmartRectEdges(
            Polyline poly, Point3d fbMin, Point3d fbMax,
            out double minX, out double maxX, out double minY, out double maxY)
        {
            const double LENGTH_THRESHOLD = 1500.0;
            minX = double.MaxValue; maxX = double.MinValue;
            minY = double.MaxValue; maxY = double.MinValue;
            bool foundX = false, foundY = false;

            int n = poly.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(poly.GetBulgeAt(i)) >= 0.001) continue; // bỏ cạnh cong

                int     next = (i + 1) % n;                          // khép vòng như VBA
                Point2d v1   = poly.GetPoint2dAt(i);
                Point2d v2   = poly.GetPoint2dAt(next);
                double  dX   = Math.Abs(v2.X - v1.X);
                double  dY   = Math.Abs(v2.Y - v1.Y);
                if (Math.Sqrt(dX * dX + dY * dY) <= LENGTH_THRESHOLD) continue; // bỏ cạnh ngắn

                if (dX < 1.0)   // cạnh đứng → mép trái/phải
                {
                    if (v1.X < minX) minX = v1.X;
                    if (v1.X > maxX) maxX = v1.X;
                    foundX = true;
                }
                if (dY < 1.0)   // cạnh ngang → mép trên/dưới
                {
                    if (v1.Y < minY) minY = v1.Y;
                    if (v1.Y > maxY) maxY = v1.Y;
                    foundY = true;
                }
            }

            if (!foundX) { minX = fbMin.X; maxX = fbMax.X; }
            if (!foundY) { minY = fbMin.Y; maxY = fbMax.Y; }
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

        /// <summary>
        /// Khoảng cách NGẮN NHẤT từ điểm tới đường boundary (mọi cạnh). Nếu &lt; clearance thì
        /// outer ring của lỗ cắt qua biên (Type B) — dùng để đánh dấu đỏ / kiểm tra.
        /// </summary>
        public static double DistanceToBoundary(Point3d pt, Polyline boundary)
        {
            double best = double.MaxValue;
            int n = boundary.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                Point2d a = boundary.GetPoint2dAt(i);
                Point2d b = boundary.GetPoint2dAt((i + 1) % n);
                double d = DistPointToSegment(pt.X, pt.Y, a.X, a.Y, b.X, b.Y);
                if (d < best) best = d;
            }
            return best;
        }

        private static double DistPointToSegment(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            if (l2 < 1e-12) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            double t = ((px - ax) * dx + (py - ay) * dy) / l2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double cx = ax + t * dx, cy = ay + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }
    }
}
