using System.Text;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Gui;

/// <summary>
/// デモ用（<c>--smoke</c>・「デモを表示」ボタン）。合成のファイルをメモリ上に置き、実際の棚卸しエンジン（Engine）に通す。
/// ディスクには何も書かない。実在の環境の内容は入れない。
/// </summary>
internal static class DemoInventory
{
    private static readonly (string Path, string Text)[] Files =
    [
        ("ExtendedScripts/example.js", "var demo = 1;"),
        ("ExtendedStyles/example.css", "a { color: black; }"),
        ("ExtendedSqls/sample-query.json", """{"Name":"sample-query","CommandText":"select 1"}"""),
        ("ExtendedSqls/sample-query.json.sql", "select 2"),
        ("ExtendedServerScripts/sample-hook.json", """{"Name":"sample-hook","Body":"// demo"}"""),
        ("ExtendedFields/sample-field.json", "{}"),
        ("ExtendedScripts/readme.txt", "memo"),
    ];

    /// <summary>合成データを実際のエンジンで棚卸しした表示用の結果。</summary>
    public static InventoryView Create()
    {
        var source = new MemorySource(Files.ToDictionary(f => f.Path, f => Encoding.UTF8.GetBytes(f.Text), StringComparer.Ordinal));
        return InventoryView.From(new InventoryEngine(source, "demo", InventoryRules.Empty).Run());
    }

    private sealed class MemorySource(Dictionary<string, byte[]> files) : ISourceProvider
    {
        public IReadOnlyList<SourceItem> List() =>
            files.Select(f => new SourceItem(f.Key, f.Value.Length, false)).OrderBy(i => i.RelativePath, StringComparer.Ordinal).ToList();

        public byte[] Read(string relativePath) => files[relativePath];
    }
}
