using System.Collections.Generic;

namespace SDS.Models
{
    public abstract class BaseEntityInfo
    {
        public string Handle { get; set; }
        public string EntityType { get; set; }
        public string Layer { get; set; }
        public string Color { get; set; }
    }

    public class LineInfo : BaseEntityInfo
    {
        public double Length { get; set; }
        public double Angle { get; set; }
        public string StartPoint { get; set; }
        public string EndPoint { get; set; }
    }

    public class PolylineInfo : BaseEntityInfo
    {
        public double Area { get; set; }
        public double Length { get; set; } // Chu vi
        public string PointList { get; set; }
        public bool IsClosed { get; set; }
    }

    public class TextInfo : BaseEntityInfo
    {
        public string Content { get; set; }
        public string InsertionPoint { get; set; }
        public double TextSize { get; set; }
    }

    public class CircleInfo : BaseEntityInfo
    {
        public string Center { get; set; }
        public double Radius { get; set; }
    }

    public class BlockInfo : BaseEntityInfo
    {
        public string Name { get; set; }
        public string InsertionPoint { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    }

    public class SplineInfo : BaseEntityInfo
    {
        public double Length { get; set; }
    }

    public class LeaderInfo : BaseEntityInfo
    {
        public string Content { get; set; }
        public string InsertionPoint { get; set; }
        public double TextSize { get; set; }
    }
}