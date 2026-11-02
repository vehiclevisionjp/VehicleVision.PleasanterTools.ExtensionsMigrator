namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Gui.Tests;

public sealed class SampleInventoryTests
{
    [Fact]
    public void 合成データは実在の環境を想定させる名前を含まない()
    {
        var rows = SampleInventory.Create();

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.StartsWith("Extended", r.Path, StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Path.Contains("vhub", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 要約は分類を固定の順で並べ_無い分類は_0_件にする()
    {
        var summary = SampleInventory.Summarize(SampleInventory.Create());

        Assert.Equal("Migrate: 5  Replace: 0  Keep: 1  Exclude: 0  NeedsReview: 1", summary);
    }

    [Fact]
    public void 一覧の文字列は分類と種類とパスを含む()
    {
        var text = SampleInventory.Format(new SampleRow("ExtendedScripts/a.js", "Script", "Migrate"));

        Assert.Contains("Migrate", text, StringComparison.Ordinal);
        Assert.Contains("Script", text, StringComparison.Ordinal);
        Assert.EndsWith("ExtendedScripts/a.js", text, StringComparison.Ordinal);
    }
}
