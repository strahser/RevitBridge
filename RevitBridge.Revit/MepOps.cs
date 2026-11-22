using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    internal static class MepOps
    {
        public static object PlaceAirTerminal(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var familyName = args?.Value<string>("familyName");
            var typeName = args?.Value<string>("typeName");
            var levelId = args?.Value<long?>("levelId");
            var x = args?.Value<double?>("xMm") ?? 0;
            var y = args?.Value<double?>("yMm") ?? 0;
            var z = args?.Value<double?>("zMm") ?? 0;
            var rotationDeg = args?.Value<double?>("rotationDeg") ?? 0;

            var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_DuctTerminal).Cast<FamilySymbol>()
                .FirstOrDefault(s =>
                    (string.IsNullOrWhiteSpace(familyName) || s.Family.Name == familyName) &&
                    (string.IsNullOrWhiteSpace(typeName) || s.Name == typeName))
                ?? throw new InvalidOperationException("Air terminal family/type not found.");

            var level = (levelId != null ? doc.GetElement(new ElementId(levelId.Value)) as Level : null)
                        ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
            var point = new XYZ(BridgeOps.Mm(x), BridgeOps.Mm(y), BridgeOps.Mm(z));

            FamilyInstance instance;
            using (var tx = new Transaction(doc, "Bridge Place Air Terminal"))
            {
                tx.Start();
                if (!symbol.IsActive) symbol.Activate();
                instance = doc.Create.NewFamilyInstance(point, symbol, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                if (Math.Abs(rotationDeg) > 1e-9)
                {
                    var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, instance.Id, axis,
                        rotationDeg * Math.PI / 180.0);
                }
                tx.Commit();
            }

            var connectors = new List<Dictionary<string, object>>();
            var manager = instance.MEPModel?.ConnectorManager;
            if (manager != null)
            {
                foreach (Connector connector in manager.Connectors)
                {
                    var cs = connector.CoordinateSystem;
                    connectors.Add(new Dictionary<string, object>
                    {
                        { "originMm", new[]
                            {
                                connector.Origin.X * 304.8,
                                connector.Origin.Y * 304.8,
                                connector.Origin.Z * 304.8,
                            }
                        },
                        { "direction", new[]
                            {
                                cs?.BasisZ.X ?? 0, cs?.BasisZ.Y ?? 0, cs?.BasisZ.Z ?? 0,
                            }
                        },
                        { "domain", connector.Domain.ToString() },
                        { "shape", connector.Shape.ToString() },
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "id", instance.Id.Value },
                { "family", symbol.Family.Name },
                { "type", symbol.Name },
                { "level", level.Name },
                { "connectors", connectors },
                { "valid", connectors.Count > 0 },
            };
        }

        public static object ListMechanicalSystems(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var systems = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystem))
                .Cast<MechanicalSystem>()
                .Select(s => new Dictionary<string, object>
                {
                    { "id", s.Id.Value },
                    { "name", s.Name },
                    { "members", s.Elements?.Size ?? 0 },
                    { "empty", s.IsEmpty },
                    { "wellConnected", s.IsWellConnected },
                    { "sections", s.SectionsCount },
                })
                .ToList();
            return new Dictionary<string, object>
            {
                { "count", systems.Count }, { "items", systems },
            };
        }

        public static object ValidateSystem(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var systemId = args?.Value<long?>("systemId");
            var name = args?.Value<string>("name");

            MechanicalSystem system = null;
            if (systemId != null)
                system = doc.GetElement(new ElementId(systemId.Value)) as MechanicalSystem;
            if (system == null && !string.IsNullOrWhiteSpace(name))
                system = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystem))
                    .Cast<MechanicalSystem>()
                    .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            system ??= new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystem))
                .Cast<MechanicalSystem>().FirstOrDefault();
            if (system == null) throw new InvalidOperationException("Mechanical system not found.");

            var categories = new[]
            {
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_DuctTerminal,
                BuiltInCategory.OST_MechanicalEquipment,
            };

            var members = categories
                .SelectMany(category => new FilteredElementCollector(doc)
                    .OfCategory(category).WhereElementIsNotElementType().ToElements())
                .GroupBy(e => e.Id.Value).Select(g => g.First())
                .Where(e =>
                {
                    var parameter = e.LookupParameter("Имя системы")
                                    ?? e.LookupParameter("System Name");
                    return parameter != null && string.Equals(parameter.AsString(),
                        system.Name, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            var ducts = members.OfType<Duct>().ToList();
            var terminals = members
                .Where(e => e.Category?.Id.Value == (int)BuiltInCategory.OST_DuctTerminal)
                .OfType<FamilyInstance>().ToList();

            var ductClassification = ducts
                .Select(d => d.LookupParameter("Классификация систем")?.AsString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            var classificationMismatches = members
                .Select(e => new
                {
                    Element = e,
                    Classification = e.LookupParameter("Классификация систем")?.AsString(),
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Classification) &&
                            !string.IsNullOrWhiteSpace(ductClassification) &&
                            !string.Equals(x.Classification, ductClassification,
                                StringComparison.OrdinalIgnoreCase))
                .Select(x => new Dictionary<string, object>
                {
                    { "id", x.Element.Id.Value },
                    { "category", x.Element.Category?.Name ?? "" },
                    { "classification", x.Classification },
                })
                .ToList();

            var zeroFlow = new List<Dictionary<string, object>>();
            foreach (var terminal in terminals)
            {
                var flow = terminal.LookupParameter("Расход")
                           ?? terminal.LookupParameter("Flow");
                var value = flow?.AsDouble() ?? 0;
                if (value <= 0)
                {
                    zeroFlow.Add(new Dictionary<string, object>
                    {
                        { "id", terminal.Id.Value },
                        { "name", terminal.Name },
                        { "flow", value },
                    });
                }
            }

            var openConnectors = new List<Dictionary<string, object>>();
            foreach (var member in members)
            {
                ConnectorManager manager = null;
                if (member is MEPCurve curve) manager = curve.ConnectorManager;
                else if (member is FamilyInstance instance && instance.MEPModel != null)
                    manager = instance.MEPModel.ConnectorManager;
                if (manager == null) continue;

                foreach (Connector connector in manager.Connectors)
                {
                    if (connector.IsConnected) continue;
                    var cs = connector.CoordinateSystem;
                    openConnectors.Add(new Dictionary<string, object>
                    {
                        { "id", member.Id.Value },
                        { "category", member.Category?.Name ?? "" },
                        { "originMm", new[]
                            {
                                connector.Origin.X * 304.8,
                                connector.Origin.Y * 304.8,
                                connector.Origin.Z * 304.8,
                            }
                        },
                        { "direction", new[]
                            {
                                cs?.BasisZ.X ?? 0, cs?.BasisZ.Y ?? 0, cs?.BasisZ.Z ?? 0,
                            }
                        },
                        { "domain", connector.Domain.ToString() },
                    });
                }
            }

            var issues = new List<string>();
            if (system.IsEmpty) issues.Add("system is empty");
            if (!system.IsWellConnected) issues.Add("system is not well connected");
            if (system.IsMultipleNetwork) issues.Add("system is multiple network");
            if (!system.IsValid) issues.Add("system is not valid");
            if (ducts.Count == 0) issues.Add("no ducts in system");
            if (terminals.Count == 0) issues.Add("no air terminals in system");
            if (classificationMismatches.Count > 0) issues.Add("system classification mismatch");
            if (zeroFlow.Count > 0) issues.Add("air terminal flow is zero");

            var assembled = !system.IsEmpty && !system.IsMultipleNetwork &&
                            ducts.Count > 0 && terminals.Count > 0 &&
                            classificationMismatches.Count == 0 && zeroFlow.Count == 0;

            return new Dictionary<string, object>
            {
                { "id", system.Id.Value },
                { "name", system.Name },
                { "systemType", system.SystemType.ToString() },
                { "members", members.Count },
                { "ducts", ducts.Count },
                { "terminals", terminals.Count },
                { "wellConnected", system.IsWellConnected },
                { "empty", system.IsEmpty },
                { "multipleNetwork", system.IsMultipleNetwork },
                { "validSystem", system.IsValid },
                { "assembled", assembled },
                { "sections", system.SectionsCount },
                { "ductClassification", ductClassification },
                { "classificationMismatches", classificationMismatches },
                { "zeroFlowTerminals", zeroFlow },
                { "openConnectors", openConnectors },
                { "issues", issues },
                { "valid", issues.Count == 0 },
            };
        }

        public static object ConnectTerminalToDuct(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var terminal = doc.GetElement(BridgeOps.Id(args, "terminalId")) as FamilyInstance
                ?? throw new InvalidOperationException("Air terminal FamilyInstance not found.");
            var duct = doc.GetElement(BridgeOps.Id(args, "ductId")) as Duct
                ?? throw new InvalidOperationException("Duct not found.");

            bool ok;
            using (var tx = new Transaction(doc, "Bridge Connect Terminal To Duct"))
            {
                tx.Start();
                ok = MechanicalUtils.ConnectAirTerminalOnDuct(doc, terminal.Id, duct.Id);
                tx.Commit();
            }
            if (!ok) throw new InvalidOperationException("ConnectAirTerminalOnDuct returned false.");
            return new Dictionary<string, object> { { "terminalId", terminal.Id.Value }, { "ductId", duct.Id.Value } };
        }

        public static object CreateMechanicalSystem(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var typeName = args?.Value<string>("typeName") ?? "ADSK_М_Приток";
            var name = args?.Value<string>("name") ?? ("ТЕСТ_Приток_" + DateTime.Now.ToString("HHmmss"));
            var ids = args?["elementIds"]?.ToObject<long[]>() ?? Array.Empty<long>();

            var type = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                ?? new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType))
                    .Cast<MechanicalSystemType>().FirstOrDefault()
                ?? throw new InvalidOperationException("No mechanical system type.");

            MechanicalSystem system;
            string addError = null;
            using (var tx = new Transaction(doc, "Bridge Create Mechanical System"))
            {
                tx.Start();
                system = MechanicalSystem.Create(doc, type.Id, name);
                if (ids.Length > 0)
                {
                    var connectors = new ConnectorSet();
                    foreach (var id in ids)
                    {
                        var element = doc.GetElement(new ElementId(id));
                        ConnectorManager manager = null;
                        if (element is MEPCurve curve) manager = curve.ConnectorManager;
                        else if (element is FamilyInstance instance && instance.MEPModel != null)
                            manager = instance.MEPModel.ConnectorManager;
                        if (manager == null) continue;
                        foreach (Connector connector in manager.Connectors)
                        {
                            try { connectors.Insert(connector); } catch (Exception) { }
                        }
                    }
                    if (connectors.Size > 0)
                    {
                        try { system.Add(connectors); }
                        catch (Exception ex) { addError = ex.Message; }
                    }
                }
                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                { "id", system.Id.Value }, { "name", system.Name }, { "type", type.Name },
                { "members", system.Elements?.Size ?? 0 },
                { "addError", addError },
                { "valid", (system.Elements?.Size ?? 0) > 0 },
            };
        }
    }
}
