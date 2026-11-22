using Newtonsoft.Json;
using RevitBridge.Contracts;
using RevitBridge.Core;
using TUnit.Core;

namespace RevitBridge.Core.Tests;

public class CommandSafetyTests
{
    [Test]
    public async Task Read_Commands_Are_ReadOnly_And_Idempotent()
    {
        var safety = CommandSafetyDefaults.For("sheet.list", "read");
        await Assert.That(safety.ReadOnly).IsTrue();
        await Assert.That(safety.WritesDocument).IsFalse();
        await Assert.That(safety.Idempotent).IsTrue();
    }

    [Test]
    public async Task Create_Command_Writes_But_Is_Not_Destructive()
    {
        var safety = CommandSafetyDefaults.For("sheet.create", "write");
        await Assert.That(safety.ReadOnly).IsFalse();
        await Assert.That(safety.WritesDocument).IsTrue();
        await Assert.That(safety.Destructive).IsFalse();
        await Assert.That(safety.RequiresConfirmation).IsFalse();
    }

    [Test]
    public async Task Clear_Command_Is_Destructive_And_Requires_Backup()
    {
        var safety = CommandSafetyDefaults.For("model.clear", "write");
        await Assert.That(safety.Destructive).IsTrue();
        await Assert.That(safety.RequiresConfirmation).IsTrue();
        await Assert.That(safety.BackupRequired).IsTrue();
    }

    [Test]
    public async Task Save_Requires_Confirmation_But_Not_Backup()
    {
        var safety = CommandSafetyDefaults.For("revit.save_document", "write");
        await Assert.That(safety.Destructive).IsFalse();
        await Assert.That(safety.RequiresConfirmation).IsTrue();
        await Assert.That(safety.BackupRequired).IsFalse();
    }

    [Test]
    public async Task Safety_Is_Serialized_Into_Command_Descriptor()
    {
        var descriptor = new CommandDescriptor
        {
            Name = "model.clear",
            Description = "clear",
            Provider = "test",
            Transaction = "write",
            Safety = CommandSafetyDefaults.For("model.clear", "write"),
        };
        var json = JsonConvert.SerializeObject(descriptor);

        await Assert.That(json).Contains("\"safety\"");
        await Assert.That(json).Contains("\"destructive\":true");
        await Assert.That(json).Contains("\"backupRequired\":true");
    }
}
