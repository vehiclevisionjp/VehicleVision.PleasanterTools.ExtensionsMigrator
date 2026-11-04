namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Cli;

/// <summary>終了コード。既存の PleasanterTools の CLI と同じ（成功 0、失敗 1、引数・設定の誤り 2）。</summary>
public static class ExitCodes
{
    /// <summary>成功。</summary>
    public const int Success = 0;

    /// <summary>処理は動いたが、失敗・検査の指摘がある。</summary>
    public const int Failure = 1;

    /// <summary>引数・設定の誤り、または未実装のコマンド。何も実行していない。</summary>
    public const int UsageError = 2;
}
