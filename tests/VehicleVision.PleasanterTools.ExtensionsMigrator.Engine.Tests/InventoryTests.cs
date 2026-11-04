using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Tests;

/// <summary>合成データだけで、棚卸し・変換を試す。実環境の内容は使わない。</summary>
public sealed class InventoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "em-test-" + Guid.NewGuid().ToString("N"));

    public InventoryTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Put(string relative, string text, bool bom = false)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        File.WriteAllBytes(path, bom ? [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. bytes] : bytes);
    }

    private InventoryReport Run(InventoryRules? rules = null) =>
        new InventoryEngine(new LocalSourceProvider(root), "src1", rules ?? InventoryRules.Empty).Run();

    private static InventoryEntry Entry(InventoryReport report, string path) =>
        report.Entries.Single(e => e.SourceKey == "src1:" + path);

    [Fact]
    public void Script_と_Style_は拡張子込みのファイル名で移行対象になる()
    {
        Put("ExtendedScripts/sub/a.js", "var x = '日本語';\r\nvar y;\r\n", bom: true);
        Put("ExtendedStyles/b.css", "a { }\n");

        var report = Run();

        var script = Entry(report, "ExtendedScripts/sub/a.js");
        Assert.Equal(Classification.Migrate, script.Classification);
        Assert.Equal("Script", script.ExtensionType);
        Assert.Equal("a.js", script.Name);
        Assert.True(script.Bom);
        Assert.Equal("crlf", script.LineEnding);
        Assert.Equal(ExtensionConverter.Sha256("var x = '日本語';\r\nvar y;\r\n"), script.BodySha256);
        Assert.Equal("lf", Entry(report, "ExtendedStyles/b.css").LineEnding);
    }

    [Fact]
    public void Sql_は併設本文があれば空でもそれを優先する()
    {
        Put("ExtendedSqls/inline.json", """{"Name":"inline","CommandText":"select 1","Api":true}""");
        Put("ExtendedSqls/both.json", """{"Name":"both","CommandText":"select 1"}""");
        Put("ExtendedSqls/both.json.sql", "select 2");
        Put("ExtendedSqls/emptycar.json", """{"Name":"empty","CommandText":"select 3"}""");
        Put("ExtendedSqls/emptycar.json.sql", "");

        var report = Run();

        Assert.Equal("inline", Entry(report, "ExtendedSqls/inline.json").BodySource);
        var both = Entry(report, "ExtendedSqls/both.json");
        Assert.Equal("sidecar", both.BodySource);
        Assert.Equal(2, both.Files.Count);
        Assert.Equal(ExtensionConverter.Sha256("select 2"), both.BodySha256);
        var empty = Entry(report, "ExtendedSqls/emptycar.json");
        Assert.Equal("sidecar", empty.BodySource);
        Assert.Equal(ExtensionConverter.Sha256(string.Empty), empty.BodySha256);
    }

    [Fact]
    public void 変換は名前なし_元の_Disabled_未知の設定を保ち_本文のキーだけを分ける()
    {
        const string json = """{"Zeta":[3,1],"Disabled":true,"Description":"d","UnknownSetting":{"b":1,"a":2},"Body":"x"}""";

        var converted = ExtensionConverter.FromJsonDefinition("ServerScript", json, null);

        Assert.Equal(string.Empty, converted.ExtensionName);
        Assert.True(converted.Disabled);
        Assert.Equal("d", converted.Description);
        Assert.Equal("x", converted.Body);
        var settings = JsonNode.Parse(converted.ExtensionSettings)!.AsObject();
        Assert.False(settings.ContainsKey("Body"));
        Assert.Equal("""{"Description":"d","Disabled":true,"UnknownSetting":{"a":2,"b":1},"Zeta":[3,1]}""", converted.ExtensionSettings);
    }

    [Fact]
    public void NavigationMenu_は本文を使わず設定をそのまま保つ()
    {
        var converted = ExtensionConverter.FromJsonDefinition("NavigationMenu", """{"Name":"m","Body":"keep"}""", null);

        Assert.Equal(string.Empty, converted.Body);
        Assert.Equal("none", converted.BodySource);
        Assert.Contains("\"Body\":\"keep\"", converted.ExtensionSettings, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HtmlBodyBottom_ja.html", "HtmlBodyBottom", "ja")]
    [InlineData("Foo_Bar_en.html", "Foo_Bar", "en")]
    [InlineData("Plain.html", "Plain", null)]
    [InlineData("Trailing_.html", "Trailing_", null)]
    public void Html_は挿入位置と言語をファイル名から分ける(string file, string name, string? language)
    {
        var converted = ExtensionConverter.FromHtmlFile(file, "<p/>");

        Assert.Equal(name, converted.ExtensionName);
        Assert.Equal(language, converted.Language);
    }

    [Fact]
    public void 空の_Html_は読み込まれないので保持にする()
    {
        Put("ExtendedHtmls/Empty_ja.html", "");

        var entry = Entry(Run(), "ExtendedHtmls/Empty_ja.html");

        Assert.Equal(Classification.Keep, entry.Classification);
        Assert.Equal("empty-html-not-loaded", entry.Reason);
    }

    [Fact]
    public void 孤立本文_未知の拡張子_不正なJSON_不正なUTF8は要確認にする()
    {
        Put("ExtendedSqls/orphan.json.sql", "select 1");
        Put("ExtendedScripts/readme.txt", "x");
        Put("ExtendedServerScripts/bad.json", "{ not json");
        Directory.CreateDirectory(Path.Combine(root, "ExtendedStyles"));
        File.WriteAllBytes(Path.Combine(root, "ExtendedStyles", "bin.css"), [0xFF, 0xFE, 0x41]);

        var report = Run();

        Assert.Equal("orphan-sidecar", Entry(report, "ExtendedSqls/orphan.json.sql").Reason);
        Assert.Equal("unknown-extension", Entry(report, "ExtendedScripts/readme.txt").Reason);
        Assert.Equal("invalid-definition", Entry(report, "ExtendedServerScripts/bad.json").Reason);
        Assert.Equal("invalid-utf8", Entry(report, "ExtendedStyles/bin.css").Reason);
        Assert.All(report.Entries, e => Assert.Equal(Classification.NeedsReview, e.Classification));
        Assert.Equal(4, report.Diagnostics.Count(d => d.Severity == "error"));
    }

    [Fact]
    public void 対応しないフォルダは保持にし_Fields_と_Plugin_は未確認の理由を付ける()
    {
        Put("ExtendedColumnDefinitions/x.json", "{}");
        Put("ExtendedFields/f.json", "{}");
        Put("ExtendedPlugins/p.json", "{}");
        Put("OtherFolder/ignored.json", "{}");

        var report = Run();

        Assert.Equal("no-extensions-type", Entry(report, "ExtendedColumnDefinitions/x.json").Reason);
        Assert.Equal("type-not-verified", Entry(report, "ExtendedFields/f.json").Reason);
        Assert.Equal("type-not-verified", Entry(report, "ExtendedPlugins/p.json").Reason);
        Assert.All(report.Entries, e => Assert.Equal(Classification.Keep, e.Classification));
        Assert.Equal(3, report.Entries.Count);
    }

    [Fact]
    public void 利用者の規則で保持_対象外_置換を指定できる()
    {
        Put("ExtendedScripts/keep_me.js", "1");
        Put("ExtendedScripts/skip/a.js", "2");
        Put("ExtendedStyles/old-gen.css", "3");
        Put("ExtendedStyles/plain.css", "4");
        var rules = InventoryRules.Parse("""
            {"formatVersion":1,"rules":[
              {"match":"ExtendedScripts/keep_*.js","classification":"keep","reason":"sample"},
              {"match":"ExtendedScripts/skip/**","classification":"exclude"},
              {"match":"ExtendedStyles/old-*.css","classification":"replace","reason":"regenerated"}]}
            """);

        var report = Run(rules);

        Assert.Equal(Classification.Keep, Entry(report, "ExtendedScripts/keep_me.js").Classification);
        Assert.Equal("sample", Entry(report, "ExtendedScripts/keep_me.js").RuleNote);
        Assert.Equal(Classification.Exclude, Entry(report, "ExtendedScripts/skip/a.js").Classification);
        Assert.Equal(Classification.Replace, Entry(report, "ExtendedStyles/old-gen.css").Classification);
        Assert.Equal(Classification.Migrate, Entry(report, "ExtendedStyles/plain.css").Classification);
        Assert.Equal(1, report.Summary.UnitsByClassification["Migrate"]);
        Assert.Equal(1, report.Summary.UnitsByClassification["Replace"]);
    }

    [Theory]
    [InlineData("""{"formatVersion":2,"rules":[]}""")]
    [InlineData("""{"formatVersion":1,"rules":[{"match":"a","classification":"delete"}]}""")]
    [InlineData("""{"formatVersion":1,"rules":[{"classification":"keep"}]}""")]
    [InlineData("not json")]
    public void 不正な規則ファイルは拒否する(string json) =>
        Assert.Throws<FormatException>(() => InventoryRules.Parse(json));

    [Fact]
    public void 同名と空名は指摘に出すが分類は変えない()
    {
        Put("ExtendedSqls/a.json", """{"Name":"same","CommandText":"1"}""");
        Put("ExtendedSqls/sub/b.json", """{"Name":"same","CommandText":"2"}""");
        Put("ExtendedSqls/c.json", """{"CommandText":"3"}""");

        var report = Run();

        Assert.Equal(2, report.Diagnostics.Count(d => d.Code == "duplicate-name"));
        Assert.Equal(1, report.Diagnostics.Count(d => d.Code == "empty-name"));
        Assert.All(report.Entries, e => Assert.Equal(Classification.Migrate, e.Classification));
    }

    [Fact]
    public void 出力に本文を含めない()
    {
        Put("ExtendedScripts/s.js", "const token = 'TOP-SECRET-BODY';");
        Put("ExtendedSqls/q.json", """{"Name":"q","CommandText":"select 'TOP-SECRET-SQL'"}""");

        var json = InventoryJson.Serialize(Run());

        Assert.DoesNotContain("TOP-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
    }

    [Fact]
    public void 棚卸しの_JSON_は往復できる()
    {
        Put("ExtendedScripts/s.js", "1");

        var report = Run();
        var restored = InventoryJson.Deserialize(InventoryJson.Serialize(report));

        Assert.Empty(InventoryComparer.Compare(restored, report));
        Assert.Equal(InventoryReport.CurrentFormatVersion, restored.FormatVersion);
    }

    [Fact]
    public void ルートの外へ出るパスは読まない()
    {
        var provider = new LocalSourceProvider(root);

        Assert.Throws<InvalidOperationException>(() => provider.Read("../outside.txt"));
        Assert.Throws<InvalidOperationException>(() => provider.Read("ExtendedScripts/../../outside.txt"));
    }

    [Fact]
    public void リンクは辿らず要確認にする()
    {
        var target = Path.Combine(Path.GetTempPath(), "em-link-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "x.js"), "1");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ExtendedScripts"));
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "ExtendedScripts", "linked"), target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // この環境ではリンクを作れない
            }

            var report = Run();

            var entry = Assert.Single(report.Entries);
            Assert.Equal("link-or-reparse-point", entry.Reason);
            Assert.Equal(Classification.NeedsReview, entry.Classification);
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
