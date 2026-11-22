using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Core;

namespace RevitBridge
{
    /// <summary>
    /// High-level sheet composition: crop views to the model, choose a standard scale,
    /// pack viewports and schedules inside the title block and validate the result.
    /// </summary>
    internal static class SheetLayoutOps
    {
        private const double MmPerFt = 304.8;

        private static Document Doc(UIApplication app)
        {
            return app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("Нет активного документа.");
        }

        private static double Ft(double mm) => mm / MmPerFt;
        private static double Mm(double ft) => ft * MmPerFt;

        private sealed class Placement
        {
            public string Kind;
            public ElementId Id;
            public string Name;
            public double X;
            public double Y; // top-left, mm
            public double W;
            public double H;
            public int Scale;
            public View View;
            public ViewSchedule Schedule;
        }

        private sealed class TitleBlockInfo
        {
            public ElementId SymbolId;
            public string FamilyName;
            public string Name;
            public double W;
            public double H;
        }

        private sealed class ScheduleEstimate
        {
            public ViewSchedule Schedule;
            public double W;
            public double H;
        }

        private sealed class Rect
        {
            public string Name;
            public double X0, Y0, X1, Y1;
            public double W => X1 - X0;
            public double H => Y1 - Y0;
        }

        public static object LayoutSheet(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var margin = args?.Value<double?>("marginMm") ?? 10;
            var gap = args?.Value<double?>("gapMm") ?? 10;
            var bottomReserve = args?.Value<double?>("bottomReserveMm") ?? 20;
            var cropMargin = args?.Value<double?>("cropMarginMm") ?? 1000;
            var cropIncludeLinked = args?.Value<bool?>("cropIncludeLinked") ?? false;
            var requestedScale = args?.Value<int?>("scale") ?? 0;
            var clearSheet = args?.Value<bool?>("clearSheet") ?? true;
            var viewIds = args?["viewIds"]?.ToObject<long[]>() ?? Array.Empty<long>();
            var scheduleIds = args?["scheduleIds"]?.ToObject<long[]>() ?? Array.Empty<long>();
            var sheetId = args?.Value<long?>("sheetId");

            var views = viewIds
                .Select(id => doc.GetElement(new ElementId(id)) as View)
                .Where(v => v != null && !v.IsTemplate && !(v is ViewSheet))
                .ToList();
            if (views.Count == 0) throw new ArgumentException("args.viewIds must contain at least one non-template view.");

            var schedules = scheduleIds
                .Select(id => doc.GetElement(new ElementId(id)) as ViewSchedule)
                .Where(s => s != null && !s.IsTemplate)
                .ToList();

            // Only room explication is allowed on a drawing sheet. Specifications
            // are exported separately (DOCX/PDF, GOST) and must not be placed here.
            var nonExplication = schedules
                .Where(s => s.Name.IndexOf("экспликац", StringComparison.OrdinalIgnoreCase) < 0 &&
                            s.Name.IndexOf("explication", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();
            if (nonExplication.Count > 0)
            {
                throw new ArgumentException(
                    "Only room explication schedules may be placed on a sheet. " +
                    "Specifications are exported separately: " +
                    string.Join(", ", nonExplication.Select(s => s.Name)));
            }

            // Crop views to the host spaces/MEP extents on their level.
            var crops = new Dictionary<long, Rect>();
            foreach (var view in views)
            {
                var rect = ComputeCrop(doc, view, cropMargin, cropIncludeLinked);
                if (rect != null) crops[view.Id.Value] = rect;
            }

            // Estimate explication table sizes before choosing a sheet format.
            var estimates = new List<ScheduleEstimate>();
            foreach (var schedule in schedules)
            {
                var data = schedule.GetTableData();
                var body = data.GetSectionData(SectionType.Body);
                var header = data.GetSectionData(SectionType.Header);
                var columns = Math.Max(body.NumberOfColumns, 1);
                var rows = Math.Max(body.NumberOfRows + (header?.NumberOfRows ?? 0), 1);
                estimates.Add(new ScheduleEstimate
                {
                    Schedule = schedule,
                    W = Math.Min(170, 30 + columns * 16),
                    H = 8 + rows * 6,
                });
            }

            ViewSheet sheet;
            if (sheetId != null)
            {
                sheet = doc.GetElement(new ElementId(sheetId.Value)) as ViewSheet
                    ?? throw new InvalidOperationException("Sheet not found: " + sheetId.Value);
            }
            else
            {
                var requestedName = args?.Value<string>("titleBlockName");
                var formats = MeasureTitleBlocks(doc);
                if (!string.IsNullOrWhiteSpace(requestedName))
                {
                    formats = formats
                        .Where(f => string.Equals(f.Name, requestedName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                if (formats.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No title block format found" +
                        (string.IsNullOrWhiteSpace(requestedName) ? "." : ": " + requestedName));
                }

                var chosen = ChooseTitleBlock(formats, crops, estimates,
                    margin, gap, bottomReserve, requestedScale,
                    !string.IsNullOrWhiteSpace(requestedName), out _);
                using (var tx = new Transaction(doc, "Bridge Create Layout Sheet"))
                {
                    tx.Start();
                    sheet = ViewSheet.Create(doc, chosen.SymbolId);
                    try { sheet.SheetNumber = args?.Value<string>("number") ?? "ТЕСТ-ОВ-01"; } catch (Exception) { }
                    try { sheet.Name = args?.Value<string>("name") ?? "Тестовый лист ОВ"; } catch (Exception) { }
                    tx.Commit();
                }
            }

            var titleBlockElement = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();
            var tbBox = titleBlockElement?.get_BoundingBox(sheet);
            if (tbBox == null) throw new InvalidOperationException("Title block bounding box is not available.");

            var tbX0 = Mm(tbBox.Min.X);
            var tbY0 = Mm(tbBox.Min.Y);
            var tbX1 = Mm(tbBox.Max.X);
            var tbY1 = Mm(tbBox.Max.Y);
            var x0 = tbX0 + margin;
            var y0 = tbY0 + margin + bottomReserve;
            var x1 = tbX1 - margin;
            var y1 = tbY1 - margin;
            if (x1 <= x0 || y1 <= y0) throw new InvalidOperationException("Title block is too small for margins.");

            // Clear previous sheet content (views are kept in the project, only viewports move).
            if (clearSheet)
            {
                var requestedViewIds = new HashSet<long>(viewIds);
                var viewportIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport)).Cast<Viewport>()
                    .Where(vp => vp.SheetId == sheet.Id || requestedViewIds.Contains(vp.ViewId.Value))
                    .Select(vp => vp.Id).ToList();
                var scheduleInstanceIds = new FilteredElementCollector(doc, sheet.Id)
                    .OfClass(typeof(ScheduleSheetInstance)).ToElementIds().ToList();
                if (viewportIds.Count > 0 || scheduleInstanceIds.Count > 0)
                {
                    using (var tx = new Transaction(doc, "Bridge Clear Sheet Layout"))
                    {
                        tx.Start();
                        foreach (var id in viewportIds.Concat(scheduleInstanceIds))
                        {
                            try { doc.Delete(id); } catch (Exception) { }
                        }
                        tx.Commit();
                    }
                }
            }

            var scheduleColumn = schedules.Count > 0 ? Math.Min(170, (x1 - x0) * 0.42) : 0;
            var planRight = scheduleColumn > 0 ? x1 - scheduleColumn - gap : x1;
            var planBottom = y0;
            var planW = planRight - x0;
            var planH = y1 - planBottom;

            var maxRequired = 1.0;
            foreach (var view in views)
            {
                var rect = crops.TryGetValue(view.Id.Value, out var r) ? r : FallbackRect(view);
                maxRequired = Math.Max(maxRequired, Math.Max(rect.W / planW, rect.H / planH));
            }
            var standardScales = new[] { 50, 100, 150, 200, 250, 300, 400, 500 };
            var scale = requestedScale > 0
                ? requestedScale
                : standardScales.FirstOrDefault(s => s >= maxRequired);
            if (scale <= 0) scale = standardScales.Last();

            using (var tx = new Transaction(doc, "Bridge Crop And Scale Views"))
            {
                tx.Start();
                foreach (var view in views)
                {
                    if (crops.TryGetValue(view.Id.Value, out var rect)) SetCrop(view, rect);
                    view.Scale = scale;
                }
                tx.Commit();
            }

            var items = new List<Placement>();
            foreach (var view in views)
            {
                var rect = crops.TryGetValue(view.Id.Value, out var r) ? r : FallbackRect(view);
                items.Add(new Placement
                {
                    Kind = "view",
                    Id = view.Id,
                    Name = view.Name,
                    W = rect.W / scale + 8,
                    H = rect.H / scale + 8,
                    Scale = scale,
                    View = view,
                });
            }

            foreach (var estimate in estimates)
            {
                items.Add(new Placement
                {
                    Kind = "schedule",
                    Id = estimate.Schedule.Id,
                    Name = estimate.Schedule.Name,
                    W = scheduleColumn > 0 ? Math.Min(estimate.W, scheduleColumn) : estimate.W,
                    H = estimate.H,
                    Scale = 1,
                    Schedule = estimate.Schedule,
                });
            }

            items = items.OrderByDescending(i => i.H).ThenByDescending(i => i.W).ToList();

            var createdViewportIds = new List<ElementId>();
            var createdScheduleIds = new List<ElementId>();
            using (var tx = new Transaction(doc, "Bridge Layout Sheet"))
            {
                tx.Start();

                // Views: shelf packing in the plan area.
                double px = x0, py = y1, shelfHeight = 0;
                foreach (var item in items.Where(i => i.Kind == "view"))
                {
                    if (px + item.W > planRight && px > x0)
                    {
                        px = x0;
                        py -= shelfHeight + gap;
                        shelfHeight = 0;
                    }
                    item.X = px;
                    item.Y = py;
                    var center = new XYZ(Ft(item.X + item.W / 2), Ft(item.Y - item.H / 2), 0);
                    var vp = Viewport.Create(doc, sheet.Id, item.View.Id, center);
                    createdViewportIds.Add(vp.Id);
                    px += item.W + gap;
                    shelfHeight = Math.Max(shelfHeight, item.H);
                }

                // Schedules: one column on the right, top-down.
                if (scheduleColumn > 0)
                {
                    double sx = x1 - scheduleColumn;
                    double sy = y1;
                    foreach (var item in items.Where(i => i.Kind == "schedule"))
                    {
                        item.X = sx;
                        item.Y = sy;
                        if (item.W > scheduleColumn) item.W = scheduleColumn;
                        var point = new XYZ(Ft(item.X), Ft(item.Y - item.H), 0);
                        var instance = ScheduleSheetInstance.Create(doc, sheet.Id, item.Schedule.Id, point);
                        createdScheduleIds.Add(instance.Id);
                        sy -= item.H + gap;
                    }
                }
                else
                {
                    double sx = x0, sy = y1 - (planH * 0.65);
                    foreach (var item in items.Where(i => i.Kind == "schedule"))
                    {
                        item.X = sx;
                        item.Y = sy;
                        var point = new XYZ(Ft(item.X), Ft(item.Y - item.H), 0);
                        var instance = ScheduleSheetInstance.Create(doc, sheet.Id, item.Schedule.Id, point);
                        createdScheduleIds.Add(instance.Id);
                        sy -= item.H + gap;
                    }
                }

                tx.Commit();
            }

            // Validate actual placement rectangles.
            var rects = new List<Rect>();
            var placements = new List<Dictionary<string, object>>();
            foreach (var vp in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                var box = vp.GetBoxOutline();
                if (box == null) continue;
                var rect = new Rect
                {
                    Name = doc.GetElement(vp.ViewId)?.Name ?? ("Viewport " + vp.Id.Value),
                    X0 = Mm(box.MinimumPoint.X),
                    Y0 = Mm(box.MinimumPoint.Y),
                    X1 = Mm(box.MaximumPoint.X),
                    Y1 = Mm(box.MaximumPoint.Y),
                };
                rects.Add(rect);
                placements.Add(new Dictionary<string, object>
                {
                    { "kind", "view" }, { "id", vp.ViewId.Value }, { "name", rect.Name },
                    { "x0Mm", rect.X0 }, { "y0Mm", rect.Y0 },
                    { "x1Mm", rect.X1 }, { "y1Mm", rect.Y1 },
                    { "widthMm", rect.W }, { "heightMm", rect.H },
                });
            }
            foreach (var si in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                var box = si.get_BoundingBox(sheet);
                if (box == null) continue;
                var rect = new Rect
                {
                    Name = doc.GetElement(si.ScheduleId)?.Name ?? ("Schedule " + si.Id.Value),
                    X0 = Mm(box.Min.X), Y0 = Mm(box.Min.Y),
                    X1 = Mm(box.Max.X), Y1 = Mm(box.Max.Y),
                };
                rects.Add(rect);
                placements.Add(new Dictionary<string, object>
                {
                    { "kind", "schedule" }, { "id", si.ScheduleId.Value }, { "name", rect.Name },
                    { "x0Mm", rect.X0 }, { "y0Mm", rect.Y0 },
                    { "x1Mm", rect.X1 }, { "y1Mm", rect.Y1 },
                    { "widthMm", rect.W }, { "heightMm", rect.H },
                });
            }

            var outside = new List<Dictionary<string, object>>();
            var overlaps = new List<Dictionary<string, object>>();
            var tolerance = 1.0;
            foreach (var rect in rects)
            {
                if (rect.X0 < x0 - tolerance || rect.Y0 < y0 - tolerance ||
                    rect.X1 > x1 + tolerance || rect.Y1 > y1 + tolerance)
                {
                    outside.Add(new Dictionary<string, object>
                    {
                        { "name", rect.Name },
                        { "rect", new[] { rect.X0, rect.Y0, rect.X1, rect.Y1 } },
                        { "usable", new[] { x0, y0, x1, y1 } },
                    });
                }
            }
            for (var i = 0; i < rects.Count; i++)
            {
                for (var j = i + 1; j < rects.Count; j++)
                {
                    var a = rects[i];
                    var b = rects[j];
                    var overlapX = Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0);
                    var overlapY = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0);
                    if (overlapX > tolerance && overlapY > tolerance)
                    {
                        overlaps.Add(new Dictionary<string, object>
                        {
                            { "a", a.Name }, { "b", b.Name },
                            { "overlapMm2", overlapX * overlapY },
                        });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "sheetId", sheet.Id.Value },
                { "sheetNumber", sheet.SheetNumber },
                { "sheetName", sheet.Name },
                { "sheetWidthMm", tbX1 - tbX0 },
                { "sheetHeightMm", tbY1 - tbY0 },
                { "usable", new[] { x0, y0, x1, y1 } },
                { "scale", scale },
                { "crops", views.Select(v =>
                    {
                        var rect = crops.TryGetValue(v.Id.Value, out var r) ? r : FallbackRect(v);
                        return new Dictionary<string, object>
                        {
                            { "id", v.Id.Value },
                            { "name", v.Name },
                            { "level", (v as ViewPlan)?.GenLevel?.Name ?? "" },
                            { "levelId", v.LevelId.Value },
                            { "x0Mm", rect.X0 }, { "y0Mm", rect.Y0 },
                            { "x1Mm", rect.X1 }, { "y1Mm", rect.Y1 },
                            { "widthMm", rect.W }, { "heightMm", rect.H },
                        };
                    }).ToList()
                },
                { "placements", placements },
                { "outside", outside },
                { "overlaps", overlaps },
                { "valid", outside.Count == 0 && overlaps.Count == 0 },
            };
        }

        public static object CreateLevelExplication(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var levelId = new ElementId(args?.Value<long?>("levelId")
                ?? throw new ArgumentException("args.levelId is required"));
            var level = doc.GetElement(levelId) as Level
                ?? throw new InvalidOperationException("Level not found: " + levelId.Value);

            var category = doc.Settings.Categories.Cast<Category>()
                .FirstOrDefault(c => c.Id.Value == (int)BuiltInCategory.OST_MEPSpaces)
                ?? throw new InvalidOperationException("Spaces category not found.");

            ViewSchedule schedule;
            bool filterApplied = false;
            string filterError = "";
            using (var tx = new Transaction(doc, "Bridge Create Level Explication"))
            {
                tx.Start();
                schedule = ViewSchedule.CreateSchedule(doc, category.Id);
                try
                {
                    schedule.Name = args?.Value<string>("name") ?? ("Экспликация " + level.Name);
                }
                catch (Exception) { }

                var definition = schedule.Definition;
                var schedulable = definition.GetSchedulableFields();
                var requestedFields = args?["fields"]?.ToObject<string[]>()
                    ?? new[] { "Номер", "Имя", "Площадь", "Уровень" };
                foreach (var fieldName in requestedFields)
                {
                    var field = schedulable.FirstOrDefault(f =>
                    {
                        try { return f.GetName(doc).Equals(fieldName, StringComparison.OrdinalIgnoreCase); }
                        catch (Exception) { return false; }
                    });
                    if (field != null) definition.AddField(field);
                }

                try
                {
                    var levelField = definition.GetFieldOrder()
                        .FirstOrDefault(fieldId =>
                        {
                            try
                            {
                                return definition.GetField(fieldId).GetName()
                                    .IndexOf("Уровень", StringComparison.OrdinalIgnoreCase) >= 0;
                            }
                            catch (Exception) { return false; }
                        });
                    if (levelField != null)
                    {
                        definition.AddFilter(new ScheduleFilter(
                            levelField, ScheduleFilterType.Equal, levelId));
                        filterApplied = true;
                    }
                }
                catch (Exception ex) { filterError = ex.Message; }

                doc.Regenerate();
                tx.Commit();
            }

            var data = schedule.GetTableData();
            var body = data.GetSectionData(SectionType.Body);
            return new Dictionary<string, object>
            {
                { "id", schedule.Id.Value },
                { "name", schedule.Name },
                { "level", level.Name },
                { "filterApplied", filterApplied },
                { "filterError", filterError },
                { "bodyRows", body.NumberOfRows },
                { "bodyColumns", body.NumberOfColumns },
                { "valid", filterApplied && body.NumberOfRows > 0 },
            };
        }

        public static object ListTitleBlocks(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var items = MeasureTitleBlocks(doc)
                .GroupBy(f => f.FamilyName + "|" + f.Name + "|"
                              + Math.Round(f.W) + "x" + Math.Round(f.H))
                .Select(g => g.First())
                .OrderBy(f => f.W * f.H)
                .Select(f => new Dictionary<string, object>
                {
                    { "id", f.SymbolId.Value },
                    { "familyName", f.FamilyName },
                    { "name", f.Name },
                    { "widthMm", f.W },
                    { "heightMm", f.H },
                    { "format", SheetLayoutMath.FormatName(f.W, f.H) },
                })
                .ToList();
            return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
        }

        private static List<TitleBlockInfo> MeasureTitleBlocks(Document doc)
        {
            var result = new List<TitleBlockInfo>();
            // Не группируем по s.Name: у разных семейств бывают одноимённые
            // типы (например, «Форма 3» для A2 и A3). Иначе один из форматов
            // теряется и compose_sheet не видит A2.
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>()
                .Where(s => s.Family != null)
                .ToList();

            using (var tx = new Transaction(doc, "Bridge Measure Title Blocks"))
            {
                tx.Start();
                foreach (var symbol in symbols)
                {
                    try
                    {
                        var sheet = ViewSheet.Create(doc, symbol.Id);
                        doc.Regenerate();
                        var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType()
                            .FirstElement();
                        var box = titleBlock?.get_BoundingBox(sheet);
                        if (box != null)
                        {
                            result.Add(new TitleBlockInfo
                            {
                                SymbolId = symbol.Id,
                                FamilyName = symbol.Family.Name,
                                Name = string.IsNullOrWhiteSpace(symbol.Name)
                                    ? symbol.Family.Name
                                    : symbol.Name,
                                W = Mm(box.Max.X - box.Min.X),
                                H = Mm(box.Max.Y - box.Min.Y),
                            });
                        }
                    }
                    catch (Exception) { }
                }
                tx.RollBack();
            }
            return result;
        }

        private static TitleBlockInfo ChooseTitleBlock(
            List<TitleBlockInfo> formats,
            Dictionary<long, Rect> crops,
            List<ScheduleEstimate> estimates,
            double margin,
            double gap,
            double bottomReserve,
            int requestedScale,
            bool allowCustom,
            out bool fits)
        {
            var unique = formats
                .Where(f => allowCustom || SheetLayoutMath.FormatName(f.W, f.H) != "custom")
                .GroupBy(f => f.FamilyName + "|" + f.Name + "|"
                              + Math.Round(f.W) + "x" + Math.Round(f.H))
                .Select(g => g.First())
                .OrderBy(f => f.W * f.H)
                .ToList();
            if (unique.Count == 0) throw new InvalidOperationException("No title block formats measured.");
            fits = false;

            foreach (var format in unique)
            {
                var usableW = format.W - 2 * margin;
                var usableH = format.H - 2 * margin - bottomReserve;
                if (usableW < 80 || usableH < 80) continue;

                var scheduleW = estimates.Count > 0 ? estimates.Max(e => e.W) : 0;
                if (scheduleW > usableW * 0.65) continue;
                var planW = usableW - (scheduleW > 0 ? scheduleW + gap : 0);
                if (planW < 100) continue;

                var scale = requestedScale > 0
                    ? requestedScale
                    : SheetLayoutMath.RequiredScale(
                        crops.Values.Select(r => new SheetLayoutMath.Box(r.W, r.H)),
                        planW, usableH);
                var viewItems = crops.Values
                    .Select(r => new Placement { W = r.W / scale + 8, H = r.H / scale + 8 })
                    .OrderByDescending(i => i.H).ToList();
                if (!SheetLayoutMath.ShelfFits(
                        viewItems.Select(i => new SheetLayoutMath.Box(i.W, i.H)).ToList(),
                        planW, usableH, gap)) continue;

                var scheduleHeight = estimates.Sum(e => e.H + gap);
                if (scheduleHeight > usableH + gap) continue;

                fits = true;
                return format;
            }

            return unique.Last();
        }

        public static object DebugSpaces(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var items = new List<Dictionary<string, object>>();
            foreach (var space in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType().Cast<Space>())
            {
                var box = space.get_BoundingBox(null);
                var point = space.Location as LocationPoint;
                items.Add(new Dictionary<string, object>
                {
                    { "id", space.Id.Value },
                    { "number", space.Number },
                    { "levelId", space.LevelId.Value },
                    { "levelName", space.Level?.Name ?? "" },
                    { "levelParam", space.LookupParameter("Уровень")?.AsValueString() ?? "" },
                    { "hasBox", box != null },
                    { "boxMinMm", box == null ? null : new[] { Mm(box.Min.X), Mm(box.Min.Y), Mm(box.Min.Z) } },
                    { "boxMaxMm", box == null ? null : new[] { Mm(box.Max.X), Mm(box.Max.Y), Mm(box.Max.Z) } },
                    { "locationType", space.Location?.GetType().Name ?? "" },
                    { "locMm", point == null ? null : new[] { Mm(point.Point.X), Mm(point.Point.Y), Mm(point.Point.Z) } },
                    { "area", space.Area },
                });
            }
            return new Dictionary<string, object>
            {
                { "count", items.Count },
                { "items", items.Take(10).ToList() },
            };
        }

        private static Rect ComputeCrop(Document doc, View view, double cropMargin, bool includeLinked)
        {
            var level = (view as ViewPlan)?.GenLevel ?? doc.GetElement(view.LevelId) as Level;
            if (level == null) return FallbackRect(view);

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            var found = false;

            void AddBox(BoundingBoxXYZ box)
            {
                if (box == null) return;
                minX = Math.Min(minX, Mm(box.Min.X));
                minY = Math.Min(minY, Mm(box.Min.Y));
                maxX = Math.Max(maxX, Mm(box.Max.X));
                maxY = Math.Max(maxY, Mm(box.Max.Y));
                found = true;
            }

            foreach (var space in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType().Cast<Space>())
            {
                var levelId = space.get_Parameter(BuiltInParameter.ROOM_LEVEL_ID)?.AsElementId()
                              ?? space.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId()
                              ?? space.LookupParameter("Уровень")?.AsElementId();
                var onLevel = (levelId != null && levelId == level.Id) ||
                              space.LevelId == level.Id ||
                              string.Equals(space.Level?.Name, level.Name, StringComparison.OrdinalIgnoreCase);
                if (!onLevel) continue;

                var box = space.get_BoundingBox(null) ?? space.get_BoundingBox(view);
                if (space.Location is LocationPoint location)
                {
                    // Space bounding boxes in this template include long linked-room
                    // tails; the location point is the reliable room center for cropping.
                    box = new BoundingBoxXYZ
                    {
                        Min = location.Point - new XYZ(Ft(2500), Ft(2500), Ft(100)),
                        Max = location.Point + new XYZ(Ft(2500), Ft(2500), Ft(100)),
                    };
                }
                AddBox(box);
            }

            // Fallback: if the level filter did not match (space level parameters differ
            // between templates), use the union of all host spaces.
            if (!found)
            {
                foreach (var space in new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_MEPSpaces)
                    .WhereElementIsNotElementType().Cast<Space>())
                {
                    var box = space.get_BoundingBox(null) ?? space.get_BoundingBox(view);
                    if (space.Location is LocationPoint location)
                    {
                        box = new BoundingBoxXYZ
                        {
                            Min = location.Point - new XYZ(Ft(2500), Ft(2500), Ft(100)),
                            Max = location.Point + new XYZ(Ft(2500), Ft(2500), Ft(100)),
                        };
                    }
                    AddBox(box);
                }
            }

            // Optionally include the linked AR floor plate for the same level.
            if (includeLinked)
            {
                foreach (var link in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                var transform = link.GetTotalTransform();
                foreach (var room in new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType().Cast<Room>())
                {
                    if (!string.Equals(room.Level?.Name, level.Name,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    var box = room.get_BoundingBox(null);
                    if (box == null) continue;

                    var corners = new[]
                    {
                        new XYZ(box.Min.X, box.Min.Y, box.Min.Z),
                        new XYZ(box.Min.X, box.Max.Y, box.Min.Z),
                        new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
                        new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
                        new XYZ(box.Min.X, box.Min.Y, box.Max.Z),
                        new XYZ(box.Min.X, box.Max.Y, box.Max.Z),
                        new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
                        new XYZ(box.Max.X, box.Max.Y, box.Max.Z),
                    }.Select(p => transform.OfPoint(p)).ToList();

                    AddBox(new BoundingBoxXYZ
                    {
                        Min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
                        Max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)),
                    });
                }
                }
            }

            foreach (var duct in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_DuctCurves)
                .WhereElementIsNotElementType().Cast<Duct>()
                .Where(d => d.LevelId == level.Id))
            {
                AddBox(duct.get_BoundingBox(null));
            }

