using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Ledger;

/// <summary>操作の記録の種類。意図は操作の前に残し、結果不明は推測せずに照合する。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JournalEventType
{
    IntentRecorded,
    ResponseConfirmed,
    OutcomeUnknown,
    Reconciled,
}

/// <summary>sourceKey と登録した ExtensionId の対応。<c>ExtensionName</c> だけでは識別しない。</summary>
public sealed record LedgerEntry(string SourceKey, int? ExtensionId);

/// <summary>操作の記録。</summary>
public sealed record JournalEvent(int Sequence, DateTimeOffset At, string SourceKey, JournalEventType Type, string? Detail);

/// <summary>台帳が壊れている・読めない。空の台帳として扱ってはいけない（重複作成の原因になる）。</summary>
public sealed class LedgerCorruptException(string message) : Exception(message);

/// <summary>sourceKey→ExtensionId の台帳と操作の記録。</summary>
/// <remarks>
/// 保存は一時ファイルへ書いて置き換える（前の正常な内容を壊さない）。
/// 内容のチェックサムを持ち、途中で切れたファイル・書き換えを読み込み時に検出する。
/// 秘密（API キー・本文）は入れない。
/// </remarks>
public sealed class LedgerFile
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required string SourceId { get; init; }

    public List<LedgerEntry> Entries { get; init; } = [];

    public List<JournalEvent> Events { get; init; } = [];

    /// <summary>内容のチェックサム（<see cref="Entries"/> と <see cref="Events"/> と <see cref="SourceId"/> から計算する）。</summary>
    public string? Checksum { get; init; }

    /// <summary>空の台帳。</summary>
    public static LedgerFile Empty(string sourceId) => new() { SourceId = sourceId };

    /// <summary>ファイルから読む。無ければ空。壊れていれば <see cref="LedgerCorruptException"/>。</summary>
    public static LedgerFile Load(string path, string sourceId)
    {
        if (!File.Exists(path))
        {
            return Empty(sourceId);
        }

        LedgerFile? ledger;
        try
        {
            ledger = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(path, Encoding.UTF8), Options);
        }
        catch (JsonException)
        {
            throw new LedgerCorruptException("The ledger file is not valid JSON.");
        }
        catch (IOException)
        {
            throw new LedgerCorruptException("The ledger file could not be read.");
        }

        if (ledger is null || ledger.FormatVersion != CurrentFormatVersion || ledger.Checksum != ComputeChecksum(ledger))
        {
            throw new LedgerCorruptException("The ledger file is damaged or has an unsupported format (checksum mismatch).");
        }

        if (ledger.SourceId != sourceId)
        {
            throw new LedgerCorruptException("The ledger belongs to a different source id.");
        }

        if (ledger.Entries.Select(e => e.SourceKey).Distinct(StringComparer.Ordinal).Count() != ledger.Entries.Count)
        {
            throw new LedgerCorruptException("The ledger contains the same source key more than once.");
        }

        return ledger;
    }

    /// <summary>保存する。一時ファイルへ書き、フラッシュしてから置き換える。</summary>
    public void Save(string path)
    {
        var content = JsonSerializer.Serialize(
            new LedgerFile
            {
                FormatVersion = FormatVersion,
                SourceId = SourceId,
                Entries = Entries,
                Events = Events,
                Checksum = ComputeChecksum(this),
            },
            Options);
        InventoryJson.WriteAtomic(path, content);
    }

    /// <summary>sourceKey に対応する ID。無ければ <c>null</c>。</summary>
    public int? FindExtensionId(string sourceKey) =>
        Entries.FirstOrDefault(e => string.Equals(e.SourceKey, sourceKey, StringComparison.Ordinal))?.ExtensionId;

    /// <summary>台帳に sourceKey があるか（ID が未確定の場合を含む）。</summary>
    public bool Contains(string sourceKey) =>
        Entries.Any(e => string.Equals(e.SourceKey, sourceKey, StringComparison.Ordinal));

    private static string ComputeChecksum(LedgerFile ledger)
    {
        var canonical = JsonSerializer.Serialize(new { ledger.SourceId, ledger.Entries, ledger.Events }, Options);
        return ExtensionConverter.Sha256(canonical);
    }
}
