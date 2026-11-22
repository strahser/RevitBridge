using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    internal static class ViewSheetOps
    {
        public static object ListViews(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var includeTemplates = args?.Value<bool?>("includeTemplates") ?? true;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => includeTemplates || !v.IsTemplate)
                .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name)
                .Select(v =>
                {
                    // View templates have no crop box; Outline throws for them.
                    var outline = v.IsTemplate ? null : v.Outline;
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
            var doc = BridgeOps.Doc(app);
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
                        { "titleBlockId", titleBlock == null ? 0 : titleBlock.Id.Value },
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

        public static object SetViewScale(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var view = doc.GetElement(BridgeOps.Id(args, "viewId")) as View
                ?? throw new InvalidOperationException("View not found.");
            var scale = args?.Value<int?>("scale") ?? 100;
            if (scale <= 0) throw new ArgumentException("args.scale must be positive");
            using (var tx = new Transaction(doc, "Bridge Set View Scale"))
            {
                tx.Start();
                view.Scale = scale;
                tx.Commit();
            }
            return new Dictionary<string, object>
            {
                { "id", view.Id.Value }, { "name", view.Name }, { "scale", view.Scale },
            };
        }

        public static object SetViewCrop(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var view = doc.GetElement(BridgeOps.Id(args, "viewId")) as View
                ?? throw new InvalidOperationException("View not found.");
            var min = args?["minMm"]?.ToObject<double[]>() ?? new double[] { 0, 0 };
            var max = args?["maxMm"]?.ToObject<double[]>() ?? new double[] { 1000, 1000 };
            var level = (view as ViewPlan)?.GenLevel;
            var zBase = level?.Elevation ?? 0;
            using (var tx = new Transaction(doc, "Bridge Set View Crop"))
            {
                tx.Start();
                view.CropBox = new BoundingBoxXYZ
                {
                    Transform = Transform.Identity,
                    Min = new XYZ(BridgeOps.Mm(min[0]), BridgeOps.Mm(min[1]), zBase - BridgeOps.Mm(1000)),
                    Max = new XYZ(BridgeOps.Mm(max[0]), BridgeOps.Mm(max[1]), zBase + BridgeOps.Mm(10000)),
                };
                view.CropBoxActive = true;
                view.CropBoxVisible = false;
                tx.Commit();
            }
            return new Dictionary<string, object>
            {
                { "id", view.Id.Value }, { "name", view.Name },
                { "cropActive", view.CropBoxActive }, { "scale", view.Scale },
            };
        }

        public static object CreateSheet(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var number = args?.Value<string>("number") ?? "ТЕСТ-01";
            var name = args?.Value<string>("name") ?? "Тестовый лист ОВ";
            var titleBlockName = args?.Value<string>("titleBlockName");
            var titleBlockFamily = args?.Value<string>("titleBlockFamily");

            var candidates = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>()
                .Where(s => s.Family != null)
                .Where(s => string.IsNullOrWhiteSpace(titleBlockFamily) ||
                            string.Equals(s.Family.Name, titleBlockFamily,
                                          StringComparison.OrdinalIgnoreCase))
                .ToList();
            var titleBlock = candidates
                .FirstOrDefault(s => string.IsNullOrWhiteSpace(titleBlockName) ||
                                     string.Equals(s.Name, titleBlockName,
                                                   StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    "Title block family symbol not loaded: "
                    + (titleBlockFamily ?? "*") + " / " + (titleBlockName ?? "*"));

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
            var doc = BridgeOps.Doc(app);
            var sheet = doc.GetElement(BridgeOps.Id(args, "sheetId")) as ViewSheet
                ?? throw new InvalidOperationException("Sheet not found.");
            var view = doc.GetElement(BridgeOps.Id(args, "viewId")) as View
                ?? throw new InvalidOperationException("View not found.");
            var point = new XYZ(BridgeOps.Mm(args?.Value<double?>("xMm") ?? 20),
                BridgeOps.Mm(args?.Value<double?>("yMm") ?? 20), 0);

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
            var doc = BridgeOps.Doc(app);
            var sheet = doc.GetElement(BridgeOps.Id(args, "sheetId")) as ViewSheet
                ?? throw new InvalidOperationException("Sheet not found.");
            var schedule = doc.GetElement(BridgeOps.Id(args, "scheduleId")) as ViewSchedule
                ?? throw new InvalidOperationException("Schedule not found.");
            var point = new XYZ(BridgeOps.Mm(args?.Value<double?>("xMm") ?? 20),
                BridgeOps.Mm(args?.Value<double?>("yMm") ?? 150), 0);

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
            var doc = BridgeOps.Doc(app);
            var path = args?.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("args.path is required");
            var sheetId = BridgeOps.Id(args, "sheetId");
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
