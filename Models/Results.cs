using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SDS.Core.Models
{
    public class ApdlResult : INotifyPropertyChanged
    {
        public Point3d Origin { get; set; }
        public Vector3d XAxis { get; set; }
        public Vector3d YAxis { get; set; }
        public string BlockName { get; set; }
        public double FactorValue { get; set; }
        public Handle VehicleHandle { get; set; }
        public string TirePrintLayer { get; set; } = "x_V-tpr";
        public double TextHeight { get; set; }
        private int _timeStep;
        private double _force;
        private int _csysId;
        public double XMin_Orig { get; set; }
        public double XMax_Orig { get; set; }
        public double YMin_Orig { get; set; }
        public double YMax_Orig { get; set; }

        public double Force { get; set; }
        public int TimeStep { get => _timeStep; set { _timeStep = value; OnPropertyChanged(); } }
        public int CsysId { get => _csysId; set { _csysId = value; OnPropertyChanged(); } }
        // public double Force { get => _force; set { _force = value; OnPropertyChanged(); } }

        public double XMin { get; set; }
        public double XMax { get; set; }
        public double YMin { get; set; }
        public double YMax { get; set; }
        public Point3dCollection WorldVertices { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}