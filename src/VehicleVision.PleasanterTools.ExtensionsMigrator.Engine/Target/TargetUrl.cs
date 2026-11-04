namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Target;

/// <summary>接続先 URL の検査。API キーをリクエストのボディに載せるため、平文の通信と別ホストへの転送を許さない。</summary>
public static class TargetUrl
{
    /// <summary>HTTPS だけを許す。例外は、ローカルの検証環境（loopback）への HTTP。</summary>
    /// <returns>問題があれば説明。無ければ <c>null</c>。</returns>
    public static string? Validate(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "The base URL is not a valid absolute URL.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "The base URL must not contain credentials.";
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "The base URL must not contain a query or fragment.";
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return null;
        }

        return "The base URL must use HTTPS (HTTP is allowed only for a loopback host such as a local test environment).";
    }

    /// <summary>plan に残す識別情報。スキームとホスト（とポート・パス）だけで、秘密は含まない。</summary>
    public static string Describe(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
