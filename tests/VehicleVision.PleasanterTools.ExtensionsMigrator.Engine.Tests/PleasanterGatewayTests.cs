using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.DotNetClient.Models;

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
    [InlineData(HttpStatusCode.OK, PleasanterErrorKind.None)]
    [InlineData(HttpStatusCode.Unauthorized, PleasanterErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.TooManyRequests, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, PleasanterErrorKind.Transient)]
    public void Classify_は本文の_StatusCode_を扱い方へ分類する(HttpStatusCode status, PleasanterErrorKind expected)
        => Assert.Equal(expected, PleasanterGateway.Classify(status));

    [Theory]
    [InlineData("""{"StatusCode":200,"Response":{}}""", PleasanterErrorKind.None, 200)]
    [InlineData("""{"StatusCode":401}""", PleasanterErrorKind.Unauthorized, 401)]
    [InlineData("""{"StatusCode":403}""", PleasanterErrorKind.Permanent, 403)]
    [InlineData("""{"StatusCode":429}""", PleasanterErrorKind.Transient, 429)]
    [InlineData("""{"StatusCode":500}""", PleasanterErrorKind.Transient, 500)]
    [InlineData("""{"StatusCode":"403"}""", PleasanterErrorKind.Permanent, 403)]
    [InlineData("""{"Id":1}""", PleasanterErrorKind.None, null)]
    [InlineData("""{"StatusCode":"abc"}""", PleasanterErrorKind.None, null)]
    public async Task HTTP_が_200_でも_本文の_StatusCode_が失敗なら失敗にする(string body, PleasanterErrorKind kind, int? apiStatus)
    {
        var gateway = new PleasanterGateway(new HttpClient(new StubHandler(HttpStatusCode.OK, body)), Options);

        var result = await gateway.GetExtensionsAsync();

        Assert.Equal(kind, result.ErrorKind);
        Assert.Equal(apiStatus, result.ApiStatusCode);
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
    public async Task 型付きの呼び出しの結果も_補う処理を通せる()
    {
        // 将来の Extensions の作成・更新・削除用。版の問い合わせは、接続設定で止める（テストでは通信を増やさない）
        var options = new PleasanterOptions { BaseUrl = Options.BaseUrl, ApiKey = Key, DetectServerVersion = false };
        var handler = new StubHandler(HttpStatusCode.OK, """{"StatusCode":403,"Message":"denied"}""");
        var gateway = new PleasanterGateway(new HttpClient(handler), options);

        var raw = await gateway.Client.Extensions.CreateAsync(new ExtensionApiModel { ExtensionName = "n" });
        var result = PleasanterGateway.Supplement(raw);

        Assert.True(raw.IsSuccess);
        Assert.Equal(PleasanterErrorKind.Permanent, result.ErrorKind);
        Assert.Equal("https://example.invalid/api/Extensions/Create", Assert.Single(handler.Uris));
    }

    private sealed class StubHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}",
        bool throwOnSend = false) : HttpMessageHandler
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
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
