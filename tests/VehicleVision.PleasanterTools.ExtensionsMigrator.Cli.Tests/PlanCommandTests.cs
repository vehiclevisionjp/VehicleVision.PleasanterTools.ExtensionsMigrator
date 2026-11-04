using System.Net;
using System.Text;
using System.Text.Json;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Plan;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Cli.Tests;

/// <summary>plan コマンド。対象 API はスタブ、ソースは合成データ。実環境へは接続しない。</summary>
public sealed class PlanCommandTests : IDisposable
{
    private const string Key = "plan-test-secret-key";
    private const string Base = "https://pleasanter.example.invalid/sub";

    private readonly string root = Path.Combine(Path.GetTempPath(), "em-plan-" + Guid.NewGuid().ToString("N"));

    public PlanCommandTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Put(string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static string DataJson(int count, int tenant = 1)
    {
        var sb = new StringBuilder("{\"StatusCode\":200,\"Response\":{\"Data\":[");
        for (var i = 1; i <= count; i++)
        {
            sb.Append(i > 1 ? "," : "")
              .Append(CultureInvariant($"{{\"ExtensionId\":{i},\"TenantId\":{tenant},\"ExtensionType\":\"Script\",\"ExtensionName\":\"n{i}.js\",\"ExtensionSettings\":{{}},\"Body\":\"b{i}\",\"Disabled\":false,\"Comments\":\"c\"}}"));
        }

        return sb.Append("]}}").ToString();
    }

    private static string CultureInvariant(FormattableString s) => FormattableString.Invariant(s);

    private static string? Env(string name) => name switch
    {
        "PLEASANTER_BASE_URL" => Base,
        "PLEASANTER_API_KEY" => Key,
        _ => null,
    };

    [Fact]
    public async Task plan_コマンドは_Get_だけを呼び_plan_を原子的に書き_キーを出さない()
    {
        Put("ExtendedScripts/a.js", "1");
        var handler = default(CountingHandler);
        var command = new PlanCommand(() => new HttpClient(handler = new CountingHandler(_ => Json(HttpStatusCode.OK, DataJson(3)))));
        var app = new CommandLineApp([command]);
        var output = new StringWriter();
        var error = new StringWriter();
        var outPath = Path.Combine(Path.GetTempPath(), "em-plan-out-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var code = await app.RunAsync(["plan", "--source", root, "--source-id", "s", "--tenant-id", "1", "--out", outPath], output, error, Env);

            Assert.Equal(ExitCodes.Success, code);
            Assert.All(handler!.Uris, u => Assert.EndsWith("/api/extensions/Get", u, StringComparison.Ordinal));
            var written = File.ReadAllText(outPath);
            Assert.Equal(PlanReport.CurrentFormatVersion, JsonDocument.Parse(written).RootElement.GetProperty("formatVersion").GetInt32());
            Assert.DoesNotContain(Key, written + output + error, StringComparison.Ordinal);
            Assert.Contains("never deleted", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Fact]
    public async Task plan_コマンドは引数_URL_台帳_API_の失敗を終了コードで区別する()
    {
        Put("ExtendedScripts/a.js", "1");
        var ok = new PlanCommand(() => new HttpClient(new CountingHandler(_ => Json(HttpStatusCode.OK, DataJson(0)))));
        var denied = new PlanCommand(() => new HttpClient(new CountingHandler(_ => Json(HttpStatusCode.Unauthorized, "{}"))));
        string?[] http = ["http://example.com"];

        async Task<int> Run(PlanCommand command, string[] args, Func<string, string?>? env = null) =>
            await new CommandLineApp([command]).RunAsync(args, new StringWriter(), new StringWriter(), env ?? Env);

        Assert.Equal(ExitCodes.UsageError, await Run(ok, ["plan", "--source", root]));
        Assert.Equal(ExitCodes.UsageError, await Run(ok, ["plan", "--source", root, "--tenant-id", "x"]));
        Assert.Equal(ExitCodes.UsageError, await Run(ok, ["plan", "--source", root, "--tenant-id", "1"], n => n == "PLEASANTER_BASE_URL" ? http[0] : Env(n)));
        Assert.Equal(ExitCodes.UsageError, await Run(ok, ["plan", "--source", root, "--tenant-id", "1", "--out", Path.Combine(root, "p.json")]));
        Assert.Equal(ExitCodes.Failure, await Run(denied, ["plan", "--source", root, "--tenant-id", "1"]));

        var bad = Path.Combine(Path.GetTempPath(), "em-badledger-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(bad, "{ truncated");
        try
        {
            Assert.Equal(ExitCodes.Failure, await Run(ok, ["plan", "--source", root, "--source-id", "s", "--tenant-id", "1", "--ledger", bad]));
        }
        finally
        {
            File.Delete(bad);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}
