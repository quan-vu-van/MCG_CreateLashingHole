using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Utilities;

namespace MCG_CreateLashingHole.Services
{
    /// <summary>
    /// Phase 3 — Điền lỗ lashing vào khu vực đặc biệt giữa 2 hole hiện có (Start/End Anchor).
    /// Implements GAP HANDLING 3 cases từ VBA gốc:
    ///   Case 1: gap mod spacing ≈ 0  → fill đều tại spacing chuẩn
    ///   Case 2: remainder lớn        → thêm 1 slot, re-space đều
    ///   Case 3: remainder nhỏ        → điều chỉnh spacing để hấp thụ remainder
    /// Vẽ DUAL CIRCLE (inner + outer) khớp với BlockPackingService.
    /// </summary>
    public class GridGenerationService
    {
        private const string LOG_PREFIX = "[GridGeneration]";

        private readonly CollisionEngineService _collision;
        private const double TOLERANCE = 1.0;

        public GridGenerationService(CollisionEngineService collision)
        {
            _collision = collision;
        }

        // ─────────────────────────────────────────────────────────────
        // API chính
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Điền holes vào khoảng trống giữa startHole (Start Anchor) và endHole (End Anchor).
        /// Trả về danh sách ObjectId của innerCircle đã tạo.
        /// </summary>
        public List<ObjectId> RegenerateSpecialArea(
            Circle           startHole,
            Circle           endHole,
            Polyline         boundary,
            double           spacing,
            double           offset,
            bool             isVertical,
            LashingInputParams p,
            Transaction      tr,
            BlockTableRecord space)
        {
            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Bắt đầu RegenerateSpecialArea...");

            var created  = new List<ObjectId>();
            var db       = space.Database;
            double radius = startHole.Radius; // Dùng radius của Start Anchor

            // Đảm bảo layers tồn tại
            BlockPackingService.EnsureLayerExists(LashingInputParams.LAYER_INNER_HOLE,  db, tr);
            BlockPackingService.EnsureLayerExists(LashingInputParams.LAYER_OUTER_CLEAR, db, tr);

            Point3d startCenter = startHole.Center;
            Point3d endCenter   = endHole.Center;

            // Xác định trục động (moving) và trục cố định
            double startCoord = isVertical ? startCenter.Y : startCenter.X;
            double endCoord   = isVertical ? endCenter.Y   : endCenter.X;
            double fixedCoord = isVertical ? startCenter.X : startCenter.Y;

            double gap = Math.Abs(endCoord - startCoord);
            double dir = endCoord > startCoord ? 1.0 : -1.0; // Hướng từ start → end

            System.Diagnostics.Debug.WriteLine(
                $"{LOG_PREFIX} gap={gap:F0}mm, spacing={spacing:F0}mm, isVertical={isVertical}");

            // --- Check case where Start and End are too close ---
            if (gap < spacing * 0.5)
            {
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Khoảng cách quá nhỏ, bỏ qua.");
                return created;
            }

            // --- GAP HANDLING LOGIC (3 Cases từ VBA) ---
            double baseCoord   = Math.Min(startCoord, endCoord); // Start anchor tọa độ nhỏ hơn
            List<double> coords = CalculateIntermediateCoords(gap, spacing, baseCoord, dir);

            System.Diagnostics.Debug.WriteLine(
                $"{LOG_PREFIX} Sẽ tạo {coords.Count} lỗ trung gian.");

            // --- Generate intermediate points (không tạo lại Start/End Anchor) ---
            foreach (double coord in coords)
            {
                Point3d candidate = isVertical
                    ? new Point3d(fixedCoord, coord, 0)
                    : new Point3d(coord, fixedCoord, 0);

                if (!AutoCADGeometryHelper.IsInsidePolylineOrEdge(candidate, boundary))
                    continue;

                // Gap #4: Vẽ DUAL CIRCLE — khớp với BlockPackingService
                ObjectId innerId = DrawCircle(candidate, radius,
                    LashingInputParams.LAYER_INNER_HOLE, tr, space, db);
                DrawCircle(candidate, p.ClearanceRadius,
                    LashingInputParams.LAYER_OUTER_CLEAR, tr, space, db);

                created.Add(innerId);
            }

            System.Diagnostics.Debug.WriteLine(
                $"{LOG_PREFIX} RegenerateSpecialArea THÀNH CÔNG: {created.Count} lỗ tạo mới.");
            return created;
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #3: GAP HANDLING — 3 Cases từ VBA gốc
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tính toán tọa độ các lỗ trung gian theo 3 cases GAP HANDLING.
        /// Trả về danh sách tọa độ (theo trục động) không bao gồm start/end anchor.
        /// </summary>
        private static List<double> CalculateIntermediateCoords(
            double gap, double spacing, double baseCoord, double dir)
        {
            var coords = new List<double>();

            // Số khoảng hoàn chỉnh và phần dư
            int    nIntervals = (int)Math.Floor(gap / spacing);
            double remainder  = gap - nIntervals * spacing;

            if (nIntervals <= 0) return coords;

            double actualSpacing;

            if (Math.Abs(remainder) < TOLERANCE)
            {
                // Case 1: gap chia đều cho spacing → fill đúng vị trí chuẩn
                System.Diagnostics.Debug.WriteLine("[GridGeneration] GAP Case 1: Fill đều chuẩn.");
                actualSpacing = spacing;
            }
            else if (remainder > spacing * 0.5)
            {
                // Case 2: D > spacing → thêm 1 slot, re-space đều
                // VBA: "Case 2: D > spacing -> Insert points"
                nIntervals++;
                actualSpacing = gap / nIntervals;
                System.Diagnostics.Debug.WriteLine(
                    $"[GridGeneration] GAP Case 2: Thêm slot → actualSpacing={actualSpacing:F1}mm");
            }
            else
            {
                // Case 3: Remainder nhỏ → hấp thụ vào spacing
                // VBA: "Case 3: Reposition Li-1"
                actualSpacing = gap / nIntervals;
                System.Diagnostics.Debug.WriteLine(
                    $"[GridGeneration] GAP Case 3: Điều chỉnh spacing → actualSpacing={actualSpacing:F1}mm");
            }

            // --- Generate intermediate points (không bao gồm i=0 và i=nIntervals = anchors) ---
            for (int i = 1; i < nIntervals; i++)
            {
                double coord = baseCoord + i * actualSpacing;
                coords.Add(coord);
            }

            return coords;
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        private static ObjectId DrawCircle(Point3d center, double radius, string layer,
            Transaction tr, BlockTableRecord space, Database db)
        {
            var circle = new Circle();
            circle.SetDatabaseDefaults(db); // initialize entity với database defaults trước AppendEntity
            circle.Center = center;
            circle.Normal = Vector3d.ZAxis;
            circle.Radius = radius;
            circle.Layer  = layer;
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);
            return circle.ObjectId;
        }
    }
}
