using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    /// <summary>
    /// Coverage operations ported from the RevitMcpServer command set that are not
    /// covered by McpOps/ModelingOps. Kept separate from the transport layer.
    /// </summary>
    internal static class ExtendedOps
    {
        private static Document Doc(UIApplication app)
        {
            return app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("Нет активного документа.");
        }

        private static double Mm(double mm) => mm / 304.8;

        private static XYZ P(JToken token)
        {
            var values = token?.ToObject<double[]>() ?? new double[] { 0, 0, 0 };
            return new XYZ(Mm(values.Length > 0 ? values[0] : 0),
                Mm(values.Length > 1 ? values[1] : 0),
                Mm(values.Length > 2 ? values[2] : 0));
        }

        private static object Describe(Element e, Document doc)
        {
            var level = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null;
            return new Dictionary<string, object>
            {
                { "id", e.Id.Value },
                { "category", e.Category?.Name ?? "" },
                { "name", e.Name ?? "" },
                { "typeName", doc.GetElement(e.GetTypeId())?.Name ?? "" },
                { "level", level ?? "" },
                { "uniqueId", e.UniqueId ?? "" },
            };
        }

        public static object CurrentViewInfo(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("Нет активного документа.");
            var view = uidoc.ActiveView;
            return new Dictionary<string, object>
            {
                { "id", view.Id.Value },
                { "name", view.Name },
                { "viewType", view.ViewType.ToString() },
                { "detailLevel", view.DetailLevel.ToString() },
                { "scale", view.Scale },
                { "isTemplate", view.IsTemplate },
                { "cropBoxActive", view.CropBoxActive },
                { "cropBoxVisible", view.CropBoxVisible },
            };
        }

        public static object CurrentViewElements(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var view = doc.ActiveView;
            var categories = args?["categories"]?.ToObject<List<string>>();
            var limit = Math.Max(1, args?["limit"]?.Value<int>() ?? 100);

            var items = new List<object>();
            var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
            foreach (var element in collector)
            {
                if (categories != null && categories.Count > 0 &&
                    !categories.Contains(element.Category?.Name ?? "", StringComparer.OrdinalIgnoreCase))
                    continue;
                items.Add(Describe(element, doc));
                if (items.Count >= limit) break;
            }

            return new Dictionary<string, object>
            {
                { "view", view.Name },
                { "count", items.Count },
                { "items", items },
            };
        }

        public static object SelectedElements(UIApplication app, JObject args)
        {
            var uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("Нет активного документа.");
            var doc = uidoc.Document;
            var limit = Math.Max(1, args?["limit"]?.Value<int>() ?? 100);
            var items = uidoc.Selection.GetElementIds()
                .Take(limit)
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .Select(e => Describe(e, doc))
                .ToList();
            return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
        }

        public static object TagWalls(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var view = doc.ActiveView;
            var limit = Math.Max(1, args?["limit"]?.Value<int>() ?? 500);
            var walls = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .Take(limit)
                .ToList();

            var created = 0;
            using (var tx = new Transaction(doc, "Bridge Tag Walls"))
            {
                tx.Start();
                foreach (var wall in walls)
                {
                    try
                    {
                        var point = (wall.Location as LocationCurve)?.Curve?.Evaluate(0.5, true) ?? XYZ.Zero;
                        var tag = IndependentTag.Create(doc, view.Id, new Reference(wall), false,
                            TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
                        if (tag != null) created++;
                    }
                    catch (Exception) { }
                }
                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                { "walls", walls.Count }, { "created", created },
                { "hint", created == 0 ? "Wall tag family is not loaded in the project." : "" },
            };
        }

        public static object TagRooms(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var view = doc.ActiveView;
            var limit = Math.Max(1, args?["limit"]?.Value<int>() ?? 500);
            var rooms = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Take(limit)
                .ToList();

            var created = 0;
            using (var tx = new Transaction(doc, "Bridge Tag Rooms"))
            {
                tx.Start();
                foreach (var room in rooms)
                {
                    try
                    {
                        var point = (room.Location as LocationPoint)?.Point ?? XYZ.Zero;
                        var tag = IndependentTag.Create(doc, view.Id, new Reference(room), false,
                            TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
                        if (tag != null) created++;
                    }
                    catch (Exception) { }
                }
                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                { "rooms", rooms.Count }, { "created", created },
                { "hint", created == 0 ? "Room tag family is not loaded in the project." : "" },
            };
        }

        public static object ModelStatistics(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements();
            var byCategory = elements.GroupBy(e => e.Category?.Name ?? "<none>")
                .OrderByDescending(g => g.Count())
                .Select(g => new Dictionary<string, object> { { "category", g.Key }, { "count", g.Count() } })
                .ToList();
            var byType = elements.GroupBy(e => doc.GetElement(e.GetTypeId())?.Name ?? "<none>")
                .OrderByDescending(g => g.Count())
                .Take(50)
                .Select(g => new Dictionary<string, object> { { "type", g.Key }, { "count", g.Count() } })
                .ToList();
            var byLevel = elements.GroupBy(e => e.LevelId != ElementId.InvalidElementId
                    ? doc.GetElement(e.LevelId)?.Name ?? "<none>" : "<none>")
                .OrderByDescending(g => g.Count())
                .Select(g => new Dictionary<string, object> { { "level", g.Key }, { "count", g.Count() } })
                .ToList();

            return new Dictionary<string, object>
            {
                { "total", elements.Count },
                { "byCategory", byCategory },
                { "byType", byType },
                { "byLevel", byLevel },
            };
        }

        public static object MaterialQuantities(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements();
            var aggregate = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in elements)
            {
                ICollection<ElementId> materialIds;
                try { materialIds = element.GetMaterialIds(false); }
                catch (Exception) { continue; }

                foreach (var materialId in materialIds)
                {
                    var material = doc.GetElement(materialId);
                    if (material == null) continue;
                    var area = 0.0;
                    var volume = 0.0;
                    try { area = element.GetMaterialArea(materialId, false); } catch (Exception) { }
                    try { volume = element.GetMaterialVolume(materialId); } catch (Exception) { }

                    if (!aggregate.TryGetValue(material.Name, out var values))
                        aggregate[material.Name] = values = new double[2];
                    values[0] += area;
                    values[1] += volume;
                }
            }

            var items = aggregate.OrderByDescending(kv => kv.Value[1])
                .Select(kv => new Dictionary<string, object>
                {
                    { "material", kv.Key },
                    { "area_m2", Math.Round(kv.Value[0], 3) },
                    { "volume_m3", Math.Round(kv.Value[1], 3) },
                }).ToList();

            return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
        }

        public static object FilterElements(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var category = args?.Value<string>("category");
            var level = args?.Value<string>("level");
            var typeName = args?.Value<string>("typeName");
            var parameterName = args?.Value<string>("parameterName");
            var parameterValue = args?.Value<string>("parameterValue");
            var limit = Math.Max(1, args?.Value<int?>("limit") ?? 200);

            var items = new List<object>();
            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (!string.IsNullOrWhiteSpace(category) &&
                    !string.Equals(element.Category?.Name, category, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(level))
                {
                    var levelName = element.LevelId != ElementId.InvalidElementId
                        ? doc.GetElement(element.LevelId)?.Name : null;
                    if (!string.Equals(levelName, level, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (!string.IsNullOrWhiteSpace(typeName) &&
                    !string.Equals(doc.GetElement(element.GetTypeId())?.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(parameterName))
                {
                    var parameter = element.LookupParameter(parameterName);
                    if (parameter == null) continue;
                    var value = parameter.AsValueString() ?? parameter.AsString() ?? parameter.AsInteger().ToString();
                    if (!string.IsNullOrWhiteSpace(parameterValue) &&
                        !string.Equals(value, parameterValue, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                items.Add(Describe(element, doc));
                if (items.Count >= limit) break;
            }

            return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
        }

        public static object OperateElement(UIApplication app, JObject args)
        {
            var uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("Нет активного документа.");
            var doc = uidoc.Document;
            var view = doc.ActiveView;
            var action = (args?.Value<string>("action") ?? "select").ToLowerInvariant();
            var ids = (args?["ids"]?.ToObject<long[]>() ?? Array.Empty<long>())
                .Select(v => new ElementId(v)).ToList();
            if (ids.Count == 0) throw new ArgumentException("args.ids is required");

            if (action == "select")
            {
                uidoc.Selection.SetElementIds(ids);
                return new Dictionary<string, object> { { "action", action }, { "count", ids.Count } };
            }

            using (var tx = new Transaction(doc, "Bridge Operate Element"))
            {
                tx.Start();
                switch (action)
                {
                    case "hide":
                        view.HideElements(ids);
                        break;
                    case "isolate":
                        view.IsolateElementsTemporary(ids);
                        break;
                    case "unhide":
                        view.UnhideElements(ids);
                        break;
                    case "color":
                        var color = ParseColor(args?.Value<string>("color") ?? "#FF0000");
                        var overrides = new OverrideGraphicSettings();
                        overrides.SetProjectionLineColor(color);
                        overrides.SetSurfaceForegroundPatternColor(color);
                        foreach (var id in ids) view.SetElementOverrides(id, overrides);
                        break;
                    default:
                        throw new ArgumentException("Unknown action: " + action);
                }
                tx.Commit();
            }

            return new Dictionary<string, object> { { "action", action }, { "count", ids.Count } };
        }

        public static object ElementParameters(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var elementId = args?.Value<long?>("elementId")
                ?? throw new ArgumentException("args.elementId is required");
            var element = doc.GetElement(new ElementId(elementId))
                ?? throw new InvalidOperationException("Element not found: " + elementId);

            var items = new List<Dictionary<string, object>>();
            void Collect(Element source, string scope)
            {
                if (source == null) return;
                foreach (Parameter p in source.Parameters)
                {
                    object value;
                    switch (p.StorageType)
                    {
                        case StorageType.String: value = p.AsString(); break;
                        case StorageType.Integer: value = p.AsInteger(); break;
                        case StorageType.Double: value = p.AsDouble(); break;
                        case StorageType.ElementId: value = p.AsElementId()?.Value; break;
                        default: value = null; break;
                    }
                    items.Add(new Dictionary<string, object>
                    {
                        { "scope", scope },
                        { "name", p.Definition?.Name ?? "" },
                        { "builtIn", (p.Definition as InternalDefinition)?.BuiltInParameter.ToString() ?? "" },
                        { "storageType", p.StorageType.ToString() },
                        { "value", value },
                        { "valueString", p.AsValueString() ?? "" },
                        { "isReadOnly", p.IsReadOnly },
                        { "isShared", p.IsShared },
                    });
                }
            }

            Collect(element, "instance");
            var type = doc.GetElement(element.GetTypeId());
            Collect(type, "type");

            return new Dictionary<string, object>
            {
                { "id", element.Id.Value },
                { "name", element.Name ?? "" },
                { "typeName", type?.Name ?? "" },
                { "count", items.Count },
                { "parameters", items },
            };
        }

        public static object ColorSplash(UIApplication app, JObject args)
        {
            var uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("Нет активного документа.");
            var doc = uidoc.Document;
            var view = doc.ActiveView;
            var parameterName = args?.Value<string>("parameterName");
            if (string.IsNullOrWhiteSpace(parameterName)) throw new ArgumentException("args.parameterName is required");

            var elements = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .ToElements();

            var values = new List<string>();
            foreach (var element in elements)
            {
                var p = element.LookupParameter(parameterName);
                var value = p?.AsValueString() ?? p?.AsString();
                if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value)) values.Add(value);
            }

            var palette = new[]
            {
                new Color(0x2E, 0x86, 0xDE), new Color(0xD9, 0x53, 0x4F), new Color(0x2A, 0xA1, 0x98),
                new Color(0xE6, 0x9F, 0x00), new Color(0x8E, 0x44, 0xAD), new Color(0x5A, 0xA5, 0x50),
                new Color(0xC0, 0x39, 0x2B), new Color(0x7F, 0x8C, 0x8D),
            };

            var pattern = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(p => p.GetFillPattern().IsSolidFill);

            var mapping = new Dictionary<string, string>();
            using (var tx = new Transaction(doc, "Bridge Color Splash"))
            {
                tx.Start();
                for (var i = 0; i < values.Count; i++)
                {
                    var value = values[i];
                    var color = palette[i % palette.Length];
                    mapping[value] = string.Format("#{0:X2}{1:X2}{2:X2}", color.Red, color.Green, color.Blue);
                    var overrides = new OverrideGraphicSettings();
                    overrides.SetProjectionLineColor(color);
                    overrides.SetSurfaceForegroundPatternColor(color);
                    if (pattern != null)
                    {
                        overrides.SetSurfaceForegroundPatternId(pattern.Id);
                        overrides.SetSurfaceForegroundPatternVisible(true);
                    }
                    foreach (var element in elements)
                    {
                        var p = element.LookupParameter(parameterName);
                        var elementValue = p?.AsValueString() ?? p?.AsString();
                        if (string.Equals(elementValue, value, StringComparison.Ordinal))
                            view.SetElementOverrides(element.Id, overrides);
                    }
                }
                tx.Commit();
            }

            return new Dictionary<string, object> { { "parameter", parameterName }, { "values", mapping } };
        }

        public static object CreateDimensions(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var view = doc.ActiveView;
            var id1 = args?.Value<long?>("elementId1");
            var id2 = args?.Value<long?>("elementId2");
            if (id1 == null || id2 == null)
                throw new ArgumentException("args.elementId1 and args.elementId2 are required");

            var e1 = doc.GetElement(new ElementId(id1.Value));
            var e2 = doc.GetElement(new ElementId(id2.Value));
            if (e1 == null || e2 == null) throw new ArgumentException("Element not found");

            var p1 = (e1.Location as LocationPoint)?.Point
                     ?? (e1.Location as LocationCurve)?.Curve?.Evaluate(0.5, true)
                     ?? XYZ.Zero;
            var p2 = (e2.Location as LocationPoint)?.Point
                     ?? (e2.Location as LocationCurve)?.Curve?.Evaluate(0.5, true)
                     ?? XYZ.Zero;
            var offset = Mm(args?.Value<double?>("offsetMm") ?? 500);

            var refs = new ReferenceArray();
            refs.Append(new Reference(e1));
            refs.Append(new Reference(e2));
            var line = Line.CreateBound(new XYZ(p1.X, p1.Y + offset, p1.Z), new XYZ(p2.X, p2.Y + offset, p2.Z));

            Dimension dimension = null;
            using (var tx = new Transaction(doc, "Bridge Create Dimension"))
            {
                tx.Start();
                dimension = doc.Create.NewDimension(view, line, refs);
                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                { "id", dimension?.Id.Value ?? 0 },
                { "value", dimension?.ValueString ?? "" },
            };
        }

        public static object CreateStructuralFraming(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var levelId = args?.Value<long?>("levelId")
                ?? throw new ArgumentException("args.levelId is required");
            var level = doc.GetElement(new ElementId(levelId)) as Level
                ?? throw new ArgumentException("Level not found");
            var start = P(args?["startMm"]);
            var end = P(args?["endMm"]);
            var spacing = Mm(args?.Value<double?>("spacingMm") ?? 3000);
            var count = Math.Max(1, args?.Value<int?>("count") ?? 3);

            var symbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .Cast<FamilySymbol>()
                .FirstOrDefault();

            if (symbol == null)
                throw new InvalidOperationException("No structural framing family type is loaded.");

            var created = 0;
            using (var tx = new Transaction(doc, "Bridge Structural Framing"))
            {
                tx.Start();
                if (!symbol.IsActive) symbol.Activate();
                for (var i = 0; i < count; i++)
                {
                    var offset = spacing * i;
                    var s = new XYZ(start.X, start.Y + offset, start.Z);
                    var e = new XYZ(end.X, end.Y + offset, end.Z);
                    try
                    {
                        doc.Create.NewFamilyInstance(Line.CreateBound(s, e), symbol, level, StructuralType.Beam);
                        created++;
                    }
                    catch (Exception) { }
                }
                tx.Commit();
            }

            return new Dictionary<string, object> { { "created", created }, { "requested", count } };
        }

        public static object CreateLineElement(UIApplication app, JObject args)
        {
            var kind = (args?.Value<string>("kind") ?? "wall").ToLowerInvariant();
            var start = args?["startMm"]?.ToObject<double[]>() ?? new double[] { 0, 0, 0 };
            var end = args?["endMm"]?.ToObject<double[]>() ?? new double[] { 1000, 0, 0 };

            switch (kind)
            {
                case "wall":
                    return McpOps.CreateWall(app, args?.Value<string>("typeId") ?? "",
                        args?.Value<string>("baseLevel") ?? "", args?.Value<string>("topLevel") ?? "",
                        start, end, args?.Value<bool?>("flip") ?? false);
                case "pipe":
                    return McpOps.CreatePipe(app, args?.Value<string>("systemType") ?? "",
                        args?.Value<string>("pipeType") ?? "", args?.Value<long?>("levelId") ?? 0,
                        start, end, args?.Value<double?>("diameterMm") ?? 100);
                case "duct":
                    return McpOps.CreateDuct(app, args?.Value<string>("systemType") ?? "",
                        args?.Value<string>("ductType") ?? "", args?.Value<long?>("levelId") ?? 0,
                        start, end, args?.Value<double?>("widthMm") ?? 400,
                        args?.Value<double?>("heightMm") ?? 200);
                default:
                    throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        private static Color ParseColor(string value)
        {
            value = (value ?? "#FF0000").TrimStart('#');
            if (value.Length != 6) return new Color(255, 0, 0);
            return new Color(
                byte.Parse(value.Substring(0, 2), NumberStyles.HexNumber),
                byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber),
                byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber));
        }
    }
}
