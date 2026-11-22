using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Core;
using Rect = RevitBridge.SheetLayoutOps.Rect;

namespace RevitBridge
{
    /// <summary>
    /// View-side helpers for sheet composition: snapshots, view duplication,
    /// crop resolution, system filters and viewport UI.
    /// </summary>
    internal static class SheetViewOps
    {
        internal static Dictionary<string, object> SnapshotView(View view)
        {
            var result = new Dictionary<string, object>
            {
                { "scale", view.Scale },
                { "cropActive", view.CropBoxActive },
                { "cropMin", "" },
                { "cropMax", "" },
            };
            if (view.CropBoxActive && view.CropBox != null)
            {
                result["cropMin"] = $"{BridgeUnits.Mm(view.CropBox.Min.X):F3};{BridgeUnits.Mm(view.CropBox.Min.Y):F3}";
                result["cropMax"] = $"{BridgeUnits.Mm(view.CropBox.Max.X):F3};{BridgeUnits.Mm(view.CropBox.Max.Y):F3}";
            }
            return result;
        }
        internal static View DuplicateView(Document doc, View source, string requestedName,
            string sheetNumber, bool independent)
        {
            var option = independent ? ViewDuplicateOption.Duplicate : ViewDuplicateOption.AsDependent;
            ElementId id;
            View view;
            using (var tx = new Transaction(doc, "Bridge Compose Sheet: duplicate view"))
            {
                tx.Start();
                try { id = source.Duplicate(option); }
                catch (Exception) { id = source.Duplicate(ViewDuplicateOption.AsDependent); }
                view = doc.GetElement(id) as View
                       ?? throw new InvalidOperationException("Failed to duplicate view: " + source.Name);
                try
                {
                    view.Name = string.IsNullOrWhiteSpace(requestedName)
                        ? source.Name + "_L" + (sheetNumber ?? "").Replace(" ", "")
                        : requestedName;
                }
                catch (Exception) { }
                tx.Commit();
            }
            return view;
        }
        internal static Rect ResolveCrop(Document doc, JObject spec, View source,
            double cropMargin, bool includeLinked)
        {
            var explicitCrop = ParseRect4(spec["cropMm"]);
            if (explicitCrop != null) { explicitCrop.Name = source.Name; return explicitCrop; }

            var systemId = spec.Value<long?>("systemId");
            if (systemId.HasValue)
            {
                var system = doc.GetElement(new ElementId(systemId.Value)) as MEPSystem;
                if (system == null)
                    throw new InvalidOperationException("MEP system not found: " + systemId.Value);
                var rect = BBoxOf(system.Elements.Cast<Element>(), cropMargin);
                if (rect != null) { rect.Name = source.Name + " (" + system.Name + ")"; return rect; }
            }

            if (spec.Value<bool?>("autoCrop") ?? false)
            {
                var rect = AutoCrop(doc, source, cropMargin, includeLinked);
                if (rect != null) return rect;
            }

            return null;
        }
        internal static Rect ParseRect4(JToken token)
        {
            if (token == null) return null;
            var values = token.ToObject<double[]>();
            if (values == null || values.Length != 4) return null;
            return new Rect { X0 = values[0], Y0 = values[1], X1 = values[2], Y1 = values[3] };
        }
        internal static Rect ViewRect(View view)
        {
            var outline = view.Outline;
            if (outline == null) return new Rect { X0 = 0, Y0 = 0, X1 = 100, Y1 = 100 };
            var scale = Math.Max(view.Scale, 1);
            return new Rect
            {
                X0 = 0, Y0 = 0,
                X1 = (outline.Max.U - outline.Min.U) * BridgeUnits.MmPerFt * scale,
                Y1 = (outline.Max.V - outline.Min.V) * BridgeUnits.MmPerFt * scale,
            };
        }
        internal static void SetCrop(View view, Rect rect)
        {
            var level = (view as ViewPlan)?.GenLevel;
            var zBase = level?.Elevation ?? 0;
            var box = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(BridgeUnits.Ft(rect.X0), BridgeUnits.Ft(rect.Y0), zBase - BridgeUnits.Ft(1000)),
                Max = new XYZ(BridgeUnits.Ft(rect.X1), BridgeUnits.Ft(rect.Y1), zBase + BridgeUnits.Ft(10000)),
            };
            view.CropBox = box;
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
        }
        internal static Dictionary<string, object> RectInfo(Rect rect)
        {
            return new Dictionary<string, object>
            {
                { "x0Mm", rect.X0 }, { "y0Mm", rect.Y0 },
                { "x1Mm", rect.X1 }, { "y1Mm", rect.Y1 },
                { "widthMm", rect.W }, { "heightMm", rect.H },
            };
        }
        /// <summary>
        /// Hide the other systems and colour the requested one. This is a test
        /// aid: the production version should use parameter filters instead of
        /// per-element overrides.
        /// </summary>
        internal static void ApplySystemFilter(Document doc, View view, string systemName,
            bool colorBySystem)
        {
            var categories = new[]
            {
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_DuctTerminal,
                BuiltInCategory.OST_DuctInsulations,
                BuiltInCategory.OST_MechanicalEquipment,
            };
            var elements = new List<Element>();
            foreach (var category in categories)
            {
                try
                {
                    elements.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(category).WhereElementIsNotElementType());
                }
                catch (Exception) { }
            }

