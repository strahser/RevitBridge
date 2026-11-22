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
    /// Sheet composition v2.
    ///
    /// Differences from the legacy pipeline.layout_sheet:
    ///  - never changes source working views (a copy is made with View.Duplicate);
    ///  - uses an explicit safe box that excludes the stamp and the left graphene
    ///    (the full title block bbox is deliberately NOT used as a usable area);
    ///  - places items, measures the ACTUAL boxes (Viewport.GetBoxOutline /
    ///    ScheduleSheetInstance.get_BoundingBox), then moves them to exact positions;
    ///  - reports placements / outside / overlaps / valid for automated checks.
    /// </summary>
    internal static class SheetComposer
    {
        private static Document Doc(UIApplication app)
        {
            return app.ActiveUIDocument?.Document
                   ?? throw new InvalidOperationException("Нет активного документа.");
        }
        internal sealed class Item
        {
            public string Kind;              // "view" or "schedule"
            public long SourceId;
            public string Name;
            public double EstW, EstH;
            public double W, H;              // measured on sheet
            public double X, Y;              // final top-left, relative to title block origin
            public double? ReqX, ReqY;       // explicit top-left from the spec
            public int Scale = 1;
            public View View;                // duplicate for viewports
            public View Schedule;            // ViewSchedule for tables
            public ElementId InstanceId;
            public string Error;
            public Dictionary<string, object> CropInfo;
            public Dictionary<string, object> ViewportUi;
        }
        /// <summary>
        /// Create a loadable .rfa from a title-block .rft template and load it into
        /// the active project. Used to add a clean A4/A2 title block when the
        /// template only contains A3 drawing forms and A4 title/change sheets.
        /// </summary>
        public static object LoadTitleBlockTemplate(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var templatePath = args?.Value<string>("templatePath");
            var rfaPath = args?.Value<string>("rfaPath");
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
                throw new ArgumentException("templatePath not found: " + templatePath);
            if (string.IsNullOrWhiteSpace(rfaPath) || !Path.IsPathRooted(rfaPath))
                throw new ArgumentException("rfaPath must be an absolute path.");

            var folder = Path.GetDirectoryName(rfaPath);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

            var familyDoc = app.Application.NewFamilyDocument(templatePath)
                ?? throw new InvalidOperationException("NewFamilyDocument returned null: " + templatePath);
            try
            {
                familyDoc.SaveAs(rfaPath);
            }
            finally
            {
                familyDoc.Close(false);
            }

            Family family = null;
            using (var tx = new Transaction(doc, "Bridge Load Title Block"))
            {
                tx.Start();
                doc.LoadFamily(rfaPath, out family);
                tx.Commit();
            }

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .Where(s => family == null || s.Family.Id == family.Id)
                .Select(s => new Dictionary<string, object>
                {
                    { "id", s.Id.Value },
                    { "name", s.Name },
                    { "family", s.Family.Name },
                })
                .ToList();

            return new Dictionary<string, object>
            {
                { "templatePath", templatePath },
                { "rfaPath", rfaPath },
                { "family", family?.Name ?? "" },
                { "symbols", symbols },
                { "valid", symbols.Count > 0 },
            };
        }
        public static object ComposeSheet(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            args = args ?? new JObject();

            var titleBlockName = args.Value<string>("titleBlockName") ?? "Форма 3";
            if (titleBlockName.IndexOf("форма 6", StringComparison.OrdinalIgnoreCase) >= 0 ||
                titleBlockName.IndexOf("form 6", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new ArgumentException(
                    "Форма 6 is a specification form and must not be used for drawing sheets.");
            }

            var margin = args.Value<double?>("marginMm") ?? 10;
            var gap = args.Value<double?>("gapMm") ?? 12;
            var stampReserve = args.Value<double?>("stampReserveMm") ?? 0;
            var cropMargin = args.Value<double?>("cropMarginMm") ?? 1000;
            var includeLinked = args.Value<bool?>("cropIncludeLinked") ?? false;
            var clearSheet = args.Value<bool?>("clearSheet") ?? true;
            var preferIndependentCopy = args.Value<bool?>("independentCopy") ?? true;
            var drawFrame = args.Value<bool?>("drawFrame") ?? false;
            var labelViews = args.Value<bool?>("labelViews") ?? true;

            ViewSheet sheet;
            long? sheetId = args.Value<long?>("sheetId");
            if (sheetId.HasValue)
            {
                sheet = doc.GetElement(new ElementId(sheetId.Value)) as ViewSheet
                        ?? throw new InvalidOperationException("Sheet not found: " + sheetId.Value);
            }
            else
            {
                var symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => string.Equals(s.Name, titleBlockName, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(s.Family.Name, titleBlockName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Title block not found: " + titleBlockName);

                using (var tx = new Transaction(doc, "Bridge Compose Sheet: create"))
                {
                    tx.Start();
                    sheet = ViewSheet.Create(doc, symbol.Id);
                    try { sheet.SheetNumber = args.Value<string>("number") ?? "ТЕСТ-NEW"; } catch (Exception) { }
                    try { sheet.Name = args.Value<string>("name") ?? "Тестовый лист"; } catch (Exception) { }
                    tx.Commit();
                }
            }

            var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().FirstElement()
                ?? throw new InvalidOperationException("Title block instance not found on sheet.");
            var tbBox = titleBlock.get_BoundingBox(sheet)
                        ?? throw new InvalidOperationException("Title block bounding box is not available.");
            var originX = BridgeUnits.Mm(tbBox.Min.X);
            var originY = BridgeUnits.Mm(tbBox.Min.Y);
            var sheetW = BridgeUnits.Mm(tbBox.Max.X - tbBox.Min.X);
            var sheetH = BridgeUnits.Mm(tbBox.Max.Y - tbBox.Min.Y);
            var format = SheetLayoutMath.SheetLayoutFormat(sheetW, sheetH, titleBlock.Name);
            var tbType = doc.GetElement(titleBlock.GetTypeId()) as ElementType;
            var tbFamily = tbType?.FamilyName ?? "";
            var generated = titleBlock.Name.StartsWith("NB_", StringComparison.OrdinalIgnoreCase) ||
                            tbFamily.StartsWith("NB_", StringComparison.OrdinalIgnoreCase);

            // Safe box is relative to the title block lower-left corner.
            // For A3 (Форма 3 / А3А): left grif x=5..15 mm, stamp y=0..60 mm,
            // frame x=20..415 mm. A conservative x=20..410, y=60..287 is clean.
            var safe = SheetViewOps.ParseRect4(args["safeBoxMm"]) ??
                       DefaultSafeBox(format, generated ? tbFamily : titleBlock.Name);
            var usable = new Rect
            {
                Name = "usable",
                X0 = safe.X0 + margin,
                Y0 = safe.Y0 + margin + stampReserve,
                X1 = safe.X1 - margin,
                Y1 = safe.Y1 - margin,
            };
            if (usable.W < 60 || usable.H < 60)
                throw new InvalidOperationException("Safe box is too small for margins: " +
                                                    $"{usable.W:F1}x{usable.H:F1} mm.");
            if (labelViews)
            {
                // Reserve a strip above the first row for view titles placed on top.
                usable.Y1 -= 8;
                gap = Math.Max(gap, 14);
            }

            if (clearSheet) ClearSheet(doc, sheet);
            if (drawFrame && !generated) SheetAnnotationOps.DrawFrame(doc, sheet, originX, originY, sheetW, sheetH);
            if (generated) SheetAnnotationOps.DrawStampText(doc, sheet, originX, originY, sheetW, sheetH, args);

            var items = new List<Item>();
            var sourceBaseline = new Dictionary<long, Dictionary<string, object>>();

            foreach (var vspecToken in (JArray)args["views"] ?? new JArray())
            {
                var vspec = vspecToken as JObject ?? new JObject();
                var srcId = vspec.Value<long?>("id")
                            ?? throw new ArgumentException("Every views[] entry needs an id.");
                var source = doc.GetElement(new ElementId(srcId)) as View
                             ?? throw new InvalidOperationException("View not found: " + srcId);
                if (source.IsTemplate || source is ViewSheet)
                    throw new ArgumentException("views[] entry is a template or sheet: " + srcId);

                if (!sourceBaseline.ContainsKey(srcId))
                    sourceBaseline[srcId] = SheetViewOps.SnapshotView(source);

                var targetScale = vspec.Value<int?>("scale") ?? source.Scale;
                if (targetScale <= 0) targetScale = 100;

                var crop = SheetViewOps.ResolveCrop(doc, vspec, source, cropMargin, includeLinked);
                var duplicate = SheetViewOps.DuplicateView(doc, source, vspec.Value<string>("name"),
                    sheet.SheetNumber, preferIndependentCopy);
                using (var tx = new Transaction(doc, "Bridge Compose Sheet: prepare view"))
                {
                    tx.Start();
                    if (duplicate.Scale != targetScale) duplicate.Scale = targetScale;
                    if (crop != null) SheetViewOps.SetCrop(duplicate, crop);
                    tx.Commit();
                }

                var systemName = vspec.Value<string>("systemName");
                var colorBySystem = vspec.Value<bool?>("colorBySystem") ?? false;
                if (!string.IsNullOrWhiteSpace(systemName) || colorBySystem)
                    SheetViewOps.ApplySystemFilter(doc, duplicate, systemName, colorBySystem);

                var viewRect = crop ?? SheetViewOps.ViewRect(duplicate);
                var item = new Item
                {
                    Kind = "view",
                    SourceId = srcId,
                    Name = vspec.Value<string>("name") ?? duplicate.Name,
                    Scale = duplicate.Scale,
                    View = duplicate,
                    ReqX = vspec.Value<double?>("xMm"),
                    ReqY = vspec.Value<double?>("yMm"),
                    EstW = viewRect.W / Math.Max(duplicate.Scale, 1) + 8,
                    EstH = viewRect.H / Math.Max(duplicate.Scale, 1) + 14,
                    CropInfo = crop == null ? null : SheetViewOps.RectInfo(crop),
                };
                items.Add(item);
            }

            foreach (var sspecToken in (JArray)args["schedules"] ?? new JArray())
            {
                var sspec = sspecToken as JObject ?? new JObject();
                var sid = sspec.Value<long?>("id")
                          ?? throw new ArgumentException("Every schedules[] entry needs an id.");
                var schedule = doc.GetElement(new ElementId(sid)) as ViewSchedule
                               ?? throw new InvalidOperationException("Schedule not found: " + sid);
                var table = schedule.GetTableData();
                var body = table.GetSectionData(SectionType.Body);
                var header = table.GetSectionData(SectionType.Header);
                var columns = Math.Max(body.NumberOfColumns, 1);
                var rows = Math.Max(body.NumberOfRows + (header?.NumberOfRows ?? 0), 1);
                items.Add(new Item
                {
                    Kind = "schedule",
                    SourceId = sid,
                    Name = sspec.Value<string>("name") ?? schedule.Name,
                    Schedule = schedule,
                    ReqX = sspec.Value<double?>("xMm"),
                    ReqY = sspec.Value<double?>("yMm"),
                    EstW = sspec.Value<double?>("widthMm") ?? Math.Min(170, 30 + columns * 16),
                    EstH = sspec.Value<double?>("heightMm") ?? 8 + rows * 6,
                });
            }

            if (items.Count == 0)
                throw new ArgumentException("Specify at least one view or schedule.");

            // Phase 1: create everything at estimated positions so Revit can size the boxes.
            ApplyPositions(items, PlanPositions(items, usable, gap));
            var creationErrors = new List<Dictionary<string, object>>();
            using (var tx = new Transaction(doc, "Bridge Compose Sheet: place"))
            {
                tx.Start();
                foreach (var item in items)
                {
                    try
                    {
                        if (item.Kind == "view")
                        {
                            var center = new XYZ(BridgeUnits.Ft(originX + item.X + item.W / 2),
                                                 BridgeUnits.Ft(originY + item.Y - item.H / 2), 0);
                            var vp = Viewport.Create(doc, sheet.Id, item.View.Id, center);
                            item.InstanceId = vp.Id;
                            item.ViewportUi = SheetViewOps.EnableViewportUi(doc, vp);
                        }
                        else
                        {
                            var point = new XYZ(BridgeUnits.Ft(originX + item.X), BridgeUnits.Ft(originY + item.Y), 0);
                            var si = ScheduleSheetInstance.Create(doc, sheet.Id, item.Schedule.Id, point);
                            item.InstanceId = si.Id;
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Error = ex.Message;
                        creationErrors.Add(new Dictionary<string, object>
                        {
                            { "name", item.Name }, { "error", ex.Message },
                        });
                    }
                }
                doc.Regenerate();
                tx.Commit();
            }

            // Phase 2: measure ACTUAL boxes.
            Measure(doc, sheet, originX, originY, items);

            // Phase 3: re-pack using measured sizes and move to exact positions.
            var planned = PlanPositions(items, usable, gap);
            ApplyPositions(items, planned);
            var moveErrors = new List<Dictionary<string, object>>();
            using (var tx = new Transaction(doc, "Bridge Compose Sheet: align"))
            {
                tx.Start();
                foreach (var item in items.Where(i => i.InstanceId != null && i.W > 0 && i.H > 0))
                {
                    try
                    {
                        var element = doc.GetElement(item.InstanceId);
                        if (item.Kind == "view")
                        {
                            var vp = element as Viewport;
                            if (vp == null) continue;
                            vp.SetBoxCenter(new XYZ(BridgeUnits.Ft(originX + item.X + item.W / 2),
                                                    BridgeUnits.Ft(originY + item.Y - item.H / 2), 0));
                        }
                        else
                        {
                            var si = element as ScheduleSheetInstance;
                            var lp = si?.Location as LocationPoint;
                            if (lp == null) continue;
                            var target = new XYZ(BridgeUnits.Ft(originX + item.X), BridgeUnits.Ft(originY + item.Y), 0);
                            lp.Move(target - lp.Point);
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Error = ex.Message;
                        moveErrors.Add(new Dictionary<string, object>
                        {
                            { "name", item.Name }, { "error", ex.Message },
                        });
                    }
                }
                doc.Regenerate();
                tx.Commit();
            }

            // Phase 4: measure after the move; schedules have a constant offset
            // between their insertion point and their visible bounding box, so a
            // second correction pass is needed for exact coordinates.
            Measure(doc, sheet, originX, originY, items);
            var finalPlan = PlanPositions(items, usable, gap);
            using (var tx = new Transaction(doc, "Bridge Compose Sheet: fine align"))
            {
                tx.Start();
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.InstanceId == null || item.W <= 0) continue;
                    var dx = finalPlan[i][0] - item.X;
                    var dy = finalPlan[i][1] - item.Y;
                    if (Math.Abs(dx) < 0.05 && Math.Abs(dy) < 0.05) continue;
                    try
                    {
                        var element = doc.GetElement(item.InstanceId);
                        if (item.Kind == "view")
                        {
                            var vp = element as Viewport;
                            if (vp != null)
                                vp.SetBoxCenter(new XYZ(
                                    BridgeUnits.Ft(originX + finalPlan[i][0] + item.W / 2),
                                    BridgeUnits.Ft(originY + finalPlan[i][1] - item.H / 2), 0));
                        }
                        else
                        {
                            var lp = (element as ScheduleSheetInstance)?.Location as LocationPoint;
                            if (lp != null) lp.Move(new XYZ(BridgeUnits.Ft(dx), BridgeUnits.Ft(dy), 0));
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Error = ex.Message;
                        moveErrors.Add(new Dictionary<string, object>
                        {
                            { "name", item.Name }, { "fineAlignError", ex.Message },
                        });
                    }
                }
                doc.Regenerate();
                tx.Commit();
            }

            // Phase 5: final validation on real boxes.
            Measure(doc, sheet, originX, originY, items);
            var tolerance = 1.0; // mm
            var validationItems = items.Where(i => i.InstanceId != null && i.W > 0)
                .Select(i => new SheetValidation.Box(i.Name, i.X, i.Y - i.H, i.X + i.W, i.Y))
                .ToList();
            var validation = SheetValidation.Validate(
                validationItems,
                new SheetValidation.Box("usable", usable.X0, usable.Y0, usable.X1, usable.Y1),
                new SheetValidation.Box("safe", safe.X0, safe.Y0, safe.X1, safe.Y1),
                sheetW, sheetH, tolerance);

            // Keep the JSON shape (keys, types and order) exactly as before.
            var outside = validation.Outside.Select(e => new Dictionary<string, object>
            {
                { "name", e.Name },
                { "rect", e.Rect },
                { "usable", e.Usable },
            }).ToList();
            var forbidden = validation.Forbidden.Select(e => new Dictionary<string, object>
            {
                { "name", e.Name }, { "zone", e.Zone }, { "overlapMm2", e.OverlapMm2 },
            }).ToList();
            var overlaps = validation.Overlaps.Select(e => new Dictionary<string, object>
            {
                { "a", e.A }, { "b", e.B }, { "overlapMm2", e.OverlapMm2 },
            }).ToList();

            var sourceViewsUnchanged = true;
            foreach (var pair in sourceBaseline)
            {
                var view = doc.GetElement(new ElementId(pair.Key)) as View;
                if (view == null) { sourceViewsUnchanged = false; continue; }
                var now = SheetViewOps.SnapshotView(view);
                foreach (var key in pair.Value.Keys)
                {
                    if (!now.ContainsKey(key) ||
                        !Equals(now[key]?.ToString(), pair.Value[key]?.ToString()))
                        sourceViewsUnchanged = false;
                }
            }

            if (labelViews) SheetAnnotationOps.AddViewLabels(doc, sheet, items, originX, originY, usable);

            var placements = items.Select(i => new Dictionary<string, object>
            {
                { "kind", i.Kind },
                { "sourceId", i.SourceId },
                { "id", i.Kind == "view" ? i.View?.Id.Value ?? 0 : (i.Schedule?.Id.Value ?? 0) },
                { "instanceId", i.InstanceId?.Value ?? 0 },
                { "name", i.Name },
                { "scale", i.Scale },
                { "x0Mm", i.X }, { "y0Mm", i.Y - i.H },
                { "x1Mm", i.X + i.W }, { "y1Mm", i.Y },
                { "widthMm", i.W }, { "heightMm", i.H },
                { "crop", i.CropInfo },
                { "viewportUi", i.ViewportUi },
                { "error", i.Error },
            }).ToList();

            return new Dictionary<string, object>
            {
                { "sheetId", sheet.Id.Value },
                { "sheetNumber", sheet.SheetNumber },
                { "sheetName", sheet.Name },
                { "titleBlock", titleBlock.Name },
                { "format", format },
                { "sheetWidthMm", sheetW },
                { "sheetHeightMm", sheetH },
                { "originMm", new[] { originX, originY } },
                { "safeBoxMm", new[] { safe.X0, safe.Y0, safe.X1, safe.Y1 } },
                { "usableMm", new[] { usable.X0, usable.Y0, usable.X1, usable.Y1 } },
                { "scale", items.Where(i => i.Kind == "view").Select(i => (int?)i.Scale).FirstOrDefault() ?? 0 },
                { "placements", placements },
                { "outside", outside },
                { "overlaps", overlaps },
                { "forbidden", forbidden },
                { "creationErrors", creationErrors },
                { "moveErrors", moveErrors },
                { "sourceViewsUnchanged", sourceViewsUnchanged },
                { "valid", outside.Count == 0 && overlaps.Count == 0 && forbidden.Count == 0 &&
                           creationErrors.Count == 0 && moveErrors.Count == 0 && sourceViewsUnchanged },
            };
        }
        private static Rect DefaultSafeBox(string format, string titleBlockName)
        {
            if (string.Equals(titleBlockName, "NB_A4", StringComparison.OrdinalIgnoreCase) ||
                (format == "A4" && titleBlockName.StartsWith("NB_", StringComparison.OrdinalIgnoreCase)))
                return new Rect { Name = "safe", X0 = 20, Y0 = 65, X1 = 287, Y1 = 200 };
            if (titleBlockName.StartsWith("NB_", StringComparison.OrdinalIgnoreCase))
            {
                if (format == "A3") return new Rect { X0 = 20, Y0 = 65, X1 = 410, Y1 = 287 };
                if (format == "A2") return new Rect { X0 = 20, Y0 = 65, X1 = 584, Y1 = 410 };
                if (format == "A1") return new Rect { X0 = 20, Y0 = 65, X1 = 831, Y1 = 584 };
                if (format == "A0") return new Rect { X0 = 20, Y0 = 65, X1 = 1179, Y1 = 831 };
            }
            if (format == "A3")
                return new Rect { Name = "safe", X0 = 20, Y0 = 60, X1 = 410, Y1 = 287 };
            if (format == "A4")
            {
                // A4К is a title page: there is no clean A4 drawing title block in
                // this template. The box below avoids the frame and the signature
                // strip, but the title-page text still lives in this area.
                return new Rect { Name = "safe", X0 = 25, Y0 = 75, X1 = 200, Y1 = 287 };
            }
            return new Rect { Name = "safe", X0 = 20, Y0 = 60, X1 = Math.Min(410, 0), Y1 = 287 };
        }
        private static void ClearSheet(Document doc, ViewSheet sheet)
        {
            var viewportIds = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(Viewport)).ToElementIds().ToList();
            var scheduleIds = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance)).ToElementIds().ToList();
            var textIds = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(TextNote)).ToElementIds().ToList();
            if (viewportIds.Count == 0 && scheduleIds.Count == 0 && textIds.Count == 0) return;
            using (var tx = new Transaction(doc, "Bridge Compose Sheet: clear"))
            {
                tx.Start();
                foreach (var id in viewportIds.Concat(scheduleIds).Concat(textIds))
                {
                    try { doc.Delete(id); } catch (Exception) { }
                }
                tx.Commit();
            }
        }
        private static List<double[]> PlanPositions(List<Item> items, Rect usable, double gap)
        {
            var mathItems = items.Select(item => new SheetLayoutMath.LayoutItem
            {
                W = item.W,
                H = item.H,
                EstW = item.EstW,
                EstH = item.EstH,
                ReqX = item.ReqX,
                ReqY = item.ReqY,
            }).ToList();
            var points = SheetLayoutMath.PlanPositions(
                mathItems,
                new SheetLayoutMath.Rect
                {
                    X0 = usable.X0,
                    Y0 = usable.Y0,
                    X1 = usable.X1,
                    Y1 = usable.Y1,
                },
                gap);
            return points.Select(p => new[] { p.X, p.Y }).ToList();
        }
        private static void ApplyPositions(List<Item> items, List<double[]> positions)
        {
            for (var i = 0; i < items.Count && i < positions.Count; i++)
            {
                items[i].X = positions[i][0];
                items[i].Y = positions[i][1];
            }
        }
        private static void Measure(Document doc, ViewSheet sheet, double originX, double originY,
            List<Item> items)
        {
            foreach (var item in items)
            {
                if (item.InstanceId == null) continue;
                var element = doc.GetElement(item.InstanceId);
                if (element == null) continue;
                if (item.Kind == "view")
                {
                    var vp = element as Viewport;
                    var box = vp?.GetBoxOutline();
                    if (box == null) continue;
                    item.X = BridgeUnits.Mm(box.MinimumPoint.X) - originX;
                    item.W = BridgeUnits.Mm(box.MaximumPoint.X - box.MinimumPoint.X);
                    item.H = BridgeUnits.Mm(box.MaximumPoint.Y - box.MinimumPoint.Y);
                    item.Y = BridgeUnits.Mm(box.MaximumPoint.Y) - originY; // top edge in sheet coordinates
                }
                else
                {
                    var si = element as ScheduleSheetInstance;
                    var box = si?.get_BoundingBox(sheet);
                    if (box == null) continue;
                    item.X = BridgeUnits.Mm(box.Min.X) - originX;
                    item.W = BridgeUnits.Mm(box.Max.X - box.Min.X);
                    item.H = BridgeUnits.Mm(box.Max.Y - box.Min.Y);
                    item.Y = BridgeUnits.Mm(box.Max.Y) - originY;
                }
            }
        }
    }
}
