using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using MCG_CreateLashingHole.Models;

namespace MCG_CreateLashingHole.UI
{
    /// <summary>
    /// Palette nhập tham số — mô hình VBA UserForm: chỉ nhập liệu + bấm START.
    /// Mọi bước thực hiện (chọn boundary, structures, P1/P2, Y/N…) chạy tuần tự
    /// tại command line qua các lệnh MCG_LH_RUN / MCG_LH_AUDIT / MCG_LH_INTERFERE.
    /// Palette KHÔNG giữ state bản vẽ, KHÔNG gọi service trực tiếp.
    /// </summary>
    public partial class LashingHolePalette : UserControl
    {
        private const string LOG_PREFIX = "[LashingHolePalette]";

        // Registry path để lưu settings giữa các phiên (như VBA GetSetting/SaveSetting)
        private const string REG_PATH = @"Software\MCG_LashingHole\Settings";

        /// <summary>Khởi tạo palette</summary>
        public LashingHolePalette()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e) => LoadSettings();

        // ─────────────────────────────────────────────────────────────
        // Settings — Registry (khớp hành vi VBA UserForm_Initialize)
        // ─────────────────────────────────────────────────────────────
        private void LoadSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_PATH))
                {
                    if (key == null) return;
                    txtPanelName.Text    = GetReg(key, "PanelName",       "PNL_01");
                    txtHoleDiameter.Text = GetReg(key, "HoleDiameter",    "55");
                    txtClearance.Text    = GetReg(key, "ClearanceRadius", "75");
                    txtOffsetX.Text      = GetReg(key, "OffsetX",         "150");
                    txtOffsetY.Text      = GetReg(key, "OffsetY",         "150");
                    txtSpacingX.Text     = GetReg(key, "SpacingX",        "500");
                    txtSpacingY.Text     = GetReg(key, "SpacingY",        "500");

                    int mode = int.Parse(GetReg(key, "LocationMode", "0"));
                    rbPortSide.IsChecked  = mode == 0;
                    rbStarBoard.IsChecked = mode == 1;
                    rbCenter.IsChecked    = mode == 2;

                    chkAutomatic.IsChecked     = GetReg(key, "IsAutomatic",     "0") == "1";
                    chkCheckAdjacent.IsChecked = GetReg(key, "IsCheckAdjacent", "0") == "1";
                }
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Settings loaded from Registry.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} LoadSettings warning: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_PATH))
                {
                    if (key == null) return;
                    key.SetValue("PanelName",       txtPanelName.Text);
                    key.SetValue("HoleDiameter",    txtHoleDiameter.Text);
                    key.SetValue("ClearanceRadius", txtClearance.Text);
                    key.SetValue("OffsetX",         txtOffsetX.Text);
                    key.SetValue("OffsetY",         txtOffsetY.Text);
                    key.SetValue("SpacingX",        txtSpacingX.Text);
                    key.SetValue("SpacingY",        txtSpacingY.Text);
                    key.SetValue("LocationMode",    GetSelectedModeIndex().ToString());
                    key.SetValue("IsAutomatic",     chkAutomatic.IsChecked == true ? "1" : "0");
                    key.SetValue("IsCheckAdjacent", chkCheckAdjacent.IsChecked == true ? "1" : "0");
                }
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Settings saved to Registry.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} SaveSettings warning: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Build params từ UI controls
        // ─────────────────────────────────────────────────────────────
        private LashingInputParams BuildParams() => new LashingInputParams
        {
            PanelName       = txtPanelName.Text.Trim(),
            HoleDiameter    = ParseDouble(txtHoleDiameter.Text, 55.0),
            ClearanceRadius = ParseDouble(txtClearance.Text, 75.0),
            OffsetX         = ParseDouble(txtOffsetX.Text, 150.0),
            OffsetY         = ParseDouble(txtOffsetY.Text, 150.0),
            SpacingX        = ParseDouble(txtSpacingX.Text, 500.0),
            SpacingY        = ParseDouble(txtSpacingY.Text, 500.0),
            LocationMode    = GetSelectedMode(),
            IsAutomaticMode = chkAutomatic.IsChecked == true,
            IsCheckAdjacent = chkCheckAdjacent.IsChecked == true,
        };

        private LashingLocationMode GetSelectedMode()
        {
            if (rbStarBoard.IsChecked == true) return LashingLocationMode.StarBoard;
            if (rbCenter.IsChecked    == true) return LashingLocationMode.Center;
            return LashingLocationMode.PortSide;
        }

        private int GetSelectedModeIndex()
        {
            if (rbStarBoard.IsChecked == true) return 1;
            if (rbCenter.IsChecked    == true) return 2;
            return 0;
        }

        private static double ParseDouble(string s, double fallback)
            => double.TryParse(s, System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;

        // ─────────────────────────────────────────────────────────────
        // Validate input (khớp VBA ValidateInput)
        // ─────────────────────────────────────────────────────────────
        private bool ValidateInput(LashingInputParams p)
        {
            if (string.IsNullOrWhiteSpace(p.PanelName))
            {
                SetStatus("Vui lòng nhập Panel Name (ví dụ: PNL_01).", Colors.OrangeRed);
                txtPanelName.Focus();
                return false;
            }
            if (p.HoleDiameter <= 0)
            {
                SetStatus("Hole Diameter phải > 0.", Colors.OrangeRed);
                txtHoleDiameter.Focus();
                return false;
            }
            if (p.ClearanceRadius <= 0)
            {
                SetStatus("Clearance Radius phải > 0.", Colors.OrangeRed);
                txtClearance.Focus();
                return false;
            }
            if (p.ClearanceRadius < p.HoleDiameter / 2.0)
            {
                SetStatus("Clearance Radius phải lớn hơn bán kính lỗ.", Colors.OrangeRed);
                txtClearance.Focus();
                return false;
            }
            if (p.SpacingX <= 0 || p.SpacingY <= 0)
            {
                SetStatus("Spacing X/Y phải > 0.", Colors.OrangeRed);
                txtSpacingX.Focus();
                return false;
            }
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        // Button handlers — chỉ dispatch lệnh, flow chạy ở command line
        // ─────────────────────────────────────────────────────────────
        private void CmdStart_Click(object sender, RoutedEventArgs e)
            => Dispatch("MCG_LH_RUN", "Running create flow — follow the command line...");

        private void CmdBatch_Click(object sender, RoutedEventArgs e)
            => Dispatch("MCG_LH_BATCH", "Batch multi-panel — select boundaries + P/C/S on the command line...");

        private void CmdAudit_Click(object sender, RoutedEventArgs e)
            => Dispatch("MCG_LH_AUDIT", "Running spacing audit — follow the command line...");

        private void CmdAuditInterference_Click(object sender, RoutedEventArgs e)
            => Dispatch("MCG_LH_INTERFERE", "Running interference audit — follow the command line...");

        /// <summary>Lưu settings → publish params → trả focus bản vẽ → gửi lệnh chạy tuần tự</summary>
        private void Dispatch(string command, string statusText)
        {
            try
            {
                var p = BuildParams();
                if (!ValidateInput(p)) return;

                SaveSettings();
                LashingParamsStore.Current = p;

                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    SetStatus("Không có bản vẽ đang mở.", Colors.OrangeRed);
                    return;
                }

                // Trả focus về bản vẽ trước khi flow bắt đầu prompt
                Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
                doc.SendStringToExecute(command + "\n", true, false, true);
                SetStatus(statusText, Colors.LightGreen);
            }
            catch (Exception ex)
            {
                SetStatus($"Lỗi: {ex.Message}", Colors.OrangeRed);
                System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} Dispatch LỖI: {ex}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────
        private void SetStatus(string msg, Color color)
        {
            lblStatus.Text       = msg;
            lblStatus.Foreground = new SolidColorBrush(color);
        }

        private static string GetReg(RegistryKey key, string name, string defaultValue)
            => key.GetValue(name, defaultValue)?.ToString() ?? defaultValue;
    }
}
