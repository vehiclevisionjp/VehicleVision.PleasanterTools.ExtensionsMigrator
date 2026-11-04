using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine;

/// <summary>
/// <see cref="PleasanterResponse"/> に、DotNetClient 0.2.0 が返さない本文の <c>StatusCode</c> を足した結果。
/// </summary>
/// <param name="ErrorKind">扱い方。HTTP が 200 でも <paramref name="ApiStatusCode"/> が 200 以外なら、その値で分類し直した結果。</param>
/// <param name="StatusCode">HTTP のステータス。応答が無ければ <c>null</c>。</param>
/// <param name="Id">作成・更新されたレコードの ID。</param>
/// <param name="Message">Pleasanter が返した文言。</param>
/// <param name="Body">応答の中身。</param>
/// <param name="ApiStatusCode">応答 JSON の <c>StatusCode</c>。</param>
public sealed record PleasanterResult(
    PleasanterErrorKind ErrorKind,
    int? StatusCode = null,
    long? Id = null,
    string? Message = null,
    JsonNode? Body = null,
    int? ApiStatusCode = null)
    : PleasanterResponse(ErrorKind, StatusCode, Id, Message, Body)
{
    /// <summary>失敗の説明に使うステータス。本文の <c>StatusCode</c> があればそれ、なければ HTTP のステータス。</summary>
    public int? EffectiveStatusCode => ApiStatusCode ?? StatusCode;
}

/// <summary>DotNetClient の <see cref="PleasanterApiClient"/> を、本ツールの呼び方で包む。</summary>
/// <remarks>
/// <para>通信は呼び出し側が渡す <see cref="HttpClient"/> で行う。本番は <see cref="PleasanterHttp.CreateClient"/>
/// （リダイレクトを辿らない・Cookie 無し）を使う。API キーは本文に載るため、<c>BaseUrl</c> の検査と秘密を出さない扱いは DotNetClient に任せる。</para>
/// <para>**DotNetClient 0.2.0 に無いものを、ここで補っている。** HTTP が 200 でも、本文の <c>StatusCode</c> が 200 以外なら失敗として分類する
/// （DotNetClient #95・PR #96。develop にあるが 0.2.0 には無い）。
/// DotNetClient #95 を含む版へ上げたら、<see cref="Supplement"/> は消せる。</para>
/// <para>Retry-After は読まない。本ツールは再試行せず、失敗は人に返すため。</para>
/// <para>読み取りは <see cref="PleasanterApiClient.PostJsonAsync"/> を使う。型付きのメソッド（<c>client.Items.*</c>・<c>client.Extensions.*</c>）は、
/// 初回に版の問い合わせ（<c>api/utility/GetLicenseInfo</c>）を 1 回行うが、<c>PostJsonAsync</c> は行わないため、読み取りの呼び出し回数が増えない。
/// 将来の Extensions の作成・更新・削除は、版で使えない口を送る前に止められる型付き（<see cref="Client"/> の <c>Extensions</c>）を使い、
/// 結果は <see cref="Supplement"/> に通す。</para>
/// </remarks>
public sealed class PleasanterGateway
{
    /// <summary>通信と接続設定で作る。</summary>
    public PleasanterGateway(HttpClient httpClient, IPleasanterOptionsProvider optionsProvider)
        => Client = new PleasanterApiClient(httpClient, optionsProvider);

    /// <summary>通信と固定の接続設定で作る。</summary>
    public PleasanterGateway(HttpClient httpClient, PleasanterOptions options)
        : this(httpClient, new StaticPleasanterOptionsProvider(options))
    {
    }

    /// <summary>型付きの呼び出し（<c>Extensions.CreateAsync</c> など）が要るときの、包んでいるクライアント。</summary>
    public PleasanterApiClient Client { get; }

    /// <summary>接続先へ到達し、API キーが拒否されないことを確かめる。</summary>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        var result = await PostAsync("api/users/Get", cancellationToken).ConfigureAwait(false);
        return result.ErrorKind is not (PleasanterErrorKind.Unauthorized or PleasanterErrorKind.Unknown)
            && result.EffectiveStatusCode is not (int)HttpStatusCode.NotFound;
    }

    /// <summary>Extensions API の一覧を取り出す（読み取りだけ）。</summary>
    public Task<PleasanterResult> GetExtensionsAsync(CancellationToken cancellationToken = default)
        => PostAsync("api/extensions/Get", cancellationToken);

    /// <summary>DotNetClient の結果に、本文の <c>StatusCode</c> による失敗の分類を足す。</summary>
    /// <remarks>DotNetClient #95 を含む版へ上げたら消せる（その版は、本文の <c>StatusCode</c> を <c>ErrorKind</c> に反映する）。</remarks>
    public static PleasanterResult Supplement(PleasanterResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var apiStatus = ReadInt(response.Body?["StatusCode"]);
        var kind = response.ErrorKind;
        if (kind is PleasanterErrorKind.None && apiStatus is not null && apiStatus != 200)
        {
            // HTTP は 200 でも、JSON の StatusCode が失敗なら失敗として扱う
            kind = Classify((HttpStatusCode)apiStatus.Value);
        }

        return new PleasanterResult(kind, response.StatusCode, response.Id, response.Message, response.Body, apiStatus);
    }

    // DotNetClient の Classify は内部用のため、同じ規則をここに持つ（DotNetClient #95 を含む版へ上げたら、Supplement と一緒に消せる）
    internal static PleasanterErrorKind Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => PleasanterErrorKind.None,
        HttpStatusCode.Unauthorized => PleasanterErrorKind.Unauthorized,
        HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            => PleasanterErrorKind.Permanent,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            => PleasanterErrorKind.Transient,
        _ => (int)statusCode >= 500 ? PleasanterErrorKind.Transient : PleasanterErrorKind.Permanent,
    };

    private async Task<PleasanterResult> PostAsync(string path, CancellationToken cancellationToken)
    {
        var response = await Client.PostJsonAsync(path, null, cancellationToken).ConfigureAwait(false);
        return Supplement(response);
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }
}
