using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using OptiScalerInjector.Core;

namespace OptiScalerInjector;

public sealed class GameItem
{
    public Game Game { get; init; } = null!;
    public string Name => Game.Name;
    public string Source => Game.Source;
    public string Status { get; set; } = "";
}

public sealed class AppConfig
{
    public string Pkg { get; set; } = "";
    public string Rt { get; set; } = "";
    public string Proxy { get; set; } = "dxgi.dll";
    public bool Nr { get; set; } = true;
    public bool Pre { get; set; } = true;
    public bool Fin { get; set; }
    public bool Dx12 { get; set; }
    public bool Dx11 { get; set; }
    public bool Log { get; set; } = true;
    public int Passes { get; set; } = 1;
    public double Scale { get; set; } = 1.0;
}

public partial class MainWindow : Window
{
    public static readonly string AppDir = AppContext.BaseDirectory;
    public static readonly string CacheDir = Path.Combine(AppDir, "cache");
    static readonly string ConfigPath = Path.Combine(AppDir, "config.json");

    readonly string gpuName, gpuDriver, gpuGen;
    List<Game> games = new();
    List<ExeDir> exeDirs = new();
    Game? curGame;
    bool busy;

    public MainWindow()
    {
        InitializeComponent();
        (gpuName, gpuDriver) = GpuInfo.Detect();
        gpuGen = GpuInfo.Generation(gpuName);
        LblGpu.Text = (gpuName == "" ? "未检测到 NVIDIA 显卡" : gpuName) + (gpuDriver == "" ? "" : $"  |  驱动 {gpuDriver}");
        CbProxy.ItemsSource = Const.ProxyChoices;
        CbProxy.SelectedIndex = 0;
        LoadConfig();
        if (TxtPkg.Text.Trim() == "" || !(File.Exists(TxtPkg.Text) || Directory.Exists(TxtPkg.Text)))
        {
            var auto = Installer.AutodetectPackage();
            if (auto != null) { TxtPkg.Text = auto; Log($"自动找到发布包: {auto}", "ok"); }
        }
        if (TxtRt.Text.Trim() == "" || !File.Exists(TxtRt.Text.Trim()) && !TxtRt.Text.EndsWith(".zip"))
        {
            var cached = RuntimeSource.FindCachedDefault(CacheDir);   // 默认运行库 SF-v2:之前下载/解压过就直接用
            if (cached != null) TxtRt.Text = cached;
        }
        Loaded += async (_, _) => { CheckPackage(); CheckRuntime(); await RefreshGames(); };
        Closing += (_, _) => SaveConfig();
    }

    // ------------------------------------------------------------ 配置
    void LoadConfig()
    {
        try
        {
            var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
            if (c == null) return;
            TxtPkg.Text = c.Pkg; TxtRt.Text = c.Rt;
            CbProxy.SelectedItem = Const.ProxyChoices.Contains(c.Proxy) ? c.Proxy : "dxgi.dll";
            ChkNr.IsChecked = c.Nr; ChkPre.IsChecked = c.Pre; ChkFin.IsChecked = c.Fin;
            ChkDx12.IsChecked = c.Dx12; ChkDx11.IsChecked = c.Dx11; ChkLog.IsChecked = c.Log;
            CbPasses.SelectedIndex = Math.Clamp(c.Passes, 1, 3) - 1;
            SldScale.Value = Math.Clamp(c.Scale, 0.25, 1.0);
        }
        catch { /* 首次运行没有配置 */ }
    }

