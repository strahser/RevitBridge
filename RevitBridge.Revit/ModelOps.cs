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
            ActivateSafeView(doc, app.ActiveUIDocument);

            // Delete viewports first: a viewport cannot survive its view.
            var viewportIds = new FilteredElementCollector(doc).OfClass(typeof(Viewport))
                .Cast<Viewport>().Select(v => v.Id).ToList();
            var viewports = DeleteIds(doc, viewportIds, "Bridge Clear Viewports");

            var viewIds = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet) &&
                            (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan))
                .Select(v => v.Id).ToList();
            var views = DeleteIds(doc, viewIds, "Bridge Clear Views");

            var linkIds = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Select(l => l.Id).ToList();
            var links = DeleteIds(doc, linkIds, "Bridge Clear Links");

            var modelIds = CollectModelElements(doc, out var spaces, out var model);
            DeleteIds(doc, modelIds, "Bridge Clear Model Elements");

            return new Dictionary<string, object>
            {
                { "links", links }, { "spaces", spaces }, { "model", model },
                { "views", views }, { "viewports", viewports },
            };
        }

        private static void ActivateSafeView(Document doc, UIDocument uidoc)
        {
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
        }

        private static int DeleteIds(Document doc, List<ElementId> ids, string transactionName)
        {
            if (ids.Count == 0) return 0;
            var deleted = 0;
            using (var tx = new Transaction(doc, transactionName))
            {
                tx.Start();
                foreach (var id in ids)
                {
                    try { doc.Delete(id); deleted++; } catch (Exception) { }
                }
                tx.Commit();
            }
            return deleted;
        }

        private static List<ElementId> CollectModelElements(Document doc,
            out int spaces, out int model)
        {
            spaces = 0;
            model = 0;
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
            return modelIds;
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

        private sealed class SpaceContext
        {
            public Phase Phase;
            public List<Level> Levels;
            public RevitLinkInstance Link;
        }

        private sealed class SpaceCreationState
        {
            public int Created;
            public int Skipped;
            public List<ElementId> CreatedIds = new List<ElementId>();
            public List<Tuple<ElementId, long, string, double>> HeightChecks =
                new List<Tuple<ElementId, long, string, double>>();
        }

        private sealed class HeightReport
        {
            public int Checked;
            public int Matched;
            public int Skipped;
            public List<Dictionary<string, object>> Mismatches;
            public bool Valid;
        }

        private static SpaceContext PrepareSpaceContext(Document doc)
        {
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

            return new SpaceContext { Phase = phase, Levels = levels, Link = link };
        }

        private static bool CreateSpacesFromLink(Document doc, SpaceContext context,
            bool fitHeight, SpaceCreationState state)
        {
            var link = context.Link;
            if (link != null && link.GetLinkDocument() != null)
            {
                var linkDoc = link.GetLinkDocument();
                var transform = link.GetTotalTransform();
                foreach (var room in new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>())
                {
                    CreateSpaceFromRoom(doc, room, transform, context, fitHeight, state);
                }
                return true;
            }
            return false;
        }

        private static void CreateSpaceFromRoom(Document doc, Room room, Transform transform,
            SpaceContext context, bool fitHeight, SpaceCreationState state)
        {
            var point = (room.Location as LocationPoint)?.Point
                        ?? (room.get_BoundingBox(null) is BoundingBoxXYZ b
                            ? (b.Min + b.Max) * 0.5 : null);
            if (point == null) { state.Skipped++; return; }
            var hostPoint = transform.OfPoint(point);
            var level = context.Levels.FirstOrDefault(l => l.Name == room.Level?.Name)
                        ?? context.Levels.OrderBy(l => Math.Abs(l.Elevation - hostPoint.Z)).First();
            try
            {
                var space = doc.Create.NewSpace(level, context.Phase, new UV(hostPoint.X, hostPoint.Y));
                if (space == null) { state.Skipped++; return; }
                try { space.Number = room.Number; space.Name = room.Name; } catch (Exception) { }

                var roomHeight = ApplyRoomHeight(space, room, fitHeight);
                state.HeightChecks.Add(Tuple.Create(space.Id, room.Id.Value,
                    room.Number ?? "", roomHeight));

                state.CreatedIds.Add(space.Id);
                state.Created++;
            }
            catch (Exception) { state.Skipped++; }
        }

        private static double ApplyRoomHeight(Space space, Room room, bool fitHeight)
        {
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
            return roomHeight;
        }

        private static void CreateSpacesFromCircuits(Document doc, SpaceContext context,
            bool fitHeight, SpaceCreationState state)
        {
            // Fallback: plan circuits per level (works when the host itself is bounded).
            foreach (var level in context.Levels)
            {
                PlanTopology topology;
                try { topology = doc.get_PlanTopology(level); } catch (Exception) { continue; }
                foreach (PlanCircuit circuit in topology.Circuits)
                {
                    try
                    {
                        var uv = circuit.GetPointInside();
                        if (doc.GetSpaceAtPoint(new XYZ(uv.U, uv.V, level.Elevation), context.Phase) != null) continue;
                        var space = doc.Create.NewSpace(level, context.Phase, uv);
                        if (space == null) { state.Skipped++; continue; }
                        state.CreatedIds.Add(space.Id);
                        state.Created++;
                    }
                    catch (Exception) { state.Skipped++; }
                }
            }
        }

        private static int CountBounded(Document doc, List<ElementId> createdIds)
        {
            // Post-condition: every created space must exist and be bounded (Area > 0).
            var bounded = 0;
            foreach (var id in createdIds)
            {
                var space = doc.GetElement(id) as Space;
                if (space != null && space.Area > 0) bounded++;
            }
            return bounded;
        }

        private static void RegenerateSpaces(Document doc)
        {
            using (var regen = new Transaction(doc, "Bridge Regenerate Spaces"))
            {
                regen.Start();
                doc.Regenerate();
                regen.Commit();
            }
        }

        private static HeightReport ValidateHeights(Document doc,
            List<Tuple<ElementId, long, string, double>> checks)
        {
            // Height validator: Space.UnboundedHeight vs linked Room.UnboundedHeight (or V/A).
            var heightChecked = 0;
            var heightMatched = 0;
            var heightSkipped = 0;
            var heightMismatches = new List<Dictionary<string, object>>();
            foreach (var check in checks)
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
            return new HeightReport
            {
                Checked = heightChecked,
                Matched = heightMatched,
                Skipped = heightSkipped,
                Mismatches = heightMismatches,
                Valid = heightValid,
            };
        }

        private static Dictionary<string, object> BuildResult(SpaceCreationState state,
            int bounded, HeightReport report)
        {
            var created = state.Created;
            var skipped = state.Skipped;
            var heightChecked = report.Checked;
            var heightMatched = report.Matched;
            var heightSkipped = report.Skipped;
            var heightMismatches = report.Mismatches;
            var heightValid = report.Valid;
            return new Dictionary<string, object>
            {
                { "created", created }, { "skipped", skipped },
                { "bounded", bounded }, { "unbounded", created - bounded },
                { "heightChecked", heightChecked }, { "heightMatched", heightMatched },
                { "heightMismatched", report.Checked - report.Matched },
                { "heightSkipped", heightSkipped },
                { "heightMismatches", heightMismatches },
                { "heightValid", heightValid },
                { "valid", (created == 0 || bounded == created) && heightValid },
            };
        }

        public static object CreateSpaces(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var context = PrepareSpaceContext(doc);
            var state = new SpaceCreationState();
            var fitHeight = args?.Value<bool?>("fitHeight") ?? true;

            using (var tx = new Transaction(doc, "Bridge Create Spaces"))
            {
                tx.Start();
                if (!CreateSpacesFromLink(doc, context, fitHeight, state))
                    CreateSpacesFromCircuits(doc, context, fitHeight, state);
                tx.Commit();
            }

            var bounded = CountBounded(doc, state.CreatedIds);
            RegenerateSpaces(doc);
            var report = ValidateHeights(doc, state.HeightChecks);
            return BuildResult(state, bounded, report);
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
