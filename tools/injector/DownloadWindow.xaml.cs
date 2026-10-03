using System.Windows;
using OptiScalerInjector.Core;

namespace OptiScalerInjector;

public partial class DownloadWindow : Window
{
    readonly MainWindow main;
    bool downloading;
    public string? ResultPath { get; private set; }

    public DownloadWindow(MainWindow owner)
    {
        InitializeComponent();
        main = owner;
        Loaded += async (_, _) => await Fetch();
        Closing += (_, e) => { if (downloading) e.Cancel = true; };
    }

    async Task Fetch()
    {
        try
        {
            var list = await ReleaseClient.ListAsync(m => main.Log(m));
            Lst.ItemsSource = list;
            if (list.Count > 0)
            {
                Lst.SelectedIndex = 0;
                BtnGo.IsEnabled = true;
                LblInfo.Text = "带 -rtx40-mfg 的是附带 RTX40 MFG 解锁的变体;普通使用选不带的。";
            }
            else LblInfo.Text = "两个仓库都没有可用的 Release zip。请用 package_release.ps1 自行打包后选择本地目录。";
        }
        catch (Exception e) { LblInfo.Text = "获取失败: " + e.Message; }
    }

    async void OnGo(object s, RoutedEventArgs e)
    {
        if (Lst.SelectedItem is not ReleaseAsset a) return;
        BtnGo.IsEnabled = false; downloading = true;
        main.Log($"▶ 下载 {a.Name} ({a.Repo} {a.Tag})");
        var prog = new Progress<(long Done, long Total)>(p =>
        {
            Bar.Value = p.Done * 100.0 / Math.Max(p.Total, 1);
            LblInfo.Text = $"下载中 {p.Done >> 20} / {p.Total >> 20} MB";
        });
        try
        {
            ResultPath = await ReleaseClient.DownloadAsync(a, System.IO.Path.Combine(MainWindow.CacheDir, "downloads"), prog, m => main.Log(m));
            downloading = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            downloading = false;
            main.Log($"✗ 下载失败: {ex.Message}", "bad");
            LblInfo.Text = "下载失败: " + ex.Message;
            BtnGo.IsEnabled = true;
        }
    }
}
