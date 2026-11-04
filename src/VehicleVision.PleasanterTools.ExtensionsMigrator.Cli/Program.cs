using VehicleVision.PleasanterTools.ExtensionsMigrator.Cli;

var app = new CommandLineApp(
[
    new CheckCommand(CreateHttpClient),
    new InventoryCommand(),
    new ValidateCommand(),
    new PlanCommand(CreateHttpClient),
]);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return await app.RunAsync(
        args,
        Console.Out,
        Console.Error,
        Environment.GetEnvironmentVariable,
        cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Canceled.");
    return ExitCodes.Failure;
}

// API キーはリクエストのボディに載せるため、リダイレクトは辿らない（別ホストへ漏らさない）
static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });
