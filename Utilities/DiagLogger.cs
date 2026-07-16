using System;
using System.IO;

namespace MCG_CreateLashingHole.Utilities
{
    /// <summary>
    /// File-based logger để chẩn đoán FATAL ERROR.
    /// Log được ghi ra %TEMP%\MCG_LashingHole_diag.txt.
    /// File tồn tại SAU crash → mở ra xem dòng cuối cùng = crash point.
    /// XÓA class này sau khi đã xác định được root cause.
    /// </summary>
    public static class DiagLogger
    {
        private static readonly string LOG_FILE =
            Path.Combine(Path.GetTempPath(), "MCG_LashingHole_diag.txt");

        /// <summary>Xóa log cũ, bắt đầu phiên mới.</summary>
        public static void Clear()
        {
            try
            {
                File.WriteAllText(LOG_FILE, $"=== MCG LashingHole Diagnostic === {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            }
            catch { }
        }

        /// <summary>Ghi 1 dòng log — flush ngay lập tức.</summary>
        public static void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            try
            {
                // Flush ngay: mỗi lần ghi là 1 lần mở file → chậm hơn nhưng đảm bảo ghi xong trước crash
                File.AppendAllText(LOG_FILE, line + "\n");
            }
            catch { }
            System.Diagnostics.Debug.WriteLine($"[MCG-DIAG] {msg}");
        }

        /// <summary>Trả về đường dẫn file log để hiển thị cho user.</summary>
        public static string GetLogPath() => LOG_FILE;
    }
}
