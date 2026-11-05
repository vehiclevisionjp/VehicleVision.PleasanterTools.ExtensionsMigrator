using System.Net;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine;

/// <summary>DotNetClient の <see cref="PleasanterApiClient"/> を、本ツールの呼び方で包む。</summary>
/// <remarks>
/// <para>通信は呼び出し側が渡す <see cref="HttpClient"/> で行う。本番は <see cref="PleasanterHttp.CreateClient"/>
/// （リダイレクトを辿らない・Cookie 無し）を使う。API キーは本文に載るため、<c>BaseUrl</c> の検査と秘密を出さない扱いは DotNetClient に任せる。</para>
/// <para>HTTP が 200 でも、本文の <c>StatusCode</c> が 200 以外なら、DotNetClient 0.3.0 以降が失敗として返す
/// （<see cref="PleasanterResponse.ErrorKind"/>。本文の値は <see cref="PleasanterResponse.ApplicationStatusCode"/>、
/// <see cref="PleasanterResponse.StatusCode"/> は HTTP のまま）。本ツールで補う処理は持たない。</para>
/// <para><see cref="PleasanterResponse.RetryAfter"/>（0.4.0）は使わない。本ツールは再試行せず、失敗は人に返すため。</para>
/// <para>読み取りは <see cref="PleasanterApiClient.PostJsonAsync"/> を使う。型付きのメソッド（<c>client.Items.*</c>・<c>client.Extensions.*</c>）は、
/// 初回に版の問い合わせ（<c>api/utility/GetLicenseInfo</c>）を 1 回行うが、<c>PostJsonAsync</c> は行わないため、読み取りの呼び出し回数が増えない。
/// 将来の Extensions の作成・更新・削除は、版で使えない口を送る前に止められる型付き（<see cref="Client"/> の <c>Extensions</c>）を使い、
/// 結果はそのまま <see cref="PleasanterResponse"/> として扱える。</para>
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
            && (result.ApplicationStatusCode ?? result.StatusCode) is not (int)HttpStatusCode.NotFound;
    }

    /// <summary>Extensions API の一覧を取り出す（読み取りだけ）。</summary>
    public Task<PleasanterResponse> GetExtensionsAsync(CancellationToken cancellationToken = default)
        => PostAsync("api/extensions/Get", cancellationToken);

    private Task<PleasanterResponse> PostAsync(string path, CancellationToken cancellationToken)
        => Client.PostJsonAsync(path, null, cancellationToken);
}
