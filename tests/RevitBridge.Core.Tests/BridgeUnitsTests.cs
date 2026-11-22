using RevitBridge.Core;
using TUnit.Core;

namespace RevitBridge.Core.Tests;

public class BridgeUnitsTests
{
    [Test]
    public async Task Ft_Converts_Mm_To_Feet()
    {
        await Assert.That(BridgeUnits.Ft(304.8)).IsEqualTo(1.0);
    }

    [Test]
    public async Task Mm_Converts_Feet_To_Mm()
    {
        await Assert.That(BridgeUnits.Mm(1)).IsEqualTo(304.8);
    }

    [Test]
    public async Task RoundTrip_Preserves_Value()
    {
        var roundTrip = BridgeUnits.Mm(BridgeUnits.Ft(1234.5));
        await Assert.That(Math.Abs(roundTrip - 1234.5) < 1e-9).IsTrue();
    }
}
