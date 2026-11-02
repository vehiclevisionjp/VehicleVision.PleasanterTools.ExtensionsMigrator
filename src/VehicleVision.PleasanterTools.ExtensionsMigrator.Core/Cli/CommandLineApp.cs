using System.Reflection;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Cli;

/// <summary>コマンドを振り分ける入口。画面への出力と環境変数は差し替えられる。</summary>
public sealed class CommandLineApp(IReadOnlyList<ICommand> commands)
{
    /// <summary>実行する。</summary>
    public async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, string?> getEnvironmentVariable,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            WriteHelp(args.Length == 0 ? error : output);
            return args.Length == 0 ? ExitCodes.UsageError : ExitCodes.Success;
        }

        if (args[0] == "--version")
        {
            output.WriteLine(Version());
            return ExitCodes.Success;
        }

        var command = commands.FirstOrDefault(c => string.Equals(c.Name, args[0], StringComparison.Ordinal));
        if (command is null)
        {
            error.WriteLine($"Unknown command: {args[0]}");
            WriteHelp(error);
            return ExitCodes.UsageError;
        }

        var parsed = ParsedArguments.Parse(args.Skip(1), command.ValueOptions, command.FlagOptions);
        if (parsed.Error is not null)
        {
            error.WriteLine(parsed.Error);
            return ExitCodes.UsageError;
        }

        var context = new CommandContext(parsed, output, error, getEnvironmentVariable);
        return await command.RunAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown";

    private void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: extensions-migrator <command> [options]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        foreach (var command in commands)
        {
            writer.WriteLine($"  {command.Name,-10} {command.Summary}");
        }

        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success, 1 failure, 2 usage or configuration error.");
        writer.WriteLine("The API key is read from the environment (PLEASANTER_API_KEY) and is never printed.");
    }
}
