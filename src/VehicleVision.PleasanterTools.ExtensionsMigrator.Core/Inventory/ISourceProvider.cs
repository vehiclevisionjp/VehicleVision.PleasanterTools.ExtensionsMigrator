namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

/// <summary>ソース上の 1 つの項目。</summary>
/// <param name="RelativePath">ルートからの相対パス（区切りは <c>/</c>）。</param>
/// <param name="Length">バイト数。リンクなら 0。</param>
/// <param name="IsLink">シンボリックリンク・reparse point（辿らず、読まない）。</param>
public sealed record SourceItem(string RelativePath, long Length, bool IsLink);

/// <summary>ソースの読み取り口。最初はローカルのファイル。Azure（Kudu）などは別の実装として足す。</summary>
public interface ISourceProvider
{
    /// <summary><c>Extended*</c> で始まる最上位のフォルダの下にある項目を、再帰して一覧する。</summary>
    IReadOnlyList<SourceItem> List();

    /// <summary>ファイルの内容を読む。ルートの外へ出るパスは拒否する。</summary>
    byte[] Read(string relativePath);
}
