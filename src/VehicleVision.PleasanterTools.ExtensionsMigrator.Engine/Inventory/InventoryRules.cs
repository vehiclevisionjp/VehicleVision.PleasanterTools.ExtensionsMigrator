using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

/// <summary>利用者が決める、保持・対象外・置換の規則。既定は空（特定の環境の名前を前提にしない）。</summary>
public sealed class InventoryRules
{
    private readonly List<Rule> rules;

    private InventoryRules(List<Rule> rules) => this.rules = rules;

    /// <summary>規則なし。</summary>
    public static InventoryRules Empty { get; } = new([]);

    /// <summary>規則の JSON を読む。形が違えば <see cref="FormatException"/>。</summary>
    /// <remarks>形式: <c>{"formatVersion":1,"rules":[{"match":"ExtendedSqls/Sample*.json","classification":"keep","reason":"..."}]}</c></remarks>
    public static InventoryRules Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new FormatException("The rules file is not valid JSON.");
        }

        using (document)
        {
            var rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object
                || !rootElement.TryGetProperty("formatVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != 1
                || !rootElement.TryGetProperty("rules", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The rules file must be an object with formatVersion 1 and a rules array.");
            }

            var parsed = new List<Rule>();
            foreach (var element in list.EnumerateArray())
            {
                var match = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("match", out var m)
                    ? m.GetString()
                    : null;
                var kind = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("classification", out var c)
                    ? c.GetString()
                    : null;
                if (string.IsNullOrEmpty(match))
                {
                    throw new FormatException("Each rule needs a non-empty match.");
                }

                var classification = kind switch
                {
                    "keep" => Classification.Keep,
                    "exclude" => Classification.Exclude,
                    "replace" => Classification.Replace,
                    _ => throw new FormatException("A rule classification must be keep, exclude or replace."),
                };
                var reason = element.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString()
                    : null;
                parsed.Add(new Rule(GlobToRegex(match), classification, reason));
            }

            return new InventoryRules(parsed);
        }
    }

    /// <summary>最初に一致した規則を返す。無ければ <c>null</c>。</summary>
    public (Classification Classification, string? Reason)? Match(string relativePath)
    {
        foreach (var rule in rules)
        {
            if (rule.Pattern.IsMatch(relativePath))
            {
                return (rule.Classification, rule.Reason);
            }
        }

        return null;
    }

    private static Regex GlobToRegex(string glob)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var ch = glob[i];
            if (ch == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    builder.Append(".*");
                    i++;
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(ch.ToString()));
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private sealed record Rule(Regex Pattern, Classification Classification, string? Reason);
}
