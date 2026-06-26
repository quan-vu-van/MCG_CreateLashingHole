using Autodesk.AutoCAD.DatabaseServices;
using SDS.Models;
using SDS.Utilities;
using System.Collections.Generic;

namespace SDS.Services
{
    public class ExtractionService
    {
        public BaseEntityInfo ExtractData(Entity ent)
        {
            if (ent == null) return null;

            string handle = ent.Handle.ToString();
            string layer = ent.Layer;
            string color = ent.Color.ToString();

            try
            {
                return ent switch
                {
                    Line l => ExtractLine(l, handle, layer, color),
                    Polyline pl => ExtractPolyline(pl, handle, layer, color),
                    Circle c => ExtractCircle(c, handle, layer, color),
                    BlockReference br => ExtractBlock(br, handle, layer, color),
                    Spline s => ExtractSpline(s, handle, layer, color),
                    MLeader ml => ExtractMLeader(ml, handle, layer, color),
                    DBText t => ExtractText(t, handle, layer, color),
                    MText mt => ExtractMText(mt, handle, layer, color),
                    _ => null // Trả về null cho các đối tượng không hỗ trợ thay vì văng lỗi
                };
            }
            catch (System.Exception ex)
            {
                // Log lỗi nếu cần thiết trong môi trường đóng tàu (ví dụ đối tượng bị Proxy)
                return null;
            }
        }

        private LineInfo ExtractLine(Line l, string h, string ly, string c) => new LineInfo
        {
            Handle = h,
            Layer = ly,
            Color = c,
            EntityType = "Line",
            Length = l.Length,
            Angle = GeometryUtils.RadianToDegree(l.Angle),
            StartPoint = GeometryUtils.FormatPoint(l.StartPoint),
            EndPoint = GeometryUtils.FormatPoint(l.EndPoint)
        };

        private PolylineInfo ExtractPolyline(Polyline pl, string h, string ly, string c) => new PolylineInfo
        {
            Handle = h,
            Layer = ly,
            Color = c,
            EntityType = "Polyline",
            Area = pl.Closed ? pl.Area : 0,
            Length = pl.Length,
            IsClosed = pl.Closed,
            PointList = GeometryUtils.GetPolyPoints(pl)
        };

        private TextInfo ExtractText(DBText t, string h, string ly, string c) => new TextInfo
        {
            Handle = h,
            Layer = ly,
            Color = c,
            EntityType = "Text",
            Content = t.TextString,
            InsertionPoint = GeometryUtils.FormatPoint(t.Position),
            TextSize = t.Height
        };

        private TextInfo ExtractMText(MText mt, string h, string ly, string c) => new TextInfo
        {
            Handle = h,
            Layer = ly,
            Color = c,
            EntityType = "MText",
            Content = mt.Contents,
            InsertionPoint = GeometryUtils.FormatPoint(mt.Location),
            TextSize = mt.TextHeight
        };

        private CircleInfo ExtractCircle(Circle cir, string h, string ly, string c) => new CircleInfo
        {
            Handle = h,
            Layer = ly,
            Color = c,
            EntityType = "Circle",
            Center = GeometryUtils.FormatPoint(cir.Center),
            Radius = cir.Radius
        };

        private BlockInfo ExtractBlock(BlockReference br, string h, string ly, string c)
        {
            var info = new BlockInfo
            {
                Handle = h,
                Layer = ly,
                Color = c,
                EntityType = "Block",
                Name = br.Name,
                InsertionPoint = GeometryUtils.FormatPoint(br.Position)
            };

            // Sử dụng Transaction của tài liệu hiện hành để lấy Attribute
            var tr = br.Database.TransactionManager.TopTransaction;
            foreach (ObjectId id in br.AttributeCollection)
            {
                // Thay vì id.Open, ta dùng tr.GetObject
                var att = tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (att != null) info.Attributes.Add(att.Tag, att.TextString);
            }
            return info;
        }

        private SplineInfo ExtractSpline(Spline s, string h, string ly, string c)
        {
            // Spline dùng tham số (parameter) để tính chiều dài
            double length = s.GetDistanceAtParameter(s.EndParam);
            return new SplineInfo
            {
                Handle = h,
                Layer = ly,
                Color = c,
                EntityType = "Spline",
                Length = length
            };
        }

        private LeaderInfo ExtractMLeader(MLeader ml, string h, string ly, string c)
        {
            return new LeaderInfo
            {
                Handle = h,
                Layer = ly,
                Color = c,
                EntityType = "MLeader",
                Content = ml.ContentType == ContentType.MTextContent ? ml.MText.Contents : "Block Content",
                // MLeader dùng TextLocation cho nội dung chữ
                InsertionPoint = GeometryUtils.FormatPoint(ml.TextLocation),
                TextSize = ml.TextHeight
            };
        }

        private LeaderInfo ExtractLeader(Leader ld, string h, string ly, string c) => new LeaderInfo
        {
            Handle = h,
            Layer = ly,
            Color = c,
            EntityType = "Leader",
            Content = "Leader",
            InsertionPoint = GeometryUtils.FormatPoint(ld.FirstVertex),
            TextSize = 0 // Leader cũ không lưu text trực tiếp
        };
    }
}