using System;
using System.Collections.Generic;

namespace RevitBridge.Core
{
    /// <summary>
    /// Pure placement validation for sheet items. No Revit API, no transactions,
    /// no JSON: callers pass millimetre boxes and get the same three violation
    /// lists that SheetComposeOps used to build inline (Phase 5).
    /// </summary>
    public static class SheetValidation
    {
        public sealed class Box
        {
            public string Name;
            public double X0, Y0, X1, Y1; // mm, relative to title block lower-left

            public Box() { }

            public Box(string name, double x0, double y0, double x1, double y1)
            {
                Name = name;
                X0 = x0;
                Y0 = y0;
                X1 = x1;
                Y1 = y1;
            }

            public double W => X1 - X0;
            public double H => Y1 - Y0;
        }

        public sealed class OutsideEntry
        {
            public string Name;
            public double[] Rect;   // x0, y0, x1, y1
            public double[] Usable; // x0, y0, x1, y1
        }

        public sealed class ForbiddenEntry
        {
            public string Name;
            public string Zone;
            public double OverlapMm2;
        }

        public sealed class OverlapEntry
        {
            public string A;
            public string B;
            public double OverlapMm2;
        }

        public sealed class Result
        {
            public readonly List<OutsideEntry> Outside = new List<OutsideEntry>();
            public readonly List<OverlapEntry> Overlaps = new List<OverlapEntry>();
            public readonly List<ForbiddenEntry> Forbidden = new List<ForbiddenEntry>();

            public bool Valid => Outside.Count == 0 && Overlaps.Count == 0 && Forbidden.Count == 0;
        }

        /// <summary>
        /// Validate measured item boxes against the usable area, the stamp and
        /// the left grif, then check every pair for mutual overlap. A positive
        /// overlap strictly greater than <paramref name="tolerance"/> counts as
        /// a violation; touching edges and sub-millimetre noise do not.
        /// </summary>
        public static Result Validate(IReadOnlyList<Box> items, Box usable, Box safe,
            double sheetW, double sheetH, double tolerance = 1.0)
        {
            var result = new Result();
            if (items == null || usable == null || safe == null) return result;

            var stamp = new Box("stamp", 0, 0, sheetW, safe.Y0);
            var grif = new Box("leftGrif", 0, 0, safe.X0, sheetH);

            var measured = new List<Box>(items.Count);
            foreach (var item in items)
            {
                if (item == null) continue;
                measured.Add(item);

                if (item.X0 < usable.X0 - tolerance || item.Y0 < usable.Y0 - tolerance ||
                    item.X1 > usable.X1 + tolerance || item.Y1 > usable.Y1 + tolerance)
                {
                    result.Outside.Add(new OutsideEntry
                    {
                        Name = item.Name,
                        Rect = new[] { item.X0, item.Y0, item.X1, item.Y1 },
                        Usable = new[] { usable.X0, usable.Y0, usable.X1, usable.Y1 },
                    });
                }

                foreach (var zone in new[] { stamp, grif })
                {
                    var ox = Math.Min(item.X1, zone.X1) - Math.Max(item.X0, zone.X0);
                    var oy = Math.Min(item.Y1, zone.Y1) - Math.Max(item.Y0, zone.Y0);
                    if (ox > tolerance && oy > tolerance)
                    {
                        result.Forbidden.Add(new ForbiddenEntry
                        {
                            Name = item.Name,
                            Zone = zone.Name,
                            OverlapMm2 = ox * oy,
                        });
                    }
                }
            }

            for (var a = 0; a < measured.Count; a++)
            {
                for (var b = a + 1; b < measured.Count; b++)
                {
                    var ra = measured[a];
                    var rb = measured[b];
                    var ox = Math.Min(ra.X1, rb.X1) - Math.Max(ra.X0, rb.X0);
                    var oy = Math.Min(ra.Y1, rb.Y1) - Math.Max(ra.Y0, rb.Y0);
                    if (ox > tolerance && oy > tolerance)
                    {
                        result.Overlaps.Add(new OverlapEntry
                        {
                            A = ra.Name,
                            B = rb.Name,
                            OverlapMm2 = ox * oy,
                        });
                    }
                }
            }

            return result;
        }
    }
}
