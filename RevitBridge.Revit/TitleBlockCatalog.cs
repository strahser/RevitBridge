using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Core;

namespace RevitBridge
{
    /// <summary>
    /// Title block catalog: measures title block formats from a document,
    /// lists them for the bridge and chooses a format that fits the layout.
    /// Behavior, names and JSON shape are unchanged from SheetLayoutOps.
    /// </summary>
    internal static class TitleBlockCatalog
    {
        internal sealed class TitleBlockInfo
        {
            public ElementId SymbolId;
            public string FamilyName;
            public string Name;
            public double W;
            public double H;
        }

        public static object List(UIApplication app, JObject args)
        {
            var doc = SheetLayoutOps.Doc(app);
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

        internal static List<TitleBlockInfo> MeasureTitleBlocks(Document doc)
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
                                W = SheetLayoutOps.Mm(box.Max.X - box.Min.X),
                                H = SheetLayoutOps.Mm(box.Max.Y - box.Min.Y),
                            });
                        }
                    }
                    catch (Exception) { }
                }
                tx.RollBack();
            }
            return result;
        }

        internal static TitleBlockInfo ChooseTitleBlock(
            List<TitleBlockInfo> formats,
            Dictionary<long, SheetLayoutOps.Rect> crops,
            List<SheetLayoutOps.ScheduleEstimate> estimates,
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
                    .Select(r => new SheetLayoutOps.Placement { W = r.W / scale + 8, H = r.H / scale + 8 })
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
    }
}
