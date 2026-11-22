using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    /// <summary>
    /// Read-only collection of MEP elements for the OV specification.
    ///
    /// The collector resolves the real system through MEPSystem/connectors (not
    /// only through the "Имя системы" parameter), reads type and instance
    /// parameters, and returns diagnostics for orphan/missing data. Grouping and
    /// DOCX/PDF rendering live outside Revit.
    /// </summary>
    internal static class SpecOps
    {
        private const double MmPerFoot = 304.8;

        private static readonly string[] DefaultCategories =
        {
            "OST_DuctCurves",
            "OST_FlexDuctCurves",
            "OST_DuctFitting",
            "OST_DuctAccessory",
            "OST_DuctTerminal",
            "OST_DuctInsulations",
            "OST_MechanicalEquipment",
        };

        private static Document Doc(UIApplication app) =>
            app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Нет активного документа.");

        private static double Mm(double feet) => feet * MmPerFoot;
        private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        public static object Collect(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var categories = args?["categories"]?.ToObject<string[]>() ?? DefaultCategories;
            var levelIds = args?["levelIds"]?.ToObject<long[]>() ?? Array.Empty<long>();
            var systemNames = args?["systemNames"]?.ToObject<string[]>() ?? Array.Empty<string>();
            var includeUnassigned = args?.Value<bool?>("includeUnassigned") ?? true;

            var elements = new List<Element>();
            var seen = new HashSet<long>();
            foreach (var categoryName in categories)
            {
                if (!Enum.TryParse(categoryName, true, out BuiltInCategory category)) continue;
                try
                {
                    foreach (var element in new FilteredElementCollector(doc)
                                 .OfCategory(category).WhereElementIsNotElementType())
                    {
                        if (seen.Add(element.Id.Value)) elements.Add(element);
                    }
                }
                catch (Exception) { }
            }

            if (levelIds.Length > 0)
            {
                var allowed = new HashSet<long>(levelIds);
                elements = elements.Where(e =>
                {
                    try { return allowed.Contains(e.LevelId.Value); }
                    catch (Exception) { return false; }
                }).ToList();
            }

            var rows = new List<Dictionary<string, object>>();
            var unassigned = new List<Dictionary<string, object>>();
            var missingSize = new List<Dictionary<string, object>>();
            var missingMark = new List<Dictionary<string, object>>();
            var missingUnit = new List<Dictionary<string, object>>();
            var excluded = new List<Dictionary<string, object>>();
            var systemCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in elements.OrderBy(e => e.Id.Value))
            {
                try
                {
                    var row = Describe(doc, element);
                    if (row.TryGetValue("exclude", out var excludeValue) && excludeValue is bool excludedFlag && excludedFlag)
                        excluded.Add(Brief(row));
                    var system = (string)row["systemName"];
                    if (string.IsNullOrWhiteSpace(system))
                    {
                        unassigned.Add(Brief(row));
                        if (!includeUnassigned) continue;
                    }
                    else
                    {
                        systemCounts[system] = systemCounts.TryGetValue(system, out var n) ? n + 1 : 1;
                        if (systemNames.Length > 0 &&
                            !systemNames.Any(s => SameSystem(s, system)))
                            continue;
                    }

                    if ((string)row["sizeText"] == "" &&
                        (double)row["diameterMm"] <= 0 &&
                        (double)row["widthMm"] <= 0 &&
                        (double)row["heightMm"] <= 0)
                        missingSize.Add(Brief(row));
                    if (string.IsNullOrWhiteSpace((string)row["mark"]))
                        missingMark.Add(Brief(row));
                    if (string.IsNullOrWhiteSpace((string)row["unit"]))
                        missingUnit.Add(Brief(row));

                    rows.Add(row);
                }
                catch (Exception ex)
                {
                    unassigned.Add(new Dictionary<string, object>
                    {
                        { "sourceId", element.Id.Value },
                        { "category", element.Category?.Name ?? "" },
                        { "error", ex.Message },
                    });
                }
            }

            var payload = new Dictionary<string, object>
            {
                { "count", rows.Count },
                { "elements", rows },
                { "diagnostics", new Dictionary<string, object>
                    {
                        { "targetCount", elements.Count },
                        { "assignedCount", elements.Count - unassigned.Count },
                        { "unassignedCount", unassigned.Count },
                        { "unassigned", unassigned },
                        { "missingSize", missingSize },
                        { "missingMark", missingMark },
                        { "missingUnit", missingUnit },
                        { "excludedCount", excluded.Count },
                        { "excluded", excluded },
                        { "systemCounts", systemCounts.Select(kv => new Dictionary<string, object>
                            { { "system", kv.Key }, { "count", kv.Value } }).ToList() },
                    }
                },
            };

            var resultFile = args?.Value<string>("resultFile");
            if (!string.IsNullOrWhiteSpace(resultFile))
            {
                try
                {
                    var folder = Path.GetDirectoryName(resultFile);
                    if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
                    File.WriteAllText(resultFile, JsonConvert.SerializeObject(payload, Formatting.Indented));
                }
                catch (Exception) { }
            }

            return payload;
        }

        private static Dictionary<string, object> Describe(Document doc, Element element)
        {
            var type = doc.GetElement(element.GetTypeId()) as ElementType;
            var family = type?.FamilyName ?? "";
            var typeName = type?.Name ?? "";
            var sizeText = ParamString(element, "Размер");
            if (string.IsNullOrWhiteSpace(sizeText)) sizeText = ParamString(element, "Свободный размер");

            var diameter = DiameterMm(element, type);
            var width = WidthMm(element, type);
            var height = HeightMm(element, type);
            var system = SystemNameOf(element);
            var mark = TypeString(type, "ADSK_Марка");
            if (string.IsNullOrWhiteSpace(mark)) mark = ParamString(element, "ADSK_Марка");
            var name = TypeString(type, "ADSK_Наименование");
            if (string.IsNullOrWhiteSpace(name))
            {
                var descriptor = TypeString(type, "Комментарии к типоразмеру");
                if (string.IsNullOrWhiteSpace(descriptor)) descriptor = family;
                if (string.IsNullOrWhiteSpace(descriptor)) descriptor = typeName;
                name = descriptor;
                if (!string.IsNullOrWhiteSpace(sizeText)) name += " " + sizeText;
            }
            var exclude = name.IndexOf("!Не учитывать", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          typeName.IndexOf("!Не учитывать", StringComparison.OrdinalIgnoreCase) >= 0;
            var unit = TypeString(type, "ADSK_Единица измерения");
            if (string.IsNullOrWhiteSpace(unit)) unit = CategoryUnit(element.Category?.Id.Value);
            var mass = ParamKg(type, element, "ADSK_Масса");
            var code = TypeString(type, "ADSK_Код изделия");
            if (string.IsNullOrWhiteSpace(code)) code = ParamString(element, "ADSK_Код изделия");
            var manufacturer = TypeString(type, "ADSK_Завод-изготовитель");
            if (string.IsNullOrWhiteSpace(manufacturer)) manufacturer = ParamString(element, "ADSK_Завод-изготовитель");

            var lengthM = 0.0;
            var curveLengthM = 0.0;
            var lengthDeltaMm = 0.0;
            if (element is MEPCurve curve)
            {
                lengthM = ParamFeet(curve, BuiltInParameter.CURVE_ELEM_LENGTH) * 0.3048;
                try
                {
                    if ((curve.Location as LocationCurve)?.Curve != null)
                    {
                        curveLengthM = curve.Location as LocationCurve != null
                            ? ((LocationCurve)curve.Location).Curve.Length * 0.3048
                            : 0.0;
                        lengthDeltaMm = Math.Abs(lengthM - curveLengthM) * 1000.0;
                    }
                }
                catch (Exception) { }
            }

            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(system)) warnings.Add("no_system");
            if (diameter <= 0 && width <= 0 && height <= 0 && string.IsNullOrWhiteSpace(sizeText))
                warnings.Add("no_size");
            if (string.IsNullOrWhiteSpace(mark)) warnings.Add("no_mark");
            if (lengthDeltaMm > 1.0) warnings.Add("length_delta");

            return new Dictionary<string, object>
            {
                { "sourceId", element.Id.Value },
                { "category", element.Category?.Name ?? "" },
                { "family", family },
                { "typeName", typeName },
                { "systemName", system },
                { "systemType", SystemTypeOf(element) },
                { "level", LevelOf(doc, element) },
                { "sizeText", sizeText },
                { "sizeKind", diameter > 0 ? "round" : (width > 0 || height > 0 ? "rect" : "") },
                { "diameterMm", diameter },
                { "widthMm", width },
                { "heightMm", height },
                { "lengthM", lengthM },
                { "curveLengthM", curveLengthM },
                { "lengthDeltaMm", lengthDeltaMm },
                { "name", name },
                { "mark", mark },
                { "code", code },
                { "manufacturer", manufacturer },
                { "unit", unit },
                { "massKg", mass > 0 ? mass : (object)null },
                { "exclude", exclude },
                { "excludeReason", exclude ? "template_marker" : "" },
                { "warnings", warnings },
            };
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
                        if (connector.MEPSystem != null) return connector.MEPSystem.Name;
                }
            }
            catch (Exception) { }
            return ParamString(element, "Имя системы");
        }

        private static string SystemTypeOf(Element element)
        {
            try
            {
                if (element is MEPCurve curve && curve.MEPSystem is MechanicalSystem system)
                    return system.SystemType.ToString();
            }
            catch (Exception) { }
            return "";
        }

        private static string LevelOf(Document doc, Element element)
        {
            try
            {
                if (element.LevelId != ElementId.InvalidElementId)
                    return doc.GetElement(element.LevelId)?.Name ?? "";
            }
            catch (Exception) { }
            return "";
        }

        private static double DiameterMm(Element element, ElementType type)
        {
            try
            {
                if (element is MEPCurve curve)
                {
                    var d = Mm(curve.Diameter);
                    if (d > 0.1) return d;
                }
            }
            catch (Exception) { }
            var value = ParamMm(element, "Диаметр");
            if (value > 0.1) return value;
            return ParamMm(type, "Диаметр");
        }

        private static double WidthMm(Element element, ElementType type)
        {
            try
            {
                if (element is MEPCurve curve)
                {
                    var value = Mm(curve.Width);
                    if (value > 0.1) return value;
                }
            }
            catch (Exception) { }
            var param = ParamMm(element, "Ширина");
            return param > 0.1 ? param : ParamMm(type, "Ширина");
        }

        private static double HeightMm(Element element, ElementType type)
        {
            try
            {
                if (element is MEPCurve curve)
                {
                    var value = Mm(curve.Height);
                    if (value > 0.1) return value;
                }
            }
            catch (Exception) { }
            var param = ParamMm(element, "Высота");
            return param > 0.1 ? param : ParamMm(type, "Высота");
        }

        private static string CategoryUnit(long? categoryId)
        {
            if (categoryId == null) return "";
            var id = categoryId.Value;
            if (id == (long)BuiltInCategory.OST_DuctCurves ||
                id == (long)BuiltInCategory.OST_FlexDuctCurves) return "м";
            if (id == (long)BuiltInCategory.OST_DuctFitting ||
                id == (long)BuiltInCategory.OST_DuctAccessory ||
                id == (long)BuiltInCategory.OST_DuctTerminal ||
                id == (long)BuiltInCategory.OST_MechanicalEquipment) return "шт.";
            if (id == (long)BuiltInCategory.OST_DuctInsulations) return "м³";
            return "";
        }

        private static string ParamString(Element element, string name)
        {
            try { return element?.LookupParameter(name)?.AsString() ?? ""; }
            catch (Exception) { return ""; }
        }

        private static string TypeString(ElementType type, string name)
        {
            try { return type?.LookupParameter(name)?.AsString() ?? ""; }
            catch (Exception) { return ""; }
        }

        private static double ParamMm(Element element, string name)
        {
            try
            {
                var p = element?.LookupParameter(name);
                return p == null ? 0.0 : p.AsDouble() * MmPerFoot;
            }
            catch (Exception) { return 0.0; }
        }

        private static double ParamFeet(Element element, BuiltInParameter parameter)
        {
            try { return element?.get_Parameter(parameter)?.AsDouble() ?? 0.0; }
            catch (Exception) { return 0.0; }
        }

        private static double ParamKg(ElementType type, Element element, string name)
        {
            try
            {
                var p = type?.LookupParameter(name) ?? element.LookupParameter(name);
                return p?.AsDouble() ?? 0.0;
            }
            catch (Exception) { return 0.0; }
        }

        private static bool SameSystem(string a, string b) =>
            string.Equals((a ?? "").Replace(" ", ""), (b ?? "").Replace(" ", ""),
                StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, object> Brief(Dictionary<string, object> row) =>
            new Dictionary<string, object>
            {
                { "sourceId", row["sourceId"] },
                { "category", row["category"] },
                { "name", row["name"] },
                { "systemName", row["systemName"] },
                { "sizeText", row["sizeText"] },
            };
    }
}
