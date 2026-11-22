using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    internal static class ModelOps
    {
        public static object ClearModel(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
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
            var doc = BridgeOps.Doc(app);
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
            var doc = BridgeOps.Doc(app);
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
            var doc = BridgeOps.Doc(app);
            doc.Regenerate();
            return new Dictionary<string, object> { { "ok", true } };
        }

        public static object CreateSpaces(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
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
            var doc = BridgeOps.Doc(app);
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
            var doc = BridgeOps.Doc(app);
            var level = doc.GetElement(BridgeOps.Id(args, "levelId")) as Level
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
            var doc = BridgeOps.Doc(app);
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
    }
}
