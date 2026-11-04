using System.Net;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Cli;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Tests;

public sealed class CommandLineAppTests
{
    private const string SecretKey = "test-secret-key-0123456789";

    private static readonly IReadOnlySet<string> NoOptions = new HashSet<string>();

    private static CommandLineApp CreateApp(Func<HttpClient>? factory = null) => new(
    [
        new CheckCommand(factory ?? (() => new HttpClient(new FixedHandler(HttpStatusCode.OK)))),
        new NotImplementedCommand("inventory", "List."),
    ]);

    private static async Task<(int Code, string Out, string Err)> RunAsync(
        CommandLineApp app,
        string[] args,
        Func<string, string?>? env = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await app.RunAsync(args, output, error, env ?? (_ => null), CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task 引数が無ければ使い方を標準エラーへ出して_2_で終わる()
    {
        var result = await RunAsync(CreateApp(), []);

        Assert.Equal(ExitCodes.UsageError, result.Code);
        Assert.Contains("Usage:", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task help_は標準出力へ出して_0_で終わる()
    {
        var result = await RunAsync(CreateApp(), ["--help"]);

        Assert.Equal(ExitCodes.Success, result.Code);
        Assert.Contains("inventory", result.Out, StringComparison.Ordinal);
        Assert.Contains("not implemented yet", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未知のコマンドは_2_で終わる()
    {
        var result = await RunAsync(CreateApp(), ["unknown"]);

        Assert.Equal(ExitCodes.UsageError, result.Code);
        Assert.Contains("Unknown command: unknown", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未実装のコマンドは何も実行せず_2_で終わる()
    {
        var result = await RunAsync(CreateApp(), ["inventory"]);

        Assert.Equal(ExitCodes.UsageError, result.Code);
        Assert.Contains("not implemented yet", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 仕様に無いオプションは黙って無視せず_2_で終わる()
    {
        var result = await RunAsync(CreateApp(), ["check", "--nope"]);

        Assert.Equal(ExitCodes.UsageError, result.Code);
        Assert.Contains("Unknown option: --nope", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 環境変数が無ければ_2_で終わり_値は出さない()
    {
        var result = await RunAsync(CreateApp(), ["check"]);

        Assert.Equal(ExitCodes.UsageError, result.Code);
        Assert.Contains("PLEASANTER_BASE_URL", result.Err + result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task check_は接続できれば_0_を返し_API_キーを出力しない()
    {
        var result = await RunAsync(CreateApp(), ["check"], Environment);

        Assert.Equal(ExitCodes.Success, result.Code);
        Assert.Equal("Connected." + System.Environment.NewLine, result.Out);
        Assert.DoesNotContain(SecretKey, result.Out + result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task check_は認証に失敗したら_1_を返し_API_キーを出力しない()
    {
        var app = CreateApp(() => new HttpClient(new FixedHandler(HttpStatusCode.Unauthorized)));

        var result = await RunAsync(app, ["check"], Environment);

        Assert.Equal(ExitCodes.Failure, result.Code);
        Assert.DoesNotContain(SecretKey, result.Out + result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void PleasanterOptions_の文字列表現は_API_キーを含まない()
    {
        var options = PleasanterEnvironment.FromEnvironment(Environment);

        Assert.DoesNotContain(SecretKey, options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 引数の解析は等号と空白の両方で値を受け_重複と欠落を誤りにする()
    {
        var values = new HashSet<string> { "--out", "--config" };
        var flags = new HashSet<string> { "--yes" };

        var ok = ParsedArguments.Parse(["--out=a", "--config", "b", "--yes", "x"], values, flags);
        Assert.Null(ok.Error);
        Assert.Equal("a", ok.Value("--out"));
        Assert.Equal("b", ok.Value("--config"));
        Assert.True(ok.Flag("--yes"));
        Assert.Equal(["x"], ok.Positionals);

        Assert.NotNull(ParsedArguments.Parse(["--out", "a", "--out", "b"], values, flags).Error);
        Assert.NotNull(ParsedArguments.Parse(["--out"], values, flags).Error);
        Assert.NotNull(ParsedArguments.Parse(["--yes=1"], values, flags).Error);
        Assert.NotNull(ParsedArguments.Parse(["--x"], NoOptions, NoOptions).Error);
    }

    private static string? Environment(string name) => name switch
    {
        "PLEASANTER_BASE_URL" => "https://example.invalid",
        "PLEASANTER_API_KEY" => SecretKey,
        _ => null,
    };

    private sealed class FixedHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }
}
