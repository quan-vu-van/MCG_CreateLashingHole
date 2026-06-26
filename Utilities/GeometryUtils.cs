using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System.Text;

namespace SDS.Utilities
{
    public static class GeometryUtils
    {
        public static string FormatPoint(Point3d p) => $"{p.X:F2}, {p.Y:F2}";

        public static double RadianToDegree(double rad) => rad * (180.0 / System.Math.PI);

        public static string GetPolyPoints(Polyline pl)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                var p = pl.GetPoint2dAt(i);
                sb.Append($"({p.X:F1},{p.Y:F1}) ");
            }
            return sb.ToString().Trim();
        }
    }
}