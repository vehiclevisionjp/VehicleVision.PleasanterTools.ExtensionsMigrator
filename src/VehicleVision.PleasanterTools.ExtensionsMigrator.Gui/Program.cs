using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Gui;

// --smoke: ウィンドウを開いて数秒後に閉じ、終了コード 0 で終わる（起動の確認用）
var smoke = args.Contains("--smoke", StringComparer.Ordinal);
var exitCode = AppBuilder.Configure(() => new App(smoke))
    .UsePlatformDetect()
    .StartWithClassicDesktopLifetime(args);
if (smoke)
{
    Console.Out.WriteLine($"Smoke finished with exit code {exitCode}.");
}

return exitCode;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Gui
{
    /// <summary>アプリケーション。テーマだけを設定する（XAML は使わない）。</summary>
    internal sealed class App(bool smoke) : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow(smoke);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
