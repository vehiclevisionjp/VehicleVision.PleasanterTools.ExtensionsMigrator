using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Cli;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Ledger;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Plan;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Target;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Tests;

/// <summary>対象 API はスタブ、ソースは合成データ。実環境へは接続しない。</summary>
public sealed class PlanTests : IDisposable
{
    private const string Key = "plan-test-secret-key";
    private const string Base = "https://pleasanter.example.invalid/sub";

    private readonly string root = Path.Combine(Path.GetTempPath(), "em-plan-" + Guid.NewGuid().ToString("N"));

    public PlanTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Put(string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private InventoryReport Inventory() => new InventoryEngine(new LocalSourceProvider(root), "s", InventoryRules.Empty).Run();

    private static ExtensionRecord Rec(int id, string type, string name, string body, string settings = "{}", int? tenant = 1, bool disabled = true) =>
        new(id, tenant, type, name, ExtensionsApiClient.SettingsHash(JsonNode.Parse(settings)), ExtensionConverter.Sha256(body), disabled);

    private static PlanReport Build(InventoryReport inventory, IReadOnlyList<ExtensionRecord> records, LedgerFile? ledger = null) =>
        PlanBuilder.Build(inventory, records, ledger ?? LedgerFile.Empty("s"), Base, 1, DateTimeOffset.UnixEpoch);

    private static PlanEntry Find(PlanReport plan, string path) => plan.Entries.Single(e => e.SourceKey == "s:" + path);

    [Fact]
    public void 対象に無ければ無効で作成する予定になり_空名も区別する()
    {
        Put("ExtendedScripts/a.js", "1");
        Put("ExtendedSqls/n.json", """{"CommandText":"select 1"}""");

        var plan = Build(Inventory(), []);

        Assert.Equal(PlanAction.CreateDisabled, Find(plan, "ExtendedScripts/a.js").Action);
        Assert.Equal("new", Find(plan, "ExtendedScripts/a.js").Reason);
        Assert.Equal("new-empty-name", Find(plan, "ExtendedSqls/n.json").Reason);
        Assert.Equal(2, plan.ActionCounts["CreateDisabled"]);
        Assert.Equal(["Script"], plan.ExecutionUnits.Keys.Where(k => k == "Script"));
    }

    [Fact]
    public void 台帳の対応先が一致すれば_InSync_で_何もしない()
    {
        Put("ExtendedScripts/a.js", "1");
        var ledger = LedgerFile.Empty("s");
        ledger.Entries.Add(new LedgerEntry("s:ExtendedScripts/a.js", 7));

        var plan = Build(Inventory(), [Rec(7, "Script", "a.js", "1")], ledger);

        Assert.Equal(PlanAction.InSync, Find(plan, "ExtendedScripts/a.js").Action);
        Assert.Equal(7, Find(plan, "ExtendedScripts/a.js").ExtensionId);
        Assert.Equal(0, plan.UnmanagedTargetRecords);
    }

    [Fact]
    public void 台帳の対応先が食い違う_または対象に無ければ_Conflict()
    {
        Put("ExtendedScripts/a.js", "1");
        Put("ExtendedScripts/b.js", "2");
        var ledger = LedgerFile.Empty("s");
        ledger.Entries.Add(new LedgerEntry("s:ExtendedScripts/a.js", 7));
        ledger.Entries.Add(new LedgerEntry("s:ExtendedScripts/b.js", 8));

        var plan = Build(Inventory(), [Rec(7, "Script", "a.js", "changed in target")], ledger);

        Assert.Equal("registered-record-differs", Find(plan, "ExtendedScripts/a.js").Reason);
        Assert.Equal("ledger-id-missing-in-target", Find(plan, "ExtendedScripts/b.js").Reason);
        Assert.Equal(2, plan.ActionCounts["Conflict"]);
    }

    [Fact]
    public void 意図だけ記録されて_ID_が未確定なら_作成せず_Conflict()
    {
        Put("ExtendedScripts/a.js", "1");
        var ledger = LedgerFile.Empty("s");
        ledger.Entries.Add(new LedgerEntry("s:ExtendedScripts/a.js", null));

        var plan = Build(Inventory(), [], ledger);

        Assert.Equal("ledger-id-unknown", Find(plan, "ExtendedScripts/a.js").Reason);
        Assert.Equal(PlanAction.Conflict, Find(plan, "ExtendedScripts/a.js").Action);
    }

    [Fact]
    public void 台帳に無い既存レコードは_自動で上書きせず_Conflict_にする()
    {
        Put("ExtendedScripts/same.js", "1");
        Put("ExtendedScripts/named.js", "2");
        Put("ExtendedSqls/empty.json", """{"CommandText":"x"}""");

        var plan = Build(
            Inventory(),
            [
                Rec(1, "Script", "same.js", "1"),
                Rec(2, "Script", "named.js", "different"),
                Rec(3, "Sql", string.Empty, "other"),
                Rec(4, "Style", "unrelated.css", "z"),
            ]);

        Assert.Equal("unmanaged-record-same-content", Find(plan, "ExtendedScripts/same.js").Reason);
        Assert.Equal("unmanaged-record-same-name", Find(plan, "ExtendedScripts/named.js").Reason);
        Assert.Equal("unmanaged-record-empty-name", Find(plan, "ExtendedSqls/empty.json").Reason);
        Assert.Equal(4, plan.UnmanagedTargetRecords);
    }

    [Fact]
    public void 他のテナントのレコードは照合に使わず_数だけ出す()
    {
        Put("ExtendedScripts/a.js", "1");

        var plan = Build(Inventory(), [Rec(1, "Script", "a.js", "1", tenant: 2)]);

        Assert.Equal(PlanAction.CreateDisabled, Find(plan, "ExtendedScripts/a.js").Action);
        Assert.Equal(1, plan.Target.RecordsInOtherTenants);
        Assert.Equal(0, plan.Target.RecordsInTenant);
    }

    [Fact]
    public void 要確認は_Blocked_保持と置換は_Retain_で_対象には何もしない()
    {
        Put("ExtendedScripts/readme.txt", "x");
        Put("ExtendedColumns/c.json", "{}");

        var plan = Build(Inventory(), [Rec(1, "Script", "readme.txt", "x")]);

        Assert.Equal(PlanAction.Blocked, Find(plan, "ExtendedScripts/readme.txt").Action);
        Assert.Equal(PlanAction.Retain, Find(plan, "ExtendedColumns/c.json").Action);
    }

    [Fact]
    public void ソースが無い_対象にだけあるレコードは_削除せず_管理外として数える()
    {
        Put("ExtendedScripts/a.js", "1");

        var plan = Build(Inventory(), [Rec(1, "Script", "gone.js", "x"), Rec(2, "Style", "s.css", "y")]);

        Assert.Equal(2, plan.UnmanagedTargetRecords);
        Assert.DoesNotContain(plan.Entries, e => e.ExtensionId is 1 or 2);
    }

    [Fact]
    public void plan_の_ID_は内容から決まり_作成日時では変わらない_本文を含まない()
    {
        Put("ExtendedScripts/a.js", "TOP-SECRET-BODY");
        var inventory = Inventory();

        var first = PlanBuilder.Build(inventory, [], LedgerFile.Empty("s"), Base, 1, DateTimeOffset.UnixEpoch);
        var second = PlanBuilder.Build(inventory, [], LedgerFile.Empty("s"), Base, 1, DateTimeOffset.UtcNow);
        Put("ExtendedScripts/b.js", "2");
        var third = PlanBuilder.Build(Inventory(), [], LedgerFile.Empty("s"), Base, 1, DateTimeOffset.UnixEpoch);

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.NotEqual(first.PlanId, third.PlanId);
        var json = PlanBuilder.Serialize(first);
        Assert.DoesNotContain("TOP-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, json, StringComparison.Ordinal);
    }

    // --- 台帳 ---

    [Fact]
    public void 台帳は保存して読み直せ_壊れた_切れた_改ざんは拒否する()
    {
        var path = Path.Combine(root, "ledger.json");
        var ledger = LedgerFile.Empty("s");
        ledger.Entries.Add(new LedgerEntry("s:ExtendedScripts/a.js", 5));
        ledger.Events.Add(new JournalEvent(1, DateTimeOffset.UnixEpoch, "s:ExtendedScripts/a.js", JournalEventType.IntentRecorded, null));
        ledger.Save(path);

        var loaded = LedgerFile.Load(path, "s");
        Assert.Equal(5, loaded.FindExtensionId("s:ExtendedScripts/a.js"));
        Assert.Single(loaded.Events);

        var text = File.ReadAllText(path);
        File.WriteAllText(path, text[..(text.Length / 2)]);
        Assert.Throws<LedgerCorruptException>(() => LedgerFile.Load(path, "s"));

        File.WriteAllText(path, text.Replace("\"extensionId\": 5", "\"extensionId\": 6", StringComparison.Ordinal));
        Assert.Throws<LedgerCorruptException>(() => LedgerFile.Load(path, "s"));

        File.WriteAllText(path, text);
        Assert.Throws<LedgerCorruptException>(() => LedgerFile.Load(path, "other"));
    }

    [Fact]
    public void 台帳が無ければ空_保存に失敗しても前の内容を壊さない()
    {
        var path = Path.Combine(root, "ledger.json");
        Assert.Empty(LedgerFile.Load(path, "s").Entries);

        var ledger = LedgerFile.Empty("s");
        ledger.Entries.Add(new LedgerEntry("s:a", 1));
        ledger.Save(path);
        var before = File.ReadAllText(path);

        // 置き換え先をフォルダにして、保存を失敗させる
        var blocked = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blocked);
        Assert.ThrowsAny<Exception>(() => ledger.Save(blocked));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(root, ".*.tmp"));
    }

