using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRuntime = Autodesk.AutoCAD.Runtime;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Utilities;

namespace MCG_CreateLashingHole.Services
{
    /// <summary>
    /// AUDIT INTERFERENCE — port VBA mod_LashingHoleInterference.CheckLashingHole_InterferenceStructure:
    /// với mỗi outer clearance circle (layer Mechanical-AM_9) trong boundary, kiểm tra giao cắt
    /// với từng cấu kiện. Nếu va chạm → chèn block "LashingInterfereCloudMark" (copy từ Symbol.dwg
    /// qua ObjectDBX) tại tâm lỗ. Dùng Circle.IntersectWith giống VBA (độc lập với engine đặt lỗ).
    /// </summary>
    public class InterferenceAuditService
    {
        private const string LOG_PREFIX  = "[InterferenceAudit]";
        private const string BLOCK_NAME  = "LashingInterfereCloudMark";
        private const string SYMBOL_PATH = @"C:\CustomTools\Symbol.dwg";
        private const double TOLERANCE   = 0.0001;
        private const double R_TOL       = 0.5;

        /// <summary>Chạy audit. Trả về số lỗ va chạm; message mô tả kết quả.</summary>
        public int RunAudit(
            LashingInputParams p, Polyline boundary, IList<Entity> structures,
            Transaction tr, BlockTableRecord space, Database db, out string message)
        {
            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Bắt đầu RunAudit...");

            // 1) Đảm bảo block cảnh báo tồn tại (copy từ Symbol.dwg nếu cần)
            bool     blockOk = EnsureInterferenceBlock(db, tr, out string blkMsg);
            ObjectId blkId   = ObjectId.Null;
            if (blockOk)
            {
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt != null && bt.Has(BLOCK_NAME)) blkId = bt[BLOCK_NAME];
                else blockOk = false;
            }

            // 2) Thu thập outer clearance circle trong boundary
            var outerCircles = CollectOuterCircles(p, boundary, tr, space);
            if (outerCircles.Count == 0)
            {
                message = "AUDIT: Không tìm thấy outer clearance circle nào trong boundary.";
                return 0;
            }
            if (structures == null || structures.Count == 0)
            {
                message = "AUDIT: Chưa chọn cấu kiện để kiểm tra va chạm.";
                return 0;
            }

            // 3) Kiểm tra giao cắt từng lỗ × cấu kiện
            int marks = 0;
            foreach (var circle in outerCircles)
            {
                bool collided = false;
                foreach (var s in structures)
                {
                    if (CircleHitsStructure(circle, s)) { collided = true; break; }
                }
                if (!collided) continue;

                marks++;
                if (blockOk)
                {
                    var bref = new BlockReference(circle.Center, blkId) { Layer = LashingInputParams.LAYER_OUTER_CLEAR };
                    space.AppendEntity(bref);
                    tr.AddNewlyCreatedDBObject(bref, true);
                }
            }

            string blkNote = blockOk ? "" : $" (⚠ {blkMsg} — không chèn được cloud mark)";
            message = marks == 0
                ? "AUDIT: Không có lỗ nào va chạm với cấu kiện."
                : $"AUDIT: Phát hiện {marks} lỗ va chạm{blkNote}.";

            System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} RunAudit xong: {marks} va chạm.");
            return marks;
        }

        // ─────────────────────────────────────────────────────────────
        // Giao cắt circle × structure (port logic IntersectWith của VBA)
        // ─────────────────────────────────────────────────────────────
        private static bool CircleHitsStructure(Circle circle, Entity structure)
        {
            var pts = new Point3dCollection();
            try { circle.IntersectWith(structure, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero); }
            catch { return false; }

            if (pts.Count >= 3) return true;   // VBA uBound>5: cắt xuyên nhiều điểm → va chạm chắc chắn

            if (pts.Count == 2)                // VBA uBound=5: 2 giao điểm → kiểm tra chord ăn sâu
            {
                var mid = new Point3d((pts[0].X + pts[1].X) / 2.0,
                                      (pts[0].Y + pts[1].Y) / 2.0, 0);
                double dist = Math.Sqrt(Math.Pow(circle.Center.X - mid.X, 2) +
                                        Math.Pow(circle.Center.Y - mid.Y, 2));
                return dist <= circle.Radius - TOLERANCE;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────
        // Thu thập outer clearance circle (layer AM_9, bán kính ≈ clearance) trong boundary
        // ─────────────────────────────────────────────────────────────
        private static List<Circle> CollectOuterCircles(
            LashingInputParams p, Polyline boundary, Transaction tr, BlockTableRecord space)
        {
            var    result      = new List<Circle>();
            double rOuter       = p.ClearanceRadius;
            var    circleClass  = AcRuntime.RXObject.GetClass(typeof(Circle));

            foreach (ObjectId id in space)
            {
                if (!id.IsValid || id.IsErased) continue;
                if (!id.ObjectClass.IsDerivedFrom(circleClass)) continue; // proxy-safe

                Circle c;
                try { c = tr.GetObject(id, OpenMode.ForRead) as Circle; }
                catch { continue; }
                if (c == null || c.IsErased) continue;

                if (c.Layer == LashingInputParams.LAYER_OUTER_CLEAR &&
                    Math.Abs(c.Radius - rOuter) < R_TOL &&
                    AutoCADGeometryHelper.IsInsidePolylineOrEdge(c.Center, boundary))
                {
                    result.Add(c);
                }
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // Đảm bảo block cảnh báo tồn tại — port DbxCopyBlock (ObjectDBX)
        // ─────────────────────────────────────────────────────────────
        private static bool EnsureInterferenceBlock(Database db, Transaction tr, out string message)
        {
            message = "";
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt != null && bt.Has(BLOCK_NAME)) return true;

            if (!File.Exists(SYMBOL_PATH))
            {
                message = $"Không thấy {SYMBOL_PATH}";
                return false;
            }

            try
            {
                using (var sideDb = new Database(false, true))
                {
                    sideDb.ReadDwgFile(SYMBOL_PATH, FileShare.Read, true, null);

                    var ids = new ObjectIdCollection();
                    using (var st = sideDb.TransactionManager.StartTransaction())
                    {
                        var sbt = st.GetObject(sideDb.BlockTableId, OpenMode.ForRead) as BlockTable;
                        if (sbt != null && sbt.Has(BLOCK_NAME)) ids.Add(sbt[BLOCK_NAME]);
                        st.Commit();
                    }

                    if (ids.Count == 0)
                    {
                        message = $"Symbol.dwg không chứa block '{BLOCK_NAME}'";
                        return false;
                    }

                    var map = new IdMapping();
                    db.WblockCloneObjects(ids, db.BlockTableId, map,
                        DuplicateRecordCloning.Ignore, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                message = $"Lỗi copy block: {ex.Message}";
                return false;
            }
        }
    }
}
