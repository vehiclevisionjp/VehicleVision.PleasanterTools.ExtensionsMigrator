using System.Globalization;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Ledger;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Plan;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Target;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Cli;

/// <summary>ソースと対象の Extensions API（読み取りだけ）を照合し、変更を伴わない plan を出す。</summary>
public sealed class PlanCommand(Func<HttpClient> httpClientFactory, TimeProvider? clock = null) : ICommand
{
    private static readonly IReadOnlySet<string> Values =
        new HashSet<string>(SourceOptions.Values) { "--tenant-id", "--ledger", "--out" };

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public string Name => "plan";

    public string Summary => "Compare the source with the target Extensions API and write a read-only plan.";

    public IReadOnlySet<string> ValueOptions => Values;

    public IReadOnlySet<string> FlagOptions => None;

    public async Task<int> RunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var engine = SourceOptions.CreateEngine(context.Arguments, out var provider, out var error);
        if (engine is null || provider is null)
        {
            context.Error.WriteLine(error);
            return ExitCodes.UsageError;
        }

        if (!int.TryParse(context.Arguments.Value("--tenant-id"), NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId))
        {
            context.Error.WriteLine("Option --tenant-id <number> is required.");
            return ExitCodes.UsageError;
        }

        var outPath = context.Arguments.Value("--out");
        if (outPath is not null && InventoryCommand.IsUnder(Path.GetFullPath(outPath), provider.RootPath))
        {
            context.Error.WriteLine("Option --out must not point inside the source directory.");
            return ExitCodes.UsageError;
        }

        PleasanterOptions options;
        try
        {
            options = PleasanterEnvironment.FromEnvironment(context.GetEnvironmentVariable);
        }
        catch (InvalidOperationException ex)
        {
            context.Error.WriteLine(ex.Message);
            return ExitCodes.UsageError;
        }

        var urlProblem = TargetUrl.Validate(options.BaseUrl);
        if (urlProblem is not null)
        {
            context.Error.WriteLine(urlProblem);
            return ExitCodes.UsageError;
        }

        var report = engine.Run();
        LedgerFile ledger;
        try
        {
            var ledgerPath = context.Arguments.Value("--ledger");
            ledger = ledgerPath is null ? LedgerFile.Empty(report.SourceId) : LedgerFile.Load(ledgerPath, report.SourceId);
        }
        catch (LedgerCorruptException ex)
        {
            context.Error.WriteLine(ex.Message);
            return ExitCodes.Failure;
        }

        using var httpClient = httpClientFactory();
        var listed = await new ExtensionsApiClient(new PleasanterGateway(httpClient, options))
            .ListAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            context.Error.WriteLine(listed.FailureReason);
            return ExitCodes.Failure;
        }

        var plan = PlanBuilder.Build(report, listed.Records, ledger, options.BaseUrl, tenantId, (clock ?? TimeProvider.System).GetUtcNow());
        context.Output.WriteLine($"Plan {plan.PlanId[..12]}  Source: {plan.SourceId}  Target: {plan.Target.BaseUrl} (tenant {plan.Target.TenantId})");
        context.Output.WriteLine($"Target records read: {plan.Target.RecordsRead} (in tenant {plan.Target.RecordsInTenant}, other tenants {plan.Target.RecordsInOtherTenants})");
        foreach (var pair in plan.ActionCounts)
        {
            context.Output.WriteLine($"  {pair.Key,-14} {pair.Value,4}");
        }

        context.Output.WriteLine($"Target records not managed by this source: {plan.UnmanagedTargetRecords} (never deleted by the tool)");
        if (plan.ActionCounts["Conflict"] > 0 || plan.ActionCounts["Blocked"] > 0)
        {
            context.Output.WriteLine("The plan has conflicts or blocked settings. Resolve them before any change command.");
        }

        if (outPath is not null)
        {
            InventoryJson.WriteAtomic(outPath, PlanBuilder.Serialize(plan));
            context.Output.WriteLine("Plan written.");
        }

        return ExitCodes.Success;
    }
}
