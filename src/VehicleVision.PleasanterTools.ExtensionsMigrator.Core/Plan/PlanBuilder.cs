using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Ledger;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Target;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Plan;

/// <summary>plan の 1 件ごとの予定。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanAction
{
    /// <summary>対象に無いので、無効状態で作成する予定。</summary>
    CreateDisabled,

    /// <summary>台帳の対応先が、ソースの内容と一致している（何もしない）。</summary>
    InSync,

    /// <summary>ファイルのまま保持する（保持・対象外・置換）。対象 API には何もしない。</summary>
    Retain,

    /// <summary>所有関係・対応が不明、または内容が食い違う。自動では上書きせず、人が決める。</summary>
    Conflict,

    /// <summary>要確認のため、このままでは実行できない。</summary>
    Blocked,
}

/// <summary>plan の 1 件。</summary>
public sealed record PlanEntry
{
    public required string SourceKey { get; init; }

    public string? ExtensionType { get; init; }

    public string? Name { get; init; }

    public required Classification Classification { get; init; }

    public required PlanAction Action { get; init; }

    /// <summary>理由を表す固定のコード。</summary>
    public required string Reason { get; init; }

    /// <summary>対象の既存レコード（台帳の対応先、または競合の相手）。</summary>
    public int? ExtensionId { get; init; }

    public bool OriginalDisabled { get; init; }

    public string? BodySha256 { get; init; }

    public string? SettingsSha256 { get; init; }
}

/// <summary>対象の識別情報。API キーなどの秘密は含まない。</summary>
public sealed record PlanTarget(string BaseUrl, int TenantId, int RecordsRead, int RecordsInTenant, int RecordsInOtherTenants);

/// <summary>読み取り専用の plan。本文は埋め込まない（ハッシュだけ）。</summary>
public sealed record PlanReport
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required string ToolVersion { get; init; }

    public required string RuleSet { get; init; }

    public required string SourceId { get; init; }

    public required PlanTarget Target { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>内容から決まる ID（作成日時を除く）。以後のコマンドは、確認した plan の ID を指定する。</summary>
    public required string PlanId { get; init; }

    public required IReadOnlyDictionary<string, int> ActionCounts { get; init; }

    /// <summary>実行単位（種類ごと）。同じ種類の設定は、まとめて検証・復旧する。</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> ExecutionUnits { get; init; }

    /// <summary>台帳にも棚卸しにも対応しない、対象のレコードの数。削除の対象にはしない。</summary>
    public required int UnmanagedTargetRecords { get; init; }

    public required IReadOnlyList<PlanEntry> Entries { get; init; }
}

/// <summary>棚卸しと、対象の既存レコード・台帳から plan を作る。何も書かない。</summary>
public static class PlanBuilder
{
    private static readonly string EmptyBodyHash = ExtensionConverter.Sha256(string.Empty);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PlanReport Build(
        InventoryReport inventory,
        IReadOnlyList<ExtensionRecord> records,
        LedgerFile ledger,
        string baseUrl,
        int tenantId,
        DateTimeOffset now)
    {
        var inTenant = records.Where(r => r.TenantId is null || r.TenantId == tenantId).ToList();
        var byId = inTenant.ToDictionary(r => r.ExtensionId);
        var entries = new List<PlanEntry>();
        var claimed = new HashSet<int>();

        foreach (var item in inventory.Entries)
        {
            var entry = Decide(item, inTenant, byId, ledger, claimed);
            entries.Add(entry);
        }

        var ordered = entries.OrderBy(e => e.SourceKey, StringComparer.Ordinal).ToList();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in Enum.GetNames<PlanAction>())
        {
            counts[name] = 0;
        }

        foreach (var entry in ordered)
        {
            counts[entry.Action.ToString()]++;
        }

        var units = ordered
            .Where(e => e.Action is PlanAction.CreateDisabled or PlanAction.InSync && e.ExtensionType is not null)
            .GroupBy(e => e.ExtensionType!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(e => e.SourceKey).ToList());

