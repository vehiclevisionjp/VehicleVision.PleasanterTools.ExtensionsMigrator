using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.DotNetClient.Models;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Target;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Tests;

/// <summary>DotNetClient 自体（BaseUrl の検査・本文の組み立て・分類）は DotNetClient 側でテストされる。ここでは包んだ部分だけを確かめる。</summary>
public sealed class PleasanterGatewayTests
{
    private const string Key = "gateway-test-secret-key";

    private static readonly PleasanterOptions Options = new()
    {
        BaseUrl = "https://example.invalid/",
        ApiKey = Key,
    };

    [Theory]
    [InlineData("""{"StatusCode":200,"Response":{}}""", PleasanterErrorKind.None, 200)]
    [InlineData("""{"StatusCode":401}""", PleasanterErrorKind.Unauthorized, 401)]
    [InlineData("""{"StatusCode":403}""", PleasanterErrorKind.Permanent, 403)]
    [InlineData("""{"StatusCode":429}""", PleasanterErrorKind.Transient, 429)]
    [InlineData("""{"StatusCode":500}""", PleasanterErrorKind.Transient, 500)]
    // 文字列の "403" は DotNetClient 0.3.0 以降は読まない（0.2.0 向けの自前の補いは読んでいた）。Pleasanter は数値で返す
    [InlineData("""{"StatusCode":"403"}""", PleasanterErrorKind.None, null)]
    [InlineData("""{"Id":1}""", PleasanterErrorKind.None, null)]
    [InlineData("""{"StatusCode":"abc"}""", PleasanterErrorKind.None, null)]
    public async Task HTTP_が_200_でも_本文の_StatusCode_が失敗なら失敗にする(string body, PleasanterErrorKind kind, int? apiStatus)
    {
        var gateway = new PleasanterGateway(new HttpClient(new StubHandler(HttpStatusCode.OK, body)), Options);

        var result = await gateway.GetExtensionsAsync();

        Assert.Equal(kind, result.ErrorKind);
        Assert.Equal(apiStatus, result.ApplicationStatusCode);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task HTTP_が失敗なら_本文の_StatusCode_で分類し直さない()
    {
        var gateway = new PleasanterGateway(
            new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, """{"StatusCode":200}""")), Options);

        var result = await gateway.GetExtensionsAsync();

        Assert.Equal(PleasanterErrorKind.Transient, result.ErrorKind);
        Assert.Equal(500, result.StatusCode);
    }

    [Theory]
    [InlineData(401, PleasanterErrorKind.Unauthorized)]
    [InlineData(403, PleasanterErrorKind.Permanent)]
    [InlineData(429, PleasanterErrorKind.Transient)]
    [InlineData(500, PleasanterErrorKind.Transient)]
    public async Task 一覧の取得は_HTTP_200_でも本文の_StatusCode_が失敗なら失敗にし_HTTP_のステータスは出さない(int apiStatus, PleasanterErrorKind kind)
    {
        var handler = new StubHandler(HttpStatusCode.OK, $$"""{"StatusCode":{{apiStatus}},"Message":"denied-secret-text"}""");
        var client = new ExtensionsApiClient(new PleasanterGateway(new HttpClient(handler), Options));

        var result = await client.ListAsync();

        Assert.Equal(kind, result.ErrorKind);
        Assert.False(result.IsSuccess);
        Assert.Contains(apiStatus.ToString(System.Globalization.CultureInfo.InvariantCulture), result.FailureReason!);
        Assert.DoesNotContain("denied-secret-text", result.FailureReason!);
    }

    [Fact]
    public async Task Retry_After_が付いた失敗でも_再試行せず_1_回だけ呼び_RetryAfter_を保持する()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "{}", retryAfter: "30");
        var gateway = new PleasanterGateway(new HttpClient(handler), Options);

        var response = await gateway.GetExtensionsAsync();
        var listed = await new ExtensionsApiClient(gateway).ListAsync();

        Assert.Equal(PleasanterErrorKind.Transient, response.ErrorKind);
        Assert.Equal(TimeSpan.FromSeconds(30), response.RetryAfter);
        Assert.Equal(PleasanterErrorKind.Transient, listed.ErrorKind);
        Assert.Equal(2, handler.Uris.Count);
    }

    [Fact]
    public async Task 読み取りは_ApiKey_と_ApiVersion_をボディへ載せ_版を問い合わせない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"StatusCode":200,"Response":{"Data":[]}}""");
        var gateway = new PleasanterGateway(new HttpClient(handler), Options);

        var result = await gateway.GetExtensionsAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("https://example.invalid/api/extensions/Get", Assert.Single(handler.Uris));
        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal(Key, (string?)body["ApiKey"]);
        Assert.Equal(1.1m, (decimal?)body["ApiVersion"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}", true)]
    [InlineData(HttpStatusCode.OK, """{"StatusCode":401}""", false)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", false)]
    [InlineData(HttpStatusCode.NotFound, "{}", false)]
    [InlineData(HttpStatusCode.Forbidden, "{}", true)]
    public async Task 接続確認は_認証失敗と_404_を接続失敗にする(HttpStatusCode status, string body, bool connected)
    {
        var gateway = new PleasanterGateway(new HttpClient(new StubHandler(status, body)), Options);

        Assert.Equal(connected, await gateway.CheckConnectionAsync());
    }

    [Fact]
    public async Task 通信に失敗したら_Unknown_を返し_取り消しは例外のまま伝える()
    {
        var down = new PleasanterGateway(new HttpClient(new StubHandler(throwOnSend: true)), Options);
        Assert.Equal(PleasanterErrorKind.Unknown, (await down.GetExtensionsAsync()).ErrorKind);
        Assert.False(await down.CheckConnectionAsync());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gateway = new PleasanterGateway(new HttpClient(new StubHandler()), Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetExtensionsAsync(cts.Token));
    }

    [Fact]
    public async Task 型付きの呼び出しも_本文の_StatusCode_が失敗なら失敗として返る()
    {
        // 将来の Extensions の作成・更新・削除用。版の問い合わせは、接続設定で止める（テストでは通信を増やさない）
        var options = new PleasanterOptions { BaseUrl = Options.BaseUrl, ApiKey = Key, DetectServerVersion = false };
        var handler = new StubHandler(HttpStatusCode.OK, """{"StatusCode":403,"Message":"denied"}""");
        var gateway = new PleasanterGateway(new HttpClient(handler), options);

        var result = await gateway.Client.Extensions.CreateAsync(new ExtensionApiModel { ExtensionName = "n" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PleasanterErrorKind.Permanent, result.ErrorKind);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(403, result.ApplicationStatusCode);
        Assert.Equal("https://example.invalid/api/Extensions/Create", Assert.Single(handler.Uris));
    }

    private sealed class StubHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}",
        bool throwOnSend = false,
        string? retryAfter = null) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throwOnSend)
            {
                throw new HttpRequestException("stub failure " + Key);
            }

            Uris.Add(request.RequestUri!.ToString());
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        }
    }
}
