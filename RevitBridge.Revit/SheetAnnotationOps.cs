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
    /// Text and frame annotation helpers for composed sheets.
    /// </summary>
    internal static class SheetAnnotationOps
    {
        internal static void DrawStampText(Document doc, ViewSheet sheet, double originX,
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
                        new XYZ(BridgeUnits.Ft(originX + x), BridgeUnits.Ft(originY + y), 0), value, type.Id);
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
        internal static void AddViewLabels(Document doc, ViewSheet sheet, List<SheetComposer.Item> items,
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
                            new XYZ(BridgeUnits.Ft(originX + item.X + 2), BridgeUnits.Ft(originY + y), 0),
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
        internal static void DrawFrame(Document doc, ViewSheet sheet, double originX, double originY,
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
                            new XYZ(BridgeUnits.Ft(originX + x1), BridgeUnits.Ft(originY + y1), 0),
                            new XYZ(BridgeUnits.Ft(originX + x2), BridgeUnits.Ft(originY + y2), 0)));
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
                            new XYZ(BridgeUnits.Ft(originX + sx + 45), BridgeUnits.Ft(originY + sy + 34), 0),
                            "ТЕСТ РАЗМЕЩЕНИЯ A4", textType.Id);
                        TextNote.Create(doc, sheet.Id,
                            new XYZ(BridgeUnits.Ft(originX + sx + 45), BridgeUnits.Ft(originY + sy + 12), 0),
                            "Лист 1", textType.Id);
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
    }
}
