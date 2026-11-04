using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Target;

/// <summary>対象の Extensions API から読んだ 1 件。本文は秘密を含みうるので、出力・ログへは出さない。</summary>
/// <param name="ExtensionId">ID。</param>
/// <param name="TenantId">テナント。</param>
/// <param name="ExtensionType">種類。</param>
/// <param name="ExtensionName">名前（空のこともある）。</param>
/// <param name="SettingsSha256">設定の意味上のハッシュ（キー順を整えたもの）。</param>
/// <param name="BodySha256">本文のハッシュ。</param>
/// <param name="Disabled">無効か。</param>
public sealed record ExtensionRecord(
    int ExtensionId,
    int? TenantId,
    string ExtensionType,
    string ExtensionName,
    string SettingsSha256,
    string BodySha256,
    bool Disabled);

/// <summary>一覧の取得結果。</summary>
public sealed record ExtensionListResult(
    IReadOnlyList<ExtensionRecord> Records,
    PleasanterErrorKind ErrorKind,
    string? FailureReason)
{
    public bool IsSuccess => ErrorKind is PleasanterErrorKind.None;
}

/// <summary>Extensions API の読み取り（<c>Get</c> だけ）。作成・更新・削除はしない。</summary>
/// <remarks>
/// 応答の本文は保持せず、ハッシュだけにする。失敗の理由は固定の文言だけを返し、応答の本文は出さない。
/// 件数の上限（ページング）の有無は参照ソースの読みでは確認できていないため、返った分をすべて扱う（実機は未確認）。
/// </remarks>
public sealed class ExtensionsApiClient(PleasanterApiClient client)
{
    public async Task<ExtensionListResult> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.GetExtensionsAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return Failure(response.ErrorKind, Describe(response.ErrorKind, response.StatusCode));
        }

        var apiStatus = response.Body?["StatusCode"]?.GetValue<int?>();
        if (apiStatus is not null and not 200)
        {
            var kind = apiStatus == 401 ? PleasanterErrorKind.Unauthorized : PleasanterErrorKind.Permanent;
            return Failure(kind, Describe(kind, apiStatus));
        }

        if (response.Body?["Response"]?["Data"] is not JsonArray data)
        {
            return Failure(PleasanterErrorKind.Permanent, "The response did not contain a list of extensions.");
        }

        var records = new List<ExtensionRecord>(data.Count);
        foreach (var item in data)
        {
            var record = item is JsonObject obj ? Parse(obj) : null;
            if (record is null)
            {
                return Failure(PleasanterErrorKind.Permanent, "The response contained a record that could not be read.");
            }

            records.Add(record);
        }

        return new ExtensionListResult(records, PleasanterErrorKind.None, null);
    }

    private static ExtensionRecord? Parse(JsonObject obj)
    {
        if (obj["ExtensionId"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id))
        {
            return null;
        }

        var type = obj["ExtensionType"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : string.Empty;
        var name = obj["ExtensionName"] is JsonValue n && n.TryGetValue<string>(out var ns) ? ns : string.Empty;
        var body = obj["Body"] is JsonValue b && b.TryGetValue<string>(out var bs) ? bs : string.Empty;
        var disabled = obj["Disabled"] is JsonValue d && d.TryGetValue<bool>(out var db) && db;
        int? tenant = obj["TenantId"] is JsonValue tv && tv.TryGetValue<int>(out var ti) ? ti : null;
        return new ExtensionRecord(
            id,
            tenant,
            type,
            name,
            SettingsHash(obj["ExtensionSettings"]),
            ExtensionConverter.Sha256(body),
            disabled);
    }

    /// <summary>設定は、オブジェクトでも JSON を含む文字列でも返りうるので、どちらも同じ形に整えてからハッシュにする。</summary>
    internal static string SettingsHash(JsonNode? settings)
    {
        JsonNode? node = settings;
        if (settings is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                node = null;
            }
        }

        return ExtensionConverter.Sha256(node is null ? "null" : CanonicalJson.Serialize(node));
    }

    private static ExtensionListResult Failure(PleasanterErrorKind kind, string reason) => new([], kind, reason);

    private static string Describe(PleasanterErrorKind kind, int? status) => kind switch
    {
        PleasanterErrorKind.Unauthorized => "Authentication failed (HTTP/API status 401). Check the API key.",
        PleasanterErrorKind.Unknown => "No response was received (connection, timeout or TLS error).",
        PleasanterErrorKind.Transient => $"A temporary error occurred (status {status}).",
        _ when status is 403 => "Forbidden (status 403). The extensions API may be disabled for the tenant, or the user lacks privilege.",
        _ when status is >= 300 and < 400 => $"A redirect (status {status}) was not followed.",
        _ => $"The request was rejected (status {status}).",
    };
}
