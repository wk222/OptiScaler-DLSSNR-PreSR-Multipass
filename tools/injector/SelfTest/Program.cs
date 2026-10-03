// 无需真实游戏/发布包的自检: 假包 + 假游戏目录,验证安装 / 备份 / 卸载还原 / INI 补丁。
using OptiScalerInjector.Core;
using System.Text;

const string Ini = """
[Upscalers]
Dx11Upscaler=auto
Dx12Upscaler=auto

[Log]
LogToFile=auto
LogLevel=auto

[ProcessFilter]
TargetProcessName=auto

[DlssNr]
Enabled=auto
RunBeforeSR=auto
Passes=auto
WorkingScale=auto
FinishedPicture=false
""";

void Check(bool cond, string what)
{
    if (!cond) { Console.WriteLine("FAIL: " + what); Environment.Exit(1); }
    Console.WriteLine("ok   " + what);
}

var t = Path.Combine(Path.GetTempPath(), "oi_selftest_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    var pkg = Path.Combine(t, "pkg");
    Directory.CreateDirectory(Path.Combine(pkg, "OptiScaler", "plugins"));
    Directory.CreateDirectory(Path.Combine(pkg, "Licenses"));
    Directory.CreateDirectory(Path.Combine(pkg, "docs"));
    Directory.CreateDirectory(Path.Combine(pkg, "Injector"));
    File.WriteAllBytes(Path.Combine(pkg, "OptiScaler.dll"), [.. "MZ"u8, .. Encoding.Unicode.GetBytes("OptiScaler.dll")]);
    File.WriteAllText(Path.Combine(pkg, "OptiScaler.ini"), Ini.Replace("\r\n", "\n"));
    File.WriteAllBytes(Path.Combine(pkg, Const.ForwarderName), "fwd"u8.ToArray());
    File.WriteAllText(Path.Combine(pkg, "OptiScaler", "plugins", "a.asi"), "a");
    File.WriteAllText(Path.Combine(pkg, "Licenses", "L.txt"), "x");
    File.WriteAllText(Path.Combine(pkg, "setup_windows.bat"), "x");
    File.WriteAllText(Path.Combine(pkg, "README.md"), "x");
    File.WriteAllText(Path.Combine(pkg, "docs", "d.md"), "x");
    File.WriteAllText(Path.Combine(pkg, "Injector", "OptiScalerInjector.exe"), "x");

    var root = Path.Combine(t, "Game");
    var game = Path.Combine(root, "Binaries", "Win64");
    Directory.CreateDirectory(game);
    File.WriteAllBytes(Path.Combine(game, "Game-Win64-Shipping.exe"), new byte[6 << 20]);
    File.WriteAllText(Path.Combine(game, "dxgi.dll"), "reshade-original");
    var rt = Path.Combine(t, "rt.dll");
    File.WriteAllText(rt, "fake-runtime");

    var dirs = Scanner.FindExeDirs(root);
    Check(dirs.Count > 0 && dirs[0].Path == game, "FindExeDirs 选中 Binaries\\Win64");

    var opt = new InstallOptions { RuntimePath = rt, Passes = 2, WorkingScale = 0.5, Dx12Dlss = true };
    Installer.Install(pkg, game, opt, Console.WriteLine);
    Check(File.ReadAllBytes(Path.Combine(game, "dxgi.dll")).Take(2).SequenceEqual("MZ"u8.ToArray()), "OptiScaler.dll 已改名为 dxgi.dll");
    Check(File.Exists(Path.Combine(game, Const.ForwarderName)) && File.Exists(Path.Combine(game, "OptiScaler", "plugins", "a.asi")), "转发器与后端目录已复制");
    Check(!File.Exists(Path.Combine(game, "setup_windows.bat")) && !Directory.Exists(Path.Combine(game, "docs"))
          && !Directory.Exists(Path.Combine(game, "Injector")), "setup/docs/Injector 未被复制");
    var ini = File.ReadAllText(Path.Combine(game, "OptiScaler.ini"));
    Check(ini.Contains("Enabled=true") && ini.Contains("Passes=2") && ini.Contains("WorkingScale=0.5"), "DlssNr 项已写入");
    Check(ini.Contains("Dx12Upscaler=dlss") && ini.Contains("Dx11Upscaler=auto") && ini.Contains("LogToFile=true"), "Upscalers/Log 项已写入");
    Check(!File.ReadAllBytes(Path.Combine(game, "OptiScaler.ini")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "INI 无 BOM");
    Check(Scanner.ForeignOptiScaler(game).SequenceEqual(new[] { "dxgi.dll" }), "识别出 OptiScaler 代理 dll");

    Installer.Install(pkg, game, opt, Console.WriteLine);   // 重装: 先还原再装
    Installer.Uninstall(game, Console.WriteLine);
    Check(File.ReadAllText(Path.Combine(game, "dxgi.dll")) == "reshade-original", "卸载后原 dxgi.dll 已还原");
    Check(!Directory.Exists(Path.Combine(game, "OptiScaler")) && !File.Exists(Path.Combine(game, Const.ManifestName))
          && !Directory.Exists(Path.Combine(game, Const.BackupDir)), "卸载后无残留");

    var patched = Installer.PatchIni("[A]\nX=1\n[B]\nQ=1", new() { [("A", "Y")] = "2", [("C", "Z")] = "3" });
    Check(patched == "[A]\nX=1\nY=2\n[B]\nQ=1\n[C]\nZ=3", "PatchIni 追加缺失键/段");

    // zip -> dll 解压(假 zip)
    var zipPath = Path.Combine(t, "rt.zip");
    using (var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, rt, Const.RuntimeName);
    var resolved = RuntimeSource.Resolve(zipPath, Path.Combine(t, "cache"));
    Check(File.Exists(resolved) && Path.GetFileName(resolved) == Const.RuntimeName, "RuntimeSource.Resolve 能从 zip 取出 dll");
    Check(GpuInfo.RuntimeVerdict(rt, "40").Item1 == Level.Warn, "未知哈希给出警告");

    // 真实的默认运行库(若本机存在):哈希应被识别为 SF-v2
    const string real = @"D:\dev\dlssnr_runtime\nvngx_dlssnr.dll";
    if (File.Exists(real))
        Check(GpuInfo.RuntimeVerdict(real, "40").Item1 == Level.Ok, "SF-v2 运行库在 RTX 40 上被识别为可用");

    var (name, drv) = GpuInfo.Detect();
    Console.WriteLine($"GPU: {name} {drv} gen={GpuInfo.Generation(name)}");
    Console.WriteLine("games: " + string.Join(" | ", Scanner.ScanAll().Take(8).Select(g => g.Name)));
    Console.WriteLine("ALL PASS");
}
finally { try { Directory.Delete(t, true); } catch { } }
