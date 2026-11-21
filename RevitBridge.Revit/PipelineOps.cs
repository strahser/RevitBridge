using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    /// <summary>
    /// End-to-end pipeline operations: clean template, link AR, create spaces,
    /// views/templates, simple duct network, systems, sheets and PDF.
    /// Kept in the Revit layer; the bridge remains a transport/registry.
    /// </summary>
    internal static class PipelineOps
    {
        private static Document Doc(UIApplication app)
        {
            return app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("Нет активного документа.");
        }

        private static ElementId Id(JToken token, string key)
        {
            var value = token?.Value<long?>(key);
            if (value == null) throw new ArgumentException("args." + key + " is required");
            return new ElementId(value.Value);
        }

        private static double Mm(double mm) => mm / 304.8;

        private static XYZ P(JToken token)
        {
            var values = token?.ToObject<double[]>() ?? new double[] { 0, 0, 0 };
            return new XYZ(Mm(values.Length > 0 ? values[0] : 0),
                Mm(values.Length > 1 ? values[1] : 0),
                Mm(values.Length > 2 ? values[2] : 0));
        }

        public static object ListViews(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var includeTemplates = args?.Value<bool?>("includeTemplates") ?? true;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => includeTemplates || !v.IsTemplate)
                .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name)
                .Select(v =>
                {
                    var outline = v.Outline;
                    return new Dictionary<string, object>
                    {
                        { "id", v.Id.Value },
                        { "name", v.Name },
                        { "viewType", v.ViewType.ToString() },
                        { "isTemplate", v.IsTemplate },
                        { "scale", v.Scale },
                        { "cropActive", v.CropBoxActive },
                        { "outlineMinMm", outline == null ? null : new[]
                            {
                                outline.Min.U * 304.8, outline.Min.V * 304.8,
                            }
                        },
                        { "outlineMaxMm", outline == null ? null : new[]
                            {
                                outline.Max.U * 304.8, outline.Max.V * 304.8,
                            }
                        },
                        { "widthMm", outline == null ? 0 : (outline.Max.U - outline.Min.U) * 304.8 },
                        { "heightMm", outline == null ? 0 : (outline.Max.V - outline.Min.V) * 304.8 },
                        { "template", v.ViewTemplateId != ElementId.InvalidElementId
                            ? doc.GetElement(v.ViewTemplateId)?.Name ?? "" : "" },
                        { "sheetNumber", v is ViewSheet sheet ? sheet.SheetNumber : "" },
                    };
                }).ToList();
            return new Dictionary<string, object> { { "count", views.Count }, { "items", views } };
        }

        public static object ListSheets(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber)
                .Select(s =>
                {
                    var titleBlock = new FilteredElementCollector(doc, s.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsNotElementType()
                        .FirstElement();
                    var outline = s.Outline;
                    var tbBox = titleBlock?.get_BoundingBox(s);
                    return new Dictionary<string, object>
                    {
                        { "id", s.Id.Value },
                        { "number", s.SheetNumber },
                        { "name", s.Name },
                        { "views", s.GetAllPlacedViews().Count },
                        { "schedules", new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance)).GetElementCount() },
                        { "widthMm", outline == null ? 0 : (outline.Max.U - outline.Min.U) * 304.8 },
                        { "heightMm", outline == null ? 0 : (outline.Max.V - outline.Min.V) * 304.8 },
                        { "tbWidthMm", tbBox == null ? 0 : (tbBox.Max.X - tbBox.Min.X) * 304.8 },
                        { "tbHeightMm", tbBox == null ? 0 : (tbBox.Max.Y - tbBox.Min.Y) * 304.8 },
                        { "titleBlock", titleBlock == null ? "" :
                            (titleBlock.Name + " / " + (titleBlock.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "")) },
                    };
                }).ToList();
            return new Dictionary<string, object> { { "count", sheets.Count }, { "items", sheets } };
        }

        public static object ClearModel(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var uidoc = app.ActiveUIDocument;
            int links = 0, spaces = 0, model = 0, views = 0, viewports = 0;

            // The active view may be one of the floor plans we are about to delete.
            try
            {
                var safe = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && v is ViewSheet)
                    ?? new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.ThreeD);
                if (safe != null && uidoc != null) uidoc.ActiveView = safe;
            }
            catch (Exception) { }

            // Delete viewports first: a viewport cannot survive its view.
            var viewportIds = new FilteredElementCollector(doc).OfClass(typeof(Viewport))
                .Cast<Viewport>().Select(v => v.Id).ToList();
            if (viewportIds.Count > 0)
            {
                using (var tx = new Transaction(doc, "Bridge Clear Viewports"))
                {
                    tx.Start();
                    foreach (var id in viewportIds)
                    {
                        try { doc.Delete(id); viewports++; } catch (Exception) { }
                    }
                    tx.Commit();
                }
            }

            var viewIds = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet) &&
                            (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan))
                .Select(v => v.Id).ToList();
            if (viewIds.Count > 0)
            {
                using (var tx = new Transaction(doc, "Bridge Clear Views"))
                {
                    tx.Start();
                    foreach (var id in viewIds)
                    {
                        try { doc.Delete(id); views++; } catch (Exception) { }
                    }
                    tx.Commit();
                }
            }

            var linkIds = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Select(l => l.Id).ToList();
            if (linkIds.Count > 0)
            {
                using (var tx = new Transaction(doc, "Bridge Clear Links"))
                {
                    tx.Start();
                    foreach (var id in linkIds)
                    {
                        try { doc.Delete(id); links++; } catch (Exception) { }
                    }
                    tx.Commit();
                }
            }

            var modelIds = new List<ElementId>();
            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements())
            {
                if (element is Level || element is Grid || element is View || element is ViewSheet ||
                    element is ViewSchedule || element is ScheduleSheetInstance || element is Viewport)
                    continue;
                if (element.Category?.Id.Value == (int)BuiltInCategory.OST_TitleBlocks)
                    continue;
                if (element is Space) spaces++;
                else model++;
                modelIds.Add(element.Id);
            }

            if (modelIds.Count > 0)
            {
                using (var tx = new Transaction(doc, "Bridge Clear Model Elements"))
                {
                    tx.Start();
                    foreach (var id in modelIds)
                    {
                        try { doc.Delete(id); } catch (Exception) { }
                    }
                    tx.Commit();
                }
            }

            return new Dictionary<string, object>
            {
                { "links", links }, { "spaces", spaces }, { "model", model },
                { "views", views }, { "viewports", viewports },
            };
        }

        public static object LinkRevit(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var path = args?.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new ArgumentException("args.path must point to an existing .rvt");
            var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);

            RevitLinkType type = null;
            using (var tx = new Transaction(doc, "Bridge Link Revit"))
            {
                tx.Start();
                try
                {
                    var loadResult = RevitLinkType.Create(doc, modelPath, new RevitLinkOptions(false));
                    type = doc.GetElement(loadResult.ElementId) as RevitLinkType;
                }
                catch (Exception)
                {
                    type = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType))
                        .Cast<RevitLinkType>()
                        .FirstOrDefault(t =>
                        {
                            var reference = t.GetExternalFileReference();
                            if (reference == null) return false;
                            var absolute = ModelPathUtils.ConvertModelPathToUserVisiblePath(
                                reference.GetAbsolutePath());
                            return string.Equals(absolute, path, StringComparison.OrdinalIgnoreCase);
                        });
                }

                if (type == null) throw new InvalidOperationException("Revit link type was not created/loaded.");

                var roomBounding = type.LookupParameter("Граница помещения")
                                   ?? type.LookupParameter("Room Bounding");
                if (roomBounding != null && !roomBounding.IsReadOnly) roomBounding.Set(1);

                var instance = RevitLinkInstance.Create(doc, type.Id);
                tx.Commit();
                return new Dictionary<string, object>
                {
                    { "typeId", type.Id.Value },
                    { "instanceId", instance.Id.Value },
                    { "path", path },
                };
            }
        }

        /// <summary>
        /// Turns on "Room Bounding" for Revit links so spaces can pick up linked room boundaries.
        /// </summary>
        public static object SetLinkRoomBounding(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var enabled = args?.Value<bool?>("enabled") ?? true;
            var idFilter = args?.Value<long?>("linkTypeId");
            var changed = new List<long>();

            using (var tx = new Transaction(doc, "Bridge Link Room Bounding"))
            {
                tx.Start();
                foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType))
                    .Cast<RevitLinkType>())
                {
                    if (idFilter != null && type.Id.Value != idFilter.Value) continue;
                    var parameter = type.LookupParameter("Граница помещения")
                                    ?? type.LookupParameter("Room Bounding");
                    if (parameter == null || parameter.IsReadOnly) continue;
                    parameter.Set(enabled ? 1 : 0);
                    changed.Add(type.Id.Value);
                }
                doc.Regenerate();
                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                { "enabled", enabled }, { "changed", changed },
            };
        }

        public static object Regenerate(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            doc.Regenerate();
            return new Dictionary<string, object> { { "ok", true } };
        }

        public static object CreateSpaces(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var phase = doc.Phases.Cast<Phase>().OrderBy(p => p.Id.Value).FirstOrDefault()
                ?? throw new InvalidOperationException("No phase in document.");
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var link = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().FirstOrDefault();

            // Spaces become bounded only when the link type is marked as room bounding.
            if (link != null)
            {
                var linkType = doc.GetElement(link.GetTypeId()) as RevitLinkType;
                var roomBounding = linkType?.LookupParameter("Граница помещения")
                                   ?? linkType?.LookupParameter("Room Bounding");
                if (roomBounding != null && !roomBounding.IsReadOnly && roomBounding.AsInteger() == 0)
                {
                    using (var tx = new Transaction(doc, "Bridge Link Room Bounding"))
                    {
                        tx.Start();
                        roomBounding.Set(1);
                        doc.Regenerate();
                        tx.Commit();
                    }
                }
            }

            var created = 0;
            var skipped = 0;
            var createdIds = new List<ElementId>();
            var heightChecks = new List<Tuple<ElementId, long, string, double>>();
            var fitHeight = args?.Value<bool?>("fitHeight") ?? true;
            using (var tx = new Transaction(doc, "Bridge Create Spaces"))
            {
                tx.Start();

                if (link != null && link.GetLinkDocument() != null)
                {
                    var linkDoc = link.GetLinkDocument();
                    var transform = link.GetTotalTransform();
                    foreach (var room in new FilteredElementCollector(linkDoc)
                        .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>())
                    {
                        var point = (room.Location as LocationPoint)?.Point
                                    ?? (room.get_BoundingBox(null) is BoundingBoxXYZ b
                                        ? (b.Min + b.Max) * 0.5 : null);
                        if (point == null) { skipped++; continue; }
                        var hostPoint = transform.OfPoint(point);
                        var level = levels.FirstOrDefault(l => l.Name == room.Level?.Name)
                                    ?? levels.OrderBy(l => Math.Abs(l.Elevation - hostPoint.Z)).First();
                        try
                        {
                            var space = doc.Create.NewSpace(level, phase, new UV(hostPoint.X, hostPoint.Y));
                            if (space == null) { skipped++; continue; }
                            try { space.Number = room.Number; space.Name = room.Name; } catch (Exception) { }

                            // Height source: Room.UnboundedHeight, or Volume/Area when unbounded.
                            var roomHeight = room.UnboundedHeight;
                            if (!(roomHeight > 0) && room.Area > 0 && room.Volume > 0)
                                roomHeight = room.Volume / room.Area;
                            if (roomHeight > 0 && fitHeight)
                            {
                                try
                                {
                                    var upperOffset = space.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                                    if (upperOffset != null && !upperOffset.IsReadOnly)
                                        upperOffset.Set(roomHeight);
                                    else
                                        space.LimitOffset = roomHeight;
                                }
                                catch (Exception) { }
                            }
                            heightChecks.Add(Tuple.Create(space.Id, room.Id.Value,
                                room.Number ?? "", roomHeight));

                            createdIds.Add(space.Id);
                            created++;
                        }
                        catch (Exception) { skipped++; }
                    }
                }
                else
                {
                    // Fallback: plan circuits per level (works when the host itself is bounded).
                    foreach (var level in levels)
                    {
                        PlanTopology topology;
                        try { topology = doc.get_PlanTopology(level); } catch (Exception) { continue; }
                        foreach (PlanCircuit circuit in topology.Circuits)
                        {
                            try
                            {
                                var uv = circuit.GetPointInside();
                                if (doc.GetSpaceAtPoint(new XYZ(uv.U, uv.V, level.Elevation), phase) != null) continue;
                                var space = doc.Create.NewSpace(level, phase, uv);
                                if (space == null) { skipped++; continue; }
                                createdIds.Add(space.Id);
                                created++;
                            }
                            catch (Exception) { skipped++; }
                        }
                    }
                }

                tx.Commit();
            }

            // Post-condition: every created space must exist and be bounded (Area > 0).
            var bounded = 0;
            foreach (var id in createdIds)
            {
                var space = doc.GetElement(id) as Space;
                if (space != null && space.Area > 0) bounded++;
            }

            // Height validator: Space.UnboundedHeight vs linked Room.UnboundedHeight (or V/A).
            using (var regen = new Transaction(doc, "Bridge Regenerate Spaces"))
            {
                regen.Start();
                doc.Regenerate();
                regen.Commit();
            }
            var heightChecked = 0;
            var heightMatched = 0;
            var heightSkipped = 0;
            var heightMismatches = new List<Dictionary<string, object>>();
            foreach (var check in heightChecks)
            {
                var space = doc.GetElement(check.Item1) as Space;
                if (space == null) { heightSkipped++; continue; }
                var roomHeight = check.Item4;
                if (!(roomHeight > 0)) { heightSkipped++; continue; }

                heightChecked++;
                var spaceHeight = space.UnboundedHeight;
                var deltaMm = Math.Abs(spaceHeight - roomHeight) * 304.8;
                if (deltaMm <= 10) heightMatched++;
                else if (heightMismatches.Count < 20)
                {
                    heightMismatches.Add(new Dictionary<string, object>
                    {
                        { "spaceId", check.Item1.Value },
                        { "roomId", check.Item2 },
                        { "roomNumber", check.Item3 },
                        { "roomHeightMm", roomHeight * 304.8 },
                        { "spaceHeightMm", spaceHeight * 304.8 },
                        { "deltaMm", deltaMm },
                    });
                }
            }

            var heightValid = heightChecked == 0 || heightMatched == heightChecked;
            return new Dictionary<string, object>
            {
                { "created", created }, { "skipped", skipped },
                { "bounded", bounded }, { "unbounded", created - bounded },
                { "heightChecked", heightChecked }, { "heightMatched", heightMatched },
                { "heightMismatched", heightChecked - heightMatched },
                { "heightSkipped", heightSkipped },
                { "heightMismatches", heightMismatches },
                { "heightValid", heightValid },
                { "valid", (created == 0 || bounded == created) && heightValid },
            };
        }

        /// <summary>
        /// Deletes MEP spaces and validates the result with a fresh
        /// FilteredElementCollector(OST_MEP_SPACES) snapshot.
        /// </summary>
        public static object DeleteSpaces(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var requested = args?["ids"]?.ToObject<long[]>();

            List<ElementId> Snapshot() => new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType()
                .Select(e => e.Id)
                .ToList();

            var before = Snapshot();
            var targets = requested == null || requested.Length == 0
                ? before
                : before.Where(id => requested.Contains(id.Value)).ToList();

            var deleted = new List<long>();
            var failed = new List<Dictionary<string, object>>();

            if (targets.Count > 0)
            {
                using (var tx = new Transaction(doc, "Bridge Delete Spaces"))
                {
                    tx.Start();
                    foreach (var id in targets)
                    {
                        try
                        {
                            doc.Delete(id);
                            deleted.Add(id.Value);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new Dictionary<string, object>
                            {
                                { "id", id.Value }, { "error", ex.Message },
                            });
                        }
                    }
                    tx.Commit();
                }
            }

            var after = Snapshot();
            var afterIds = new HashSet<long>(after.Select(i => i.Value));
            var leftover = targets.Select(i => i.Value).Where(afterIds.Contains).ToList();
            var missing = (requested ?? Array.Empty<long>())
                .Where(id => before.All(i => i.Value != id)).ToArray();

            return new Dictionary<string, object>
            {
                { "before", before.Count },
                { "targeted", targets.Count },
                { "deleted", deleted.Count },
                { "failed", failed },
                { "after", after.Count },
                { "leftover", leftover },
                { "missingRequested", missing },
                { "valid", failed.Count == 0 && leftover.Count == 0 },
            };
        }

        public static object CreatePlanView(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var level = doc.GetElement(Id(args, "levelId")) as Level
                ?? throw new InvalidOperationException("Level not found.");
            var name = args?.Value<string>("name") ?? ("ТЕСТ_" + level.Name);
            var templateName = args?.Value<string>("templateName");

            var viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan)
                ?? throw new InvalidOperationException("No floor plan view family type.");

            ViewPlan view;
            using (var tx = new Transaction(doc, "Bridge Create Plan View"))
            {
                tx.Start();
                view = ViewPlan.Create(doc, viewType.Id, level.Id);
                try { view.Name = name; } catch (Exception) { }
                if (!string.IsNullOrWhiteSpace(templateName))
                {
                    var template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .FirstOrDefault(v => v.IsTemplate &&
                            string.Equals(v.Name, templateName, StringComparison.OrdinalIgnoreCase));
                    if (template != null) view.ViewTemplateId = template.Id;
                }
                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                { "id", view.Id.Value }, { "name", view.Name },
                { "template", view.ViewTemplateId != ElementId.InvalidElementId
                    ? doc.GetElement(view.ViewTemplateId)?.Name ?? "" : "" },
            };
        }

        public static object DeleteViews(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var ids = args?["ids"]?.ToObject<long[]>() ?? Array.Empty<long>();

            List<long> Snapshot() => new FilteredElementCollector(doc).OfClass(typeof(View))
                .ToElementIds().Select(i => i.Value).ToList();

            var before = Snapshot();
            var deleted = new List<long>();
            var failed = new List<Dictionary<string, object>>();
            using (var tx = new Transaction(doc, "Bridge Delete Views"))
            {
                tx.Start();
                foreach (var id in ids)
                {
                    try
                    {
                        doc.Delete(new ElementId(id));
                        deleted.Add(id);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "id", id }, { "error", ex.Message },
                        });
                    }
                }
                tx.Commit();
            }

            var after = Snapshot();
            var afterSet = new HashSet<long>(after);
            var leftover = ids.Where(afterSet.Contains).ToList();
            var missing = ids.Where(id => !before.Contains(id)).ToArray();

            return new Dictionary<string, object>
            {
                { "before", before.Count },
                { "requested", ids.Length },
                { "deleted", deleted.Count },
                { "failed", failed },
                { "after", after.Count },
                { "leftover", leftover },
                { "missingRequested", missing },
                { "valid", failed.Count == 0 && leftover.Count == 0 },
            };
        }

        public static object PlaceAirTerminal(UIApplication app, JObject args)
        {
            var doc = Doc(app);
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
            var point = new XYZ(Mm(x), Mm(y), Mm(z));

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
            var doc = Doc(app);
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

        public static object SetParameter(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var elementId = Id(args, "elementId");
            var element = doc.GetElement(elementId)
                ?? throw new InvalidOperationException("Element not found: " + elementId.Value);
            var name = args?.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("args.name is required");

            var parameter = element.LookupParameter(name)
                ?? throw new InvalidOperationException("Parameter not found: " + name);
            if (parameter.IsReadOnly) throw new InvalidOperationException("Parameter is read-only: " + name);

            var token = args?["value"];
            using (var tx = new Transaction(doc, "Bridge Set Parameter"))
            {
                tx.Start();
                if (token is JValue value)
                {
                    switch (value.Type)
                    {
                        case JTokenType.Float: parameter.Set(value.Value<double>()); break;
                        case JTokenType.Integer: parameter.Set(value.Value<int>()); break;
                        case JTokenType.Boolean: parameter.Set(value.Value<bool>() ? 1 : 0); break;
                        default: parameter.Set(value.ToString()); break;
                    }
                }
                else if (token != null)
                {
                    parameter.Set(token.ToString());
                }
                doc.Regenerate();
                tx.Commit();
            }

            object current;
            switch (parameter.StorageType)
            {
                case StorageType.String: current = parameter.AsString(); break;
                case StorageType.Integer: current = parameter.AsInteger(); break;
                case StorageType.Double: current = parameter.AsDouble(); break;
                case StorageType.ElementId: current = parameter.AsElementId()?.Value; break;
                default: current = null; break;
            }

            return new Dictionary<string, object>
            {
                { "elementId", elementId.Value }, { "name", name },
                { "value", current }, { "valid", true },
            };
        }

        public static object ValidateSystem(UIApplication app, JObject args)
        {
            var doc = Doc(app);
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

        public static object GetScheduleData(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var schedule = doc.GetElement(Id(args, "scheduleId")) as ViewSchedule
                ?? throw new InvalidOperationException("Schedule not found.");
            var maxRows = args?.Value<int?>("maxRows") ?? 25;

            var data = schedule.GetTableData();
            var body = data.GetSectionData(SectionType.Body);
            var header = data.GetSectionData(SectionType.Header);

            var fields = schedule.Definition.GetFieldOrder()
                .Select(fieldId => schedule.Definition.GetField(fieldId)?.GetName() ?? "")
                .ToList();

            string Cell(TableSectionData section, int row, int col)
            {
                try { return section?.GetCellText(row, col) ?? ""; }
                catch (Exception) { return ""; }
            }

            var headerRows = new List<string[]>();
            for (var row = 0; row < (header?.NumberOfRows ?? 0) && row < 3; row++)
            {
                var values = new string[header.NumberOfColumns];
                for (var col = 0; col < values.Length; col++) values[col] = Cell(header, row, col);
                headerRows.Add(values);
            }

            var rows = new List<string[]>();
            for (var row = 0; row < body.NumberOfRows && row < maxRows; row++)
            {
                var values = new string[body.NumberOfColumns];
                for (var col = 0; col < values.Length; col++) values[col] = Cell(body, row, col);
                rows.Add(values);
            }

            return new Dictionary<string, object>
            {
                { "id", schedule.Id.Value },
                { "name", schedule.Name },
                { "bodyRows", body.NumberOfRows },
                { "bodyColumns", body.NumberOfColumns },
                { "headerRows", header?.NumberOfRows ?? 0 },
                { "fields", fields },
                { "itemized", schedule.Definition.IsItemized },
                { "header", headerRows },
                { "rows", rows },
                { "valid", body.NumberOfRows > 0 && body.NumberOfColumns > 0 },
            };
        }

        public static object ConnectTerminalToDuct(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var terminal = doc.GetElement(Id(args, "terminalId")) as FamilyInstance
                ?? throw new InvalidOperationException("Air terminal FamilyInstance not found.");
            var duct = doc.GetElement(Id(args, "ductId")) as Duct
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
            var doc = Doc(app);
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

        public static object CreateSheet(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var number = args?.Value<string>("number") ?? "ТЕСТ-01";
            var name = args?.Value<string>("name") ?? "Тестовый лист ОВ";
            var titleBlockName = args?.Value<string>("titleBlockName");

            var titleBlock = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>()
                .FirstOrDefault(s => string.IsNullOrWhiteSpace(titleBlockName) ||
                                     string.Equals(s.Name, titleBlockName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Title block family symbol not loaded.");

            ViewSheet sheet;
            using (var tx = new Transaction(doc, "Bridge Create Sheet"))
            {
                tx.Start();
                sheet = ViewSheet.Create(doc, titleBlock.Id);
                try { sheet.SheetNumber = number; } catch (Exception) { }
                try { sheet.Name = name; } catch (Exception) { }
                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                { "id", sheet.Id.Value }, { "number", sheet.SheetNumber }, { "name", sheet.Name },
                { "titleBlock", titleBlock.Family.Name + " : " + titleBlock.Name },
                { "viewsOnSheet", sheet.GetAllPlacedViews().Count },
                { "valid", sheet.Id != ElementId.InvalidElementId },
            };
        }

        public static object PlaceViewOnSheet(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var sheet = doc.GetElement(Id(args, "sheetId")) as ViewSheet
                ?? throw new InvalidOperationException("Sheet not found.");
            var view = doc.GetElement(Id(args, "viewId")) as View
                ?? throw new InvalidOperationException("View not found.");
            var point = new XYZ(Mm(args?.Value<double?>("xMm") ?? 20),
                Mm(args?.Value<double?>("yMm") ?? 20), 0);

            Viewport viewport;
            using (var tx = new Transaction(doc, "Bridge Place View On Sheet"))
            {
                tx.Start();
                viewport = Viewport.Create(doc, sheet.Id, view.Id, point);
                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                { "sheetId", sheet.Id.Value }, { "viewId", view.Id.Value },
                { "viewportId", viewport.Id.Value },
                { "viewsOnSheet", sheet.GetAllPlacedViews().Count },
                { "valid", sheet.GetAllPlacedViews().Contains(view.Id) },
            };
        }

        public static object PlaceScheduleOnSheet(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var sheet = doc.GetElement(Id(args, "sheetId")) as ViewSheet
                ?? throw new InvalidOperationException("Sheet not found.");
            var schedule = doc.GetElement(Id(args, "scheduleId")) as ViewSchedule
                ?? throw new InvalidOperationException("Schedule not found.");
            var point = new XYZ(Mm(args?.Value<double?>("xMm") ?? 20),
                Mm(args?.Value<double?>("yMm") ?? 150), 0);

            ScheduleSheetInstance instance;
            using (var tx = new Transaction(doc, "Bridge Place Schedule On Sheet"))
            {
                tx.Start();
                instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point);
                tx.Commit();
            }

            var schedulesOnSheet = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance)).GetElementCount();
            return new Dictionary<string, object>
            {
                { "sheetId", sheet.Id.Value }, { "scheduleId", schedule.Id.Value },
                { "instanceId", instance.Id.Value },
                { "schedulesOnSheet", schedulesOnSheet },
                { "valid", schedulesOnSheet > 0 },
            };
        }

        public static object ExportSheetPdf(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var path = args?.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("args.path is required");
            var sheetId = Id(args, "sheetId");
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("args.path must be absolute");
            Directory.CreateDirectory(folder);

            var options = new PDFExportOptions
            {
                FileName = Path.GetFileNameWithoutExtension(path),
                Combine = false,
                ColorDepth = ColorDepthType.Color,
            };

            var started = DateTime.Now.AddSeconds(-2);
            doc.Export(folder, new List<ElementId> { sheetId }, options);

            var files = Directory.GetFiles(folder, "*.pdf")
                .Where(f => File.GetLastWriteTime(f) >= started)
                .OrderBy(f => f)
                .Select(f => new Dictionary<string, object>
                {
                    { "path", f },
                    { "bytes", new FileInfo(f).Length },
                })
                .ToList();

            return new Dictionary<string, object>
            {
                { "requestedPath", path },
                { "sheetId", sheetId.Value },
                { "files", files },
                { "valid", files.Count > 0 && files.All(f => (long)f["bytes"] > 0) },
            };
        }
    }
}
