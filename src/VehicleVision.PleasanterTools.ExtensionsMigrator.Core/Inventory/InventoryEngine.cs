using System.Reflection;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

/// <summary>ソースを読み、設定ごとに分類して <see cref="InventoryReport"/> を作る。何も書かない。</summary>
public sealed class InventoryEngine(ISourceProvider source, string sourceId, InventoryRules rules, TimeProvider? clock = null)
{
    /// <summary>種類ごとの読み込み場所と、変換の方式。</summary>
    private static readonly Dictionary<string, (string Type, bool Json, string? Extension, string? SidecarSuffix)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ExtendedScripts"] = ("Script", false, ".js", null),
            ["ExtendedStyles"] = ("Style", false, ".css", null),
            ["ExtendedHtmls"] = ("Html", false, ".html", null),
            ["ExtendedSqls"] = ("Sql", true, ".json", ".sql"),
            ["ExtendedServerScripts"] = ("ServerScript", true, ".json", ".js"),
            ["ExtendedNavigationMenus"] = ("NavigationMenu", true, ".json", null),
        };

    /// <summary>種類が Extensions にあるが、実装と専用の fixture で確認するまで自動では扱わないもの。</summary>
    private static readonly HashSet<string> Unverified = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExtendedFields",
        "ExtendedPlugins",
    };

    /// <summary>棚卸しする。</summary>
    public InventoryReport Run()
    {
        var items = source.List();
        var entries = new List<InventoryEntry>();
        var diagnostics = new List<InventoryDiagnostic>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items.Where(i => i.IsLink))
        {
            entries.Add(Make(item.RelativePath, null, Classification.NeedsReview, "link-or-reparse-point", [File(item.RelativePath, "link", 0, null)]));
            consumed.Add(item.RelativePath);
        }

        var files = items.Where(i => !i.IsLink).ToList();
        var paths = files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files)
        {
            var path = item.RelativePath;
            if (consumed.Contains(path) || IsUnder(path, consumed))
            {
                continue;
            }

            var folder = path[..path.IndexOf('/', StringComparison.Ordinal)];
            if (!Known.TryGetValue(folder, out var spec))
            {
                var reason = Unverified.Contains(folder) ? "type-not-verified" : "no-extensions-type";
                entries.Add(Apply(Make(path, null, Classification.Keep, reason, [Describe(item, "file")]), path));
                continue;
            }

            var name = path[(path.LastIndexOf('/') + 1)..];
            if (spec.SidecarSuffix is not null && name.EndsWith(".json" + spec.SidecarSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var definition = path[..^spec.SidecarSuffix.Length];
                if (!paths.Contains(definition))
                {
                    entries.Add(Apply(Make(path, spec.Type, Classification.NeedsReview, "orphan-sidecar", [Describe(item, "sidecar")]), path));
                }

                continue;
            }

            if (!name.EndsWith(spec.Extension!, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(Apply(Make(path, spec.Type, Classification.NeedsReview, "unknown-extension", [Describe(item, "file")]), path));
                continue;
            }

            entries.Add(Apply(Convert(item, spec, paths, files), path));
        }

        entries = entries.OrderBy(e => e.SourceKey, StringComparer.Ordinal).ToList();
        AddDiagnostics(entries, diagnostics);
        return new InventoryReport
        {
            ToolVersion = ToolVersion(),
            SourceId = sourceId,
            CreatedAt = (clock ?? TimeProvider.System).GetUtcNow(),
            Summary = Summarize(entries),
            Entries = entries,
            Diagnostics = diagnostics,
        };
    }

    private static string ToolVersion() =>
        typeof(InventoryEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    private static bool IsUnder(string path, HashSet<string> linkRoots) =>
        linkRoots.Any(root => path.StartsWith(root + "/", StringComparison.Ordinal));

    private static InventoryFile File(string path, string role, long length, string? sha) => new(path, role, length, sha);

    private InventoryFile Describe(SourceItem item, string role)
    {
        if (item.Length > LocalSourceProvider.MaxFileBytes)
        {
            return File(item.RelativePath, role, item.Length, null);
        }

        return File(item.RelativePath, role, item.Length, ExtensionConverter.Sha256(source.Read(item.RelativePath)));
    }

    private InventoryEntry Make(string path, string? type, Classification classification, string reason, IReadOnlyList<InventoryFile> files) =>
        new()
        {
            SourceKey = $"{sourceId}:{path}",
            ExtensionType = type,
            Classification = classification,
            Reason = reason,
            Files = files,
        };

    /// <summary>利用者の規則があれば、分類を上書きする（リンクと不正な形式を除く）。</summary>
    private InventoryEntry Apply(InventoryEntry entry, string primaryPath)
    {
        if (entry.Reason is "link-or-reparse-point")
        {
            return entry;
        }

        var matched = rules.Match(primaryPath);
        return matched is null
            ? entry
            : entry with { Classification = matched.Value.Classification, Reason = "rule", RuleNote = matched.Value.Reason };
    }

    private InventoryEntry Convert(
        SourceItem item,
        (string Type, bool Json, string? Extension, string? SidecarSuffix) spec,
        HashSet<string> paths,
        List<SourceItem> files)
    {
        var path = item.RelativePath;
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var role = spec.Json ? "definition" : "body";
        var primary = Describe(item, role);
        if (item.Length > LocalSourceProvider.MaxFileBytes)
        {
            return Make(path, spec.Type, Classification.NeedsReview, "too-large", [primary]);
        }

        var text = ExtensionConverter.DecodeUtf8(source.Read(path), out var bom);
        if (text is null)
        {
            return Make(path, spec.Type, Classification.NeedsReview, "invalid-utf8", [primary]);
        }

        var entryFiles = new List<InventoryFile> { primary };
        string? sidecarText = null;
        if (spec.SidecarSuffix is not null && paths.Contains(path + spec.SidecarSuffix))
        {
            var sidecarItem = files.First(f => string.Equals(f.RelativePath, path + spec.SidecarSuffix, StringComparison.OrdinalIgnoreCase));
            entryFiles.Add(Describe(sidecarItem, "sidecar"));
            if (sidecarItem.Length > LocalSourceProvider.MaxFileBytes)
            {
                return Make(path, spec.Type, Classification.NeedsReview, "too-large", entryFiles);
            }

            sidecarText = ExtensionConverter.DecodeUtf8(source.Read(sidecarItem.RelativePath), out _);
            if (sidecarText is null)
            {
                return Make(path, spec.Type, Classification.NeedsReview, "invalid-utf8", entryFiles);
            }
        }

        ConvertedExtension converted;
        if (spec.Json)
        {
            try
            {
                converted = ExtensionConverter.FromJsonDefinition(spec.Type, text, sidecarText);
            }
            catch (FormatException)
            {
                return Make(path, spec.Type, Classification.NeedsReview, "invalid-definition", entryFiles);
            }
        }
        else if (spec.Type == "Html")
        {
            if (text.Length == 0)
            {
                // Pleasanter は空の HTML ファイルを読み込まない
                return Make(path, spec.Type, Classification.Keep, "empty-html-not-loaded", entryFiles);
            }

            converted = ExtensionConverter.FromHtmlFile(fileName, text);
        }
        else
        {
            converted = ExtensionConverter.FromBodyFile(spec.Type, fileName, text);
        }

        return new InventoryEntry
        {
            SourceKey = $"{sourceId}:{path}",
            ExtensionType = spec.Type,
            Classification = Classification.Migrate,
            Reason = "convertible",
            Name = converted.ExtensionName,
            Language = converted.Language,
            OriginalDisabled = converted.Disabled,
            BodySource = converted.BodySource,
            Files = entryFiles,
            BodySha256 = converted.BodySource == "none" ? null : ExtensionConverter.Sha256(converted.Body),
            SettingsSha256 = ExtensionConverter.Sha256(converted.ExtensionSettings),
            Bom = bom,
            LineEnding = ExtensionConverter.LineEnding(converted.Body),
        };
    }

    private static InventorySummary Summarize(List<InventoryEntry> entries)
    {
        var units = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var files = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in Enum.GetNames<Classification>())
        {
            units[name] = 0;
            files[name] = 0;
        }

        foreach (var entry in entries)
        {
            var key = entry.Classification.ToString();
            units[key]++;
            files[key] += entry.Files.Count;
        }

        return new InventorySummary(units, files, entries.Count, entries.Sum(e => e.Files.Count));
    }

    private static void AddDiagnostics(List<InventoryEntry> entries, List<InventoryDiagnostic> diagnostics)
    {
        foreach (var entry in entries.Where(e => e.Classification == Classification.NeedsReview))
        {
            diagnostics.Add(new InventoryDiagnostic("error", "needs-review", $"Needs review: {entry.Reason}", entry.SourceKey));
        }

        var migrate = entries.Where(e => e.Classification == Classification.Migrate).ToList();
        foreach (var entry in migrate.Where(e => e.Name is { Length: 0 }))
        {
            diagnostics.Add(new InventoryDiagnostic("warning", "empty-name", "The name is empty. It is kept empty.", entry.SourceKey));
        }

        foreach (var group in migrate.Where(e => e.Name is { Length: > 0 }).GroupBy(e => (e.ExtensionType, e.Name)).Where(g => g.Count() > 1))
        {
            foreach (var entry in group)
            {
                diagnostics.Add(new InventoryDiagnostic("warning", "duplicate-name", "The same non-empty name is used by several settings.", entry.SourceKey));
            }
        }
    }
}