            using (var tx = new Transaction(doc, "Bridge Compose Sheet: system filter"))
            {
                tx.Start();
                foreach (var element in elements)
                {
                    var name = SystemNameOf(element);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var isTarget = !string.IsNullOrWhiteSpace(systemName) &&
                                   string.Equals(name, systemName, StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(systemName) && !isTarget)
                    {
                        try { view.HideElements(new List<ElementId> { element.Id }); }
                        catch (Exception) { }
                        continue;
                    }
                    if (!isTarget && !colorBySystem) continue;

                    var color = name.StartsWith("П", StringComparison.OrdinalIgnoreCase)
                        ? new Color(220, 30, 30)
                        : name.StartsWith("В", StringComparison.OrdinalIgnoreCase)
                            ? new Color(30, 60, 220)
                            : null;
                    if (color == null) continue;
                    try
                    {
                        var overrides = new OverrideGraphicSettings();
                        overrides.SetProjectionLineColor(color);
                        overrides.SetProjectionLineWeight(3);
                        view.SetElementOverrides(element.Id, overrides);
                    }
                    catch (Exception) { }
                }
                tx.Commit();
            }
        }
        internal static Dictionary<string, object> EnableViewportUi(Document doc, Viewport vp)
        {
            var report = new Dictionary<string, object>();
            var parameters = new[]
            {
                BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL,
                BuiltInParameter.VIEWPORT_ATTR_SHOW_BOX,
                BuiltInParameter.VIEWPORT_ATTR_SHOW_EXTENSION_LINE,
            };
            var type = doc.GetElement(vp.GetTypeId()) as ElementType;
            report["typeId"] = vp.GetTypeId().Value;
            report["typeName"] = type?.Name ?? "";
            foreach (var bip in parameters)
            {
                var key = bip.ToString();
                var entry = new Dictionary<string, object>();
                try
                {
                    var inst = vp.get_Parameter(bip);
                    entry["instance"] = inst == null ? "absent" :
                        (inst.IsReadOnly ? "readonly:" + inst.AsInteger() : "writable:" + inst.AsInteger());
                    if (inst != null && !inst.IsReadOnly) inst.Set(1);
                }
                catch (Exception ex) { entry["instanceError"] = ex.Message; }
                try
                {
                    var tp = type?.get_Parameter(bip);
                    entry["type"] = tp == null ? "absent" :
                        (tp.IsReadOnly ? "readonly:" + tp.AsInteger() : "writable:" + tp.AsInteger());
                    if (tp != null && !tp.IsReadOnly) tp.Set(1);
                }
                catch (Exception ex) { entry["typeError"] = ex.Message; }
                report[key] = entry;
            }
            return report;
        }
        private static string SystemNameOf(Element element)
        {
            try
            {
                if (element is MEPCurve curve && curve.MEPSystem != null)
                    return curve.MEPSystem.Name;
                if (element is FamilyInstance instance && instance.MEPModel?.ConnectorManager != null)
                {
                    foreach (Connector connector in instance.MEPModel.ConnectorManager.Connectors)
                    {
                        if (connector.MEPSystem != null) return connector.MEPSystem.Name;
                    }
                }
            }
            catch (Exception) { }
            try
            {
                return element.LookupParameter("Имя системы")?.AsString()
                       ?? element.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString()
                       ?? "";
            }
            catch (Exception) { return ""; }
        }
        private static Rect BBoxOf(IEnumerable<Element> elements, double margin)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            var found = false;
            foreach (var element in elements)
            {
                var box = element.get_BoundingBox(null);
                if (box == null) continue;
                minX = Math.Min(minX, BridgeUnits.Mm(box.Min.X));
                minY = Math.Min(minY, BridgeUnits.Mm(box.Min.Y));
                maxX = Math.Max(maxX, BridgeUnits.Mm(box.Max.X));
                maxY = Math.Max(maxY, BridgeUnits.Mm(box.Max.Y));
                found = true;
            }
            if (!found) return null;
            return new Rect
            {
                X0 = minX - margin, Y0 = minY - margin,
                X1 = maxX + margin, Y1 = maxY + margin,
            };
        }
        private static Rect AutoCrop(Document doc, View view, double margin, bool includeLinked)
        {
            var level = (view as ViewPlan)?.GenLevel ?? doc.GetElement(view.LevelId) as Level;
            if (level == null) return null;
            var spaces = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType().Cast<Space>()
                .Where(s => s.LevelId == level.Id).ToList();
            var rect = BBoxOf(spaces, margin);
            if (rect != null) return rect;
            if (!includeLinked) return null;

            foreach (var link in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                var transform = link.GetTotalTransform();
                var rooms = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .Cast<Room>().Where(r => r.Level != null &&
                        string.Equals(r.Level.Name, level.Name, StringComparison.OrdinalIgnoreCase));
                var points = new List<XYZ>();
                foreach (var room in rooms)
                {
                    var box = room.get_BoundingBox(null);
                    if (box == null) continue;
                    points.Add(transform.OfPoint(new XYZ(box.Min.X, box.Min.Y, box.Min.Z)));
                    points.Add(transform.OfPoint(new XYZ(box.Max.X, box.Max.Y, box.Max.Z)));
                }
                if (points.Count == 0) continue;
                return new Rect
                {
                    X0 = BridgeUnits.Mm(points.Min(p => p.X)) - margin,
                    Y0 = BridgeUnits.Mm(points.Min(p => p.Y)) - margin,
                    X1 = BridgeUnits.Mm(points.Max(p => p.X)) + margin,
                    Y1 = BridgeUnits.Mm(points.Max(p => p.Y)) + margin,
                };
            }
            return null;
        }
    }
}
