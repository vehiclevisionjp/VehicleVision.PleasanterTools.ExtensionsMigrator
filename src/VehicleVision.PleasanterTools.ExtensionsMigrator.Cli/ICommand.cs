namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Cli;

/// <summary>コマンドの実行に渡す文脈。出力と環境変数を差し替えられるようにしてある。</summary>
/// <param name="Arguments">解析済みの引数。</param>
/// <param name="Output">標準出力。</param>
/// <param name="Error">標準エラー。</param>
/// <param name="GetEnvironmentVariable">環境変数の読み出し。</param>
public sealed record CommandContext(
    ParsedArguments Arguments,
    TextWriter Output,
    TextWriter Error,
    Func<string, string?> GetEnvironmentVariable);

/// <summary>コマンド。</summary>
public interface ICommand
{
    /// <summary>コマンド名。</summary>
    string Name { get; }

    /// <summary>一覧に出す 1 行の説明（英語）。</summary>
    string Summary { get; }

    /// <summary>値を取るオプション。</summary>
    IReadOnlySet<string> ValueOptions { get; }

    /// <summary>値を取らないオプション。</summary>
    IReadOnlySet<string> FlagOptions { get; }

    /// <summary>実行する。終了コードを返す。</summary>
    Task<int> RunAsync(CommandContext context, CancellationToken cancellationToken);
}

/// <summary>まだ実装していないコマンド。何も実行せず、終了コード 2 で終わる。</summary>
public sealed class NotImplementedCommand(string name, string summary) : ICommand
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public string Name => name;

    public string Summary => summary + " (not implemented yet)";

    public IReadOnlySet<string> ValueOptions => None;

    public IReadOnlySet<string> FlagOptions => None;

    public Task<int> RunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.Error.WriteLine($"The command '{name}' is not implemented yet. Nothing was done.");
        return Task.FromResult(ExitCodes.UsageError);
    }
}
