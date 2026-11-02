using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Cli;

var app = new CommandLineApp(
[
    new CheckCommand(() => new HttpClient()),
    new NotImplementedCommand("inventory", "List the extension files and classify them."),
    new NotImplementedCommand("validate", "Validate the conversion, hashes and scope."),
    new NotImplementedCommand("plan", "Create a read-only plan against the target API."),
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
