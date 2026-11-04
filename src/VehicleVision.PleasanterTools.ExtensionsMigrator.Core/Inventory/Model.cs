using System.Text.Json.Serialization;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

/// <summary>棚卸しでの扱い。「対象外」「保持」「置換」「要確認」は別の状態として出す。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Classification
{
    /// <summary>Extensions テーブルへ移す。</summary>
    Migrate,

    /// <summary>利用者の指定で、別の生成物に置き換える（そのまま移す対象からは外す）。削除ではない。</summary>
    Replace,

    /// <summary>変更せずファイルのまま保持する。</summary>
    Keep,

    /// <summary>利用者の指定で、移行の対象外にする。</summary>
    Exclude,

    /// <summary>自動では決められない。人が確認し、規則で扱いを指定する。</summary>
    NeedsReview,
}

/// <summary>1 つのファイルの記録。本文は持たない（ハッシュだけ）。</summary>
/// <param name="Path">ルートからの相対パス（区切りは <c>/</c>）。</param>
/// <param name="Role">定義・併設本文・本文などの役割。</param>
/// <param name="Length">バイト数。</param>
/// <param name="Sha256">元のバイト列の SHA-256（小文字の 16 進）。読めなければ <c>null</c>。</param>
public sealed record InventoryFile(string Path, string Role, long Length, string? Sha256);

/// <summary>設定 1 件（1 つの定義と、それに併設するファイル）の記録。</summary>
public sealed record InventoryEntry
{
    /// <summary>ソースの識別子とルートからの相対パスの組。移行の識別に使う。</summary>
    public required string SourceKey { get; init; }

    /// <summary>種類（<c>Script</c> など）。分からなければ <c>null</c>。</summary>
    public string? ExtensionType { get; init; }

    public required Classification Classification { get; init; }

    /// <summary>扱いの理由を表す固定のコード。</summary>
    public required string Reason { get; init; }

    /// <summary>利用者の規則で決まった場合の、利用者が書いた理由。</summary>
    public string? RuleNote { get; init; }

    /// <summary>登録する名前。空のこともある。変換できない場合は <c>null</c>。</summary>
    public string? Name { get; init; }

    public string? Language { get; init; }

    /// <summary>元の設定が無効だったか。</summary>
    public bool OriginalDisabled { get; init; }

    /// <summary>本文の出どころ（<c>file</c>・<c>sidecar</c>・<c>inline</c>・<c>none</c>）。</summary>
    public string? BodySource { get; init; }

    public required IReadOnlyList<InventoryFile> Files { get; init; }

    /// <summary>デコード後の本文（BOM を除く）の SHA-256。</summary>
    public string? BodySha256 { get; init; }

    /// <summary>本文を分けた設定 JSON の意味上のハッシュ（キー順を整えたもの）。</summary>
    public string? SettingsSha256 { get; init; }

    public bool? Bom { get; init; }

    /// <summary>本文の改行（<c>crlf</c>・<c>lf</c>・<c>mixed</c>・<c>none</c>）。</summary>
    public string? LineEnding { get; init; }
}

/// <summary>集計。</summary>
public sealed record InventorySummary(
    IReadOnlyDictionary<string, int> UnitsByClassification,
    IReadOnlyDictionary<string, int> FilesByClassification,
    int Units,
    int Files);

/// <summary>指摘。</summary>
/// <param name="Severity"><c>error</c> または <c>warning</c>。</param>
/// <param name="Code">固定のコード。</param>
/// <param name="Message">英語の説明。</param>
/// <param name="SourceKey">関係する設定。</param>
public sealed record InventoryDiagnostic(string Severity, string Code, string Message, string? SourceKey);

/// <summary>棚卸しの結果。plan の元になる。秘密を含みうる本文は持たない。</summary>
public sealed record InventoryReport
{
    public const int CurrentFormatVersion = 1;

    /// <summary>読み込み規則の版。確認した Pleasanter の版と、このツールでの規則の版。</summary>
    public const string RuleSetVersion = "pleasanter-1.5.9.1/rules-1";

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required string ToolVersion { get; init; }

    public string RuleSet { get; init; } = RuleSetVersion;

    public required string SourceId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required InventorySummary Summary { get; init; }

    public required IReadOnlyList<InventoryEntry> Entries { get; init; }

    public required IReadOnlyList<InventoryDiagnostic> Diagnostics { get; init; }
}
