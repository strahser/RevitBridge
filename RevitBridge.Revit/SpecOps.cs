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

        private sealed class SpecFilters
        {
            public string[] Categories;
            public long[] LevelIds;
            public string[] SystemNames;
            public bool IncludeUnassigned;

            public static SpecFilters Parse(JObject args) => new SpecFilters
            {
                Categories = args?["categories"]?.ToObject<string[]>() ?? DefaultCategories,
                LevelIds = args?["levelIds"]?.ToObject<long[]>() ?? Array.Empty<long>(),
                SystemNames = args?["systemNames"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                IncludeUnassigned = args?.Value<bool?>("includeUnassigned") ?? true,
            };
        }

        private sealed class SpecDiagnostics
        {
            public List<Dictionary<string, object>> Rows = new();
            public List<Dictionary<string, object>> Unassigned = new();
            public List<Dictionary<string, object>> MissingSize = new();
            public List<Dictionary<string, object>> MissingMark = new();
            public List<Dictionary<string, object>> MissingUnit = new();
            public List<Dictionary<string, object>> Excluded = new();
            public Dictionary<string, int> SystemCounts = new(StringComparer.OrdinalIgnoreCase);
        }

        private static List<Element> CollectElements(Document doc, SpecFilters filters)
        {
            var elements = new List<Element>();
            var seen = new HashSet<long>();
            foreach (var categoryName in filters.Categories)
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

            if (filters.LevelIds.Length > 0)
            {
                var allowed = new HashSet<long>(filters.LevelIds);
                elements = elements.Where(e =>
                {
                    try { return allowed.Contains(e.LevelId.Value); }
                    catch (Exception) { return false; }
                }).ToList();
            }

            return elements;
        }

        private static void AddSpecRow(Document doc, Element element, SpecFilters filters, SpecDiagnostics diagnostics)
        {
            try
            {
                var row = Describe(doc, element);
                if (row.TryGetValue("exclude", out var excludeValue) && excludeValue is bool excludedFlag && excludedFlag)
                    diagnostics.Excluded.Add(Brief(row));
                var system = (string)row["systemName"];
                if (string.IsNullOrWhiteSpace(system))
                {
                    diagnostics.Unassigned.Add(Brief(row));
                    if (!filters.IncludeUnassigned) return;
                }
                else
                {
                    diagnostics.SystemCounts[system] = diagnostics.SystemCounts.TryGetValue(system, out var n) ? n + 1 : 1;
                    if (filters.SystemNames.Length > 0 &&
                        !filters.SystemNames.Any(s => SameSystem(s, system)))
                        return;
                }

                if ((string)row["sizeText"] == "" &&
                    (double)row["diameterMm"] <= 0 &&
                    (double)row["widthMm"] <= 0 &&
                    (double)row["heightMm"] <= 0)
                    diagnostics.MissingSize.Add(Brief(row));
                if (string.IsNullOrWhiteSpace((string)row["mark"]))
                    diagnostics.MissingMark.Add(Brief(row));
                if (string.IsNullOrWhiteSpace((string)row["unit"]))
                    diagnostics.MissingUnit.Add(Brief(row));

                diagnostics.Rows.Add(row);
            }
            catch (Exception ex)
            {
                diagnostics.Unassigned.Add(new Dictionary<string, object>
                {
                    { "sourceId", element.Id.Value },
                    { "category", element.Category?.Name ?? "" },
                    { "error", ex.Message },
                });
            }
        }

        private static Dictionary<string, object> BuildPayload(SpecDiagnostics diagnostics, int targetCount)
        {
            return new Dictionary<string, object>
            {
                { "count", diagnostics.Rows.Count },
                { "elements", diagnostics.Rows },
                { "diagnostics", new Dictionary<string, object>
                    {
                        { "targetCount", targetCount },
                        { "assignedCount", targetCount - diagnostics.Unassigned.Count },
                        { "unassignedCount", diagnostics.Unassigned.Count },
                        { "unassigned", diagnostics.Unassigned },
                        { "missingSize", diagnostics.MissingSize },
                        { "missingMark", diagnostics.MissingMark },
                        { "missingUnit", diagnostics.MissingUnit },
                        { "excludedCount", diagnostics.Excluded.Count },
                        { "excluded", diagnostics.Excluded },
                        { "systemCounts", diagnostics.SystemCounts.Select(kv => new Dictionary<string, object>
                            { { "system", kv.Key }, { "count", kv.Value } }).ToList() },
                    }
                },
            };
        }

        private static void WriteResultFile(Dictionary<string, object> payload, string resultFile)
        {
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
        }

        public static object Collect(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var filters = SpecFilters.Parse(args);
            var elements = CollectElements(doc, filters);
            var diagnostics = new SpecDiagnostics();
            foreach (var element in elements.OrderBy(e => e.Id.Value))
                AddSpecRow(doc, element, filters, diagnostics);
            var payload = BuildPayload(diagnostics, elements.Count);
            WriteResultFile(payload, args?.Value<string>("resultFile"));
            return payload;
        }

        private static string ResolveName(ElementType type, string family, string typeName, string sizeText)
        {
            var name = TypeString(type, "ADSK_Наименование");
            if (string.IsNullOrWhiteSpace(name))
            {
                var descriptor = TypeString(type, "Комментарии к типоразмеру");
                if (string.IsNullOrWhiteSpace(descriptor)) descriptor = family;
                if (string.IsNullOrWhiteSpace(descriptor)) descriptor = typeName;
                name = descriptor;
                if (!string.IsNullOrWhiteSpace(sizeText)) name += " " + sizeText;
            }
            return name;
        }

        private static void ReadCurveLength(Element element, out double lengthM, out double curveLengthM, out double lengthDeltaMm)
        {
            lengthM = 0.0;
            curveLengthM = 0.0;
            lengthDeltaMm = 0.0;
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
        }

        private static List<string> BuildWarnings(string system, double diameter, double width, double height, string sizeText, string mark, double lengthDeltaMm)
        {
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(system)) warnings.Add("no_system");
            if (diameter <= 0 && width <= 0 && height <= 0 && string.IsNullOrWhiteSpace(sizeText))
                warnings.Add("no_size");
            if (string.IsNullOrWhiteSpace(mark)) warnings.Add("no_mark");
            if (lengthDeltaMm > 1.0) warnings.Add("length_delta");
            return warnings;
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
            var name = ResolveName(type, family, typeName, sizeText);
            var exclude = name.IndexOf("!Не учитывать", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          typeName.IndexOf("!Не учитывать", StringComparison.OrdinalIgnoreCase) >= 0;
            var unit = TypeString(type, "ADSK_Единица измерения");
            if (string.IsNullOrWhiteSpace(unit)) unit = CategoryUnit(element.Category?.Id.Value);
            var mass = ParamKg(type, element, "ADSK_Масса");
            var code = TypeString(type, "ADSK_Код изделия");
            if (string.IsNullOrWhiteSpace(code)) code = ParamString(element, "ADSK_Код изделия");
            var manufacturer = TypeString(type, "ADSK_Завод-изготовитель");
            if (string.IsNullOrWhiteSpace(manufacturer)) manufacturer = ParamString(element, "ADSK_Завод-изготовитель");

            ReadCurveLength(element, out var lengthM, out var curveLengthM, out var lengthDeltaMm);

            var warnings = BuildWarnings(system, diameter, width, height, sizeText, mark, lengthDeltaMm);

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