    void SaveConfig()
    {
        var c = new AppConfig
        {
            Pkg = TxtPkg.Text.Trim(), Rt = TxtRt.Text.Trim(), Proxy = CbProxy.SelectedItem as string ?? "dxgi.dll",
            Nr = ChkNr.IsChecked == true, Pre = ChkPre.IsChecked == true, Fin = ChkFin.IsChecked == true,
            Dx12 = ChkDx12.IsChecked == true, Dx11 = ChkDx11.IsChecked == true, Log = ChkLog.IsChecked == true,
            Passes = CbPasses.SelectedIndex + 1, Scale = Math.Round(SldScale.Value, 2),
        };
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    // ------------------------------------------------------------ 日志 / 忙碌
    public void Log(string msg, string tag = "")
    {
        Dispatcher.BeginInvoke(() =>
        {
            var color = tag switch { "ok" => "#76B900", "warn" => "#F0B429", "bad" => "#EF5350", _ => "#C9D1D9" };
            var p = new Paragraph(new Run(msg) { Foreground = (Brush)new BrushConverter().ConvertFromString(color)! });
            TxtLog.Document.Blocks.Add(p);
            TxtLog.ScrollToEnd();
        });
    }

    /// <summary>在后台线程执行,期间显示进度条并阻止重复操作。失败时写日志并返回 (false, default)。</summary>
    async Task<(bool Ok, T? Value)> Busy<T>(Func<T> work)
    {
        if (busy) return (false, default);
        busy = true; Pb.Visibility = Visibility.Visible;
        try { return (true, await Task.Run(work)); }
        catch (Exception e) { Log($"✗ 失败: {e.Message}", "bad"); return (false, default); }
        finally { busy = false; Pb.Visibility = Visibility.Collapsed; }
    }

    // ------------------------------------------------------------ 资源
    void OnPickPkgDir(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "选择解压后的发布包目录(含 OptiScaler.dll)" };
        if (d.ShowDialog() == true) { TxtPkg.Text = d.FolderName; CheckPackage(); }
    }

