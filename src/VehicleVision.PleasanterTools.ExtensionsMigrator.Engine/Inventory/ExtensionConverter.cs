using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

/// <summary>Extensions API に登録する 1 件分。<see cref="Body"/> は秘密を含みうるので、plan・ログへ埋め込まない。</summary>
public sealed record ConvertedExtension(
    string ExtensionType,
    string ExtensionName,
    string ExtensionSettings,
    string Body,
    bool Disabled,
    string? Description,
    string BodySource,
    string? Language);

/// <summary>ファイルの定義を、Extensions の登録形式へ変換する（読み込み規則は Pleasanter 1.5.9.1 のソースで確認。実機は未確認）。</summary>
public static class ExtensionConverter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Script・Style。名前は拡張子込みのファイル名、本文は全文、設定は空のオブジェクト。</summary>
    public static ConvertedExtension FromBodyFile(string extensionType, string fileName, string body) =>
        new(extensionType, fileName, "{}", body, false, null, "file", null);

    /// <summary>Html。ファイル名の最後の <c>_</c> 以降を言語、それより前を挿入位置（名前）とする。</summary>
    public static ConvertedExtension FromHtmlFile(string fileName, string body)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var underscore = stem.LastIndexOf('_');
        var hasLanguage = underscore >= 0 && underscore < stem.Length - 1 && stem.Split('_').Length > 1;
        var language = hasLanguage ? stem[(underscore + 1)..] : null;
        var name = hasLanguage ? stem[..underscore] : stem;
        var settings = language is null ? "{}" : CanonicalJson.Serialize(new JsonObject { ["Language"] = language });
        return new ConvertedExtension("Html", name, settings, body, false, null, "file", language);
    }

    /// <summary>Sql・ServerScript・NavigationMenu の JSON の定義。併設本文があれば（空でも）それを優先する。</summary>
    /// <param name="extensionType"><c>Sql</c>・<c>ServerScript</c>・<c>NavigationMenu</c>。</param>
    /// <param name="json">定義の JSON。</param>
    /// <param name="sidecar">併設ファイルの本文。無ければ <c>null</c>。</param>
    /// <exception cref="FormatException">JSON がオブジェクトでない。</exception>
    public static ConvertedExtension FromJsonDefinition(string extensionType, string json, string? sidecar)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            throw new FormatException("The definition is not valid JSON.");
        }

        if (node is not JsonObject definition)
        {
            throw new FormatException("The definition must be a JSON object.");
        }

        var bodyKey = extensionType switch
        {
            "Sql" => "CommandText",
            "ServerScript" => "Body",
            _ => null,
        };

        var body = string.Empty;
        var bodySource = "none";
        if (bodyKey is not null)
        {
            if (sidecar is not null)
            {
                body = sidecar;
                bodySource = "sidecar";
            }
            else if (definition[bodyKey] is JsonValue value && value.TryGetValue<string>(out var inline))
            {
                body = inline;
                bodySource = "inline";
            }

            definition.Remove(bodyKey);
        }

        var name = definition["Name"] is JsonValue n && n.TryGetValue<string>(out var s) ? s : string.Empty;
        var disabled = definition["Disabled"] is JsonValue d && d.TryGetValue<bool>(out var b) && b;
        var description = definition["Description"] is JsonValue ds && ds.TryGetValue<string>(out var t) ? t : null;
        return new ConvertedExtension(
            extensionType,
            name,
            CanonicalJson.Serialize(definition),
            body,
            disabled,
            description,
            bodySource,
            null);
    }

    /// <summary>厳密な UTF-8 として読む。BOM は外す。不正なバイトがあれば <c>null</c>。</summary>
    public static string? DecodeUtf8(byte[] bytes, out bool hasBom)
    {
        hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            return StrictUtf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>本文の改行の種類。</summary>
    public static string LineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        return (crlf, lf) switch
        {
            (0, 0) => "none",
            (_, 0) => "crlf",
            (0, _) => "lf",
            _ => "mixed",
        };
    }

    /// <summary>SHA-256 を小文字の 16 進で返す。</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>文字列を UTF-8（BOM なし）にして SHA-256 を返す。</summary>
    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
}

/// <summary>キーの順を整えた JSON。意味上のハッシュに使う。配列の順は変えない。</summary>
public static class CanonicalJson
{
    /// <summary>オブジェクトのキーを序数順に並べて 1 行にする。</summary>
    public static string Serialize(JsonNode node) => Normalize(node)?.ToJsonString() ?? "null";

    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = Normalize(pair.Value);
                }

                return sorted;
            case JsonArray array:
                var result = new JsonArray();
                foreach (var item in array)
                {
                    result.Add(Normalize(item));
                }

                return result;
            default:
                return node?.DeepClone();
        }
    }
}
