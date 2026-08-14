using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using MCG_CreateLashingHole.Services;
using MCG_CreateLashingHole.UI;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(MCG_CreateLashingHole.Commands.LashingHoleCommands))]

namespace MCG_CreateLashingHole.Commands
{
    /// <summary>
    /// Đăng ký lệnh CAD — chỉ điều phối, KHÔNG chứa logic (logic nằm ở Services).
    /// Palette là nơi nhập tham số; các flow thực thi chạy tuần tự qua command line.
    /// </summary>
    public class LashingHoleCommands
    {
        private const string LOG_PREFIX = "[LashingHoleCommands]";

        private static PaletteSet _ps;

        /// <summary>Hiển thị palette nhập tham số (singleton, GUID cố định)</summary>
        [CommandMethod("MCG_CreateLashingHole")]
        public void ShowLashingHolePalette()
        {
            try
            {
                if (_ps == null)
                {
                    _ps = new PaletteSet(
                        "MCG Lashing Hole Generator",
                        new Guid("3A7F1B92-D45E-4C8A-B71F-9C4E0D2A58F3"));
                    _ps.DockEnabled = DockSides.Right | DockSides.Left;
                    _ps.Size        = new System.Drawing.Size(380, 620);
                    _ps.KeepFocus   = true;
                }

                // Tự phục hồi nội dung: nếu palette rỗng (lần đầu, hoặc do netload đè nhiều lần
                // khiến visual cũ mồ côi) → nạp lại UserControl. Tránh palette hiện trắng.
                if (_ps.Count == 0)
                    _ps.AddVisual("Generator", new LashingHolePalette());

                _ps.Visible = true;
                _ps.Dock    = DockSides.Right;

                if (_ps.Count > 0) _ps.Activate(0);
            }
            catch (System.Exception ex)
            {
                WriteError(ex);
            }
        }

        /// <summary>
        /// PHẦN 1: sinh lưới + vẽ lỗ rồi KẾT THÚC lệnh (AutoCAD repaint → lỗ hiển thị chắc chắn).
        /// Nếu có phiên chờ, hook Application.Idle để tự chạy MCG_LH_POST sau khi màn hình đã vẽ lỗ.
        /// </summary>
        [CommandMethod("MCG_LH_RUN", CommandFlags.Modal)]
        public void RunLashingFlow()
        {
            SafeRun(() => new LashingWorkflowService().RunGenerate());
            if (LashingWorkflowState.HasPending) HookContinueToPost();
        }

        /// <summary>
        /// PHẦN 2: MỘT bước special area mỗi lần chạy. Nếu còn tiếp (ContinueSpecial) thì kết thúc
        /// lệnh cho AutoCAD repaint lỗ mới rồi tự chạy lại POST qua Idle; ngược lại chạy tiếp
        /// local adjust + đóng block rồi kết thúc.
        /// </summary>
        [CommandMethod("MCG_LH_POST", CommandFlags.Modal)]
        public void RunLashingPost()
        {
            SafeRun(() => new LashingWorkflowService().RunPostProcess());
            if (LashingWorkflowState.ContinueSpecial) HookContinueToPost();
        }

        // ── Auto-chain PHẦN 1 → PHẦN 2 qua Application.Idle ──
        // Idle chỉ fire khi AutoCAD rảnh (đã repaint xong): lúc đó lỗ CHẮC CHẮN đã hiện trên
        // màn hình. One-shot: gỡ handler ngay lần fire đầu rồi mới đẩy lệnh POST.
        private static void HookContinueToPost()
        {
            AcApp.Idle -= OnIdleContinueToPost; // tránh double-hook nếu chạy START liên tiếp
            AcApp.Idle += OnIdleContinueToPost;
        }

        private static void OnIdleContinueToPost(object sender, EventArgs e)
        {
            AcApp.Idle -= OnIdleContinueToPost; // one-shot
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            doc?.SendStringToExecute("MCG_LH_POST ", true, false, true);
        }

        /// <summary>Audit spacing report — chọn boundary rồi báo cáo ra command line</summary>
        [CommandMethod("MCG_LH_AUDIT", CommandFlags.Modal)]
        public void RunSpacingAudit() => SafeRun(() => new LashingWorkflowService().RunSpacingAudit());

        /// <summary>Audit interference — chèn cloud mark tại lỗ va chạm cấu kiện</summary>
        [CommandMethod("MCG_LH_INTERFERE", CommandFlags.Modal)]
        public void RunInterferenceAudit() => SafeRun(() => new LashingWorkflowService().RunInterferenceAudit());

        /// <summary>Chạy action với xử lý lỗi chuẩn: log + thông báo user-friendly</summary>
        private static void SafeRun(Action action)
        {
            try
            {
                action();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} LỖI: {ex}");
                WriteError(ex);
            }
        }

        private static void WriteError(System.Exception ex)
        {
            var ed = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\nError: {ex.Message}");
        }
    }
}
