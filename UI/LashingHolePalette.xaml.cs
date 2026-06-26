using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SDS.Models;
using SDS.Services;

namespace SDS.UI
{
    public partial class LashingHolePalette : UserControl
    {
        private ObjectId _boundaryId = ObjectId.Null;
        private List<ObjectId> _structureIds = new List<ObjectId>();

        private readonly CollisionEngineService _collision = new CollisionEngineService();
        private readonly BlockPackingService _packing;
        private readonly GridGenerationService _grid;

        public LashingHolePalette()
        {
            InitializeComponent();
            _packing = new BlockPackingService(_collision);
            _grid = new GridGenerationService(_collision);
        }

        // ─────────────────────────────────────────────────────────────
        // Đọc params từ UI
        // ─────────────────────────────────────────────────────────────

        private LashingInputParams BuildParams()
        {
            return new LashingInputParams
            {
                PanelName       = txtPanelName.Text.Trim(),
                HoleDiameter    = ParseDouble(txtHoleDiameter.Text, 55.0),
                ClearanceRadius = ParseDouble(txtClearance.Text, 75.0),
                OffsetX         = ParseDouble(txtOffsetX.Text, 150.0),
                OffsetY         = ParseDouble(txtOffsetY.Text, 150.0),
                SpacingX        = ParseDouble(txtSpacingX.Text, 500.0),
                SpacingY        = ParseDouble(txtSpacingY.Text, 500.0),
                LocationMode    = (LashingLocationMode)(cmbLocationMode.SelectedIndex),
                IsAutomaticMode = chkAutomatic.IsChecked == true,
                IsCheckAdjacent = chkCheckAdjacent.IsChecked == true,
                HoleLayer       = "0",
                DimLayer        = "Mechanical-AM_9"
            };
        }

        private static double ParseDouble(string s, double fallback)
            => double.TryParse(s, out double v) ? v : fallback;

        // ─────────────────────────────────────────────────────────────
        // 1. Pick Boundary Polyline
        // ─────────────────────────────────────────────────────────────

        private void CmdPickBoundary_Click(object sender, RoutedEventArgs e)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
            var res = doc.Editor.GetEntity("\nChọn Boundary Polyline (đường biên tấm):");

            if (res.Status != PromptStatus.OK)
            {
                SetStatus("Không chọn được Boundary.", Colors.OrangeRed);
                return;
            }

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ent = tr.GetObject(res.ObjectId, OpenMode.ForRead) as Entity;
                if (ent is Polyline poly && poly.Closed)
                {
                    _boundaryId = res.ObjectId;
                    lblBoundary.Text = $"Handle: {ent.Handle}  |  Layer: {ent.Layer}";
                    lblBoundary.Foreground = Brushes.LightGreen;
                    SetStatus("Boundary đã chọn.", Colors.LightGreen);
                }
                else
                {
                    lblBoundary.Text = "Phải chọn Polyline đóng (Closed)!";
                    lblBoundary.Foreground = Brushes.OrangeRed;
                    SetStatus("Vui lòng chọn Closed Polyline.", Colors.OrangeRed);
                }
                tr.Commit();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 2. Pick Structural Elements
        // ─────────────────────────────────────────────────────────────

        private void CmdPickStructures_Click(object sender, RoutedEventArgs e)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();

            var opts = new PromptSelectionOptions
            {
                MessageForAdding   = "\nChọn cấu kiện tàu (beams, web plates, top plates):",
                AllowDuplicates    = false,
                RejectObjectsOnLockedLayers = false
            };

            var res = doc.Editor.GetSelection(opts);
            if (res.Status != PromptStatus.OK)
            {
                SetStatus("Không chọn được cấu kiện.", Colors.OrangeRed);
                return;
            }

            _structureIds = res.Value.GetObjectIds().ToList();
            lblStructures.Text = $"{_structureIds.Count} cấu kiện được chọn";
            lblStructures.Foreground = Brushes.LightGreen;
            SetStatus($"Đã chọn {_structureIds.Count} cấu kiện.", Colors.LightGreen);
        }

