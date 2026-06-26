using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using SDS.UI;

[assembly: CommandClass(typeof(SDS.Commands.LashingHoleCommands))]

namespace SDS.Commands
{
    public class LashingHoleCommands
    {
        private static PaletteSet _ps;

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
                    _ps.Size = new System.Drawing.Size(380, 740);
                    _ps.KeepFocus = true;
                }

                _ps.Visible = true;
                _ps.Dock = DockSides.Right;

                if (_ps.Count > 0)
                    _ps.Activate(0);
            }
            catch (System.Exception ex)
            {
                var ed = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage($"\nMCG_CreateLashingHole Error: {ex.Message}");
            }
        }
    }
}
