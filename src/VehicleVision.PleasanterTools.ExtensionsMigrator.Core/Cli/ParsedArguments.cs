namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Cli;

/// <summary>解析済みのコマンドライン引数。</summary>
public sealed class ParsedArguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);

    /// <summary>誤りがあればその説明。無ければ <c>null</c>。</summary>
    public string? Error { get; private set; }

    /// <summary>位置引数（コマンド名を除く）。</summary>
    public List<string> Positionals { get; } = [];

    /// <summary>値を取るオプションの値。無ければ <c>null</c>。</summary>
    public string? Value(string name) => values.GetValueOrDefault(name);

    /// <summary>フラグが指定されたか。</summary>
    public bool Flag(string name) => flags.Contains(name);

    /// <summary>
    /// 引数を解析する。<c>--name value</c> と <c>--name=value</c> の両方を受ける。
    /// 仕様に無いオプション、値の欠落、重複は <see cref="Error"/> にする（黙って無視しない）。
    /// </summary>
    public static ParsedArguments Parse(
        IEnumerable<string> args,
        IReadOnlySet<string> valueOptions,
        IReadOnlySet<string> flagOptions)
    {
        var result = new ParsedArguments();
        using var enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var arg = enumerator.Current;
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(arg);
                continue;
            }

            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? arg : arg[..separator];
            string? inlineValue = separator < 0 ? null : arg[(separator + 1)..];

            if (flagOptions.Contains(name))
            {
                if (inlineValue is not null)
                {
                    return result.Fail($"Option {name} does not take a value.");
                }

                result.flags.Add(name);
                continue;
            }

            if (!valueOptions.Contains(name))
            {
                return result.Fail($"Unknown option: {name}");
            }

            var value = inlineValue;
            if (value is null)
            {
                if (!enumerator.MoveNext())
                {
                    return result.Fail($"Option {name} requires a value.");
                }

                value = enumerator.Current;
            }

            if (!result.values.TryAdd(name, value))
            {
                return result.Fail($"Option {name} was specified more than once.");
            }
        }

        return result;
    }

    private ParsedArguments Fail(string message)
    {
        Error = message;
        return this;
    }
}
