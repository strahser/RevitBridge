using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitBridge.Core
{
    /// <summary>
    /// Pure sheet geometry and title-block format rules. No Revit API, no
    /// transactions, no JSON: callers pass millimetre boxes and get numbers.
    /// </summary>
    public static class SheetLayoutMath
    {
        public sealed class Box
        {
            public double W;
            public double H;

            public Box(double w, double h)
            {
                W = w;
                H = h;
            }
        }

        public sealed class Rect
        {
            public double X0;
            public double Y0;
            public double X1;
            public double Y1;

            public double W => X1 - X0;
            public double H => Y1 - Y0;
        }

        public sealed class Point
        {
            public double X;
            public double Y;

            public Point(double x, double y)
            {
                X = x;
                Y = y;
            }
        }

        public sealed class LayoutItem
        {
            public double W;
            public double H;
            public double EstW;
            public double EstH;
            public double? ReqX;
            public double? ReqY;

            public double EffectiveW => W > 0 ? W : EstW;
            public double EffectiveH => H > 0 ? H : EstH;
        }

        public static string FormatName(double w, double h)
        {
            var longSide = Math.Max(w, h);
            var shortSide = Math.Min(w, h);
            if (Math.Abs(longSide - 841) < 5 && Math.Abs(shortSide - 594) < 5) return "A1";
            if (Math.Abs(longSide - 594) < 5 && Math.Abs(shortSide - 420) < 5) return "A2";
            if (Math.Abs(longSide - 420) < 5 && Math.Abs(shortSide - 297) < 5) return "A3";
            if (Math.Abs(longSide - 297) < 5 && Math.Abs(shortSide - 210) < 5) return "A4";
            return "custom";
        }

        public static string SheetLayoutFormat(double w, double h, string fallbackName)
        {
            var longSide = Math.Max(w, h);
            var shortSide = Math.Min(w, h);
            if (Math.Abs(longSide - 841) < 8 && Math.Abs(shortSide - 594) < 8) return "A1";
            if (Math.Abs(longSide - 594) < 8 && Math.Abs(shortSide - 420) < 8) return "A2";
            if (Math.Abs(longSide - 420) < 8 && Math.Abs(shortSide - 297) < 8) return "A3";
            if (Math.Abs(longSide - 297) < 8 && Math.Abs(shortSide - 210) < 8) return "A4";
            return "custom:" + (fallbackName ?? "");
        }

        /// <summary>Finest automatic plan scale: 1:50. Callers may override per view.</summary>
        public const int FinestAutomaticScale = 50;

        private static readonly int[] StandardScales =
            { FinestAutomaticScale, 100, 150, 200, 250, 300, 400, 500 };

        /// <summary>
        /// Pick the smallest standard Revit scale denominator that fits the crops.
        /// The denominator is used as a divisor (<c>width / scale</c>), in mm.
        /// The 1:50 floor is a product decision: building plans are normally issued
        /// at 1:50..1:500; a finer automatic scale makes the sheet unreadable.
        /// </summary>
        public static int RequiredScale(IEnumerable<Box> crops, double planW, double planH)
        {
            var required = 1.0;
            foreach (var box in crops)
            {
                required = Math.Max(required, Math.Max(box.W / planW, box.H / planH));
            }
            foreach (var scale in StandardScales)
            {
                if (scale >= required) return scale;
            }
            return StandardScales.Last();
        }

        public static bool ShelfFits(IReadOnlyList<Box> items, double width, double height, double gap)
        {
            double x = 0, y = height, shelf = 0;
            foreach (var item in items)
            {
                if (x + item.W > width && x > 0)
                {
                    x = 0;
                    y -= shelf + gap;
                    shelf = 0;
                }
                if (y - item.H < -0.5) return false;
                x += item.W + gap;
                shelf = Math.Max(shelf, item.H);
            }
            return true;
        }

        public static List<Point> PlanPositions(
            IReadOnlyList<LayoutItem> items, Rect usable, double gap)
        {
            double x = usable.X0, y = usable.Y1, shelf = 0;
            var result = new List<Point>();
            foreach (var item in items)
            {
                var w = item.EffectiveW;
                var h = item.EffectiveH;
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
                result.Add(new Point(px, py));
            }
            return result;
        }
    }
}
