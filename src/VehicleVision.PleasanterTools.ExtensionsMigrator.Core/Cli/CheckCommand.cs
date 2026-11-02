namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Cli;

/// <summary>接続先へ届き、API キーが拒否されないことを確かめる。何も書かない。</summary>
public sealed class CheckCommand(Func<HttpClient> httpClientFactory) : ICommand
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public string Name => "check";

    public string Summary => "Check that the Pleasanter API is reachable and the API key is accepted.";

    public IReadOnlySet<string> ValueOptions => None;

    public IReadOnlySet<string> FlagOptions => None;

    public async Task<int> RunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        PleasanterOptions options;
        try
        {
            options = PleasanterOptions.FromEnvironment(context.GetEnvironmentVariable);
        }
        catch (InvalidOperationException ex)
        {
            context.Error.WriteLine(ex.Message);
            return ExitCodes.UsageError;
        }

        using var httpClient = httpClientFactory();
        var client = new PleasanterApiClient(httpClient, options);
        var connected = await client.CheckConnectionAsync(cancellationToken).ConfigureAwait(false);
        context.Output.WriteLine(connected ? "Connected." : "Connection failed.");
        return connected ? ExitCodes.Success : ExitCodes.Failure;
    }
}
