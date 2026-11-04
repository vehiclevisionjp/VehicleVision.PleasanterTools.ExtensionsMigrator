using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine;

/// <summary>環境変数から Pleasanter の接続設定を作る。</summary>
/// <remarks>
/// DotNetClient の <c>PleasanterOptions.FromEnvironment()</c> は環境変数の読み出しを差し替えられず、
/// <c>PLEASANTER_SERVER_VERSION</c> も読むため、ここで作る。接続先の検査は DotNetClient が通信の前に行う。
/// </remarks>
public static class PleasanterEnvironment
{
    /// <summary>環境変数 <c>PLEASANTER_BASE_URL</c> / <c>PLEASANTER_API_KEY</c> から作る。</summary>
    public static PleasanterOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>環境変数の読み出しを差し替えて作る。</summary>
    public static PleasanterOptions FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return new PleasanterOptions
        {
            BaseUrl = Required(getEnvironmentVariable, "PLEASANTER_BASE_URL"),
            ApiKey = Required(getEnvironmentVariable, "PLEASANTER_API_KEY"),
        };
    }

    private static string Required(Func<string, string?> getEnvironmentVariable, string name) =>
        getEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is not set.");
}
