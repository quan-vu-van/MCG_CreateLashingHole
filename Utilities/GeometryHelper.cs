using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;

namespace SDS.Infrastructure.Utils
{
    public static class GeometryHelper
    {
        public static Extents3d? GetIntersectionExtents(Point3dCollection rectPts, Polyline boundaryPoly)
        {
            using (DBObjectCollection curves = new DBObjectCollection())
            {
                curves.Add(boundaryPoly);
                using (DBObjectCollection regions = Region.CreateFromCurves(curves))
                {
                    if (regions.Count == 0) return null;
                    Region regionBoundary = (Region)regions[0];

                    using (Polyline rectPoly = new Polyline())
                    {
                        for (int i = 0; i < rectPts.Count; i++)
                            rectPoly.AddVertexAt(i, new Point2d(rectPts[i].X, rectPts[i].Y), 0, 0, 0);
                        rectPoly.Closed = true;

                        using (DBObjectCollection rectCurves = new DBObjectCollection())
                        {
                            rectCurves.Add(rectPoly);
                            using (DBObjectCollection rectRegions = Region.CreateFromCurves(rectCurves))
                            {
                                if (rectRegions.Count == 0) return null;
                                Region regionRect = (Region)rectRegions[0];

                                // --- SỬA LỖI Ở DÒNG NÀY (Intersect -> Intersection) ---
                                regionRect.BooleanOperation(BooleanOperationType.BoolIntersect, regionBoundary);

                                // -------------------------------------------------------

                                if (regionRect.Area > 0.001)
                                {
                                    return regionRect.GeometricExtents;
                                }
                            }
                        }
                    }
                }
            }
            return null;
        }
    }
}