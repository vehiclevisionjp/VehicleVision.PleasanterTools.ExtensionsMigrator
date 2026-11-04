namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

/// <summary>画面に出す 1 行。本文・名称・ハッシュ・ソースのフルパスは持たない。</summary>
/// <param name="Path">ソースのルートからの相対パス（区切りは <c>/</c>）。</param>
/// <param name="ExtensionType">種類。分からなければ <c>null</c>。</param>
/// <param name="Classification">分類。</param>
/// <param name="Reason">扱いの理由を表す固定のコード。</param>
public sealed record InventoryViewRow(string Path, string? ExtensionType, Classification Classification, string Reason);

/// <summary>分類 1 つぶんの件数。</summary>
/// <param name="Classification">分類。</param>
/// <param name="Units">設定の数。</param>
/// <param name="Files">ファイルの数。</param>
public sealed record InventoryViewCount(Classification Classification, int Units, int Files);

/// <summary>
/// 棚卸しの結果を、GUI などが表示するだけでよい形にしたもの。表示用の整形はここに置き、画面は並べるだけにする。
/// 本文・名称・ハッシュ・ソースのフルパスは入れない（CLI の出力と同じ方針）。
/// </summary>
public sealed record InventoryView(
    IReadOnlyList<InventoryViewRow> Rows,
    IReadOnlyList<InventoryViewCount> Counts,
    int Units,
    int Files,
    int Errors,
    int Warnings)
{
    /// <summary>棚卸しの結果から作る。分類は <see cref="Classification"/> の定義順で、無い分類も 0 件で並べる。</summary>
    public static InventoryView From(InventoryReport report)
    {
        var rows = report.Entries
            .Select(e => new InventoryViewRow(
                e.Files.Count > 0 ? e.Files[0].Path : RelativeOf(e.SourceKey),
                e.ExtensionType,
                e.Classification,
                e.Reason))
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ToList();
        var counts = Enum.GetValues<Classification>()
            .Select(c => new InventoryViewCount(
                c,
                report.Entries.Count(e => e.Classification == c),
                report.Entries.Where(e => e.Classification == c).Sum(e => e.Files.Count)))
            .ToList();
        return new InventoryView(
            rows,
            counts,
            report.Entries.Count,
            report.Entries.Sum(e => e.Files.Count),
            report.Diagnostics.Count(d => d.Severity == "error"),
            report.Diagnostics.Count(d => d.Severity == "warning"));
    }

    /// <summary>一覧に出す 1 行。</summary>
    public static string FormatRow(InventoryViewRow row) =>
        $"{row.Classification,-12} {row.ExtensionType ?? "(none)",-14} {row.Path}  [{row.Reason}]";

    /// <summary>分類ごとの件数（設定の数）を、固定の順で並べた 1 行にする。</summary>
    public string SummaryLine() => string.Join("  ", Counts.Select(c => $"{c.Classification}: {c.Units}"));

    private static string RelativeOf(string sourceKey)
    {
        var index = sourceKey.IndexOf(':', StringComparison.Ordinal);
        return index < 0 ? sourceKey : sourceKey[(index + 1)..];
    }
}

/// <summary><see cref="LocalInventory.TryRun"/> の結果の種類。パスや例外のメッセージは持たない。</summary>
public enum LocalInventoryError
{
    /// <summary>成功。</summary>
    None,

    /// <summary>フォルダが無い・指定が空。</summary>
    DirectoryNotFound,

    /// <summary>読めない（権限・入出力のエラー）。</summary>
    Unreadable,
}

/// <summary>ローカルのフォルダを棚卸しして <see cref="InventoryView"/> を返す。読むだけで、何も書かない。例外のメッセージ（パスを含みうる）は外へ出さない。</summary>
public static class LocalInventory
{
    /// <summary>棚卸しする。成功なら <see cref="LocalInventoryError.None"/>。</summary>
    public static LocalInventoryError TryRun(string? folder, out InventoryView? view)
    {
        view = null;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return LocalInventoryError.DirectoryNotFound;
        }

        try
        {
            var provider = new LocalSourceProvider(folder);
            view = InventoryView.From(new InventoryEngine(provider, "local", InventoryRules.Empty).Run());
            return LocalInventoryError.None;
        }
        catch (DirectoryNotFoundException)
        {
            return LocalInventoryError.DirectoryNotFound;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return LocalInventoryError.Unreadable;
        }
    }
}
