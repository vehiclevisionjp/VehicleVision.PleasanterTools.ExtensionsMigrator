namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

/// <summary>保存した棚卸しと現在の棚卸しを比べる。</summary>
public static class InventoryComparer
{
    /// <summary>ずれを、固定の形の英語の文で返す。ソースが変わったか、規則・ツールの版が違う。</summary>
    public static IReadOnlyList<string> Compare(InventoryReport saved, InventoryReport current)
    {
        var drifts = new List<string>();
        if (saved.RuleSet != current.RuleSet)
        {
            drifts.Add($"rule-set-changed: {saved.RuleSet} -> {current.RuleSet}");
        }

        if (saved.SourceId != current.SourceId)
        {
            drifts.Add("source-id-changed");
        }

        var before = saved.Entries.ToDictionary(e => e.SourceKey, StringComparer.Ordinal);
        var after = current.Entries.ToDictionary(e => e.SourceKey, StringComparer.Ordinal);
        foreach (var key in before.Keys.Except(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            drifts.Add($"removed: {key}");
        }

        foreach (var key in after.Keys.Except(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            drifts.Add($"added: {key}");
        }

        foreach (var key in before.Keys.Intersect(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var a = before[key];
            var b = after[key];
            var sameFiles = a.Files.Count == b.Files.Count
                && a.Files.Zip(b.Files).All(p => p.First.Path == p.Second.Path && p.First.Sha256 == p.Second.Sha256);
            if (!sameFiles)
            {
                drifts.Add($"changed: {key}");
            }
            else if (a.Classification != b.Classification)
            {
                drifts.Add($"classification-changed: {key}");
            }
        }

        return drifts;
    }
}

