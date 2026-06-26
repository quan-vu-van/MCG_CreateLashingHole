using System.Collections.Generic;
using System.Windows;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SDS.Models;

namespace SDS.UI
{
    public partial class InfoPaletteControl : System.Windows.Controls.UserControl
    {
        private SDS.Services.ExtractionService _service = new SDS.Services.ExtractionService();

        public InfoPaletteControl() => InitializeComponent();

        private void PickBtn_Click(object sender, RoutedEventArgs e)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            //Trả focus về màn hình vẽ
             Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
            var res = doc.Editor.GetEntity("\nChọn đối tượng để kiểm tra:");
            if (res.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var ent = tr.GetObject(res.ObjectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) as Entity;
                var data = _service.ExtractData(ent);
                DisplayData(data);
                tr.Commit();
            }
        }

        private void DisplayData(BaseEntityInfo data)
        {
            if (data == null) return;

            var props = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Type", data.EntityType),
                new KeyValuePair<string, string>("Handle", data.Handle),
                new KeyValuePair<string, string>("Layer", data.Layer)
            };

            // Dùng Reflection hoặc switch để lấy field nhanh
            if (data is LineInfo l)
            {
                props.Add(new KeyValuePair<string, string>("Length", l.Length.ToString("F2")));
                props.Add(new KeyValuePair<string, string>("Angle", l.Angle.ToString("F1") + "°"));
            }
            else if (data is PolylineInfo pl)
            {
                props.Add(new KeyValuePair<string, string>("Area", pl.Area.ToString("N2")));
                props.Add(new KeyValuePair<string, string>("Perimeter", pl.Length.ToString("F2")));
                props.Add(new KeyValuePair<string, string>("Points", pl.PointList));
            }
            else if (data is TextInfo t)
            {
                props.Add(new KeyValuePair<string, string>("String", t.Content));
                props.Add(new KeyValuePair<string, string>("Size", t.TextSize.ToString("F1")));
            }
            else if (data is BlockInfo b)
            {
                props.Add(new KeyValuePair<string, string>("Name", b.Name));
                foreach (var att in b.Attributes) props.Add(new KeyValuePair<string, string>("Att: " + att.Key, att.Value));
            }

            LstProperties.ItemsSource = props;
        }
    }
}
