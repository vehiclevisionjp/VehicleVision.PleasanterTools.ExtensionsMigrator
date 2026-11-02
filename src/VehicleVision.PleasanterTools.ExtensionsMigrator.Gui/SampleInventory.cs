namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Gui;

/// <summary>画面に出す 1 行。試作では合成データ。</summary>
/// <param name="Path">ルートからの相対パス（合成）。</param>
/// <param name="ExtensionType">種類。</param>
/// <param name="Classification">分類（Migrate・Keep など）。</param>
internal sealed record SampleRow(string Path, string ExtensionType, string Classification);

/// <summary>
/// 試作用の合成データと、画面に出す要約。実在の環境の内容は入れない。
/// <c>inventory</c> と結線するのは、棚卸しが develop に入ってから（#15）。
/// </summary>
internal static class SampleInventory
{
    /// <summary>合成の棚卸し結果。</summary>
    public static IReadOnlyList<SampleRow> Create() =>
    [
        new("ExtendedScripts/example.js", "Script", "Migrate"),
        new("ExtendedStyles/example.css", "Style", "Migrate"),
        new("ExtendedSqls/sample-query.json", "Sql", "Migrate"),
        new("ExtendedSqls/sample-query.json.sql", "Sql", "Migrate"),
        new("ExtendedServerScripts/sample-hook.json", "ServerScript", "Migrate"),
        new("ExtendedColumns/sample-columns.json", "(none)", "Keep"),
        new("ExtendedScripts/readme.txt", "Script", "NeedsReview"),
    ];

    /// <summary>分類ごとの件数を、固定の順で並べた 1 行にする。</summary>
    public static string Summarize(IEnumerable<SampleRow> rows)
    {
        var counts = rows.GroupBy(r => r.Classification).ToDictionary(g => g.Key, g => g.Count());
        string[] order = ["Migrate", "Replace", "Keep", "Exclude", "NeedsReview"];
        return string.Join("  ", order.Select(name => $"{name}: {counts.GetValueOrDefault(name)}"));
    }

    /// <summary>一覧に出す文字列。</summary>
    public static string Format(SampleRow row) => $"{row.Classification,-12} {row.ExtensionType,-14} {row.Path}";
}