        // ─────────────────────────────────────────────────────────────
        // CREATE LASHING HOLES
        // ─────────────────────────────────────────────────────────────

        private void CmdCreate_Click(object sender, RoutedEventArgs e)
        {
            if (_boundaryId.IsNull)
            {
                SetStatus("Chưa chọn Boundary! Nhấn nút 1 trước.", Colors.OrangeRed);
                return;
            }

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var db = doc.Database;
            var p  = BuildParams();

            // DIMSCALE = 25 như macro VBA gốc
            AcApp.SetSystemVariable("DIMSCALE", DIMSCALE_VALUE);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var boundary = tr.GetObject(_boundaryId, OpenMode.ForRead) as Polyline;
                    if (boundary == null || !boundary.Closed)
                    {
                        SetStatus("Boundary không hợp lệ hoặc chưa đóng.", Colors.OrangeRed);
                        tr.Abort();
                        return;
                    }

                    var structures = _structureIds
                        .Select(id => tr.GetObject(id, OpenMode.ForRead) as Entity)
                        .Where(ent => ent != null)
                        .ToList();

                    var bt    = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var space = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                    var created = _packing.GenerateHoles(p, boundary, structures, tr, space, db);

                    tr.Commit();
                    SetStatus($"Hoàn thành: tạo {created.Count} lỗ lashing.", Colors.LightGreen);
                }
                catch (Exception ex)
                {
                    tr.Abort();
                    SetStatus($"Lỗi: {ex.Message}", Colors.OrangeRed);
                    doc.Editor.WriteMessage($"\nMCG Error: {ex}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Phase 3 — Điền holes vào khu vực đặc biệt
        // ─────────────────────────────────────────────────────────────

        private void CmdPhase3_Click(object sender, RoutedEventArgs e)
        {
            if (_boundaryId.IsNull)
            {
                SetStatus("Chưa chọn Boundary!", Colors.OrangeRed);
                return;
            }

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();

            var res1 = doc.Editor.GetEntity("\nChọn Hole đầu tiên (Circle):");
            if (res1.Status != PromptStatus.OK) return;

            var res2 = doc.Editor.GetEntity("\nChọn Hole thứ hai (Circle):");
            if (res2.Status != PromptStatus.OK) return;

            var p  = BuildParams();
            var db = doc.Database;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var hole1    = tr.GetObject(res1.ObjectId, OpenMode.ForRead) as Circle;
                    var hole2    = tr.GetObject(res2.ObjectId, OpenMode.ForRead) as Circle;
                    var boundary = tr.GetObject(_boundaryId, OpenMode.ForRead) as Polyline;

                    if (hole1 == null || hole2 == null)
                    {
                        SetStatus("Phải chọn 2 đối tượng Circle!", Colors.OrangeRed);
                        tr.Abort(); return;
                    }
                    if (boundary == null)
                    {
                        SetStatus("Boundary không hợp lệ.", Colors.OrangeRed);
                        tr.Abort(); return;
                    }

                    // Xác định hướng (đứng hay ngang)
                    double dx = Math.Abs(hole1.Center.X - hole2.Center.X);
                    double dy = Math.Abs(hole1.Center.Y - hole2.Center.Y);
                    bool isVertical = dy > dx;

                    double spacing = isVertical ? p.SpacingY : p.SpacingX;
                    double offset  = isVertical ? p.OffsetY  : p.OffsetX;

                    var bt    = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var space = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                    var created = _grid.RegenerateSpecialArea(
                        hole1, hole2, boundary, spacing, offset, isVertical, p, tr, space);

                    tr.Commit();
                    SetStatus($"Phase 3: tạo {created.Count} lỗ bổ sung.", Colors.LightGreen);
                }
                catch (Exception ex)
                {
                    tr.Abort();
                    SetStatus($"Lỗi Phase 3: {ex.Message}", Colors.OrangeRed);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Helper UI
        // ─────────────────────────────────────────────────────────────

        private const double DIMSCALE_VALUE = 25.0;

        private void SetStatus(string msg, Color color)
        {
            lblStatus.Text = msg;
            lblStatus.Foreground = new SolidColorBrush(color);
        }
    }
}
