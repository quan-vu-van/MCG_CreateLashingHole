using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
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

            // 2) Thu thập tâm outer clearance circle trong boundary — QUÉT CẢ trong block (không phá block)
            var outerCenters = AutoCADGeometryHelper.CollectCircleCentersWorld(
                tr, space, boundary, LashingInputParams.LAYER_OUTER_CLEAR, p.ClearanceRadius, R_TOL);
            if (outerCenters.Count == 0)
            {
                message = "AUDIT: No outer clearance circle found inside the boundary.";
                return 0;
            }
            if (structures == null || structures.Count == 0)
            {
                message = "AUDIT: No structures selected for interference check.";
                return 0;
            }

            // 3) Kiểm tra giao cắt từng lỗ × cấu kiện (dựng circle ẢO ở world, không thêm vào DB)
            int marks = 0;
            foreach (var center in outerCenters)
            {
                bool collided = false;
                using (var wc = new Circle(center, Vector3d.ZAxis, p.ClearanceRadius))
                {
                    foreach (var s in structures)
                    {
                        if (CircleHitsStructure(wc, s)) { collided = true; break; }
                    }
                }
                if (!collided) continue;

                marks++;
                if (blockOk)
                {
                    var bref = new BlockReference(center, blkId) { Layer = LashingInputParams.LAYER_OUTER_CLEAR };
                    space.AppendEntity(bref);
                    tr.AddNewlyCreatedDBObject(bref, true);
                }
            }

            string blkNote = blockOk ? "" : $" ({blkMsg} - cloud mark could not be inserted)";
            message = marks == 0
                ? "AUDIT: No holes interfere with structures."
                : $"AUDIT: {marks} interfering hole(s) detected{blkNote}.";

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
        // Đảm bảo block cảnh báo tồn tại — port DbxCopyBlock (ObjectDBX)
        // ─────────────────────────────────────────────────────────────
        private static bool EnsureInterferenceBlock(Database db, Transaction tr, out string message)
        {
            message = "";
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt != null && bt.Has(BLOCK_NAME)) return true;

            if (!File.Exists(SYMBOL_PATH))
            {
                message = $"{SYMBOL_PATH} not found";
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
                        message = $"Symbol.dwg does not contain block '{BLOCK_NAME}'";
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
                message = $"Block copy error: {ex.Message}";
                return false;
            }
        }
    }
}
