using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    internal static class BridgeOps
    {
        internal static Document Doc(UIApplication app)
        {
            return app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("РќРµС‚ Р°РєС‚РёРІРЅРѕРіРѕ РґРѕРєСѓРјРµРЅС‚Р°.");
        }

        internal static ElementId Id(JToken token, string key)
        {
            var value = token?.Value<long?>(key);
            if (value == null) throw new ArgumentException("args." + key + " is required");
            return new ElementId(value.Value);
        }

        internal static double Mm(double mm) => mm / 304.8;

        internal static XYZ P(JToken token)
        {
            var values = token?.ToObject<double[]>() ?? new double[] { 0, 0, 0 };
            return new XYZ(Mm(values.Length > 0 ? values[0] : 0),
                Mm(values.Length > 1 ? values[1] : 0),
                Mm(values.Length > 2 ? values[2] : 0));
        }
    }
}