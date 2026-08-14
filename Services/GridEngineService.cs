using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Utilities;

namespace MCG_CreateLashingHole.Services
{
    /// <summary>
    /// PHASE 1 — Grid engine port trung thành từ VBA mod_MainLashingHole_V3:
    ///   GenerateCentralPoints + GenerateLineOfPoints + Adjust*/FindSafePoint*.
    /// Sinh lưới bằng cách "gieo mầm" từ effective-center rồi mọc 4 hướng (Center mode)
    /// hoặc từ P1 (PortSide/StarBoard), mỗi điểm né va chạm bằng retreat-and-gap,
    /// tái phân bố Li-1 khi khoảng cách tới mép quá nhỏ.
    /// Va chạm dùng CollisionEngineService (RAM), không vẽ circle tạm như VBA.
    /// </summary>
    public class GridEngineService
    {
        private const string LOG_PREFIX = "[GridEngine]";

        // === Hằng số — khớp CHÍNH XÁC với VBA ===
        private const double EPS                          = 0.000001;   // DEFAULT_EPSILON
        private const double MIN_DIST_FACTOR_AFTER_RETREAT = 0.25;
        private const double RETREAT_STEP_MM               = 5.0;
        private const double MIN_EDGE_DISTANCE_FOR_GAP     = 200.0;
        private const int    MAX_ITER_PER_LINE            = 100;        // MAX_POINT_GENERATION_ITERATIONS_PER_LINE
        private const double STD_MAX_RETREAT_ADDON        = 20.0;       // DEFAULT_STD_MAX_RETREAT_ADDON

        private readonly CollisionEngineService _collision;

        // Context cho 1 lần chạy (single-threaded AutoCAD) — tránh truyền qua từng hàm
        private IList<Entity> _structures;
        private IList<Line3d> _keepOut;
        private double        _clearance;
        private Polyline      _boundary;

        public GridEngineService(CollisionEngineService collision)
        {
            _collision = collision;
        }

        // ─────────────────────────────────────────────────────────────
        // API chính — map LocationMode → P1/P2 + start option, rồi gọi core
        // ─────────────────────────────────────────────────────────────
        public List<Point3d> GenerateGrid(
            LashingInputParams p,
            Polyline           boundary,
            IList<Entity>      structures,
            IList<Line3d>      keepOutZones)
        {
            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Bắt đầu GenerateGrid, mode={p.LocationMode}");

            _structures = structures ?? new List<Entity>();
            _keepOut    = keepOutZones ?? new List<Line3d>();
            _clearance  = p.ClearanceRadius;
            _boundary   = boundary;

            // Bounding box tuyệt đối tính từ đỉnh polyline (khớp VBA B_abs_min/max)
            // Full bbox — dùng làm GIỚI HẠN phát triển lưới (B_abs của VBA)
            var (min, max) = AutoCADGeometryHelper.GetSmartRectFromPolyline(boundary);

            // Mép tham chiếu P1/P2 theo nguyên tắc CẠNH DÀI 1500mm (VBA), fallback full bbox
            AutoCADGeometryHelper.GetSmartRectEdges(boundary, min, max,
                out double rMinX, out double rMaxX, out double rMinY, out double rMaxY);

            // Xác định P1/P2 + start option + effective center theo LocationMode
            Point3d p1, p2, effCenter;
            string  startOption;

            switch (p.LocationMode)
            {
                case LashingLocationMode.StarBoard:
                    // P1 = Trên-Trái, P2 = Dưới-Phải
                    p1 = new Point3d(rMinX, rMaxY, 0);
                    p2 = new Point3d(rMaxX, rMinY, 0);
                    effCenter   = new Point3d((rMinX + rMaxX) / 2.0, (rMinY + rMaxY) / 2.0, 0);
                    startOption = "P1";
                    break;

                case LashingLocationMode.Center:
                    // Cải tiến kiến trúc mới: dùng polygon centroid làm effective center
                    p1 = new Point3d(rMinX, rMinY, 0);
                    p2 = new Point3d(rMaxX, rMaxY, 0);
                    effCenter   = AutoCADGeometryHelper.GetPolygonCentroid(boundary);
                    startOption = "Center";
                    break;

                default: // PortSide
                    // P1 = Dưới-Trái, P2 = Trên-Phải
                    p1 = new Point3d(rMinX, rMinY, 0);
                    p2 = new Point3d(rMaxX, rMaxY, 0);
                    effCenter   = new Point3d((rMinX + rMaxX) / 2.0, (rMinY + rMaxY) / 2.0, 0);
                    startOption = "P1";
                    break;
            }

            return GenerateGrid(p, boundary, structures, keepOutZones,
                p1, p2, startOption, effCenter, skipCenterAdjust: false);
        }