            foreach (var terminal in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_DuctTerminal)
                .WhereElementIsNotElementType().Cast<FamilyInstance>()
                .Where(t => t.LevelId == level.Id))
            {
                AddBox(terminal.get_BoundingBox(null));
            }

            if (!found) return FallbackRect(view);
            return new Rect
            {
                Name = view.Name,
                X0 = minX - cropMargin,
                Y0 = minY - cropMargin,
                X1 = maxX + cropMargin,
                Y1 = maxY + cropMargin,
            };
        }

        private static Rect FallbackRect(View view)
        {
            var outline = view.Outline;
            var scale = Math.Max(view.Scale, 1);
            return new Rect
            {
                Name = view.Name,
                X0 = outline == null ? 0 : outline.Min.U * MmPerFt * scale,
                Y0 = outline == null ? 0 : outline.Min.V * MmPerFt * scale,
                X1 = outline == null ? 0 : outline.Max.U * MmPerFt * scale,
                Y1 = outline == null ? 0 : outline.Max.V * MmPerFt * scale,
            };
        }

        private static void SetCrop(View view, Rect rect)
        {
            var level = (view as ViewPlan)?.GenLevel;
            var zBase = level?.Elevation ?? 0;
            var zMin = zBase - Ft(1000);
            var zMax = zBase + Ft(10000);
            view.CropBox = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(Ft(rect.X0), Ft(rect.Y0), zMin),
                Max = new XYZ(Ft(rect.X1), Ft(rect.Y1), zMax),
            };
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
        }
    }
}
