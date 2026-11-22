using RevitBridge.Core;
using TUnit.Core;

namespace RevitBridge.Core.Tests;

public class SheetLayoutMathTests
{
    [Test]
    public async Task FormatName_Recognises_Standard_Sheets()
    {
        await Assert.That(SheetLayoutMath.FormatName(297, 210)).IsEqualTo("A4");
        await Assert.That(SheetLayoutMath.FormatName(420, 297)).IsEqualTo("A3");
        await Assert.That(SheetLayoutMath.FormatName(594, 420)).IsEqualTo("A2");
        await Assert.That(SheetLayoutMath.FormatName(841, 594)).IsEqualTo("A1");
        await Assert.That(SheetLayoutMath.FormatName(500, 300)).IsEqualTo("custom");
    }

    [Test]
    public async Task SheetLayoutFormat_Includes_Custom_Name()
    {
        await Assert.That(SheetLayoutMath.SheetLayoutFormat(420, 297, "X")).IsEqualTo("A3");
        await Assert.That(SheetLayoutMath.SheetLayoutFormat(500, 300, "X"))
            .IsEqualTo("custom:X");
    }

    [Test]
    public async Task RequiredScale_Uses_Standard_Denominator()
    {
        var crops = new[]
        {
            new SheetLayoutMath.Box(420, 297),
            new SheetLayoutMath.Box(210, 297),
        };
        // Preserve the legacy formula from SheetLayoutOps (it treats the
        // resulting ratio as a denominator directly). Do not change this
        // silently; the unit interpretation is tracked in P4.
        await Assert.That(SheetLayoutMath.RequiredScale(crops, 200, 150)).IsEqualTo(50);
    }

    [Test]
    public async Task ShelfFits_Detects_Overflow()
    {
        var fits = new[]
        {
            new SheetLayoutMath.Box(40, 20),
            new SheetLayoutMath.Box(40, 20),
        };
        var tooTall = new[]
        {
            new SheetLayoutMath.Box(40, 90),
            new SheetLayoutMath.Box(40, 90),
        };
        await Assert.That(SheetLayoutMath.ShelfFits(fits, 100, 50, 10)).IsTrue();
        await Assert.That(SheetLayoutMath.ShelfFits(tooTall, 100, 50, 10)).IsFalse();
    }

    [Test]
    public async Task PlanPositions_Wraps_To_Next_Shelf()
    {
        var items = new[]
        {
            new SheetLayoutMath.LayoutItem { W = 40, H = 20 },
            new SheetLayoutMath.LayoutItem { W = 40, H = 20 },
            new SheetLayoutMath.LayoutItem { W = 40, H = 20 },
        };
        var usable = new SheetLayoutMath.Rect { X0 = 0, Y0 = 0, X1 = 100, Y1 = 50 };
        var points = SheetLayoutMath.PlanPositions(items, usable, 10);

        await Assert.That(points.Count).IsEqualTo(3);
        await Assert.That(points[0].X).IsEqualTo(0);
        await Assert.That(points[1].X).IsEqualTo(50);
        await Assert.That(points[2].Y).IsEqualTo(20);
    }
}