        /// <summary>
        /// Overload với P1/P2/start-option/effective-center TƯỜNG MINH — dùng cho flow tuần tự
        /// MCG_LH_RUN (manual pick P1-P2, start P1/P2/Center, effective center do user chọn).
        /// </summary>
        public List<Point3d> GenerateGrid(
            LashingInputParams p, Polyline boundary,
            IList<Entity> structures, IList<Line3d> keepOutZones,
            Point3d p1, Point3d p2, string startOption, Point3d effCenter, bool skipCenterAdjust)
        {
            _structures = structures ?? new List<Entity>();
            _keepOut    = keepOutZones ?? new List<Line3d>();
            _clearance  = p.ClearanceRadius;
            _boundary   = boundary;

            var (min, max) = AutoCADGeometryHelper.GetSmartRectFromPolyline(boundary);
            double stdMaxRetreat = 2.0 * p.ClearanceRadius + STD_MAX_RETREAT_ADDON;

            var result = GenerateCentralPoints(
                startOption, p1, p2, effCenter, p,
                min.X, max.X, min.Y, max.Y, stdMaxRetreat, skipCenterAdjust);

            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} GenerateGrid xong: {result.Count} điểm.");
            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // Special area — port RegenerateLineFromSeed_Special
        // Mọc 1 HÀNG lỗ từ seed → endAnchor tại spacing, né va chạm bằng retreat.
        // KHÁC GenerateLineOfPoints: KHÔNG có gap-handling Case1/2/3 (bám đúng VBA Phase 3).
        // ─────────────────────────────────────────────────────────────
        public List<Point3d> RegenerateSeedLineSpecial(
            LashingInputParams p, Polyline boundary,
            IList<Entity> structures, IList<Line3d> keepOut,
            Point3d seedPt, Point3d endPt,
            double spacing, int genDir, bool axisIsX,
            double boundaryMin, double boundaryMax)
        {
            _structures = structures ?? new List<Entity>();
            _keepOut    = keepOut ?? new List<Line3d>();
            _clearance  = p.ClearanceRadius;
            _boundary   = boundary;
            double stdMaxRetreat = 2.0 * p.ClearanceRadius + STD_MAX_RETREAT_ADDON;

            var dict = new Dictionary<string, Point3d>();
            double fixedCoord = axisIsX ? seedPt.Y : seedPt.X;
            double seedVary   = axisIsX ? seedPt.X : seedPt.Y;
            double endVary    = axisIsX ? endPt.X  : endPt.Y;

            AddPointVary(dict, seedVary, fixedCoord, axisIsX);
            double lastValid = seedVary;

            // --- điểm trung gian ---
            int loop = 0;
            while (loop < MAX_ITER_PER_LINE)
            {
                loop++;
                double cand = lastValid + genDir * spacing;

                if ((genDir == 1 && cand > endVary - EPS) || (genDir == -1 && cand < endVary + EPS)) break;
                if (cand < boundaryMin || cand > boundaryMax) break;

                if (Collides(cand, fixedCoord, axisIsX))
                {
                    double tmp = cand;
                    if (AdjustPointAlongAxis(ref tmp, fixedCoord, axisIsX, genDir,
                        stdMaxRetreat, lastValid, spacing, boundaryMin, boundaryMax, false))
                        cand = tmp;
                    else break;
                }

                if ((genDir == 1 && cand > endVary - EPS) || (genDir == -1 && cand < endVary + EPS)) break;

                AddPointVary(dict, cand, fixedCoord, axisIsX);
                lastValid = cand;
            }

            // --- End anchor (isSeedAdj = true) ---
            if (endVary >= boundaryMin && endVary <= boundaryMax)
            {
                double li = endVary;
                bool ok = true;
                if (Collides(li, fixedCoord, axisIsX))
                {
                    double tmp = li;
                    if (AdjustPointAlongAxis(ref tmp, fixedCoord, axisIsX, genDir,
                        stdMaxRetreat, lastValid, spacing, boundaryMin, boundaryMax, true))
                        li = tmp;
                    else ok = false;
                }
                if (ok && Math.Abs(li - lastValid) > spacing * MIN_DIST_FACTOR_AFTER_RETREAT)
                    AddPointVary(dict, li, fixedCoord, axisIsX);
            }

            return dict.Values.ToList();
        }

