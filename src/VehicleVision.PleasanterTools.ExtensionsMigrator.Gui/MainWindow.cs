using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Gui;

/// <summary>
/// 試作のウィンドウ。フォルダを選び、合成データの棚卸し結果を表示する。読み取り専用で、選んだフォルダは読まない。
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly TextBlock folderText = new() { Text = "(フォルダは未選択)" };

    public MainWindow(bool smoke)
    {
        Title = "ExtensionsMigrator（試作）";
        Width = 900;
        Height = 560;

        var rows = SampleInventory.Create();
        var pick = new Button { Content = "ソースのフォルダを選ぶ..." };
        pick.Click += async (_, _) => await PickFolderAsync();

        var list = new ListBox
        {
            ItemsSource = rows.Select(SampleInventory.Format).ToList(),
            FontFamily = new Avalonia.Media.FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
        };

        var header = new StackPanel { Spacing = 8, Margin = new Thickness(16, 16, 16, 8) };
        header.Children.Add(new TextBlock { Text = "拡張機能の棚卸し", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "試作です。下の一覧は合成データで、選んだフォルダの内容ではありません（棚卸しとの結線は #11 のマージ後）。ファイルへは何も書きません。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        header.Children.Add(pick);
        header.Children.Add(folderText);
        header.Children.Add(new TextBlock { Text = SampleInventory.Summarize(rows) });

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new Border { Margin = new Thickness(16, 0, 16, 16), Child = list });
        Content = root;

        if (smoke)
        {
            // 起動確認用。ウィンドウが開いたら数秒後に閉じる
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
    }

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "ソースのフォルダ（Parameters に相当する場所）を選ぶ",
            AllowMultiple = false,
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        folderText.Text = path is null ? "(フォルダは未選択)" : $"選択: {path}（試作のため、中身は読みません）";
    }
}
