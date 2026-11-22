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
            var options = ComposeOptions.Parse(args);
            var sheet = FindOrCreateSheet(doc, options);
            var context = BuildContext(doc, sheet, options, args);

            if (options.ClearSheet) ClearSheet(doc, sheet);
            if (options.DrawFrame && !context.Generated)
                SheetAnnotationOps.DrawFrame(doc, sheet, context.OriginX, context.OriginY, context.SheetW, context.SheetH);
            if (context.Generated)
                SheetAnnotationOps.DrawStampText(doc, sheet, context.OriginX, context.OriginY, context.SheetW, context.SheetH, args);

            var items = BuildItems(doc, sheet, args, options, out var sourceBaseline);
            var creationErrors = CreatePlacements(doc, items, context);
            var moveErrors = AlignItems(doc, items, context);
            var validation = ValidateItems(doc, items, context);
            var sourceViewsUnchanged = CheckSourceViews(doc, sourceBaseline);
            if (options.LabelViews)
                SheetAnnotationOps.AddViewLabels(doc, sheet, items, context.OriginX, context.OriginY, context.Usable);

            return BuildResult(sheet, context, items, validation, creationErrors, moveErrors, sourceViewsUnchanged);
        }

        private sealed class ComposeOptions
        {
            public string TitleBlockName;
            public double Margin, Gap, StampReserve, CropMargin;
            public bool IncludeLinked, ClearSheet, PreferIndependentCopy, DrawFrame, LabelViews;
            public long? SheetId;
            public string Number, Name;

            public static ComposeOptions Parse(JObject args)
            {
                args = args ?? new JObject();
                var titleBlockName = args.Value<string>("titleBlockName") ?? "Форма 3";
                if (titleBlockName.IndexOf("форма 6", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    titleBlockName.IndexOf("form 6", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new ArgumentException(
                        "Форма 6 is a specification form and must not be used for drawing sheets.");
                }

                return new ComposeOptions
                {
                    TitleBlockName = titleBlockName,
                    Margin = args.Value<double?>("marginMm") ?? 10,
                    Gap = args.Value<double?>("gapMm") ?? 12,
                    StampReserve = args.Value<double?>("stampReserveMm") ?? 0,
                    CropMargin = args.Value<double?>("cropMarginMm") ?? 1000,
                    IncludeLinked = args.Value<bool?>("cropIncludeLinked") ?? false,
                    ClearSheet = args.Value<bool?>("clearSheet") ?? true,
                    PreferIndependentCopy = args.Value<bool?>("independentCopy") ?? true,
                    DrawFrame = args.Value<bool?>("drawFrame") ?? false,
                    LabelViews = args.Value<bool?>("labelViews") ?? true,
                    SheetId = args.Value<long?>("sheetId"),
                    Number = args.Value<string>("number") ?? "ТЕСТ-NEW",
                    Name = args.Value<string>("name") ?? "Тестовый лист",
                };
            }
        }

        private sealed class SheetContext
        {
            public ViewSheet Sheet;
            public Element TitleBlock;
            public double OriginX, OriginY, SheetW, SheetH;
            public string Format;
            public string TbFamily;
            public bool Generated;
            public Rect Safe, Usable;
            public double Gap;
        }

        private static ViewSheet FindOrCreateSheet(Document doc, ComposeOptions options)
        {
            ViewSheet sheet;
            if (options.SheetId.HasValue)
            {
                sheet = doc.GetElement(new ElementId(options.SheetId.Value)) as ViewSheet
                        ?? throw new InvalidOperationException("Sheet not found: " + options.SheetId.Value);
            }
            else
            {
                var symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => string.Equals(s.Name, options.TitleBlockName, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(s.Family.Name, options.TitleBlockName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Title block not found: " + options.TitleBlockName);

                using (var tx = new Transaction(doc, "Bridge Compose Sheet: create"))
                {
                    tx.Start();
                    sheet = ViewSheet.Create(doc, symbol.Id);
                    try { sheet.SheetNumber = options.Number; } catch (Exception) { }
                    try { sheet.Name = options.Name; } catch (Exception) { }
                    tx.Commit();
                }
            }
            return sheet;
        }

        private static SheetContext BuildContext(Document doc, ViewSheet sheet, ComposeOptions options, JObject args)
        {
            var context = new SheetContext { Sheet = sheet, Gap = options.Gap };
            var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().FirstElement()
                ?? throw new InvalidOperationException("Title block instance not found on sheet.");
            context.TitleBlock = titleBlock;
            var tbBox = titleBlock.get_BoundingBox(sheet)
                        ?? throw new InvalidOperationException("Title block bounding box is not available.");
            context.OriginX = BridgeUnits.Mm(tbBox.Min.X);
            context.OriginY = BridgeUnits.Mm(tbBox.Min.Y);
            context.SheetW = BridgeUnits.Mm(tbBox.Max.X - tbBox.Min.X);
            context.SheetH = BridgeUnits.Mm(tbBox.Max.Y - tbBox.Min.Y);
            context.Format = SheetLayoutMath.SheetLayoutFormat(context.SheetW, context.SheetH, titleBlock.Name);
            var tbType = doc.GetElement(titleBlock.GetTypeId()) as ElementType;
            context.TbFamily = tbType?.FamilyName ?? "";
            context.Generated = titleBlock.Name.StartsWith("NB_", StringComparison.OrdinalIgnoreCase) ||
                                context.TbFamily.StartsWith("NB_", StringComparison.OrdinalIgnoreCase);

            // Safe box is relative to the title block lower-left corner.
            // For A3 (Форма 3 / А3А): left grif x=5..15 mm, stamp y=0..60 mm,
            // frame x=20..415 mm. A conservative x=20..410, y=60..287 is clean.
            context.Safe = SheetViewOps.ParseRect4(args["safeBoxMm"]) ??
                           DefaultSafeBox(context.Format, context.Generated ? context.TbFamily : titleBlock.Name);
            var usable = new Rect
            {
                Name = "usable",
                X0 = context.Safe.X0 + options.Margin,
                Y0 = context.Safe.Y0 + options.Margin + options.StampReserve,
                X1 = context.Safe.X1 - options.Margin,
                Y1 = context.Safe.Y1 - options.Margin,
            };
            if (usable.W < 60 || usable.H < 60)
                throw new InvalidOperationException("Safe box is too small for margins: " +
                                                    $"{usable.W:F1}x{usable.H:F1} mm.");
            if (options.LabelViews)
            {
                // Reserve a strip above the first row for view titles placed on top.
                usable.Y1 -= 8;
                context.Gap = Math.Max(context.Gap, 14);
            }
            context.Usable = usable;
            return context;
        }

        private static List<Item> BuildItems(Document doc, ViewSheet sheet, JObject args,
            ComposeOptions options, out Dictionary<long, Dictionary<string, object>> sourceBaseline)
        {
            var items = BuildViewItems(doc, sheet, args, options, out sourceBaseline);
            items.AddRange(BuildScheduleItems(doc, args));
            if (items.Count == 0)
                throw new ArgumentException("Specify at least one view or schedule.");
            return items;
        }

        private static List<Item> BuildViewItems(Document doc, ViewSheet sheet, JObject args,
            ComposeOptions options, out Dictionary<long, Dictionary<string, object>> sourceBaseline)
        {
            var items = new List<Item>();
            sourceBaseline = new Dictionary<long, Dictionary<string, object>>();

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

                var crop = SheetViewOps.ResolveCrop(doc, vspec, source, options.CropMargin, options.IncludeLinked);
                var duplicate = SheetViewOps.DuplicateView(doc, source, vspec.Value<string>("name"),
                    sheet.SheetNumber, options.PreferIndependentCopy);
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
            return items;
        }

        private static List<Item> BuildScheduleItems(Document doc, JObject args)
        {
            var items = new List<Item>();
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
            return items;
        }

        private static List<Dictionary<string, object>> CreatePlacements(Document doc, List<Item> items,
            SheetContext context)
        {
            // Phase 1: create everything at estimated positions so Revit can size the boxes.
            ApplyPositions(items, PlanPositions(items, context.Usable, context.Gap));
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
                            var center = new XYZ(BridgeUnits.Ft(context.OriginX + item.X + item.W / 2),
                                                 BridgeUnits.Ft(context.OriginY + item.Y - item.H / 2), 0);
                            var vp = Viewport.Create(doc, context.Sheet.Id, item.View.Id, center);
                            item.InstanceId = vp.Id;
                            item.ViewportUi = SheetViewOps.EnableViewportUi(doc, vp);
                        }
                        else
                        {
                            var point = new XYZ(BridgeUnits.Ft(context.OriginX + item.X), BridgeUnits.Ft(context.OriginY + item.Y), 0);
                            var si = ScheduleSheetInstance.Create(doc, context.Sheet.Id, item.Schedule.Id, point);
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
            return creationErrors;
        }

        private static List<Dictionary<string, object>> AlignItems(Document doc, List<Item> items,
            SheetContext context)
        {
            // Phase 2: measure ACTUAL boxes.
            Measure(doc, context.Sheet, context.OriginX, context.OriginY, items);

            // Phase 3: re-pack using measured sizes and move to exact positions.
            var planned = PlanPositions(items, context.Usable, context.Gap);
            ApplyPositions(items, planned);
            var moveErrors = MoveToPlan(doc, items, context);

            // Phase 4: measure after the move; schedules have a constant offset
            // between their insertion point and their visible bounding box, so a
            // second correction pass is needed for exact coordinates.
            Measure(doc, context.Sheet, context.OriginX, context.OriginY, items);
            var finalPlan = PlanPositions(items, context.Usable, context.Gap);
            FineAlign(doc, items, context, finalPlan, moveErrors);
            return moveErrors;
        }

        private static List<Dictionary<string, object>> MoveToPlan(Document doc, List<Item> items,
            SheetContext context)
        {
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
                            vp.SetBoxCenter(new XYZ(BridgeUnits.Ft(context.OriginX + item.X + item.W / 2),
                                                    BridgeUnits.Ft(context.OriginY + item.Y - item.H / 2), 0));
                        }
                        else
                        {
                            var si = element as ScheduleSheetInstance;
                            var lp = si?.Location as LocationPoint;
                            if (lp == null) continue;
                            var target = new XYZ(BridgeUnits.Ft(context.OriginX + item.X), BridgeUnits.Ft(context.OriginY + item.Y), 0);
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
            return moveErrors;
        }

        private static void FineAlign(Document doc, List<Item> items, SheetContext context,
            List<double[]> finalPlan, List<Dictionary<string, object>> moveErrors)
        {
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
                                    BridgeUnits.Ft(context.OriginX + finalPlan[i][0] + item.W / 2),
                                    BridgeUnits.Ft(context.OriginY + finalPlan[i][1] - item.H / 2), 0));
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
        }

        private static SheetValidation.Result ValidateItems(Document doc, List<Item> items, SheetContext context)
        {
            // Phase 5: final validation on real boxes.
            Measure(doc, context.Sheet, context.OriginX, context.OriginY, items);
            var tolerance = 1.0; // mm
            var validationItems = items.Where(i => i.InstanceId != null && i.W > 0)
                .Select(i => new SheetValidation.Box(i.Name, i.X, i.Y - i.H, i.X + i.W, i.Y))
                .ToList();
            return SheetValidation.Validate(
                validationItems,
                new SheetValidation.Box("usable", context.Usable.X0, context.Usable.Y0, context.Usable.X1, context.Usable.Y1),
                new SheetValidation.Box("safe", context.Safe.X0, context.Safe.Y0, context.Safe.X1, context.Safe.Y1),
                context.SheetW, context.SheetH, tolerance);
        }

        private static bool CheckSourceViews(Document doc, Dictionary<long, Dictionary<string, object>> sourceBaseline)
        {
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
            return sourceViewsUnchanged;
        }

        private static List<Dictionary<string, object>> BuildPlacements(List<Item> items)
        {
            return items.Select(i => new Dictionary<string, object>
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
        }

        private static Dictionary<string, object> BuildResult(ViewSheet sheet, SheetContext context, List<Item> items,
            SheetValidation.Result validation, List<Dictionary<string, object>> creationErrors,
            List<Dictionary<string, object>> moveErrors, bool sourceViewsUnchanged)
        {
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

            var placements = BuildPlacements(items);

            return new Dictionary<string, object>
            {
                { "sheetId", sheet.Id.Value },
                { "sheetNumber", sheet.SheetNumber },
                { "sheetName", sheet.Name },
                { "titleBlock", context.TitleBlock.Name },
                { "format", context.Format },
                { "sheetWidthMm", context.SheetW },
                { "sheetHeightMm", context.SheetH },
                { "originMm", new[] { context.OriginX, context.OriginY } },
                { "safeBoxMm", new[] { context.Safe.X0, context.Safe.Y0, context.Safe.X1, context.Safe.Y1 } },
                { "usableMm", new[] { context.Usable.X0, context.Usable.Y0, context.Usable.X1, context.Usable.Y1 } },
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
