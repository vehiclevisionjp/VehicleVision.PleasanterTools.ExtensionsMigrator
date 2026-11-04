using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Target;

/// <summary>接続先 URL の検査。API キーをリクエストのボディに載せるため、平文の通信と別ホストへの転送を許さない。</summary>
public static class TargetUrl
{
    /// <summary>DotNetClient と同じ規則で検査する（https のみ。ループバックだけ http 可。資格情報・クエリ・フラグメントは不可）。</summary>
    /// <remarks>
    /// 通信の前に使い方の誤りとして止めるための事前検査。規則は DotNetClient の <see cref="PleasanterOptions.Validate"/> と同一にするため、
    /// 自前では持たない。説明に URL や API キーは含まれない。
    /// </remarks>
    /// <returns>問題があれば説明。無ければ <c>null</c>。</returns>
    public static string? Validate(string baseUrl)
    {
        try
        {
            new PleasanterOptions { BaseUrl = baseUrl, ApiKey = string.Empty }.Validate();
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>plan に残す識別情報。スキームとホスト（とポート・パス）だけで、秘密は含まない。</summary>
    public static string Describe(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