    // --- 対象 URL と API ---

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("https://example.com/sub/path", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("http://example.com", false)]
    [InlineData("https://user:pw@example.com", false)]
    [InlineData("https://example.com/?k=v", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not a url", false)]
    public void 接続先は_HTTPS_かローカルの_HTTP_だけ(string url, bool ok) =>
        Assert.Equal(ok, TargetUrl.Validate(url) is null);

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

    private static ExtensionsApiClient Api(Func<HttpRequestMessage, HttpResponseMessage> respond, out CountingHandler handler)
    {
        handler = new CountingHandler(respond);
        return new ExtensionsApiClient(new PleasanterApiClient(new HttpClient(handler), new PleasanterOptions { BaseUrl = Base, ApiKey = Key }));
    }

    [Fact]
    public async Task 一覧は_200_件を超えても返った分をすべて読み_本文はハッシュにする()
    {
        var api = Api(_ => Json(HttpStatusCode.OK, DataJson(250)), out var handler);

        var result = await api.ListAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(250, result.Records.Count);
        Assert.Equal(ExtensionConverter.Sha256("b250"), result.Records[249].BodySha256);
        Assert.Equal($"{Base}/api/extensions/Get", handler.Uris.Single());
        Assert.Contains(Key, handler.Bodies.Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", PleasanterErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.OK, """{"StatusCode":403,"Message":"plan-test-secret-key leaked?"}""", PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.OK, """{"StatusCode":401}""", PleasanterErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, "{}", PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.OK, """{"StatusCode":200,"Response":{}}""", PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.OK, """{"StatusCode":200,"Response":{"Data":[{"Name":"no id"}]}}""", PleasanterErrorKind.Permanent)]
    public async Task 認証失敗_権限不足_許可無効_不正な応答は失敗にし_応答の本文を出さない(HttpStatusCode status, string body, PleasanterErrorKind kind)
    {
        var api = Api(_ => Json(status, body), out _);

        var result = await api.ListAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(kind, result.ErrorKind);
        Assert.DoesNotContain(Key, result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 通信できなければ_Unknown_リダイレクトは辿らず失敗にする()
    {
        var down = Api(_ => throw new HttpRequestException("boom " + Key), out _);
        var downResult = await down.ListAsync();
        Assert.Equal(PleasanterErrorKind.Unknown, downResult.ErrorKind);
        Assert.DoesNotContain(Key, downResult.FailureReason, StringComparison.Ordinal);

        var redirect = Api(
            _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://evil.example.invalid/");
                return r;
            },
            out var handler);
        var redirectResult = await redirect.ListAsync();
        Assert.False(redirectResult.IsSuccess);
        Assert.Contains("redirect", redirectResult.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Uris);
    }

    // --- plan コマンド ---

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
