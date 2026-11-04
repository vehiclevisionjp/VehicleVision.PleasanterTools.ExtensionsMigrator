using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Gui;

/// <summary>
/// 試作のウィンドウ。フォルダを選ぶと、Core の棚卸し（<c>inventory</c> と同じエンジン）の結果を表示する。
/// 読み取り専用で、変換・書き込み・API 呼び出しはしない。整形は Core（<see cref="InventoryView"/>）にあり、ここは並べるだけ。
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly TextBlock modeText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summaryText = new();
    private readonly ListBox list = new() { FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace") };

    public MainWindow(bool smoke)
    {
        Title = "ExtensionsMigrator（試作）";
        Width = 900;
        Height = 560;

        var pick = new Button { Content = "ソースのフォルダを選ぶ..." };
        pick.Click += async (_, _) => await PickFolderAsync();
        var demo = new Button { Content = "デモを表示（合成データ）" };
        demo.Click += (_, _) => Show(DemoInventory.Create(), "デモ: 合成データです。選んだフォルダの内容ではありません。");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(pick);
        buttons.Children.Add(demo);

        var header = new StackPanel { Spacing = 8, Margin = new Thickness(16, 16, 16, 8) };
        header.Children.Add(new TextBlock { Text = "拡張機能の棚卸し", FontSize = 20, FontWeight = FontWeight.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "試作です。選んだフォルダ（Parameters に相当する場所）を読み取り専用で棚卸しします。ファイルへは何も書かず、変換・登録・API 呼び出しもしません。本文・ハッシュ・フルパスは表示しません。",
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(buttons);
        header.Children.Add(modeText);
        header.Children.Add(summaryText);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new Border { Margin = new Thickness(16, 0, 16, 16), Child = list });
        Content = root;

        if (smoke)
        {
            // 起動確認用。合成データを実際のエンジンに通して表示し、数秒後に閉じる
            Show(DemoInventory.Create(), "デモ: 合成データです（--smoke）。");
            Opened += (_, _) =>
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Close();
                };
                timer.Start();
            };
        }
        else
        {
            modeText.Text = "(フォルダは未選択)";
        }
    }

    private void Show(InventoryView view, string mode)
    {
        modeText.Text = mode;
        summaryText.Text = $"設定 {view.Units} 件・ファイル {view.Files} 件  |  {view.SummaryLine()}  |  指摘: エラー {view.Errors}・警告 {view.Warnings}";
        list.ItemsSource = view.Rows.Select(InventoryView.FormatRow).ToList();
    }

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "ソースのフォルダ（Parameters に相当する場所）を選ぶ",
            AllowMultiple = false,
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }

        // 棚卸しは読み取りだけ。重い場合に画面が固まらないよう、別スレッドで実行する
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        modeText.Text = "棚卸し中...";
        var (error, view) = await Task.Run(() =>
        {
            var e = LocalInventory.TryRun(path, out var v);
            return (e, v);
        });

        if (error == LocalInventoryError.None && view is not null)
        {
            Show(view, $"選択したフォルダ: {folderName}（フルパスは表示しません）");
            return;
        }

        summaryText.Text = string.Empty;
        list.ItemsSource = null;
        modeText.Text = error == LocalInventoryError.DirectoryNotFound
            ? "フォルダが見つかりません。"
            : "フォルダを読めませんでした（権限または入出力のエラー）。";
    }
}