    void OnPickPkgZip(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "选择发布包 zip", Filter = "zip|*.zip" };
        if (d.ShowDialog() == true) { TxtPkg.Text = d.FileName; CheckPackage(); }
    }

    void OnPickRuntime(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "选择 nvngx_dlssnr.dll 或含它的 zip", Filter = "DLL / zip|*.dll;*.zip" };
        if (d.ShowDialog() == true) { TxtRt.Text = d.FileName; CheckRuntime(); }
    }

    async void OnDownloadRuntime(object s, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true; Pb.Visibility = Visibility.Visible;
        Log($"▶ 下载默认运行库 {Const.DefaultRuntimeRepo} @ {Const.DefaultRuntimeTag}");
        try
        {
            var prog = new Progress<(long Done, long Total)>(p =>
                SetLabel(LblRt, $"下载中 {p.Done >> 20} / {p.Total >> 20} MB", "Muted"));
            var dll = await RuntimeSource.DownloadDefaultAsync(CacheDir, prog, m => Log(m));
            TxtRt.Text = dll;
        }
        catch (Exception ex) { Log($"✗ 下载失败: {ex.Message}", "bad"); }
        finally { busy = false; Pb.Visibility = Visibility.Collapsed; }
        CheckRuntime();
    }

    void OnDownload(object s, RoutedEventArgs e)
    {
        if (busy) return;
        var w = new DownloadWindow(this) { Owner = this };
        if (w.ShowDialog() == true && w.ResultPath != null)
        {
            TxtPkg.Text = w.ResultPath;
            CheckPackage();
            Log($"✓ 已选用 {Path.GetFileName(w.ResultPath)}", "ok");
        }
    }

    void PkgChanged(object s, RoutedEventArgs e) => CheckPackage();
    void RuntimeChanged(object s, RoutedEventArgs e) => CheckRuntime();

    void SetLabel(TextBlock tb, string text, string brush) =>
        (tb.Text, tb.Foreground) = (text, (Brush)FindResource(brush));

    void CheckPackage()
    {
        var p = TxtPkg.Text.Trim();
        if (p == "")
        {
            SetLabel(LblPkg, "未选择。点右侧\"从 GitHub 下载…\"获取发布包,或选择本地解压目录/zip。", "Warn");
            return;
        }
        if (File.Exists(p)) { SetLabel(LblPkg, "zip 文件,注入时自动解压到 cache\\", "Muted"); return; }
        try
        {
            var pkg = Installer.ResolvePackage(p, CacheDir, _ => { });
            var miss = Installer.PackageReport(pkg).Where(r => !r.Ok).Select(r => r.Name).ToList();
            if (miss.Count > 0) SetLabel(LblPkg, "⚠ 缺少: " + string.Join(", ", miss), "Warn");
            else SetLabel(LblPkg, $"✓ 发布包完整  ({pkg})", "Ok");
        }
        catch (Exception ex) { SetLabel(LblPkg, "✗ " + ex.Message, "Danger"); }
    }

    async void CheckRuntime()
    {
        var p = TxtRt.Text.Trim();
        SetLabel(LblRt, "校验中…", "Muted");
        var (lvl, msg) = await Task.Run(() =>
        {
            try
            {
                var dll = p == "" ? p : RuntimeSource.Resolve(p, CacheDir);   // zip 自动解压
                if (dll != p) Dispatcher.BeginInvoke(() => TxtRt.Text = dll);
                return GpuInfo.RuntimeVerdict(dll, gpuGen);
            }
            catch (Exception ex) { return (Level.Bad, ex.Message); }
        });
        var (icon, brush) = lvl switch
        {
            Level.Ok => ("✓ ", "Ok"), Level.Warn => ("⚠ ", "Warn"), Level.Bad => ("✗ ", "Danger"), _ => ("", "Muted"),
        };
        SetLabel(LblRt, icon + msg, brush);
    }

    // ------------------------------------------------------------ 游戏列表
    async void OnRefresh(object s, RoutedEventArgs e) => await RefreshGames();

    async Task RefreshGames()
    {
        Log("扫描 Steam / Epic / GOG ...");
        var (ok, list) = await Busy(Scanner.ScanAll);
        if (!ok || list == null) return;
        games = list;
        FillGames();
        Log($"发现 {games.Count} 个游戏", "ok");
    }

    void FillGames()
    {
        var q = TxtSearch?.Text.Trim() ?? "";
        LstGames.ItemsSource = games.Where(g => q == "" || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .Select(g => new GameItem { Game = g }).ToList();
    }

    void SearchChanged(object s, TextChangedEventArgs e) { if (LstGames != null) FillGames(); }

    void OnAddGame(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "选择游戏根文件夹" };
        if (d.ShowDialog() != true) return;
        games.Add(new Game(Path.GetFileName(d.FolderName.TrimEnd('\\')), d.FolderName, "手动"));
        TxtSearch.Text = "";
        FillGames();
        LstGames.SelectedIndex = LstGames.Items.Count - 1;
    }

    async void GameSelected(object s, SelectionChangedEventArgs e)
    {
        if (LstGames.SelectedItem is not GameItem gi || busy) return;
        var g = curGame = gi.Game;
        LblTarget.Text = "定位 exe 目录中…";
        CbTarget.ItemsSource = null; CbTarget.Text = "";
        var (ok, dirs) = await Busy(() => Scanner.FindExeDirs(g.Root));
        if (!ok || dirs == null || curGame != g) return;
        exeDirs = dirs;
        CbTarget.ItemsSource = dirs.Select(d => d.Path).ToList();
        if (dirs.Count > 0) CbTarget.SelectedIndex = 0;
        TargetChanged(null!, null!);
        var (ok2, ac) = await Busy(() => Scanner.DetectAntiCheat(g.Root));
        if (ok2 && ac is { Count: > 0 })
            Log($"⚠ {g.Name} 含反作弊组件: {string.Join(", ", ac)} — 注入可能导致封号/无法启动", "warn");
    }

    void TargetChanged(object s, RoutedEventArgs e)
    {
        var t = CbTarget.Text.Trim();
        if (t == "") { SetLabel(LblTarget, "未找到可执行文件目录,请手动浏览", "Warn"); return; }
        var d = exeDirs.FirstOrDefault(x => string.Equals(x.Path, t, StringComparison.OrdinalIgnoreCase));
        var parts = new List<string>();
        if (d != null)
        {
            parts.Add("exe: " + string.Join(", ", d.Exes.Take(4)) + (d.Exes.Count > 4 ? "…" : ""));
            if (d.Installed) parts.Add("✓ 已由本工具注入");
        }
        if (Directory.Exists(t))
        {
            var foreign = Scanner.ForeignOptiScaler(t);
            if (foreign.Count > 0 && !File.Exists(Path.Combine(t, Const.ManifestName)))
                parts.Add("⚠ 发现其它方式安装的 OptiScaler: " + string.Join(", ", foreign) + "(将被备份)");
        }
        SetLabel(LblTarget, parts.Count > 0 ? string.Join("  |  ", parts) : t, d?.Installed == true ? "Ok" : "Muted");
        MarkInstalled();
    }

    void MarkInstalled()
    {
        if (LstGames.SelectedItem is GameItem gi)
        {
            gi.Status = exeDirs.Any(d => d.Installed) ? "已注入" : "";
            LstGames.Items.Refresh();
        }
    }

    void OnPickTarget(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "选择游戏 exe 所在目录", InitialDirectory = curGame?.Root };
        if (d.ShowDialog() == true) { CbTarget.Text = d.FolderName; TargetChanged(null!, null!); }
    }

    void ScaleChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblScale != null) LblScale.Text = e.NewValue.ToString("0.00");
    }

    // ------------------------------------------------------------ 注入 / 卸载
    string? Target()
    {
        var t = CbTarget.Text.Trim();
        if (t == "" || !Directory.Exists(t)) { MessageBox.Show(this, "请先选择有效的目标目录", "提示"); return null; }
        return t;
    }

    bool Confirm(string text, string title) =>
        MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    async void OnInstall(object s, RoutedEventArgs e)
    {
        if (busy) return;
        var target = Target();
        if (target == null) return;
        var pkgSrc = TxtPkg.Text.Trim();
        if (pkgSrc == "") { MessageBox.Show(this, "请先选择发布包(目录或 zip),或点\"从 GitHub 下载…\"", "提示"); return; }

        var rt = TxtRt.Text.Trim();
        try { if (rt.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) rt = RuntimeSource.Resolve(rt, CacheDir); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "运行库", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        var (lvl, msg) = GpuInfo.RuntimeVerdict(rt, gpuGen);
        if (lvl == Level.Bad) { MessageBox.Show(this, msg, "运行库不匹配", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (lvl is Level.None or Level.Warn &&
            !Confirm(msg + "\n\n没有正确的运行库 Neural Rendering 无法初始化。仍要继续注入吗?", "运行库未确认")) return;
        if (curGame != null)
        {
            var ac = await Task.Run(() => Scanner.DetectAntiCheat(curGame.Root));
            if (ac.Count > 0 && !Confirm($"发现反作弊组件: {string.Join(", ", ac)}\n在带反作弊的游戏里注入可能导致封号。仍然继续?", "检测到反作弊")) return;
        }
        var proxy = CbProxy.SelectedItem as string ?? "dxgi.dll";
        if (File.Exists(Path.Combine(target, proxy)) && !File.Exists(Path.Combine(target, Const.ManifestName)) &&
            !Confirm($"目录中已存在 {proxy}(可能是 ReShade 等其它加载器)。\n将备份后替换,卸载时自动还原。继续?", "文件冲突")) return;

        var opt = new InstallOptions
        {
            Proxy = proxy, RuntimePath = lvl == Level.None ? null : rt,
            NrEnabled = ChkNr.IsChecked == true, RunBeforeSR = ChkPre.IsChecked == true,
            Passes = CbPasses.SelectedIndex + 1, WorkingScale = Math.Round(SldScale.Value, 2),
            FinishedPicture = ChkFin.IsChecked == true, Dx12Dlss = ChkDx12.IsChecked == true,
            Dx11Dlss12 = ChkDx11.IsChecked == true, LogToFile = ChkLog.IsChecked == true,
        };

        Log($"▶ 注入到 {target}");
        var (ok, _) = await Busy(() =>
        {
            var pkg = Installer.ResolvePackage(pkgSrc, CacheDir, m => Log(m));
            Installer.Install(pkg, target, opt, m => Log(m));
            return true;
        });
        if (ok) { Log("✓ 注入完成。启动游戏进入画面后按 Insert 打开菜单。", "ok"); RefreshInstalledState(); }
    }

    async void OnUninstall(object s, RoutedEventArgs e)
    {
        if (busy) return;
        var target = Target();
        if (target == null) return;
        if (!File.Exists(Path.Combine(target, Const.ManifestName)))
        {
            MessageBox.Show(this, "该目录没有本工具的安装记录。\n(其它方式安装的请手动删除或用 Remove_OptiScaler.bat)", "提示");
            return;
        }
        if (!Confirm($"卸载 {target} 中注入的文件,并还原被备份的原文件?", "确认卸载")) return;
        var (ok, _) = await Busy(() => { Installer.Uninstall(target, m => Log(m)); return true; });
        if (ok) { Log("✓ 已卸载", "ok"); RefreshInstalledState(); }
    }

    void RefreshInstalledState()
    {
        foreach (var d in exeDirs) d.Installed = File.Exists(Path.Combine(d.Path, Const.ManifestName));
        TargetChanged(null!, null!);
    }

    void OnOpenDir(object s, RoutedEventArgs e)
    {
        if (Target() is { } t) Process.Start(new ProcessStartInfo(t) { UseShellExecute = true });
    }

    void OnOpenIni(object s, RoutedEventArgs e)
    {
        if (Target() is not { } t) return;
        var ini = Path.Combine(t, "OptiScaler.ini");
        if (File.Exists(ini)) Process.Start(new ProcessStartInfo(ini) { UseShellExecute = true });
        else MessageBox.Show(this, "尚无 OptiScaler.ini,请先注入", "提示");
    }
}
