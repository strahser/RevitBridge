namespace RevitBridge.Core
{
    /// <summary>Revit internal units (feet) ↔ millimetres.</summary>
    public static class BridgeUnits
    {
        public const double MmPerFt = 304.8;
        public static double Ft(double mm) => mm / MmPerFt;
        public static double Mm(double ft) => ft * MmPerFt;
    }
}
