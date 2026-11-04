using System.Text;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Cli.Tests;

/// <summary>inventory・validate コマンド。合成データだけを使う。</summary>
public sealed class InventoryCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "em-test-" + Guid.NewGuid().ToString("N"));

    public InventoryCommandTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Put(string relative, string text, bool bom = false)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        File.WriteAllBytes(path, bom ? [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. bytes] : bytes);
    }

    private InventoryReport Run(InventoryRules? rules = null) =>
        new InventoryEngine(new LocalSourceProvider(root), "src1", rules ?? InventoryRules.Empty).Run();

    [Fact]
    public async Task inventory_は出力先がソースの中なら拒否し_外なら原子的に書く()
    {
        Put("ExtendedScripts/s.js", "1");
        var app = new CommandLineApp([new InventoryCommand()]);
        var output = new StringWriter();
        var error = new StringWriter();

        var inside = await app.RunAsync(["inventory", "--source", root, "--out", Path.Combine(root, "inv.json")], output, error, _ => null);
        Assert.Equal(ExitCodes.UsageError, inside);
        Assert.False(File.Exists(Path.Combine(root, "inv.json")));

        var outside = Path.Combine(Path.GetTempPath(), "em-out-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var ok = await app.RunAsync(["inventory", "--source", root, "--out", outside], output, error, _ => null);
            Assert.Equal(ExitCodes.Success, ok);
            Assert.Equal(1, InventoryJson.Deserialize(File.ReadAllText(outside)).Summary.Units);
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), ".em-out-*.tmp"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task validate_は要確認とずれを_1_で報告する()
    {
        Put("ExtendedScripts/s.js", "1");
        var saved = Path.Combine(Path.GetTempPath(), "em-saved-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            InventoryJson.WriteAtomic(saved, InventoryJson.Serialize(Run()));
            var app = new CommandLineApp([new ValidateCommand()]);

            var clean = new StringWriter();
            Assert.Equal(ExitCodes.Success, await app.RunAsync(["validate", "--source", root, "--source-id", "src1", "--inventory", saved], clean, new StringWriter(), _ => null));

            Put("ExtendedScripts/s.js", "changed");
            Put("ExtendedScripts/new.js", "2");
            var drift = new StringWriter();
            Assert.Equal(ExitCodes.Failure, await app.RunAsync(["validate", "--source", root, "--source-id", "src1", "--inventory", saved], drift, new StringWriter(), _ => null));
            Assert.Contains("changed: src1:ExtendedScripts/s.js", drift.ToString(), StringComparison.Ordinal);
            Assert.Contains("added: src1:ExtendedScripts/new.js", drift.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public async Task source_が無い_ソースが存在しない_source_id_が不正なら_2_で終わる()
    {
        var app = new CommandLineApp([new InventoryCommand()]);
        foreach (var args in new[]
                 {
                     new[] { "inventory" },
                     ["inventory", "--source", Path.Combine(root, "missing")],
                     ["inventory", "--source", root, "--source-id", "a b"],
                 })
        {
            Assert.Equal(ExitCodes.UsageError, await app.RunAsync(args, new StringWriter(), new StringWriter(), _ => null));
        }
    }
}
