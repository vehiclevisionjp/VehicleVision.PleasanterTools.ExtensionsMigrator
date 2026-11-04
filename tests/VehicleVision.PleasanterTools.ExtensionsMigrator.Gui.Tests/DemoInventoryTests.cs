using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Gui.Tests;

public sealed class DemoInventoryTests
{
    [Fact]
    public void デモは実際のエンジンを通した結果で_実在の環境を想定させる名前を含まない()
    {
        var view = DemoInventory.Create();

        Assert.NotEmpty(view.Rows);
        Assert.All(view.Rows, r => Assert.StartsWith("Extended", r.Path, StringComparison.Ordinal));
        Assert.DoesNotContain(view.Rows, r => r.Path.Contains("vhub", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Migrate: 4  Replace: 0  Keep: 1  Exclude: 0  NeedsReview: 1", view.SummaryLine());
        Assert.Equal(new InventoryViewCount(Classification.Migrate, 4, 5), view.Counts[0]);
    }
}
