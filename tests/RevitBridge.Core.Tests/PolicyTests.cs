using RevitBridge.Contracts;
using RevitBridge.Core;
using TUnit.Core;

namespace RevitBridge.Core.Tests;

public class PolicyTests
{
    private static CommandDescriptor Descriptor(string name, BridgeTransaction tx)
    {
        return new CommandDescriptor
        {
            Name = name,
            Description = name,
            Provider = "test",
            Transaction = tx.ToString().ToLowerInvariant(),
        };
    }

    [Test]
    public async Task Writes_Are_Disabled_By_Default()
    {
        var policy = new BridgePolicy();
        var error = policy.CheckCall(Descriptor("sheet.create", BridgeTransaction.Write));

        await Assert.That(policy.AllowWrites).IsFalse();
        await Assert.That(error).IsNotNull();
        await Assert.That(error.Code).IsEqualTo(BridgeErrorCodes.Forbidden);
    }

    [Test]
    public async Task Reads_Are_Allowed_By_Default()
    {
        var policy = new BridgePolicy();
        var error = policy.CheckCall(Descriptor("sheet.list", BridgeTransaction.Read));

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Writes_Are_Allowed_When_Explicitly_Enabled()
    {
        var policy = new BridgePolicy { AllowWrites = true };
        var error = policy.CheckCall(Descriptor("sheet.create", BridgeTransaction.Write));

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Denied_Command_Stays_Forbidden_Even_With_Writes()
    {
        var policy = new BridgePolicy { AllowWrites = true };
        var error = policy.CheckCall(Descriptor("bridge.eval", BridgeTransaction.Write));

        await Assert.That(error).IsNotNull();
        await Assert.That(error.Code).IsEqualTo(BridgeErrorCodes.Forbidden);
    }

    [Test]
    public async Task Legacy_Http_Only_Allows_Get()
    {
        await Assert.That(LegacyHttpPolicy.IsReadOnlyRequest("GET")).IsTrue();
        await Assert.That(LegacyHttpPolicy.IsReadOnlyRequest("get")).IsTrue();
        await Assert.That(LegacyHttpPolicy.IsReadOnlyRequest("POST")).IsFalse();
        await Assert.That(LegacyHttpPolicy.IsReadOnlyRequest("PUT")).IsFalse();
        await Assert.That(LegacyHttpPolicy.IsReadOnlyRequest("DELETE")).IsFalse();
    }
}
