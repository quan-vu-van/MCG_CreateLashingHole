using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Utilities;
using D = MCG_CreateLashingHole.Utilities.DiagLogger;

namespace MCG_CreateLashingHole.Services
{
    /// <summary>
    /// Rải lưới lỗ lashing trên tấm biên:
    ///   • Vẽ DUAL CIRCLE (innerCircle = lỗ thực / outerCircle = vùng clearance)
    ///   • Né cấu kiện bằng 8-direction safe point search
    ///   • Ghi kích thước CHỈ cho các lỗ bị điều chỉnh (adjusted holes)
    ///   • Effective center dùng polygon centroid (không phải midpoint bounding box)
    /// </summary>
    public class BlockPackingService
    {
        private const string LOG_PREFIX = "[BlockPacking]";

        private readonly CollisionEngineService _collision;

        private const double DIMSCALE  = 25.0;
        private const double TOLERANCE = 1.0;

        // Track vị trí đã điều chỉnh để tạo dimension (Gap #9)
        private readonly struct AdjustedHole
        {
            public readonly Point3d Planned;
            public readonly Point3d Actual;
            public AdjustedHole(Point3d planned, Point3d actual) { Planned = planned; Actual = actual; }
        }

        public BlockPackingService(CollisionEngineService collision)
        {
            _collision = collision;
        }

