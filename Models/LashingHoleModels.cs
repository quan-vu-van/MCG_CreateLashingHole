namespace MCG_CreateLashingHole.Models
{
    public enum LashingLocationMode
    {
        PortSide,   // P1 tại góc Dưới-Trái → P2 = Trên-Phải
        StarBoard,  // P1 tại góc Trên-Trái → P2 = Dưới-Phải
        Center      // Tâm đối xứng hình học (Geometric Centroid)
    }

    public enum LashingCollisionType
    {
        None,
        Vertical,   // Cấu kiện đứng (web plate) → ưu tiên dịch X
        Horizontal, // Cấu kiện ngang (stiffener) → ưu tiên dịch Y
        Complex     // Góc nghiêng → tìm 8 hướng
    }

    public class LashingInputParams
    {
        // Layer chuẩn — khớp CHÍNH XÁC với VBA gốc (DrawLashingHoleCircles):
        //   holeLayerName      = "0"
        //   clearanceLayerName = "Mechanical-AM_9"
        // Dimension Phase 3 (PerformSpecialAreaAdjustment_Phase3) cũng dùng "Mechanical-AM_9".
        // Audit interference (mod_LashingHoleInterference) lọc outer circle theo layer "Mechanical-AM_9".
        public const string LAYER_INNER_HOLE  = "0";               // Lỗ thực (innerCircle)
        public const string LAYER_OUTER_CLEAR = "Mechanical-AM_9"; // Vùng clearance (outerCircle)
        public const string LAYER_DIMENSION   = "Mechanical-AM_9"; // Kích thước

        public double HoleDiameter    { get; set; } = 55.0;
        public double ClearanceRadius { get; set; } = 75.0;
        public double OffsetX         { get; set; } = 150.0;
        public double OffsetY         { get; set; } = 150.0;
        public double SpacingX        { get; set; } = 500.0;
        public double SpacingY        { get; set; } = 500.0;
        public string PanelName       { get; set; } = "PNL_01";
        public LashingLocationMode LocationMode  { get; set; } = LashingLocationMode.PortSide;
        public bool   IsAutomaticMode  { get; set; } = false;
        public bool   IsCheckAdjacent  { get; set; } = false;
    }

    public class LashingCollisionResult
    {
        public bool               CollisionOccurred { get; set; } = false;
        public LashingCollisionType CollisionType   { get; set; } = LashingCollisionType.None;
        public double             DeltaX            { get; set; } = 0;
        public double             DeltaY            { get; set; } = 0;
    }

    /// <summary>
    /// Cầu nối tham số giữa Palette (WPF) và CommandMethod (flow tuần tự).
    /// Palette ghi trước khi SendStringToExecute; command đọc khi bắt đầu chạy.
    /// </summary>
    public static class LashingParamsStore
    {
        public static LashingInputParams Current { get; set; }
    }
}