        // ─────────────────────────────────────────────────────────────
        // Core — port GenerateCentralPoints
        // ─────────────────────────────────────────────────────────────
        private List<Point3d> GenerateCentralPoints(
            string startOption, Point3d p1, Point3d p2, Point3d effCenter,
            LashingInputParams p,
            double bMinX, double bMaxX, double bMinY, double bMaxY, double stdMaxRetreat,
            bool skipCenterAdjust = false)
        {
            var output = new List<Point3d>();

            // 1) Điều chỉnh effective center để không va chạm (AdjustInitialCenter8Dir).
            //    skipCenterAdjust = user đã pick effective center hợp lệ → dùng nguyên (VBA skipInitialAdjustment)
            Point3d origCenter = effCenter;
            Point3d adjCenter  = effCenter;
            if (!skipCenterAdjust && !AdjustInitialCenter8Dir(ref adjCenter, bMinX, bMaxX, bMinY, bMaxY))
            {
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Không tìm được effective center an toàn → trả về rỗng.");
                return output;
            }

            double effColX = adjCenter.X;
            double effRowY = adjCenter.Y;

            var dict = new Dictionary<string, Point3d>();

            double spacingX = p.SpacingX, spacingY = p.SpacingY;
            double offsetX  = p.OffsetX,  offsetY  = p.OffsetY;

            if (startOption == "Center")
            {
                AddPointXY(dict, effColX, effRowY);

                // Col Up (Y tăng)
                GenerateLineOfPoints(dict, adjCenter, new Point3d(effColX, bMaxY - offsetY, 0),
                    spacingY, +1, axisIsX: false, fixedCoordVal: effColX,
                    boundaryMin: bMinY, boundaryMax: bMaxY,
                    maxRetreatForStartAnchor: stdMaxRetreat, prevCoordForStartAnchorAdj: origCenter.Y,
                    adjustStartAnchor: true, adjustEndAnchor: true, standardMaxRetreat: stdMaxRetreat);

                // Col Down (Y giảm)
                GenerateLineOfPoints(dict, adjCenter, new Point3d(effColX, bMinY + offsetY, 0),
                    spacingY, -1, axisIsX: false, fixedCoordVal: effColX,
                    boundaryMin: bMinY, boundaryMax: bMaxY,
                    maxRetreatForStartAnchor: stdMaxRetreat, prevCoordForStartAnchorAdj: origCenter.Y,
                    adjustStartAnchor: true, adjustEndAnchor: true, standardMaxRetreat: stdMaxRetreat);

                // Row Right (X tăng)
                GenerateLineOfPoints(dict, adjCenter, new Point3d(bMaxX - offsetX, effRowY, 0),
                    spacingX, +1, axisIsX: true, fixedCoordVal: effRowY,
                    boundaryMin: bMinX, boundaryMax: bMaxX,
                    maxRetreatForStartAnchor: stdMaxRetreat, prevCoordForStartAnchorAdj: origCenter.X,
                    adjustStartAnchor: true, adjustEndAnchor: true, standardMaxRetreat: stdMaxRetreat);

                // Row Left (X giảm)
                GenerateLineOfPoints(dict, adjCenter, new Point3d(bMinX + offsetX, effRowY, 0),
                    spacingX, -1, axisIsX: true, fixedCoordVal: effRowY,
                    boundaryMin: bMinX, boundaryMax: bMaxX,
                    maxRetreatForStartAnchor: stdMaxRetreat, prevCoordForStartAnchorAdj: origCenter.X,
                    adjustStartAnchor: true, adjustEndAnchor: true, standardMaxRetreat: stdMaxRetreat);
            }
            else if (startOption == "P2")
            {
                int dirX = Math.Abs(p2.X - p1.X) < EPS ? 1 : Math.Sign(p2.X - p1.X);
                int dirY = Math.Abs(p2.Y - p1.Y) < EPS ? 1 : Math.Sign(p2.Y - p1.Y);

                // P2 Col — từ P2 lùi về P1 (khớp VBA case "P2")
                GenerateLineOfPoints(dict,
                    new Point3d(effColX, p2.Y - dirY * offsetY, 0),
                    new Point3d(effColX, p1.Y + dirY * offsetY, 0),
                    spacingY, -dirY, axisIsX: false, fixedCoordVal: effColX,
                    boundaryMin: bMinY, boundaryMax: bMaxY,
                    maxRetreatForStartAnchor: stdMaxRetreat, prevCoordForStartAnchorAdj: p2.Y,
                    adjustStartAnchor: false, adjustEndAnchor: false, standardMaxRetreat: stdMaxRetreat);

                // P2 Row
                GenerateLineOfPoints(dict,
                    new Point3d(p2.X - dirX * offsetX, effRowY, 0),
                    new Point3d(p1.X + dirX * offsetX, effRowY, 0),
                    spacingX, -dirX, axisIsX: true, fixedCoordVal: effRowY,
                    boundaryMin: bMinX, boundaryMax: bMaxX,
                    maxRetreatForStartAnchor: stdMaxRetreat, prevCoordForStartAnchorAdj: p2.X,
                    adjustStartAnchor: false, adjustEndAnchor: false, standardMaxRetreat: stdMaxRetreat);
            }
            else // "P1"
            {
                int dirX = Math.Abs(p2.X - p1.X) < EPS ? 1 : Math.Sign(p2.X - p1.X);
                int dirY = Math.Abs(p2.Y - p1.Y) < EPS ? 1 : Math.Sign(p2.Y - p1.Y);

                // P1 Col
                GenerateLineOfPoints(dict,
                    new Point3d(effColX, p1.Y + dirY * offsetY, 0),
                    new Point3d(effColX, p2.Y - dirY * offsetY, 0),
                    spacingY, dirY, axisIsX: false, fixedCoordVal: effColX,
                    boundaryMin: bMinY, boundaryMax: bMaxY,
                    maxRetreatForStartAnchor: stdMaxRetreat, prevCoordForStartAnchorAdj: p1.Y,
                    adjustStartAnchor: false, adjustEndAnchor: false, standardMaxRetreat: stdMaxRetreat);

                // P1 Row
                GenerateLineOfPoints(dict,
                    new Point3d(p1.X + dirX * offsetX, effRowY, 0),
                    new Point3d(p2.X - dirX * offsetX, effRowY, 0),
                    spacingX, dirX, axisIsX: true, fixedCoordVal: effRowY,
                    boundaryMin: bMinX, boundaryMax: bMaxX,
                    maxRetreatForStartAnchor: stdMaxRetreat, prevCoordForStartAnchorAdj: p1.X,
                    adjustStartAnchor: false, adjustEndAnchor: false, standardMaxRetreat: stdMaxRetreat);
            }

            if (dict.Count == 0) return output;

            // 2) Tổng hợp toạ độ X/Y duy nhất từ các điểm trên trục cơ sở
            var uniqueX = new Dictionary<string, double>();
            var uniqueY = new Dictionary<string, double>();
            var xOnRow  = new Dictionary<string, double>();
            var yOnCol  = new Dictionary<string, double>();

            foreach (var pt in dict.Values)
            {
                string xk = CoordKey(pt.X), yk = CoordKey(pt.Y);
                if (!uniqueX.ContainsKey(xk)) uniqueX[xk] = pt.X;
                if (!uniqueY.ContainsKey(yk)) uniqueY[yk] = pt.Y;

                if (startOption != "Center")
                {
                    if (Math.Abs(pt.Y - effRowY) < EPS && !xOnRow.ContainsKey(xk)) xOnRow[xk] = pt.X;
                    if (Math.Abs(pt.X - effColX) < EPS && !yOnCol.ContainsKey(yk)) yOnCol[yk] = pt.Y;
                }
            }

            IEnumerable<double> finalX = startOption == "Center" ? uniqueX.Values : xOnRow.Values;
            IEnumerable<double> finalY = startOption == "Center" ? uniqueY.Values : yOnCol.Values;

            var fx = finalX.ToList();
            var fy = finalY.ToList();
            if (fx.Count == 0 || fy.Count == 0) return output;

            // 3) Tạo lưới giao điểm, lọc điểm nằm trong boundary
            var grid = new Dictionary<string, Point3d>();
            foreach (double gx in fx)
                foreach (double gy in fy)
                {
                    var fp = new Point3d(gx, gy, 0);
                    if (AutoCADGeometryHelper.IsInsidePolylineOrEdge(fp, _boundary))
                    {
                        string key = CoordKey(gx) + "|" + CoordKey(gy);
                        if (!grid.ContainsKey(key)) grid[key] = fp;
                    }
                }

            output.AddRange(grid.Values);
            return output;
        }

