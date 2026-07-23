using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using MCG_CreateLashingHole.Services;
using MCG_CreateLashingHole.UI;

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

                    _ps.AddVisual("Generator", new LashingHolePalette());
                    _ps.DockEnabled = DockSides.Right | DockSides.Left;
                    _ps.Size        = new System.Drawing.Size(380, 620);
                    _ps.KeepFocus   = true;
                }

                _ps.Visible = true;
                _ps.Dock    = DockSides.Right;

                if (_ps.Count > 0) _ps.Activate(0);
            }
            catch (System.Exception ex)
            {
                WriteError(ex);
            }
        }

        /// <summary>Flow tuần tự tạo lỗ lashing — palette gọi qua SendStringToExecute sau nút START</summary>
        [CommandMethod("MCG_LH_RUN", CommandFlags.Modal)]
        public void RunLashingFlow() => SafeRun(() => new LashingWorkflowService().RunCreateFlow());

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
            ed?.WriteMessage($"\nLỗi: {ex.Message}");
        }
    }
}
