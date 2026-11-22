using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    /// <summary>
    /// Diagnostics for an existing title block family. Used before creating
    /// wide variants: we need the exact frame/stamp element geometry, not a
    /// hand-made stamp.
    /// </summary>
    internal static class WideTitleBlockOps
    {
        private const double MmPerFoot = 304.8;
        private static Document Doc(UIApplication app) =>
            app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Нет активного документа.");

        public static object Inspect(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var name = args?.Value<string>("familyName") ?? "Форма 3";
            var family = new FilteredElementCollector(doc).OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Family not found: " + name);

            var familyDoc = doc.EditFamily(family);
            if (familyDoc == null) throw new InvalidOperationException("EditFamily returned null.");
            try
            {
                var view = familyDoc.ActiveView
                           ?? new FilteredElementCollector(familyDoc).OfClass(typeof(View))
                               .Cast<View>().FirstOrDefault(v => !v.IsTemplate);
                var items = new List<Dictionary<string, object>>();
                foreach (var element in new FilteredElementCollector(familyDoc)
                             .WhereElementIsNotElementType())
                {
                    var box = element.get_BoundingBox(view);
                    if (box == null) continue;
                    var curve = (element as DetailLine)?.GeometryCurve
                                ?? (element as ModelLine)?.GeometryCurve;
                    var item = new Dictionary<string, object>
                    {
                        { "id", element.Id.Value },
                        { "type", element.GetType().Name },
                        { "category", element.Category?.Name ?? "" },
                        { "name", element.Name ?? "" },
                        { "x0Mm", box.Min.X * MmPerFoot },
                        { "y0Mm", box.Min.Y * MmPerFoot },
                        { "x1Mm", box.Max.X * MmPerFoot },
                        { "y1Mm", box.Max.Y * MmPerFoot },
                    };
                    if (curve != null)
                    {
                        item["p0"] = new[] { curve.GetEndPoint(0).X * MmPerFoot, curve.GetEndPoint(0).Y * MmPerFoot };
                        item["p1"] = new[] { curve.GetEndPoint(1).X * MmPerFoot, curve.GetEndPoint(1).Y * MmPerFoot };
                    }
                    items.Add(item);
                }
                return new Dictionary<string, object>
                {
                    { "family", family.Name },
                    { "count", items.Count },
                    { "items", items.OrderBy(i => (double)i["x0Mm"]).ThenBy(i => (double)i["y0Mm"]).ToList() },
                };
            }
            finally
            {
                familyDoc.Close(false);
            }
        }
    }
}
