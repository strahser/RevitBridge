using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    /// <summary>
    /// Generates a consistent set of drawing title blocks (A4..A0) with a
    /// frame and a 185x55 GOST-like stamp. The stamp text (sheet number, name,
    /// project, stage) is added as sheet annotations by SheetComposeOps.
    /// </summary>
    internal static class TitleBlockOps
    {
        private const double MmPerFoot = 304.8;

        private static readonly Dictionary<string, double[]> Formats =
            new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "A4", new[] { 297.0, 210.0 } },
                { "A3", new[] { 420.0, 297.0 } },
                { "A2", new[] { 594.0, 420.0 } },
                { "A1", new[] { 841.0, 594.0 } },
                { "A0", new[] { 1189.0, 841.0 } },
            };

        private static double Ft(double mm) => mm / MmPerFoot;

        private static Document Doc(UIApplication app) =>
            app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Нет активного документа.");

        public static object CreateSet(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var outputDir = args?.Value<string>("outputDir")
                            ?? @"D:\temp\pipeline_out\titleblocks";
            var formats = args?["formats"]?.ToObject<string[]>()
                          ?? new[] { "A4", "A3", "A2", "A1", "A0" };
            var templateDir = args?.Value<string>("templateDir")
                              ?? @"C:\ProgramData\Autodesk\RVT 2024\Family Templates\English\Titleblocks";
            Directory.CreateDirectory(outputDir);

            var result = new List<Dictionary<string, object>>();
            foreach (var format in formats)
            {
                var name = "NB_" + format.ToUpperInvariant();
                var existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => string.Equals(s.Family.Name, name,
                        StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    result.Add(new Dictionary<string, object>
                    {
                        { "format", format }, { "family", name },
                        { "status", "exists" }, { "symbolId", existing.Id.Value },
                    });
                    continue;
                }

                if (!Formats.TryGetValue(format, out var size))
                {
                    result.Add(new Dictionary<string, object>
                    {
                        { "format", format }, { "status", "unsupported" },
                    });
                    continue;
                }

                var template = Path.Combine(templateDir, format + " metric.rft");
                if (!File.Exists(template))
                {
                    result.Add(new Dictionary<string, object>
                    {
                        { "format", format }, { "status", "template_missing" },
                        { "template", template },
                    });
                    continue;
                }

                var rfa = Path.Combine(outputDir, name + ".rfa");
                var familyDoc = app.Application.NewFamilyDocument(template);
                if (familyDoc == null)
                {
                    result.Add(new Dictionary<string, object>
                    {
                        { "format", format }, { "status", "family_doc_failed" },
                    });
                    continue;
                }

                try
                {
                    var view = familyDoc.ActiveView
                               ?? new FilteredElementCollector(familyDoc)
                                   .OfClass(typeof(View)).Cast<View>()
                                   .FirstOrDefault(v => !v.IsTemplate);
                    if (view == null)
                        throw new InvalidOperationException("No view in family document.");

                    using (var tx = new Transaction(familyDoc, "NB Title Block Geometry"))
                    {
                        tx.Start();
                        DrawFrame(familyDoc, view, size[0], size[1]);
                        tx.Commit();
                    }
                    familyDoc.SaveAs(rfa);
                }
                finally
                {
                    familyDoc.Close(false);
                }

                Family family = null;
                using (var tx = new Transaction(doc, "NB Load Title Block"))
                {
                    tx.Start();
                    doc.LoadFamily(rfa, out family);
                    tx.Commit();
                }

                var symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => family != null && s.Family.Id == family.Id);
                result.Add(new Dictionary<string, object>
                {
                    { "format", format }, { "family", family?.Name ?? name },
                    { "status", symbol == null ? "loaded_no_symbol" : "created" },
                    { "symbolId", symbol?.Id.Value ?? 0 },
                    { "rfa", rfa },
                });
            }

            return new Dictionary<string, object>
            {
                { "outputDir", outputDir },
                { "items", result },
                { "created", result.Count(r => (string)r["status"] == "created") },
            };
        }

        private static void DrawFrame(Document familyDoc, View view, double width, double height)
        {
            void DrawLine(double x1, double y1, double x2, double y2)
            {
                try
                {
                    familyDoc.FamilyCreate.NewDetailCurve(view, Line.CreateBound(
                        new XYZ(Ft(x1), Ft(y1), 0), new XYZ(Ft(x2), Ft(y2), 0)));
                }
                catch (Exception) { }
            }

            // Outer frame 5 mm; inner frame 20 mm left / 5 mm other sides.
            DrawLine(5, 5, width - 5, 5);
            DrawLine(width - 5, 5, width - 5, height - 5);
            DrawLine(width - 5, height - 5, 5, height - 5);
            DrawLine(5, height - 5, 5, 5);

            DrawLine(20, 5, width - 5, 5);
            DrawLine(width - 5, 5, width - 5, height - 5);
            DrawLine(width - 5, height - 5, 20, height - 5);
            DrawLine(20, height - 5, 20, 5);

            // Stamp 185x55 at bottom-right.
            var sx = width - 190;
            var sy = 5;
            var sh = 55;
            DrawLine(sx, sy, width - 5, sy);
            DrawLine(width - 5, sy, width - 5, sy + sh);
            DrawLine(width - 5, sy + sh, sx, sy + sh);
            DrawLine(sx, sy + sh, sx, sy);

            // Horizontal and vertical field dividers (simple GOST-like grid).
            DrawLine(sx, sy + 30, width - 5, sy + 30);
            DrawLine(sx, sy + 15, width - 5, sy + 15);
            DrawLine(sx + 40, sy, sx + 40, sy + sh);
            DrawLine(sx + 65, sy, sx + 65, sy + sh);
            DrawLine(sx + 95, sy, sx + 95, sy + sh);
            DrawLine(sx + 130, sy, sx + 130, sy + sh);
            DrawLine(width - 40, sy, width - 40, sy + sh);
        }
    }
}
