using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Cli;

/// <summary><c>inventory</c> と <c>validate</c> が共有する、ソースと規則の読み込み。</summary>
internal static partial class SourceOptions
{
    internal static readonly IReadOnlySet<string> Values = new HashSet<string> { "--source", "--source-id", "--rules" };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SourceIdPattern();

    /// <summary>エンジンを作る。引数の誤りは <c>error</c> に説明を入れて <c>null</c> を返す。</summary>
    internal static InventoryEngine? CreateEngine(ParsedArguments arguments, out LocalSourceProvider? provider, out string? error)
    {
        provider = null;
        var sourcePath = arguments.Value("--source");
        if (string.IsNullOrEmpty(sourcePath))
        {
            error = "Option --source <directory> is required.";
            return null;
        }

        var sourceId = arguments.Value("--source-id") ?? "local";
        if (!SourceIdPattern().IsMatch(sourceId))
        {
            error = "Option --source-id may contain only letters, digits, '.', '_' and '-' (up to 64 characters).";
            return null;
        }

        var rules = InventoryRules.Empty;
        var rulesPath = arguments.Value("--rules");
        if (rulesPath is not null)
        {
            try
            {
                rules = InventoryRules.Parse(File.ReadAllText(rulesPath));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                error = ex is FormatException ? ex.Message : "The rules file could not be read.";
                return null;
            }
        }

        try
        {
            provider = new LocalSourceProvider(sourcePath);
        }
        catch (DirectoryNotFoundException)
        {
            error = "The source directory does not exist.";
            return null;
        }

        error = null;
        return new InventoryEngine(provider, sourceId, rules);
    }
}

/// <summary>ソースを棚卸しして、分類を出す。何も書かない（<c>--out</c> の出力先だけ）。</summary>
public sealed class InventoryCommand : ICommand
{
    private static readonly IReadOnlySet<string> OutValues = new HashSet<string>(SourceOptions.Values) { "--out" };
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public string Name => "inventory";

    public string Summary => "List the extension files under a Parameters directory and classify them (read-only).";

    public IReadOnlySet<string> ValueOptions => OutValues;

    public IReadOnlySet<string> FlagOptions => None;

    public Task<int> RunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var engine = SourceOptions.CreateEngine(context.Arguments, out var provider, out var error);
        if (engine is null || provider is null)
        {
            context.Error.WriteLine(error);
            return Task.FromResult(ExitCodes.UsageError);
        }

        var outPath = context.Arguments.Value("--out");
        if (outPath is not null && IsUnder(Path.GetFullPath(outPath), provider.RootPath))
        {
            context.Error.WriteLine("Option --out must not point inside the source directory.");
            return Task.FromResult(ExitCodes.UsageError);
        }

        var report = engine.Run();
        InventoryText.Write(context.Output, report);
        if (outPath is not null)
        {
            InventoryJson.WriteAtomic(outPath, InventoryJson.Serialize(report));
            context.Output.WriteLine("Inventory written.");
        }

        return Task.FromResult(ExitCodes.Success);
    }

    internal static bool IsUnder(string fullPath, string root) =>
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>棚卸しをやり直し、変換できないもの・重複・（指定があれば）前回の棚卸しとのずれを検査する。何も書かない。</summary>
public sealed class ValidateCommand : ICommand
{
    private static readonly IReadOnlySet<string> InventoryValues = new HashSet<string>(SourceOptions.Values) { "--inventory" };
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public string Name => "validate";

    public string Summary => "Validate the conversion and, optionally, compare with a saved inventory (read-only).";

    public IReadOnlySet<string> ValueOptions => InventoryValues;

    public IReadOnlySet<string> FlagOptions => None;

    public Task<int> RunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var engine = SourceOptions.CreateEngine(context.Arguments, out _, out var error);
        if (engine is null)
        {
            context.Error.WriteLine(error);
            return Task.FromResult(ExitCodes.UsageError);
        }

        var report = engine.Run();
        var errors = report.Diagnostics.Count(d => d.Severity == "error");
        var warnings = report.Diagnostics.Count(d => d.Severity == "warning");
        foreach (var diagnostic in report.Diagnostics)
        {
            context.Output.WriteLine($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.SourceKey}: {diagnostic.Message}");
        }

        var inventoryPath = context.Arguments.Value("--inventory");
        if (inventoryPath is not null)
        {
            InventoryReport saved;
            try
            {
                saved = InventoryJson.Deserialize(File.ReadAllText(inventoryPath));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                context.Error.WriteLine(ex is FormatException ? ex.Message : "The inventory file could not be read.");
                return Task.FromResult(ExitCodes.UsageError);
            }

            foreach (var drift in InventoryComparer.Compare(saved, report))
            {
                context.Output.WriteLine($"error: {drift}");
                errors++;
            }
        }

        context.Output.WriteLine($"Validation finished: {errors} error(s), {warnings} warning(s).");
        return Task.FromResult(errors == 0 ? ExitCodes.Success : ExitCodes.Failure);
    }
}

/// <summary>人が読む要約。ファイルの本文・パスの一覧は出さず、件数と分類だけ。</summary>
internal static class InventoryText
{
    internal static void Write(TextWriter output, InventoryReport report)
    {
        output.WriteLine($"Source: {report.SourceId}  Rule set: {report.RuleSet}");
        output.WriteLine($"Settings: {report.Summary.Units}  Files: {report.Summary.Files}");
        foreach (var pair in report.Summary.UnitsByClassification)
        {
            output.WriteLine($"  {pair.Key,-12} settings {pair.Value,4}  files {report.Summary.FilesByClassification[pair.Key],4}");
        }

        var errors = report.Diagnostics.Count(d => d.Severity == "error");
        var warnings = report.Diagnostics.Count(d => d.Severity == "warning");
        output.WriteLine($"Diagnostics: {errors} error(s), {warnings} warning(s). Run 'validate' for details.");
    }
}
