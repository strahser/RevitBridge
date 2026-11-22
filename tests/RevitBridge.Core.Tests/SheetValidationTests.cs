using RevitBridge.Core;
using TUnit.Core;

namespace RevitBridge.Core.Tests;

public class SheetValidationTests
{
    private const double SheetW = 420;
    private const double SheetH = 297;

    private static SheetValidation.Box Usable =>
        new SheetValidation.Box("usable", 30, 70, 400, 277);

    private static SheetValidation.Box Safe =>
        new SheetValidation.Box("safe", 20, 60, 410, 287);

    private static SheetValidation.Box Item(string name, double x0, double y0,
        double x1, double y1) => new SheetValidation.Box(name, x0, y0, x1, y1);

    private static SheetValidation.Result Validate(params SheetValidation.Box[] items) =>
        SheetValidation.Validate(items, Usable, Safe, SheetW, SheetH);

    [Test]
    public async Task Box_Inside_Usable_Has_No_Violations()
    {
        var result = Validate(Item("a", 100, 100, 200, 200));

        await Assert.That(result.Outside.Count).IsEqualTo(0);
        await Assert.That(result.Forbidden.Count).IsEqualTo(0);
        await Assert.That(result.Overlaps.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Box_Outside_Usable_Is_Reported_With_Rect_And_Usable()
    {
        var result = Validate(Item("a", 100, 100, 405, 200));

        await Assert.That(result.Outside.Count).IsEqualTo(1);
        await Assert.That(result.Outside[0].Name).IsEqualTo("a");
        await Assert.That(result.Outside[0].Rect[0]).IsEqualTo(100);
        await Assert.That(result.Outside[0].Rect[1]).IsEqualTo(100);
        await Assert.That(result.Outside[0].Rect[2]).IsEqualTo(405);
        await Assert.That(result.Outside[0].Rect[3]).IsEqualTo(200);
        await Assert.That(result.Outside[0].Usable[0]).IsEqualTo(30);
        await Assert.That(result.Outside[0].Usable[1]).IsEqualTo(70);
        await Assert.That(result.Outside[0].Usable[2]).IsEqualTo(400);
        await Assert.That(result.Outside[0].Usable[3]).IsEqualTo(277);
    }

    [Test]
    public async Task Box_Intersecting_Stamp_Is_Forbidden()
    {
        var result = Validate(Item("a", 100, 20, 200, 80));

        await Assert.That(result.Forbidden.Count).IsEqualTo(1);
        await Assert.That(result.Forbidden[0].Name).IsEqualTo("a");
        await Assert.That(result.Forbidden[0].Zone).IsEqualTo("stamp");
        await Assert.That(result.Forbidden[0].OverlapMm2).IsEqualTo(4000);
    }

    [Test]
    public async Task Two_Overlapping_Boxes_Are_Reported()
    {
        var result = Validate(
            Item("a", 100, 100, 200, 200),
            Item("b", 150, 150, 250, 250));

        await Assert.That(result.Overlaps.Count).IsEqualTo(1);
        await Assert.That(result.Overlaps[0].A).IsEqualTo("a");
        await Assert.That(result.Overlaps[0].B).IsEqualTo("b");
        await Assert.That(result.Overlaps[0].OverlapMm2).IsEqualTo(2500);
    }

    [Test]
    public async Task Valid_Reflects_Violations()
    {
        await Assert.That(Validate(Item("a", 100, 100, 200, 200)).Valid).IsTrue();
        await Assert.That(Validate(Item("a", 100, 100, 405, 200)).Valid).IsFalse();
        await Assert.That(Validate(
            Item("a", 100, 100, 200, 200),
            Item("b", 150, 150, 250, 250)).Valid).IsFalse();
    }

    [Test]
    public async Task Tolerance_Ignores_Exactly_One_Millimetre_Overlap()
    {
        var touching = Validate(
            Item("a", 100, 100, 200, 200),
            Item("b", 200, 100, 300, 200));
        var oneMm = Validate(
            Item("a", 100, 100, 200, 200),
            Item("b", 199, 100, 299, 200));
        var twoMm = Validate(
            Item("a", 100, 100, 200, 200),
            Item("b", 198, 100, 298, 200));

        await Assert.That(touching.Overlaps.Count).IsEqualTo(0);
        await Assert.That(oneMm.Overlaps.Count).IsEqualTo(0);
        await Assert.That(twoMm.Overlaps.Count).IsEqualTo(1);
    }
}