        // ─────────────────────────────────────────────────────────────
        // Port GenerateLineOfPoints — mọc điểm dọc 1 trục từ start → end
        // ─────────────────────────────────────────────────────────────
        private void GenerateLineOfPoints(
            Dictionary<string, Point3d> dict,
            Point3d startAnchorIn, Point3d endAnchorIn,
            double spacing, int genDirOriginal, bool axisIsX, double fixedCoordVal,
            double boundaryMin, double boundaryMax,
            double maxRetreatForStartAnchor, double prevCoordForStartAnchorAdj,
            bool adjustStartAnchor, bool adjustEndAnchor, double standardMaxRetreat)
        {
            double startVary = axisIsX ? startAnchorIn.X : startAnchorIn.Y;
            double endVary   = axisIsX ? endAnchorIn.X   : endAnchorIn.Y;

            int genDir = genDirOriginal;
            if (genDir == 0) genDir = startVary < endVary ? 1 : -1;

            // --- Trường hợp Start và End quá gần ---
            if (Math.Abs(startVary - endVary) < spacing / 2.0)
            {
                double sA = startVary;
                if ((sA > boundaryMax - EPS && genDir == 1) || (sA < boundaryMin + EPS && genDir == -1)) return;

                if (adjustStartAnchor && Collides(sA, fixedCoordVal, axisIsX))
                {
                    double tmp = sA;
                    if (AdjustPointAlongAxis(ref tmp, fixedCoordVal, axisIsX, genDir,
                        maxRetreatForStartAnchor, prevCoordForStartAnchorAdj, spacing, boundaryMin, boundaryMax, true))
                        sA = tmp;
                    else return;
                }

                if ((sA > boundaryMax - EPS && genDir == 1) || (sA < boundaryMin + EPS && genDir == -1)) return;
                AddPointVary(dict, sA, fixedCoordVal, axisIsX);
                return;
            }

            // --- Xử lý Start Anchor ---
            double startAnchor = startVary;
            if ((genDir == 1 && startAnchor > boundaryMax - EPS) ||
                (genDir == -1 && startAnchor < boundaryMin + EPS)) return;

            if (adjustStartAnchor && Collides(startAnchor, fixedCoordVal, axisIsX))
            {
                double tmp = startAnchor;
                if (AdjustPointAlongAxis(ref tmp, fixedCoordVal, axisIsX, genDir,
                    maxRetreatForStartAnchor, prevCoordForStartAnchorAdj, spacing, boundaryMin, boundaryMax, true))
                    startAnchor = tmp;
                else return;
            }

            if ((genDir == 1 && startAnchor > endVary + EPS) || (genDir == -1 && startAnchor < endVary - EPS)) return;
            if ((genDir == 1 && startAnchor > boundaryMax - EPS) || (genDir == -1 && startAnchor < boundaryMin + EPS)) return;

            AddPointVary(dict, startAnchor, fixedCoordVal, axisIsX);
            double currentVary = startAnchor;
            double LiMinus1    = currentVary;

            // --- Sinh điểm trung gian ---
            int    loop             = 0;
            double currentMaxRetreat = standardMaxRetreat;

            while (loop < MAX_ITER_PER_LINE)
            {
                loop++;
                double cand = currentVary + genDir * spacing;

                if ((genDir == 1 && cand > endVary - EPS) || (genDir == -1 && cand < endVary + EPS)) break;
                if (cand < boundaryMin || cand > boundaryMax) break;

                if (Collides(cand, fixedCoordVal, axisIsX))
                {
                    double tmp = cand;
                    if (AdjustPointAlongAxis(ref tmp, fixedCoordVal, axisIsX, genDir,
                        currentMaxRetreat, currentVary, spacing, boundaryMin, boundaryMax, false))
                        cand = tmp;
                    else break;
                }

                if (Math.Abs(cand - currentVary) > spacing * 1.1 && loop > 1) break;
                if ((genDir == 1 && cand > endVary - EPS) || (genDir == -1 && cand < endVary + EPS)) break;
                if ((genDir == 1 && cand < currentVary + EPS) || (genDir == -1 && cand > currentVary - EPS)) break;

                AddPointVary(dict, cand, fixedCoordVal, axisIsX);
                currentVary = cand;
                LiMinus1    = currentVary;
            }

            // --- Xử lý End Anchor (Li) ---
            double Li = endVary;

            if (((genDir == 1 && Li < startAnchor + EPS) || (genDir == -1 && Li > startAnchor - EPS)) &&
                Math.Abs(startAnchor - startVary) > EPS) return;
            if (Li < boundaryMin || Li > boundaryMax) return;

            if (adjustEndAnchor && Collides(Li, fixedCoordVal, axisIsX))
            {
                double tmp = Li;
                if (AdjustPointAlongAxis(ref tmp, fixedCoordVal, axisIsX, genDir,
                    standardMaxRetreat, LiMinus1, spacing, boundaryMin, boundaryMax, true))
                    Li = tmp;
                else return;
            }

            if ((genDir == 1 && Li < LiMinus1 + EPS) || (genDir == -1 && Li > LiMinus1 - EPS)) return;
            if (Li < boundaryMin || Li > boundaryMax) return;

            // --- GAP HANDLING ---
            double distToLi = Math.Abs(Li - LiMinus1);

            if (distToLi > spacing + EPS) // Case 2: D > spacing → chèn thêm điểm
            {
                double lastGap = LiMinus1;
                loop = 0;
                while (loop < MAX_ITER_PER_LINE)
                {
                    loop++;
                    double gp = lastGap + genDir * spacing;

                    if ((genDir == 1 && gp > Li - spacing + EPS) || (genDir == -1 && gp < Li + spacing - EPS)) break;
                    if ((genDir == 1 && gp >= Li - EPS) || (genDir == -1 && gp <= Li + EPS)) break;

                    if (Collides(gp, fixedCoordVal, axisIsX))
                    {
                        double tmp = gp;
                        if (AdjustPointAlongAxis(ref tmp, fixedCoordVal, axisIsX, genDir,
                            currentMaxRetreat, lastGap, spacing, boundaryMin, boundaryMax, false))
                            gp = tmp;
                        else break;
                    }

                    if ((genDir == 1 && gp >= Li - EPS) || (genDir == -1 && gp <= Li + EPS)) break;
                    if ((genDir == 1 && gp < lastGap + EPS) || (genDir == -1 && gp > lastGap - EPS)) break;

                    AddPointVary(dict, gp, fixedCoordVal, axisIsX);
                    lastGap  = gp;
                    LiMinus1 = gp;
                }
                distToLi = Math.Abs(Li - LiMinus1);
            }

            // Xử lý Li cuối cùng
            if (distToLi >= MIN_EDGE_DISTANCE_FOR_GAP - EPS && distToLi <= spacing + EPS)
            {
                // Case 1
                AddPointVary(dict, Li, fixedCoordVal, axisIsX);
            }
            else if (distToLi < MIN_EDGE_DISTANCE_FOR_GAP - EPS && distToLi > EPS)
            {
                // Case 3: Reposition Li-1
                double repo    = Li - genDir * MIN_EDGE_DISTANCE_FOR_GAP;
                string origKey = PointKey(LiMinus1, fixedCoordVal, axisIsX);
                bool   success = false;

                if (Collides(repo, fixedCoordVal, axisIsX))
                {
                    double tmp        = repo;
                    double prevForRepo = Li - genDir * (MIN_EDGE_DISTANCE_FOR_GAP + spacing);
                    double maxR        = Math.Max(MIN_EDGE_DISTANCE_FOR_GAP, standardMaxRetreat / 2.0);
                    if (maxR < RETREAT_STEP_MM && MIN_EDGE_DISTANCE_FOR_GAP >= RETREAT_STEP_MM)
                        maxR = MIN_EDGE_DISTANCE_FOR_GAP;

                    if (AdjustPointAlongAxis(ref tmp, fixedCoordVal, axisIsX, genDir,
                        maxR, prevForRepo, spacing, boundaryMin, boundaryMax, true))
                    {
                        repo = tmp;
                        if (!((genDir == 1 && repo >= Li - EPS) || (genDir == -1 && repo <= Li + EPS)))
                        {
                            AddPointVary(dict, repo, fixedCoordVal, axisIsX);
                            success = true;
                        }
                    }
                }
                else
                {
                    if (!((genDir == 1 && repo >= Li - EPS) || (genDir == -1 && repo <= Li + EPS)))
                    {
                        AddPointVary(dict, repo, fixedCoordVal, axisIsX);
                        success = true;
                    }
                }

                if (success)
                {
                    string repoKey = PointKey(repo, fixedCoordVal, axisIsX);
                    if (dict.ContainsKey(origKey) && origKey != repoKey) dict.Remove(origKey);
                }
                AddPointVary(dict, Li, fixedCoordVal, axisIsX);
            }
            else
            {
                // Case còn lại (distToLi ≈ 0 hoặc lớn hơn spacing sau chèn) → vẫn thêm Li
                AddPointVary(dict, Li, fixedCoordVal, axisIsX);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Port AdjustPointHorizontal/Vertical (gộp làm 1 — lùi ngược genDir)
        // ─────────────────────────────────────────────────────────────
        private bool AdjustPointAlongAxis(
            ref double coord, double fixedCoordVal, bool axisIsX, int genDir,
            double maxRetreat, double prevValid, double spacing,
            double bMin, double bMax, bool isSeedAdj)
        {
            int retreatDir = -genDir;
            int steps      = (int)(maxRetreat / RETREAT_STEP_MM);

            for (int i = 1; i <= steps; i++)
            {
                double nv = coord + retreatDir * RETREAT_STEP_MM * i;
                if (nv < bMin || nv > bMax) break;

                if (!isSeedAdj)
                {
                    // retreatDir luôn = -genDir → chỉ nhánh này chạy (khớp VBA)
                    if (genDir == 1  && nv < prevValid + spacing * MIN_DIST_FACTOR_AFTER_RETREAT) break;
                    if (genDir == -1 && nv > prevValid - spacing * MIN_DIST_FACTOR_AFTER_RETREAT) break;
                }

                if (!Collides(nv, fixedCoordVal, axisIsX))
                {
                    coord = nv;
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────
        // Port AdjustInitialCenter8Dir + FindSafePointAlongDirection
        // ─────────────────────────────────────────────────────────────
        private bool AdjustInitialCenter8Dir(
            ref Point3d center, double bMinX, double bMaxX, double bMinY, double bMaxY)
        {
            if (!_collision.HasAnyCollision(center, _clearance, _structures, _keepOut)) return true;

            double inv = 1.0 / Math.Sqrt(2.0);
            var dirs = new[]
            {
                (0.0, 1.0), (inv, inv), (1.0, 0.0), (inv, -inv),
                (0.0, -1.0), (-inv, -inv), (-1.0, 0.0), (-inv, inv)
            };

            double maxDist = 2.0 * _clearance + 20.0;
            foreach (var (dx, dy) in dirs)
            {
                Point3d? res = FindSafePointAlongDirection(center, dx, dy, 5.0, maxDist,
                    bMinX, bMaxX, bMinY, bMaxY);
                if (res.HasValue) { center = res.Value; return true; }
            }
            return false;
        }

        private Point3d? FindSafePointAlongDirection(
            Point3d origin, double dx, double dy, double stepInc, double maxDist,
            double bMinX, double bMaxX, double bMinY, double bMaxY)
        {
            int steps = (int)(maxDist / stepInc);
            for (int i = 1; i <= steps; i++)
            {
                double x = origin.X + dx * stepInc * i;
                double y = origin.Y + dy * stepInc * i;
                if (x < bMinX || x > bMaxX || y < bMinY || y > bMaxY) break;

                var pt = new Point3d(x, y, 0);
                if (!AutoCADGeometryHelper.IsInsidePolylineOrEdge(pt, _boundary)) break;
                if (!_collision.HasAnyCollision(pt, _clearance, _structures, _keepOut)) return pt;
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers: collision predicate + dict key (khớp VBA GetCoordKey "0.000")
        // ─────────────────────────────────────────────────────────────
        private bool Collides(double vary, double fixedCoordVal, bool axisIsX)
        {
            Point3d pt = axisIsX ? new Point3d(vary, fixedCoordVal, 0)
                                 : new Point3d(fixedCoordVal, vary, 0);
            return _collision.HasAnyCollision(pt, _clearance, _structures, _keepOut);
        }

        private static string CoordKey(double c)
            => c.ToString("0.000", CultureInfo.InvariantCulture);

        private static string PointKey(double vary, double fixedCoordVal, bool axisIsX)
            => axisIsX ? CoordKey(vary) + "|" + CoordKey(fixedCoordVal)
                       : CoordKey(fixedCoordVal) + "|" + CoordKey(vary);

        private static void AddPointVary(
            Dictionary<string, Point3d> dict, double vary, double fixedCoordVal, bool axisIsX)
        {
            Point3d pt = axisIsX ? new Point3d(vary, fixedCoordVal, 0)
                                 : new Point3d(fixedCoordVal, vary, 0);
            string key = PointKey(vary, fixedCoordVal, axisIsX);
            if (!dict.ContainsKey(key)) dict[key] = pt;
        }

        private static void AddPointXY(Dictionary<string, Point3d> dict, double x, double y)
        {
            string key = CoordKey(x) + "|" + CoordKey(y);
            if (!dict.ContainsKey(key)) dict[key] = new Point3d(x, y, 0);
        }
    }
}