        // ─────────────────────────────────────────────────────────────
        // API chính
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tạo lưới lỗ lashing. Trả về danh sách ObjectId của innerCircle (số lượng = số lỗ).
        /// </summary>
        public List<ObjectId> GenerateHoles(
            LashingInputParams p,
            Polyline           boundary,
            List<Entity>       structures,
            Transaction        tr,
            BlockTableRecord   space,
            Database           db)
        {
            D.Log("GenerateHoles START");
            double    radius = p.HoleDiameter / 2.0;
            Extents3d box    = boundary.GeometricExtents;
            D.Log($"  radius={radius}, box=({box.MinPoint.X:F0},{box.MinPoint.Y:F0})-({box.MaxPoint.X:F0},{box.MaxPoint.Y:F0})");

            D.Log("  EnsureLayerExists INNER_HOLE...");
            EnsureLayerExists(LashingInputParams.LAYER_INNER_HOLE,  db, tr);
            D.Log("  EnsureLayerExists OUTER_CLEAR...");
            EnsureLayerExists(LashingInputParams.LAYER_OUTER_CLEAR, db, tr);
            D.Log("  EnsureLayerExists DIMENSION...");
            EnsureLayerExists(LashingInputParams.LAYER_DIMENSION,   db, tr);
            D.Log("  All layers ensured");

            // Vùng cấm từ cấu kiện liền kề (nếu bật IsCheckAdjacent)
            var keepOutZones = new List<Line3d>();
            if (p.IsCheckAdjacent)
            {
                keepOutZones.AddRange(_collision.CreateVirtualKeepOutZones(
                    structures, box.MinPoint.Y, box.MaxPoint.Y, isVertical: true));
                keepOutZones.AddRange(_collision.CreateVirtualKeepOutZones(
                    structures, box.MinPoint.X, box.MaxPoint.X, isVertical: false));
                System.Diagnostics.Debug.WriteLine(
                    $"{LOG_PREFIX} Keep-out zones: {keepOutZones.Count} đường thẳng ảo.");
            }

            D.Log("  BuildGridPoints...");
            var gridPoints = BuildGridPoints(p, box, boundary);
            D.Log($"  Grid: {gridPoints.Count} points");

            var innerIds   = new List<ObjectId>();
            var adjusted   = new List<AdjustedHole>();

            foreach (Point3d candidate in gridPoints)
            {
                if (!AutoCADGeometryHelper.IsInsidePolylineOrEdge(candidate, boundary))
                    continue;

                // Kiểm tra va chạm tại vị trí lưới
                var col = _collision.GetWorstCollision(candidate, p.ClearanceRadius, structures);

                Point3d finalPt;
                if (!col.CollisionOccurred &&
                    !HasKeepOutCollision(candidate, p.ClearanceRadius, keepOutZones))
                {
                    finalPt = candidate; // Không va chạm → giữ nguyên
                }
                else
                {
                    // Gap #2: 8-direction safe point search
                    finalPt = _collision.FindSafePoint(
                        candidate, p.ClearanceRadius, structures, keepOutZones,
                        boundary, col.CollisionType);

                    if (!AutoCADGeometryHelper.IsInsidePolylineOrEdge(finalPt, boundary))
                        continue; // Không tìm được vị trí hợp lệ → bỏ qua điểm này
                }

                // Gap #1: Vẽ DUAL CIRCLE — innerCircle + outerCircle
                D.Log($"  DrawCircle INNER at ({finalPt.X:F0},{finalPt.Y:F0}) r={radius}...");
                ObjectId innerId = DrawCircle(finalPt, radius,         LashingInputParams.LAYER_INNER_HOLE,  tr, space, db);
                D.Log($"  DrawCircle OUTER at ({finalPt.X:F0},{finalPt.Y:F0}) r={p.ClearanceRadius}...");
                           /*outer =*/ DrawCircle(finalPt, p.ClearanceRadius, LashingInputParams.LAYER_OUTER_CLEAR, tr, space, db);
                D.Log($"  Both circles drawn OK");
                innerIds.Add(innerId);

                // Ghi nhận nếu đã bị điều chỉnh (Gap #9)
                if (finalPt.DistanceTo(candidate) > TOLERANCE)
                    adjusted.Add(new AdjustedHole(candidate, finalPt));
            }

            // Gap #9: Dimension CHỈ cho các lỗ bị điều chỉnh
            // db.Dimscale không set ở đây — gây reactor callback crash trong transaction.
            // dim.Dimscale được set trực tiếp trên entity trong AddAdjustedDimensions.
            if (adjusted.Count > 0)
            {
                AddAdjustedDimensions(adjusted, LashingInputParams.LAYER_DIMENSION, tr, space, db);
                System.Diagnostics.Debug.WriteLine(
                    $"{LOG_PREFIX} Thêm {adjusted.Count} kích thước lỗ điều chỉnh.");
            }

            System.Diagnostics.Debug.WriteLine(
                $"{LOG_PREFIX} GenerateHoles THÀNH CÔNG: {innerIds.Count} lỗ, {adjusted.Count} điều chỉnh.");
            return innerIds;
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #12: Build grid points — Center mode dùng polygon centroid
        // ─────────────────────────────────────────────────────────────

        private static List<Point3d> BuildGridPoints(
            LashingInputParams p, Extents3d box, Polyline boundary)
        {
            var    pts = new List<Point3d>();
            double w   = box.MaxPoint.X - box.MinPoint.X;
            double h   = box.MaxPoint.Y - box.MinPoint.Y;

            switch (p.LocationMode)
            {
                case LashingLocationMode.PortSide:
                    // P1 = góc Dưới-Trái → phát triển lên trên và sang phải
                    for (double y = box.MinPoint.Y + p.OffsetY; y <= box.MaxPoint.Y - p.OffsetY / 2; y += p.SpacingY)
                        for (double x = box.MinPoint.X + p.OffsetX; x <= box.MaxPoint.X - p.OffsetX / 2; x += p.SpacingX)
                            pts.Add(new Point3d(x, y, 0));
                    break;

                case LashingLocationMode.StarBoard:
                    // P1 = góc Trên-Trái → phát triển xuống và sang phải
                    for (double y = box.MaxPoint.Y - p.OffsetY; y >= box.MinPoint.Y + p.OffsetY / 2; y -= p.SpacingY)
                        for (double x = box.MinPoint.X + p.OffsetX; x <= box.MaxPoint.X - p.OffsetX / 2; x += p.SpacingX)
                            pts.Add(new Point3d(x, y, 0));
                    break;

                case LashingLocationMode.Center:
                    // Gap #12: Dùng polygon centroid thay vì midpoint bounding box
                    Point3d centroid = AutoCADGeometryHelper.GetPolygonCentroid(boundary);
                    int     nX       = Math.Max(1, (int)Math.Floor((w - 2 * p.OffsetX) / p.SpacingX) + 1);
                    int     nY       = Math.Max(1, (int)Math.Floor((h - 2 * p.OffsetY) / p.SpacingY) + 1);
                    double  startX   = centroid.X - (nX - 1) / 2.0 * p.SpacingX;
                    double  startY   = centroid.Y - (nY - 1) / 2.0 * p.SpacingY;
                    for (int row = 0; row < nY; row++)
                        for (int col = 0; col < nX; col++)
                            pts.Add(new Point3d(startX + col * p.SpacingX, startY + row * p.SpacingY, 0));
                    break;
            }

            return pts;
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #9: Dimension chỉ cho lỗ bị điều chỉnh
        // Tương đương VBA: AddDimensionForAdjustedHole_Local
        // ─────────────────────────────────────────────────────────────

        private static void AddAdjustedDimensions(
            IList<AdjustedHole> adjusted,
            string              dimLayer,
            Transaction         tr,
            BlockTableRecord    space,
            Database            db)
        {
            EnsureLayerExists(dimLayer, db, tr);

            foreach (var h in adjusted)
            {
                double dist = h.Planned.DistanceTo(h.Actual);
                if (dist < TOLERANCE) continue;

                // AlignedDimension từ vị trí lưới → vị trí thực tế
                Point3d dimPt = new Point3d(
                    (h.Planned.X + h.Actual.X) / 2.0,
                    Math.Min(h.Planned.Y, h.Actual.Y) - 150 * (DIMSCALE / 25.0), 0);

                try
                {
                    var dim = new AlignedDimension(h.Planned, h.Actual, dimPt,
                        string.Empty, db.Dimstyle);
                    dim.Layer    = dimLayer;
                    dim.Dimscale = DIMSCALE;
                    space.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                }
                catch { /* Bỏ qua nếu Dimstyle chưa sẵn sàng */ }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        private static ObjectId DrawCircle(Point3d center, double radius, string layer,
            Transaction tr, BlockTableRecord space, Database db)
        {
            D.Log($"    DrawCircle: new Circle()...");
            var circle = new Circle();
            D.Log($"    DrawCircle: SetDatabaseDefaults...");
            circle.SetDatabaseDefaults(db);
            D.Log($"    DrawCircle: Center={center.X:F0},{center.Y:F0}...");
            circle.Center = center;
            D.Log($"    DrawCircle: Normal...");
            circle.Normal = Vector3d.ZAxis;
            D.Log($"    DrawCircle: Radius={radius}...");
            circle.Radius = radius;
            D.Log($"    DrawCircle: Layer={layer}...");
            circle.Layer  = layer;
            D.Log($"    DrawCircle: AppendEntity...");
            space.AppendEntity(circle);
            D.Log($"    DrawCircle: AddNewlyCreatedDBObject...");
            tr.AddNewlyCreatedDBObject(circle, true);
            D.Log($"    DrawCircle: DONE objectId={circle.ObjectId}");
            return circle.ObjectId;
        }

        private static bool HasKeepOutCollision(Point3d pt, double clearance, IList<Line3d> zones)
        {
            foreach (var z in zones)
                if (CollisionEngineService.DistanceToLine(pt, z) < clearance) return true;
            return false;
        }

        public static void EnsureLayerExists(string layerName, Database db, Transaction tr)
        {
            D.Log($"    EnsureLayerExists: GetObject LayerTable ForWrite for '{layerName}'...");
            var lt = tr.GetObject(db.LayerTableId, OpenMode.ForWrite) as LayerTable;
            D.Log($"    EnsureLayerExists: lt={lt != null}, Has={lt?.Has(layerName)}");
            if (lt == null || lt.Has(layerName)) { D.Log($"    EnsureLayerExists: '{layerName}' already exists or lt null, skip"); return; }
            D.Log($"    EnsureLayerExists: creating layer '{layerName}'...");
            var ltr = new LayerTableRecord { Name = layerName };
            D.Log($"    EnsureLayerExists: lt.Add...");
            lt.Add(ltr);
            D.Log($"    EnsureLayerExists: AddNewlyCreatedDBObject...");
            tr.AddNewlyCreatedDBObject(ltr, true);
            D.Log($"    EnsureLayerExists: '{layerName}' created OK");
        }
    }
}
