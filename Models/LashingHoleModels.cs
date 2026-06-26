namespace SDS.Models
{
    public enum LashingLocationMode
    {
        PortSide,   // P1 tại góc Dưới-Trái
        StarBoard,  // P1 tại góc Trên-Trái
        Center      // Tự động rải từ tâm đối xứng của tấm biên
    }

    public enum LashingCollisionType
    {
        None,
        Vertical,   // Cấu kiện đứng (web plate) → dịch X
        Horizontal, // Cấu kiện ngang (stiffener) → dịch Y
        Complex     // Góc nghiêng → dịch cả X và Y
    }

    public class LashingInputParams
    {
        public double HoleDiameter { get; set; } = 55.0;
        public double ClearanceRadius { get; set; } = 75.0;
        public double OffsetX { get; set; } = 150.0;
        public double OffsetY { get; set; } = 150.0;
        public double SpacingX { get; set; } = 500.0;
        public double SpacingY { get; set; } = 500.0;
        public string PanelName { get; set; } = "PNL_01";
        public LashingLocationMode LocationMode { get; set; } = LashingLocationMode.PortSide;
        public bool IsAutomaticMode { get; set; } = false;
        public bool IsCheckAdjacent { get; set; } = false;
        public string HoleLayer { get; set; } = "0";
        public string DimLayer { get; set; } = "Mechanical-AM_9";
    }

    public class LashingCollisionResult
    {
        public bool CollisionOccurred { get; set; } = false;
        public LashingCollisionType CollisionType { get; set; } = LashingCollisionType.None;
        public double DeltaX { get; set; } = 0;
        public double DeltaY { get; set; } = 0;
    }
}
