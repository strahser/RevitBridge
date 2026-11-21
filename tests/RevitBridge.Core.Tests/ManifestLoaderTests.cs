using System;
using System.IO;
using RevitBridge.Core;
using TUnit.Core;

namespace RevitBridge.Core.Tests;

public class ManifestLoaderTests
{
    private static string Unique(string prefix, string ext)
    {
        // Test output folder is writable; LoadFile does not skip bin/ (only root discovery does).
        return Path.Combine(AppContext.BaseDirectory, prefix + "-" + Guid.NewGuid().ToString("N") + ext);
    }

    [Test]
    public async Task Loads_Manifest_And_Resolves_Relative_Assembly()
    {
        var dll = Unique("plugin", ".dll");
        var manifestPath = Unique("bridge", ".manifest.json");
        File.WriteAllText(dll, "");
        File.WriteAllText(manifestPath, """
        {
          "provider": "TestPlugin",
          "assembly": "PLUGIN_FILE",
          "commands": [
            { "name": "test.read", "type": "Test.Command", "kind": "externalCommand", "transaction": "read" }
          ]
        }
        """.Replace("PLUGIN_FILE", Path.GetFileName(dll)));

        try
        {
            var manifest = BridgeManifestLoader.LoadFile(manifestPath);
            await Assert.That(manifest).IsNotNull();
            await Assert.That(manifest.Manifest.Provider).IsEqualTo("TestPlugin");
            await Assert.That(manifest.Manifest.Commands.Count).IsEqualTo(1);

            var assembly = BridgeManifestLoader.ResolveAssemblyPath(manifest);
            await Assert.That(assembly).IsNotNull();
            await Assert.That(File.Exists(assembly)).IsTrue();
        }
        finally
        {
            try { File.Delete(manifestPath); } catch { }
            try { File.Delete(dll); } catch { }
        }
    }

    [Test]
    public async Task Skips_Invalid_Manifest()
    {
        var manifestPath = Unique("bad", ".manifest.json");
        File.WriteAllText(manifestPath, "{ not json");
        try
        {
            var manifest = BridgeManifestLoader.LoadFile(manifestPath);
            await Assert.That(manifest).IsNull();
        }
        finally
        {
            try { File.Delete(manifestPath); } catch { }
        }
    }
}
