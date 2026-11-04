using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Tests;

/// <summary>GUI に渡す表示用の形。合成データだけで試す。</summary>
public sealed class InventoryViewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "em-view-" + Guid.NewGuid().ToString("N"));

    public InventoryViewTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Put(string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void 実際の棚卸しの結果から一覧と分類ごとの件数を作る()
    {
        Put("ExtendedScripts/a.js", "var x = 1;");
        Put("ExtendedStyles/b.css", "a { }");
        Put("ExtendedSqls/q.json", """{"Name":"q","CommandText":"select 1"}""");
        Put("ExtendedSqls/q.json.sql", "select 2");
        Put("ExtendedFields/f.json", "{}");
        Put("ExtendedScripts/readme.txt", "memo");

        var error = LocalInventory.TryRun(root, out var view);

        Assert.Equal(LocalInventoryError.None, error);
        Assert.NotNull(view);
        Assert.Equal(5, view.Units);
        Assert.Equal(6, view.Files);
        Assert.Equal(
            ["ExtendedFields/f.json", "ExtendedScripts/a.js", "ExtendedScripts/readme.txt", "ExtendedSqls/q.json", "ExtendedStyles/b.css"],
            view.Rows.Select(r => r.Path));
        Assert.Equal("Migrate: 3  Replace: 0  Keep: 1  Exclude: 0  NeedsReview: 1", view.SummaryLine());
        Assert.Equal(new InventoryViewCount(Classification.Migrate, 3, 4), view.Counts[0]);
        Assert.Equal(1, view.Errors);
    }

    [Fact]
    public void 表示用の形に本文_ハッシュ_フルパスを含めない()
    {
        const string body = "var token = 'dummy-secret-value';";
        Put("ExtendedScripts/a.js", body);

        LocalInventory.TryRun(root, out var view);

        var text = string.Join("\n", view!.Rows.Select(InventoryView.FormatRow)) + view.SummaryLine();
        Assert.DoesNotContain("dummy-secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain(root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ExtensionConverter.Sha256(body), text, StringComparison.Ordinal);
    }

    [Fact]
    public void 対象のフォルダが無ければ_パスを含まない失敗の種類を返す()
    {
        Assert.Equal(LocalInventoryError.DirectoryNotFound, LocalInventory.TryRun(Path.Combine(root, "missing"), out var view));
        Assert.Null(view);
        Assert.Equal(LocalInventoryError.DirectoryNotFound, LocalInventory.TryRun("  ", out _));
        Assert.Equal(LocalInventoryError.DirectoryNotFound, LocalInventory.TryRun(null, out _));
    }

    [Fact]
    public void Extended_のフォルダが無ければ_空の結果で成功し_分類は_0_件で並ぶ()
    {
        Put("Other/x.txt", "x");

        Assert.Equal(LocalInventoryError.None, LocalInventory.TryRun(root, out var view));

        Assert.Empty(view!.Rows);
        Assert.Equal("Migrate: 0  Replace: 0  Keep: 0  Exclude: 0  NeedsReview: 0", view.SummaryLine());
    }

    [Fact]
    public void 棚卸しは何も書かず_元のファイルを変えない()
    {
        Put("ExtendedScripts/a.js", "x");
        var file = Path.Combine(root, "ExtendedScripts", "a.js");
        var count = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Length;
        var stamp = File.GetLastWriteTimeUtc(file);

        LocalInventory.TryRun(root, out _);

        Assert.Equal(count, Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        Assert.Equal("x", File.ReadAllText(file));
    }
}
