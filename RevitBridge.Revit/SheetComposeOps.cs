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
    internal static class SheetComposeOps
    {
        private const double MmPerFt = 304.8;

        private static Document Doc(UIApplication app)
        {
            return app.ActiveUIDocument?.Document
                   ?? throw new InvalidOperationException("Нет активного документа.");
        }

        private static double Ft(double mm) => mm / MmPerFt;
        private static double Mm(double ft) => ft * MmPerFt;

        private sealed class Rect
        {
            public string Name;
            public double X0, Y0, X1, Y1; // mm, relative to title block lower-left
            public double W => X1 - X0;
            public double H => Y1 - Y0;
        }

        private sealed class Item
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
            var originX = Mm(tbBox.Min.X);
            var originY = Mm(tbBox.Min.Y);
            var sheetW = Mm(tbBox.Max.X - tbBox.Min.X);
            var sheetH = Mm(tbBox.Max.Y - tbBox.Min.Y);
            var format = SheetLayoutFormat(sheetW, sheetH, titleBlock.Name);
            var tbType = doc.GetElement(titleBlock.GetTypeId()) as ElementType;
            var tbFamily = tbType?.FamilyName ?? "";
            var generated = titleBlock.Name.StartsWith("NB_", StringComparison.OrdinalIgnoreCase) ||
                            tbFamily.StartsWith("NB_", StringComparison.OrdinalIgnoreCase);

            // Safe box is relative to the title block lower-left corner.
            // For A3 (Форма 3 / А3А): left grif x=5..15 mm, stamp y=0..60 mm,
            // frame x=20..415 mm. A conservative x=20..410, y=60..287 is clean.
            var safe = ParseRect4(args["safeBoxMm"]) ??
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
            if (drawFrame && !generated) DrawFrame(doc, sheet, originX, originY, sheetW, sheetH);
            if (generated) DrawStampText(doc, sheet, originX, originY, sheetW, sheetH, args);

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
                    sourceBaseline[srcId] = SnapshotView(source);

                var targetScale = vspec.Value<int?>("scale") ?? source.Scale;
                if (targetScale <= 0) targetScale = 100;

                var crop = ResolveCrop(doc, vspec, source, cropMargin, includeLinked);
                var duplicate = DuplicateView(doc, source, vspec.Value<string>("name"),
                    sheet.SheetNumber, preferIndependentCopy);
                using (var tx = new Transaction(doc, "Bridge Compose Sheet: prepare view"))
                {
                    tx.Start();
                    if (duplicate.Scale != targetScale) duplicate.Scale = targetScale;
                    if (crop != null) SetCrop(duplicate, crop);
                    tx.Commit();
                }

                var systemName = vspec.Value<string>("systemName");
                var colorBySystem = vspec.Value<bool?>("colorBySystem") ?? false;
                if (!string.IsNullOrWhiteSpace(systemName) || colorBySystem)
                    ApplySystemFilter(doc, duplicate, systemName, colorBySystem);

                var viewRect = crop ?? ViewRect(duplicate);
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
                    CropInfo = crop == null ? null : RectInfo(crop),
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
                            var center = new XYZ(Ft(originX + item.X + item.W / 2),
                                                 Ft(originY + item.Y - item.H / 2), 0);
                            var vp = Viewport.Create(doc, sheet.Id, item.View.Id, center);
                            item.InstanceId = vp.Id;
                            item.ViewportUi = EnableViewportUi(doc, vp);
                        }
                        else
                        {
                            var point = new XYZ(Ft(originX + item.X), Ft(originY + item.Y), 0);
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
                            vp.SetBoxCenter(new XYZ(Ft(originX + item.X + item.W / 2),
                                                    Ft(originY + item.Y - item.H / 2), 0));
                        }
                        else
                        {
                            var si = element as ScheduleSheetInstance;
                            var lp = si?.Location as LocationPoint;
                            if (lp == null) continue;
                            var target = new XYZ(Ft(originX + item.X), Ft(originY + item.Y), 0);
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
                                    Ft(originX + finalPlan[i][0] + item.W / 2),
                                    Ft(originY + finalPlan[i][1] - item.H / 2), 0));
                        }
                        else
                        {
                            var lp = (element as ScheduleSheetInstance)?.Location as LocationPoint;
                            if (lp != null) lp.Move(new XYZ(Ft(dx), Ft(dy), 0));
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
            var outside = new List<Dictionary<string, object>>();
            var overlaps = new List<Dictionary<string, object>>();
            var forbidden = new List<Dictionary<string, object>>();
            var stamp = new Rect { Name = "stamp", X0 = 0, Y0 = 0, X1 = sheetW, Y1 = safe.Y0 };
            var grif = new Rect { Name = "leftGrif", X0 = 0, Y0 = 0, X1 = safe.X0, Y1 = sheetH };

            foreach (var item in items.Where(i => i.InstanceId != null && i.W > 0))
            {
                var r = new Rect { Name = item.Name, X0 = item.X, Y0 = item.Y - item.H,
                                   X1 = item.X + item.W, Y1 = item.Y };
                if (r.X0 < usable.X0 - tolerance || r.Y0 < usable.Y0 - tolerance ||
                    r.X1 > usable.X1 + tolerance || r.Y1 > usable.Y1 + tolerance)
                {
                    outside.Add(new Dictionary<string, object>
                    {
                        { "name", r.Name },
                        { "rect", new[] { r.X0, r.Y0, r.X1, r.Y1 } },
                        { "usable", new[] { usable.X0, usable.Y0, usable.X1, usable.Y1 } },
                    });
                }
                foreach (var zone in new[] { stamp, grif })
                {
                    var ox = Math.Min(r.X1, zone.X1) - Math.Max(r.X0, zone.X0);
                    var oy = Math.Min(r.Y1, zone.Y1) - Math.Max(r.Y0, zone.Y0);
                    if (ox > tolerance && oy > tolerance)
                        forbidden.Add(new Dictionary<string, object>
                        {
                            { "name", r.Name }, { "zone", zone.Name },
                            { "overlapMm2", ox * oy },
                        });
                }
            }

            var measured = items.Where(i => i.InstanceId != null && i.W > 0)
                .Select(i => new
                {
                    Item = i,
                    Rect = new Rect
                    {
                        Name = i.Name,
                        X0 = i.X, Y0 = i.Y - i.H, X1 = i.X + i.W, Y1 = i.Y,
                    },
                }).ToList();
            for (var a = 0; a < measured.Count; a++)
            {
                for (var b = a + 1; b < measured.Count; b++)
                {
                    var ra = measured[a].Rect;
                    var rb = measured[b].Rect;
                    var ox = Math.Min(ra.X1, rb.X1) - Math.Max(ra.X0, rb.X0);
                    var oy = Math.Min(ra.Y1, rb.Y1) - Math.Max(ra.Y0, rb.Y0);
                    if (ox > tolerance && oy > tolerance)
                        overlaps.Add(new Dictionary<string, object>
                        {
                            { "a", ra.Name }, { "b", rb.Name },
                            { "overlapMm2", ox * oy },
                        });
                }
            }

            var sourceViewsUnchanged = true;
            foreach (var pair in sourceBaseline)
            {
                var view = doc.GetElement(new ElementId(pair.Key)) as View;
                if (view == null) { sourceViewsUnchanged = false; continue; }
                var now = SnapshotView(view);
                foreach (var key in pair.Value.Keys)
                {
                    if (!now.ContainsKey(key) ||
                        !Equals(now[key]?.ToString(), pair.Value[key]?.ToString()))
                        sourceViewsUnchanged = false;
                }
            }

            if (labelViews) AddViewLabels(doc, sheet, items, originX, originY, usable);

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

        private static Dictionary<string, object> EnableViewportUi(Document doc, Viewport vp)
        {
            var report = new Dictionary<string, object>();
            var parameters = new[]
            {
                BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL,
                BuiltInParameter.VIEWPORT_ATTR_SHOW_BOX,
                BuiltInParameter.VIEWPORT_ATTR_SHOW_EXTENSION_LINE,
            };
            var type = doc.GetElement(vp.GetTypeId()) as ElementType;
            report["typeId"] = vp.GetTypeId().Value;
            report["typeName"] = type?.Name ?? "";
            foreach (var bip in parameters)
            {
                var key = bip.ToString();
                var entry = new Dictionary<string, object>();
                try
                {
                    var inst = vp.get_Parameter(bip);
                    entry["instance"] = inst == null ? "absent" :
                        (inst.IsReadOnly ? "readonly:" + inst.AsInteger() : "writable:" + inst.AsInteger());
                    if (inst != null && !inst.IsReadOnly) inst.Set(1);
                }
                catch (Exception ex) { entry["instanceError"] = ex.Message; }
                try
                {
                    var tp = type?.get_Parameter(bip);
                    entry["type"] = tp == null ? "absent" :
                        (tp.IsReadOnly ? "readonly:" + tp.AsInteger() : "writable:" + tp.AsInteger());
                    if (tp != null && !tp.IsReadOnly) tp.Set(1);
                }
                catch (Exception ex) { entry["typeError"] = ex.Message; }
                report[key] = entry;
            }
            return report;
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
                    {
                        if (connector.MEPSystem != null) return connector.MEPSystem.Name;
                    }
                }
            }
            catch (Exception) { }
            try
            {
                return element.LookupParameter("Имя системы")?.AsString()
                       ?? element.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString()
                       ?? "";
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// Hide the other systems and colour the requested one. This is a test
        /// aid: the production version should use parameter filters instead of
        /// per-element overrides.
        /// </summary>
        private static void ApplySystemFilter(Document doc, View view, string systemName,
            bool colorBySystem)
        {
            var categories = new[]
            {
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_DuctTerminal,
                BuiltInCategory.OST_DuctInsulations,
                BuiltInCategory.OST_MechanicalEquipment,
            };
            var elements = new List<Element>();
            foreach (var category in categories)
            {
                try
                {
                    elements.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(category).WhereElementIsNotElementType());
                }
                catch (Exception) { }
            }

            using (var tx = new Transaction(doc, "Bridge Compose Sheet: system filter"))
            {
                tx.Start();
                foreach (var element in elements)
                {
                    var name = SystemNameOf(element);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var isTarget = !string.IsNullOrWhiteSpace(systemName) &&
                                   string.Equals(name, systemName, StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(systemName) && !isTarget)
                    {
                        try { view.HideElements(new List<ElementId> { element.Id }); }
                        catch (Exception) { }
                        continue;
                    }
                    if (!isTarget && !colorBySystem) continue;

                    var color = name.StartsWith("П", StringComparison.OrdinalIgnoreCase)
                        ? new Color(220, 30, 30)
                        : name.StartsWith("В", StringComparison.OrdinalIgnoreCase)
                            ? new Color(30, 60, 220)
                            : null;
                    if (color == null) continue;
                    try
                    {
                        var overrides = new OverrideGraphicSettings();
                        overrides.SetProjectionLineColor(color);
                        overrides.SetProjectionLineWeight(3);
                        view.SetElementOverrides(element.Id, overrides);
                    }
                    catch (Exception) { }
                }
                tx.Commit();
            }
        }

        private static TextNoteType FindTextType(Document doc)
        {
            try
            {
                return new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>().FirstOrDefault();
            }
            catch (Exception) { return null; }
        }

        private static void DrawStampText(Document doc, ViewSheet sheet, double originX,
            double originY, double width, double height, JObject args)
        {
            var type = FindTextType(doc);
            if (type == null) return;
            var sx = Math.Max(5, width - 190);
            var sy = 5.0;
            var number = sheet.SheetNumber ?? "";
            var name = sheet.Name ?? "";
            if (name.Length > 46) name = name.Substring(0, 46);
            var project = args?.Value<string>("project") ?? "Тестовый проект";
            var objectName = args?.Value<string>("object") ?? "Коридор, уровень 2";
            var stage = args?.Value<string>("stage") ?? "Р";
            var total = 0;
            try
            {
                total = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount();
            }
            catch (Exception) { }

            void Text(double x, double y, string value)
            {
                try
                {
                    TextNote.Create(doc, sheet.Id,
                        new XYZ(Ft(originX + x), Ft(originY + y), 0), value, type.Id);
                }
                catch (Exception) { }
            }

            using (var tx = new Transaction(doc, "Bridge Compose Sheet: stamp text"))
            {
                tx.Start();
                Text(sx + 4, sy + 42, "Проект: " + project);
                Text(sx + 4, sy + 26, "Объект: " + objectName);
                Text(sx + 4, sy + 9, "Стадия: " + stage);
                // Long sheet name goes above the stamp; the stamp cells stay legible.
                Text(sx + 4, sy + 62, "Наименование: " + name);
                Text(sx + 42, sy + 9, "Лист " + number);
                Text(sx + 97, sy + 9, "Листов " + Math.Max(total, 1));
                tx.Commit();
            }
        }

        private static void AddViewLabels(Document doc, ViewSheet sheet, List<Item> items,
            double originX, double originY, Rect usable)
        {
            var type = FindTextType(doc);
            if (type == null) return;
            using (var tx = new Transaction(doc, "Bridge Compose Sheet: labels"))
            {
                tx.Start();
                foreach (var item in items.Where(i => i.Kind == "view" && i.InstanceId != null))
                {
                    var text = item.Name + "  1:" + item.Scale;
                    var y = item.Y + 3;
                    // The label sits in the margin above the viewport; allow the
                    // 8 mm label strip that was reserved before packing.
                    if (y + 4 > usable.Y1 + 8) y = item.Y - item.H + 4;
                    try
                    {
                        TextNote.Create(doc, sheet.Id,
                            new XYZ(Ft(originX + item.X + 2), Ft(originY + y), 0),
                            text, type.Id);
                    }
                    catch (Exception) { }
                }
                tx.Commit();
            }
        }

        /// <summary>
        /// Minimal frame and stamp for a blank A4 title block. Used only for the
        /// placement test while the template lacks a proper A4 drawing title block.
        /// </summary>
        private static void DrawFrame(Document doc, ViewSheet sheet, double originX, double originY,
            double width, double height)
        {
            var textType = FindTextType(doc);
            using (var tx = new Transaction(doc, "Bridge Compose Sheet: frame"))
            {
                tx.Start();
                void DrawLine(double x1, double y1, double x2, double y2)
                {
                    try
                    {
                        doc.Create.NewDetailCurve(sheet, Line.CreateBound(
                            new XYZ(Ft(originX + x1), Ft(originY + y1), 0),
                            new XYZ(Ft(originX + x2), Ft(originY + y2), 0)));
                    }
                    catch (Exception) { }
                }

                var m = 5.0;
                DrawLine(m, m, width - m, m);
                DrawLine(width - m, m, width - m, height - m);
                DrawLine(width - m, height - m, m, height - m);
                DrawLine(m, height - m, m, m);

                var sx = Math.Max(m, width - 190);
                var sy = m;
                var st = 55.0;
                DrawLine(sx, sy, width - m, sy);
                DrawLine(width - m, sy, width - m, sy + st);
                DrawLine(width - m, sy + st, sx, sy + st);
                DrawLine(sx, sy + st, sx, sy);
                DrawLine(sx, sy + 30, width - m, sy + 30);
                DrawLine(sx + 40, sy, sx + 40, sy + st);

                if (textType != null)
                {
                    try
                    {
                        TextNote.Create(doc, sheet.Id,
                            new XYZ(Ft(originX + sx + 45), Ft(originY + sy + 34), 0),
                            "ТЕСТ РАЗМЕЩЕНИЯ A4", textType.Id);
                        TextNote.Create(doc, sheet.Id,
                            new XYZ(Ft(originX + sx + 45), Ft(originY + sy + 12), 0),
                            "Лист 1", textType.Id);
                    }
                    catch (Exception) { }
                }
                tx.Commit();
            }
        }

        private static Dictionary<string, object> SnapshotView(View view)
        {
            var result = new Dictionary<string, object>
            {
                { "scale", view.Scale },
                { "cropActive", view.CropBoxActive },
                { "cropMin", "" },
                { "cropMax", "" },
            };
            if (view.CropBoxActive && view.CropBox != null)
            {
                result["cropMin"] = $"{Mm(view.CropBox.Min.X):F3};{Mm(view.CropBox.Min.Y):F3}";
                result["cropMax"] = $"{Mm(view.CropBox.Max.X):F3};{Mm(view.CropBox.Max.Y):F3}";
            }
            return result;
        }

        private static Rect ParseRect4(JToken token)
        {
            if (token == null) return null;
            var values = token.ToObject<double[]>();
            if (values == null || values.Length != 4) return null;
            return new Rect { X0 = values[0], Y0 = values[1], X1 = values[2], Y1 = values[3] };
        }

        private static string SheetLayoutFormat(double w, double h, string name)
        {
            var longSide = Math.Max(w, h);
            var shortSide = Math.Min(w, h);
            if (Math.Abs(longSide - 841) < 8 && Math.Abs(shortSide - 594) < 8) return "A1";
            if (Math.Abs(longSide - 594) < 8 && Math.Abs(shortSide - 420) < 8) return "A2";
            if (Math.Abs(longSide - 420) < 8 && Math.Abs(shortSide - 297) < 8) return "A3";
            if (Math.Abs(longSide - 297) < 8 && Math.Abs(shortSide - 210) < 8) return "A4";
            return "custom:" + name;
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

        private static View DuplicateView(Document doc, View source, string requestedName,
            string sheetNumber, bool independent)
        {
            var option = independent ? ViewDuplicateOption.Duplicate : ViewDuplicateOption.AsDependent;
            ElementId id;
            View view;
            using (var tx = new Transaction(doc, "Bridge Compose Sheet: duplicate view"))
            {
                tx.Start();
                try { id = source.Duplicate(option); }
                catch (Exception) { id = source.Duplicate(ViewDuplicateOption.AsDependent); }
                view = doc.GetElement(id) as View
                       ?? throw new InvalidOperationException("Failed to duplicate view: " + source.Name);
                try
                {
                    view.Name = string.IsNullOrWhiteSpace(requestedName)
                        ? source.Name + "_L" + (sheetNumber ?? "").Replace(" ", "")
                        : requestedName;
                }
                catch (Exception) { }
                tx.Commit();
            }
            return view;
        }

        private static Rect ResolveCrop(Document doc, JObject spec, View source,
            double cropMargin, bool includeLinked)
        {
            var explicitCrop = ParseRect4(spec["cropMm"]);
            if (explicitCrop != null) { explicitCrop.Name = source.Name; return explicitCrop; }

            var systemId = spec.Value<long?>("systemId");
            if (systemId.HasValue)
            {
                var system = doc.GetElement(new ElementId(systemId.Value)) as MEPSystem;
                if (system == null)
                    throw new InvalidOperationException("MEP system not found: " + systemId.Value);
                var rect = BBoxOf(system.Elements.Cast<Element>(), cropMargin);
                if (rect != null) { rect.Name = source.Name + " (" + system.Name + ")"; return rect; }
            }

            if (spec.Value<bool?>("autoCrop") ?? false)
            {
                var rect = AutoCrop(doc, source, cropMargin, includeLinked);
                if (rect != null) return rect;
            }

            return null;
        }

        private static Rect BBoxOf(IEnumerable<Element> elements, double margin)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            var found = false;
            foreach (var element in elements)
            {
                var box = element.get_BoundingBox(null);
                if (box == null) continue;
                minX = Math.Min(minX, Mm(box.Min.X));
                minY = Math.Min(minY, Mm(box.Min.Y));
                maxX = Math.Max(maxX, Mm(box.Max.X));
                maxY = Math.Max(maxY, Mm(box.Max.Y));
                found = true;
            }
            if (!found) return null;
            return new Rect
            {
                X0 = minX - margin, Y0 = minY - margin,
                X1 = maxX + margin, Y1 = maxY + margin,
            };
        }

        private static Rect AutoCrop(Document doc, View view, double margin, bool includeLinked)
        {
            var level = (view as ViewPlan)?.GenLevel ?? doc.GetElement(view.LevelId) as Level;
            if (level == null) return null;
            var spaces = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType().Cast<Space>()
                .Where(s => s.LevelId == level.Id).ToList();
            var rect = BBoxOf(spaces, margin);
            if (rect != null) return rect;
            if (!includeLinked) return null;

            foreach (var link in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                var transform = link.GetTotalTransform();
                var rooms = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .Cast<Room>().Where(r => r.Level != null &&
                        string.Equals(r.Level.Name, level.Name, StringComparison.OrdinalIgnoreCase));
                var points = new List<XYZ>();
                foreach (var room in rooms)
                {
                    var box = room.get_BoundingBox(null);
                    if (box == null) continue;
                    points.Add(transform.OfPoint(new XYZ(box.Min.X, box.Min.Y, box.Min.Z)));
                    points.Add(transform.OfPoint(new XYZ(box.Max.X, box.Max.Y, box.Max.Z)));
                }
                if (points.Count == 0) continue;
                return new Rect
                {
                    X0 = Mm(points.Min(p => p.X)) - margin,
                    Y0 = Mm(points.Min(p => p.Y)) - margin,
                    X1 = Mm(points.Max(p => p.X)) + margin,
                    Y1 = Mm(points.Max(p => p.Y)) + margin,
                };
            }
            return null;
        }

        private static Rect ViewRect(View view)
        {
            var outline = view.Outline;
            if (outline == null) return new Rect { X0 = 0, Y0 = 0, X1 = 100, Y1 = 100 };
            var scale = Math.Max(view.Scale, 1);
            return new Rect
            {
                X0 = 0, Y0 = 0,
                X1 = (outline.Max.U - outline.Min.U) * MmPerFt * scale,
                Y1 = (outline.Max.V - outline.Min.V) * MmPerFt * scale,
            };
        }

        private static void SetCrop(View view, Rect rect)
        {
            var level = (view as ViewPlan)?.GenLevel;
            var zBase = level?.Elevation ?? 0;
            var box = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(Ft(rect.X0), Ft(rect.Y0), zBase - Ft(1000)),
                Max = new XYZ(Ft(rect.X1), Ft(rect.Y1), zBase + Ft(10000)),
            };
            view.CropBox = box;
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
        }

        private static Dictionary<string, object> RectInfo(Rect rect)
        {
            return new Dictionary<string, object>
            {
                { "x0Mm", rect.X0 }, { "y0Mm", rect.Y0 },
                { "x1Mm", rect.X1 }, { "y1Mm", rect.Y1 },
                { "widthMm", rect.W }, { "heightMm", rect.H },
            };
        }

        private static List<double[]> PlanPositions(List<Item> items, Rect usable, double gap)
        {
            double x = usable.X0, y = usable.Y1, shelf = 0;
            var result = new List<double[]>();
            foreach (var item in items)
            {
                var w = item.W > 0 ? item.W : item.EstW;
                var h = item.H > 0 ? item.H : item.EstH;
                double px, py;
                if (item.ReqX.HasValue && item.ReqY.HasValue)
                {
                    px = item.ReqX.Value;
                    py = item.ReqY.Value;
                }
                else if (item.ReqX.HasValue)
                {
                    px = item.ReqX.Value;
                    py = y;
                    shelf = Math.Max(shelf, h);
                }
                else if (item.ReqY.HasValue)
                {
                    px = x;
                    py = item.ReqY.Value;
                    x += w + gap;
                    shelf = Math.Max(shelf, h);
                }
                else
                {
                    if (x + w > usable.X1 && x > usable.X0)
                    {
                        x = usable.X0;
                        y -= shelf + gap;
                        shelf = 0;
                    }
                    px = x;
                    py = y;
                    x += w + gap;
                    shelf = Math.Max(shelf, h);
                }
                result.Add(new[] { px, py });
            }
            return result;
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
                    item.X = Mm(box.MinimumPoint.X) - originX;
                    item.W = Mm(box.MaximumPoint.X - box.MinimumPoint.X);
                    item.H = Mm(box.MaximumPoint.Y - box.MinimumPoint.Y);
                    item.Y = Mm(box.MaximumPoint.Y) - originY; // top edge in sheet coordinates
                }
                else
                {
                    var si = element as ScheduleSheetInstance;
                    var box = si?.get_BoundingBox(sheet);
                    if (box == null) continue;
                    item.X = Mm(box.Min.X) - originX;
                    item.W = Mm(box.Max.X - box.Min.X);
                    item.H = Mm(box.Max.Y - box.Min.Y);
                    item.Y = Mm(box.Max.Y) - originY;
                }
            }
        }
    }
}