        var unmanaged = inTenant.Count(r => !claimed.Contains(r.ExtensionId));
        var target = new PlanTarget(
            TargetUrl.Describe(baseUrl),
            tenantId,
            records.Count,
            inTenant.Count,
            records.Count - inTenant.Count);
        var withoutId = new PlanReport
        {
            ToolVersion = typeof(PlanBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown",
            RuleSet = inventory.RuleSet,
            SourceId = inventory.SourceId,
            Target = target,
            CreatedAt = now,
            PlanId = string.Empty,
            ActionCounts = counts,
            ExecutionUnits = units,
            UnmanagedTargetRecords = unmanaged,
            Entries = ordered,
        };
        return withoutId with { PlanId = ComputeId(withoutId) };
    }

    public static string Serialize(PlanReport report) => JsonSerializer.Serialize(report, JsonOptions);

    private static string ComputeId(PlanReport report)
    {
        var stable = report with { CreatedAt = default, PlanId = string.Empty, ToolVersion = string.Empty };
        return ExtensionConverter.Sha256(JsonSerializer.Serialize(stable, JsonOptions));
    }

    private static PlanEntry Decide(
        InventoryEntry item,
        List<ExtensionRecord> inTenant,
        Dictionary<int, ExtensionRecord> byId,
        LedgerFile ledger,
        HashSet<int> claimed)
    {
        PlanEntry Make(PlanAction action, string reason, int? id = null) => new()
        {
            SourceKey = item.SourceKey,
            ExtensionType = item.ExtensionType,
            Name = item.Name,
            Classification = item.Classification,
            Action = action,
            Reason = reason,
            ExtensionId = id,
            OriginalDisabled = item.OriginalDisabled,
            BodySha256 = item.BodySha256,
            SettingsSha256 = item.SettingsSha256,
        };

        switch (item.Classification)
        {
            case Classification.NeedsReview:
                return Make(PlanAction.Blocked, item.Reason);
            case Classification.Keep or Classification.Exclude or Classification.Replace:
                return Make(PlanAction.Retain, item.Classification.ToString().ToLowerInvariant());
        }

        var bodyHash = item.BodySha256 ?? EmptyBodyHash;
        var mappedId = ledger.FindExtensionId(item.SourceKey);
        if (mappedId is not null)
        {
            claimed.Add(mappedId.Value);
            if (!byId.TryGetValue(mappedId.Value, out var mapped))
            {
                return Make(PlanAction.Conflict, "ledger-id-missing-in-target", mappedId);
            }

            return SameContent(item, bodyHash, mapped)
                ? Make(PlanAction.InSync, "ledger-match", mapped.ExtensionId)
                : Make(PlanAction.Conflict, "registered-record-differs", mapped.ExtensionId);
        }

        if (ledger.Contains(item.SourceKey))
        {
            // 意図だけ記録され、ID が確定していない。結果不明の可能性があるので、勝手に作成しない
            return Make(PlanAction.Conflict, "ledger-id-unknown");
        }

        var sameType = inTenant.Where(r => r.ExtensionType == item.ExtensionType && !claimed.Contains(r.ExtensionId)).ToList();
        var identical = sameType.Where(r => SameContent(item, bodyHash, r)).ToList();
        if (identical.Count > 0)
        {
            return Make(PlanAction.Conflict, identical.Count == 1 ? "unmanaged-record-same-content" : "unmanaged-records-same-content", identical[0].ExtensionId);
        }

        var sameName = sameType.Where(r => string.Equals(r.ExtensionName, item.Name, StringComparison.Ordinal)).ToList();
        if (sameName.Count > 0)
        {
            var named = item.Name is { Length: > 0 };
            return Make(PlanAction.Conflict, named ? "unmanaged-record-same-name" : "unmanaged-record-empty-name", sameName[0].ExtensionId);
        }

        return Make(PlanAction.CreateDisabled, item.Name is { Length: 0 } ? "new-empty-name" : "new");
    }

    private static bool SameContent(InventoryEntry item, string bodyHash, ExtensionRecord record) =>
        string.Equals(record.ExtensionType, item.ExtensionType, StringComparison.Ordinal)
        && string.Equals(record.ExtensionName, item.Name, StringComparison.Ordinal)
        && string.Equals(record.BodySha256, bodyHash, StringComparison.Ordinal)
        && string.Equals(record.SettingsSha256, item.SettingsSha256, StringComparison.Ordinal);
}
