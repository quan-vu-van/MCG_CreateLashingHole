using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcRuntime = Autodesk.AutoCAD.Runtime;
using Microsoft.Win32;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using MCG_CreateLashingHole.Models;
using MCG_CreateLashingHole.Services;
using MCG_CreateLashingHole.Utilities;

namespace MCG_CreateLashingHole.UI
{
    public partial class LashingHolePalette : UserControl
    {
        private const string LOG_PREFIX = "[LashingHolePalette]";

        // Gap #5: Registry path để lưu settings
        private const string REG_PATH = @"Software\MCG_LashingHole\Settings";

        // State
        private ObjectId       _boundaryId    = ObjectId.Null;
        private List<ObjectId> _structureIds  = new List<ObjectId>();

        // Services
        private readonly CollisionEngineService _collision;
        private readonly BlockPackingService    _packing;
        private readonly GridGenerationService  _grid;

        public LashingHolePalette()
        {
            InitializeComponent();
            _collision = new CollisionEngineService();
            _packing   = new BlockPackingService(_collision);
            _grid      = new GridGenerationService(_collision);
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #5: Load settings từ Registry khi Palette mở
        // ─────────────────────────────────────────────────────────────
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_PATH))
                {
                    if (key == null) return;
                    txtPanelName.Text    = GetReg(key, "PanelName",    "PNL_01");
                    txtHoleDiameter.Text = GetReg(key, "HoleDiameter", "55");
                    txtClearance.Text    = GetReg(key, "ClearanceRadius", "75");
                    txtOffsetX.Text      = GetReg(key, "OffsetX",      "150");
                    txtOffsetY.Text      = GetReg(key, "OffsetY",      "150");
                    txtSpacingX.Text     = GetReg(key, "SpacingX",     "500");
                    txtSpacingY.Text     = GetReg(key, "SpacingY",     "500");

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
        // 1. Select Boundary Polyline
        // ─────────────────────────────────────────────────────────────
        private void CmdPickBoundary_Click(object sender, RoutedEventArgs e)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (doc.LockDocument())
            {
                try
                {
                    Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
                    var res = doc.Editor.GetEntity("\nSelect Boundary Polyline (closed):");

                    if (res.Status != PromptStatus.OK)
                    {
                        SetStatus("Boundary selection cancelled.", Colors.Orange);
                        return;
                    }

                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var ent = tr.GetObject(res.ObjectId, OpenMode.ForRead) as Entity;
                        if (ent is Polyline)
                        {
                            _boundaryId       = res.ObjectId;
                            lblBoundary.Text  = $"Handle: {ent.Handle}  |  Layer: {ent.Layer}";
                            lblBoundary.Foreground = Brushes.LightGreen;
                            SetStatus("Boundary selected.", Colors.LightGreen);
                        }
                        else
                        {
                            lblBoundary.Text  = "Must select a Polyline!";
                            lblBoundary.Foreground = Brushes.OrangeRed;
                            SetStatus("Please select a Polyline.", Colors.OrangeRed);
                        }
                        tr.Commit();
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Error: {ex.Message}", Colors.OrangeRed);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 2. Select Structural Elements
        // ─────────────────────────────────────────────────────────────
        private void CmdPickStructures_Click(object sender, RoutedEventArgs e)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (doc.LockDocument())
            {
                try
                {
                    Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();

                    var opts = new PromptSelectionOptions
                    {
                        MessageForAdding          = "\nSelect structural elements (beams, web plates, stiffeners):",
                        AllowDuplicates           = false,
                        RejectObjectsOnLockedLayers = false
                    };

                    var res = doc.Editor.GetSelection(opts);
                    if (res.Status != PromptStatus.OK)
                    {
                        SetStatus("Structure selection cancelled.", Colors.Orange);
                        return;
                    }

                    _structureIds          = res.Value.GetObjectIds().ToList();
                    lblStructures.Text     = $"{_structureIds.Count} structural element(s) selected";
                    lblStructures.Foreground = Brushes.LightGreen;
                    SetStatus($"Selected {_structureIds.Count} structural element(s).", Colors.LightGreen);
                }
                catch (Exception ex)
                {
                    SetStatus($"Error: {ex.Message}", Colors.OrangeRed);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // CREATE LASHING HOLES
        // ─────────────────────────────────────────────────────────────
        private void CmdCreate_Click(object sender, RoutedEventArgs e)
        {
            DiagLogger.Clear();
            DiagLogger.Log("CmdCreate_Click ENTER");

            // Mọi button trên PaletteSet phải trả focus về bản vẽ trước khi thao tác
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
            DiagLogger.Log("SetFocusToDwgView done");

            SaveSettings();
            DiagLogger.Log("SaveSettings done");

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) { DiagLogger.Log("ERROR: doc is null"); return; }

            var p = BuildParams();
            DiagLogger.Log($"BuildParams done: mode={p.LocationMode}, spacing=({p.SpacingX},{p.SpacingY})");

            using (doc.LockDocument())
            {
                DiagLogger.Log("LockDocument acquired");
                var db = doc.Database;

                if (p.IsAutomaticMode)
                {
                    DiagLogger.Log("Auto mode: TryAutoDetectBoundary...");
                    if (!TryAutoDetectBoundary(doc))
                    {
                        SetStatus("Auto-detect: no closed polyline with area > 20 m² found.", Colors.OrangeRed);
                        return;
                    }
                    DiagLogger.Log("Auto mode: TryAutoSelectStructures...");
                    TryAutoSelectStructures(doc, p);
                }

                if (_boundaryId.IsNull)
                {
                    DiagLogger.Log("ERROR: boundaryId is null");
                    SetStatus("No boundary selected! Click '1. Select Boundary Polyline' first.", Colors.OrangeRed);
                    return;
                }
                DiagLogger.Log($"BoundaryId OK: {_boundaryId}");

                int createdCount = 0;
                DiagLogger.Log("StartTransaction...");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    DiagLogger.Log("Transaction started");
                    try
                    {
                        DiagLogger.Log("GetObject boundary...");
                        var boundary = tr.GetObject(_boundaryId, OpenMode.ForRead) as Polyline;
                        if (boundary == null)
                        {
                            DiagLogger.Log("ERROR: boundary is null after GetObject");
                            SetStatus("Invalid boundary.", Colors.OrangeRed);
                            tr.Abort(); return;
                        }
                        DiagLogger.Log("Boundary OK");

                        DiagLogger.Log($"Loading {_structureIds.Count} structures...");
                        // Pre-check type: chỉ load Curve (Polyline, Line, Arc) — bỏ qua proxy/block/text
                        var curveClass = AcRuntime.RXObject.GetClass(typeof(Curve));
                        var structures = _structureIds
                            .Where(id => id.IsValid && !id.IsErased &&
                                         id.ObjectClass.IsDerivedFrom(curveClass))
                            .Select(id => {
                                try { return tr.GetObject(id, OpenMode.ForRead) as Entity; }
                                catch { return null; }
                            })
                            .Where(ent => ent != null).ToList();
                        DiagLogger.Log($"Structures loaded: {structures.Count}");

                        DiagLogger.Log("GetObject BlockTable...");
                        var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                        DiagLogger.Log("GetObject ModelSpace...");
                        var space = tr.GetObject(bt[BlockTableRecord.ModelSpace],
                                        OpenMode.ForWrite) as BlockTableRecord;
                        DiagLogger.Log("ModelSpace OK");

                        DiagLogger.Log("Calling GenerateHoles...");
                        var created = _packing.GenerateHoles(p, boundary, structures, tr, space, db);
                        createdCount = created.Count;
                        DiagLogger.Log($"GenerateHoles done: {createdCount} holes");

                        DiagLogger.Log("tr.Commit...");
                        tr.Commit();
                        DiagLogger.Log("tr.Commit done");
                    }
                    catch (Exception ex)
                    {
                        tr.Abort();
                        DiagLogger.Log($"EXCEPTION (managed): {ex.GetType().Name}: {ex.Message}");
                        SetStatus($"Error: {ex.Message}", Colors.OrangeRed);
                        System.Diagnostics.Debug.WriteLine($"{LOG_PREFIX} CmdCreate ERROR: {ex}");
                        return;
                    }
                }

                SetStatus($"Done: {createdCount} lashing hole(s) created.", Colors.LightGreen);
                DiagLogger.Log($"CmdCreate_Click SUCCESS: {createdCount} holes. LogFile={DiagLogger.GetLogPath()}");
            }
        }


        // ─────────────────────────────────────────────────────────────
        // PHASE 2 — Fill Gap between 2 selected holes
        // ─────────────────────────────────────────────────────────────
        private void CmdPhase2_Click(object sender, RoutedEventArgs e)
        {
            if (_boundaryId.IsNull)
            {
                SetStatus("Select a boundary first.", Colors.OrangeRed);
                return;
            }

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (doc.LockDocument())
            {
                try
                {
                    Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();

                    var res1 = doc.Editor.GetEntity("\nSelect Start Hole (Circle):");
                    if (res1.Status != PromptStatus.OK)
                    {
                        SetStatus("Phase 2 cancelled (Hole 1).", Colors.Orange); return;
                    }

                    var res2 = doc.Editor.GetEntity("\nSelect End Hole (Circle):");
                    if (res2.Status != PromptStatus.OK)
                    {
                        SetStatus("Phase 2 cancelled (Hole 2).", Colors.Orange); return;
                    }

                    var p  = BuildParams();
                    var db = doc.Database;

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var hole1    = tr.GetObject(res1.ObjectId, OpenMode.ForRead) as Circle;
                        var hole2    = tr.GetObject(res2.ObjectId, OpenMode.ForRead) as Circle;
                        var boundary = tr.GetObject(_boundaryId, OpenMode.ForRead) as Polyline;

                        if (hole1 == null || hole2 == null)
                        {
                            SetStatus("Must select 2 Circle objects!", Colors.OrangeRed);
                            tr.Abort(); return;
                        }
                        if (boundary == null)
                        {
                            SetStatus("Invalid boundary.", Colors.OrangeRed);
                            tr.Abort(); return;
                        }

                        double dx         = Math.Abs(hole1.Center.X - hole2.Center.X);
                        double dy         = Math.Abs(hole1.Center.Y - hole2.Center.Y);
                        bool   isVertical = dy > dx;
                        double spacing    = isVertical ? p.SpacingY : p.SpacingX;
                        double offset     = isVertical ? p.OffsetY  : p.OffsetX;

                        var bt    = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                        var space = tr.GetObject(bt[BlockTableRecord.ModelSpace],
                                        OpenMode.ForWrite) as BlockTableRecord;

                        var created = _grid.RegenerateSpecialArea(
                            hole1, hole2, boundary, spacing, offset, isVertical, p, tr, space);

                        tr.Commit();
                        SetStatus($"Phase 2: {created.Count} intermediate hole(s) added.", Colors.LightGreen);
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Phase 2 Error: {ex.Message}", Colors.OrangeRed);
                    doc.Editor.WriteMessage($"\n[MCG Error] CmdPhase2: {ex}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #8: AUDIT — Kiểm tra lỗ trong boundary
        // Tương đương VBA: AuditHoles_ByTwoPoints
        // ─────────────────────────────────────────────────────────────
        private void CmdAudit_Click(object sender, RoutedEventArgs e)
        {
            if (_boundaryId.IsNull)
            {
                SetStatus("Select a boundary first.", Colors.OrangeRed);
                return;
            }

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var p = BuildParams();

            using (doc.LockDocument())
            {
                string report = null;

                // ✅ TRANSACTION — chỉ DB reads, KHÔNG có editor ops
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var boundary = tr.GetObject(_boundaryId, OpenMode.ForRead) as Polyline;
                        if (boundary == null)
                        {
                            SetStatus("Invalid boundary.", Colors.OrangeRed);
                            tr.Abort(); return;
                        }

                        var bt = tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                        var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead)
                                    as BlockTableRecord;

                        var holes = new List<Circle>();
                        foreach (ObjectId id in ms)
                        {
                            try
                            {
                                var ent = tr.GetObject(id, OpenMode.ForRead);
                                if (ent is Circle c
                                    && c.Layer == LashingInputParams.LAYER_INNER_HOLE
                                    && AutoCADGeometryHelper.IsInsidePolylineOrEdge(c.Center, boundary))
                                {
                                    holes.Add(c);
                                }
                            }
                            catch { continue; }
                        }

                        report = BuildAuditReport(holes, p);
                        tr.Commit();
                    }
                    catch (Exception ex)
                    {
                        tr.Abort();
                        SetStatus($"Audit error: {ex.Message}", Colors.OrangeRed);
                        return;
                    }
                }

                // ✅ Editor ops NGOÀI transaction
                if (report != null)
                {
                    SetStatus(report, Colors.LightBlue);
                    doc.Editor.WriteMessage($"\n[AUDIT]\n{report}");
                }
            }
        }

        private static string BuildAuditReport(List<Circle> holes, LashingInputParams p)
        {
            if (holes.Count == 0)
                return $"AUDIT: No holes on '{LashingInputParams.LAYER_INNER_HOLE}' inside boundary.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"AUDIT RESULT: {holes.Count} hole(s) found on '{LashingInputParams.LAYER_INNER_HOLE}'.");

            if (holes.Count < 2)
            {
                sb.Append("  → Not enough holes to check spacing.");
                return sb.ToString();
            }

            // Kiểm tra spacing theo nhóm hàng (Y) và cột (X)
            const double GROUP_TOL = 5.0;
            int irregular = 0;

            var rows = holes.GroupBy(c => Math.Round(c.Center.Y / GROUP_TOL) * GROUP_TOL);
            foreach (var row in rows)
            {
                var sorted = row.OrderBy(c => c.Center.X).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    double dist = Math.Abs(sorted[i].Center.X - sorted[i - 1].Center.X);
                    if (Math.Abs(dist - p.SpacingX) > GROUP_TOL) irregular++;
                }
            }

            var cols = holes.GroupBy(c => Math.Round(c.Center.X / GROUP_TOL) * GROUP_TOL);
            foreach (var col in cols)
            {
                var sorted = col.OrderBy(c => c.Center.Y).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    double dist = Math.Abs(sorted[i].Center.Y - sorted[i - 1].Center.Y);
                    if (Math.Abs(dist - p.SpacingY) > GROUP_TOL) irregular++;
                }
            }

            sb.Append(irregular > 0
                ? $"  → {irregular} adjusted spacing(s) detected (non-standard)."
                : "  → All spacings match standard. OK.");

            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────
        // Gap #6: Auto-detect boundary — Area > 20 m² (VBA logic)
        // ─────────────────────────────────────────────────────────────
        private bool TryAutoDetectBoundary(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            const double MIN_AREA_MM2 = 20_000_000.0;

            DiagLogger.Log("TryAutoDetectBoundary: SelectAll LWPOLYLINE (DXF filter)...");

            // Dùng SelectAll với DXF filter thay vì manual iteration ModelSpace.
            // AutoCAD native selection code tự xử lý proxy entities an toàn.
            // Tuyệt đối KHÔNG dùng foreach(ObjectId id in BlockTableRecord) —
            // native iterator crash khi gặp proxy/custom entity trong DWG phức tạp.
            var filter    = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            var selResult = doc.Editor.SelectAll(filter);

            DiagLogger.Log($"TryAutoDetectBoundary: SelectAll done, status={selResult.Status}, count={selResult.Value?.Count}");

            if (selResult.Status != PromptStatus.OK || selResult.Value == null || selResult.Value.Count == 0)
            {
                DiagLogger.Log("TryAutoDetectBoundary: no LWPOLYLINE found → false");
                return false;
            }

            ObjectId bestId     = ObjectId.Null;
            double   bestArea   = MIN_AREA_MM2;
            string   bestHandle = "";

            DiagLogger.Log("TryAutoDetectBoundary: opening transaction to check areas...");
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                DiagLogger.Log("TryAutoDetectBoundary: transaction started");
                foreach (SelectedObject so in selResult.Value)
                {
                    try
                    {
                        if (!(tr.GetObject(so.ObjectId, OpenMode.ForRead) is Polyline poly)) continue;
                        double area = poly.Area;
                        if (area > bestArea)
                        {
                            bestArea   = area;
                            bestId     = so.ObjectId;
                            bestHandle = poly.Handle.ToString();
                        }
                    }
                    catch { continue; }
                }
                tr.Commit();
                DiagLogger.Log("TryAutoDetectBoundary: transaction committed");
            }

            if (bestId.IsNull)
            {
                DiagLogger.Log("TryAutoDetectBoundary: no polyline with area > 20m²  → false");
                return false;
            }

            _boundaryId = bestId;
            DiagLogger.Log($"TryAutoDetectBoundary: found Handle={bestHandle}, Area={bestArea / 1e6:F1}m²");

            // Cập nhật UI NGOÀI transaction — không dùng Dispatcher.Invoke (đang trên UI thread)
            lblBoundary.Text       = $"Auto-detected | Handle: {bestHandle} | Area: {bestArea / 1e6:F1} m²";
            lblBoundary.Foreground = Brushes.CornflowerBlue;

            DiagLogger.Log("TryAutoDetectBoundary: return true");
            return true;
        }

        // Gap #7: Auto-select structures trong CrossingWindow của boundary
        private void TryAutoSelectStructures(
            Autodesk.AutoCAD.ApplicationServices.Document doc, LashingInputParams p)
        {
            try
            {
                // ✅ BƯỚC 1: Đọc extents trong transaction riêng, commit ngay
                Extents3d box;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var boundary = tr.GetObject(_boundaryId, OpenMode.ForRead) as Polyline;
                    if (boundary == null) { tr.Commit(); return; }
                    box = boundary.GeometricExtents;
                    tr.Commit(); // ✅ Commit TRƯỚC khi gọi bất kỳ editor op nào
                }

                // ✅ SelectCrossingWindow NGOÀI transaction
                // SelectCrossingWindow dùng model coordinates — không cần viewport zoom
                double margin    = 500;
                var    selResult = doc.Editor.SelectCrossingWindow(
                    new Point3d(box.MinPoint.X - margin, box.MinPoint.Y - margin, 0),
                    new Point3d(box.MaxPoint.X + margin, box.MaxPoint.Y + margin, 0));

                if (selResult.Status == PromptStatus.OK)
                {
                    // Lọc ngay: chỉ giữ Curve entities — proxy/block/text gây crash khi GetObject
                    var curveClass = AcRuntime.RXObject.GetClass(typeof(Curve));
                    _structureIds = selResult.Value.GetObjectIds()
                        .Where(id => id != _boundaryId && id.IsValid && !id.IsErased &&
                                     id.ObjectClass.IsDerivedFrom(curveClass))
                        .ToList();

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        lblStructures.Text      = $"Auto: {_structureIds.Count} structural element(s)";
                        lblStructures.Foreground = Brushes.CornflowerBlue;
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"{LOG_PREFIX} TryAutoSelectStructures: {ex.Message}");
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
