using Newtonsoft.Json.Linq;
using RevitBridge.Contracts;
using RevitBridge.Core;
using TUnit.Core;

namespace RevitBridge.Core.Tests;

public class BridgeCoreTests
{
    private static CommandDescriptor Descriptor(string name, BridgeTransaction tx = BridgeTransaction.Read)
    {
        return new CommandDescriptor
        {
            Name = name,
            Description = name,
            Provider = "test",
            Transaction = tx.ToString().ToLowerInvariant(),
        };
    }

    private static BridgeRouter Router(CommandRegistry registry, string token = null, bool allowWrites = true)
    {
        return new BridgeRouter(registry, new BridgePolicy
        {
            Token = token,
            AllowWrites = allowWrites,
        })
        {
            RevitVersion = "2024",
        };
    }

    [Test]
    public async Task Registry_Rejects_Duplicate_Names()
    {
        var registry = new CommandRegistry();
        registry.Register(Descriptor("x"), _ => JValue.CreateNull());

        var threw = false;
        try { registry.Register(Descriptor("x"), _ => JValue.CreateNull()); }
        catch (InvalidOperationException) { threw = true; }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Registry_Lists_Commands_Sorted()
    {
        var registry = new CommandRegistry();
        registry.Register(Descriptor("b"), _ => JValue.CreateNull());
        registry.Register(Descriptor("a"), _ => JValue.CreateNull());

        var names = registry.List().Select(x => x.Name).ToArray();
        await Assert.That(string.Join(",", names)).IsEqualTo("a,b");
    }

    [Test]
    public async Task Router_Ping_Returns_Pong()
    {
        var router = Router(new CommandRegistry());
        var response = JObject.Parse(router.Handle("{\"v\":1,\"id\":\"1\",\"type\":\"ping\"}"));

        await Assert.That(response.Value<string>("type")).IsEqualTo("pong");
        await Assert.That(response.Value<string>("id")).IsEqualTo("1");
    }

    [Test]
    public async Task Router_Hello_Requires_Token_When_Configured()
    {
        var router = Router(new CommandRegistry(), token: "secret");

        var denied = JObject.Parse(router.Handle("{\"v\":1,\"id\":\"1\",\"type\":\"hello\",\"payload\":{}}"));
        await Assert.That(denied.Value<string>("type")).IsEqualTo("call.error");
        await Assert.That(denied["payload"]?.Value<string>("code")).IsEqualTo("unauthorized");

        var allowed = JObject.Parse(router.Handle("{\"v\":1,\"id\":\"2\",\"type\":\"hello\",\"payload\":{\"token\":\"secret\"}}"));
        await Assert.That(allowed.Value<string>("type")).IsEqualTo("ready");
    }

    [Test]
    public async Task Router_Call_Dispatches_Registered_Command()
    {
        var registry = new CommandRegistry();
        registry.Register(Descriptor("echo"), args => JToken.FromObject(new { value = args?.Value<string>("value") }));
        var router = Router(registry);

        var response = JObject.Parse(router.Handle(
            "{\"v\":1,\"id\":\"1\",\"type\":\"call\",\"payload\":{\"command\":\"echo\",\"args\":{\"value\":\"hi\"}}}"));

        await Assert.That(response.Value<string>("type")).IsEqualTo("call.result");
        await Assert.That(response["payload"]?["result"]?.Value<string>("value")).IsEqualTo("hi");
    }

    [Test]
    public async Task Router_Denies_Arbitrary_Code_Execution()
    {
        var registry = new CommandRegistry();
        registry.Register(Descriptor("send_code_to_revit", BridgeTransaction.Write), _ => JValue.CreateNull());
        var router = Router(registry);

        var response = JObject.Parse(router.Handle(
            "{\"v\":1,\"id\":\"1\",\"type\":\"call\",\"payload\":{\"command\":\"send_code_to_revit\"}}"));

        await Assert.That(response.Value<string>("type")).IsEqualTo("call.error");
        await Assert.That(response["payload"]?.Value<string>("code")).IsEqualTo("forbidden");
    }

    [Test]
    public async Task Router_Denies_Writes_When_Disabled()
    {
        var registry = new CommandRegistry();
        registry.Register(Descriptor("x", BridgeTransaction.Write), _ => JValue.CreateNull());
        var router = Router(registry, allowWrites: false);

        var response = JObject.Parse(router.Handle(
            "{\"v\":1,\"id\":\"1\",\"type\":\"call\",\"payload\":{\"command\":\"x\"}}"));

        await Assert.That(response.Value<string>("type")).IsEqualTo("call.error");
        await Assert.That(response["payload"]?.Value<string>("code")).IsEqualTo("forbidden");
    }

    [Test]
    public async Task Router_Unknown_Command_Returns_Error()
    {
        var router = Router(new CommandRegistry());
        var response = JObject.Parse(router.Handle(
            "{\"v\":1,\"id\":\"1\",\"type\":\"call\",\"payload\":{\"command\":\"nope\"}}"));

        await Assert.That(response.Value<string>("type")).IsEqualTo("call.error");
        await Assert.That(response["payload"]?.Value<string>("code")).IsEqualTo("unknown_command");
    }
}
