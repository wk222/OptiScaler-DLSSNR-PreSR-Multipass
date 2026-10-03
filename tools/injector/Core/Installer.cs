using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OptiScalerInjector.Core;

public sealed class InstallOptions
{
    public string Proxy { get; set; } = "dxgi.dll";
    public string? RuntimePath { get; set; }
    public bool NrEnabled { get; set; } = true;
    public bool RunBeforeSR { get; set; } = true;
    public int Passes { get; set; } = 1;
    public double WorkingScale { get; set; } = 1.0;
    public bool FinishedPicture { get; set; }
    public bool Dx12Dlss { get; set; }
    public bool Dx11Dlss12 { get; set; }
    public bool LogToFile { get; set; } = true;
    public int LogLevel { get; set; } = 2;

    static string B(bool v) => v ? "true" : "false";

    public Dictionary<(string Section, string Key), string> IniEdits()
    {
        var e = new Dictionary<(string, string), string>
        {
            [("DlssNr", "Enabled")] = B(NrEnabled),
            [("DlssNr", "RunBeforeSR")] = B(RunBeforeSR),
            [("DlssNr", "Passes")] = Passes.ToString(),
            [("DlssNr", "WorkingScale")] = WorkingScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            [("DlssNr", "FinishedPicture")] = B(FinishedPicture),
            // 换游戏时进程名不匹配会让 OptiScaler 进入直通模式(无菜单、无 NR),所以强制 auto
            [("ProcessFilter", "TargetProcessName")] = "auto",
            [("Log", "LogToFile")] = B(LogToFile),
            [("Log", "LogLevel")] = LogLevel.ToString(),
        };
        if (Dx12Dlss) e[("Upscalers", "Dx12Upscaler")] = "dlss";
        if (Dx11Dlss12) e[("Upscalers", "Dx11Upscaler")] = "dlss_12";
        return e;
    }
}

/// <summary>安装记录,字段名与旧版 Python 工具兼容。</summary>
public sealed class Manifest
{
    [JsonPropertyName("tool")] public string Tool { get; set; } = "optiscaler-injector";
    [JsonPropertyName("proxy")] public string Proxy { get; set; } = "";
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("backups")] public Dictionary<string, string> Backups { get; set; } = new();
    [JsonPropertyName("time")] public string Time { get; set; } = "";
}

public static class Installer
{
    static readonly UTF8Encoding Utf8NoBom = new(false);

    // ---------------------------------------------------------------- 发布包
    public static string ResolvePackage(string src, string cacheRoot, Action<string> log)
    {
        if (File.Exists(src) && src.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var dest = Path.Combine(cacheRoot, "pkg_" + Path.GetFileNameWithoutExtension(src));
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            log($"解压 {Path.GetFileName(src)} -> {dest}");
            ZipFile.ExtractToDirectory(src, dest);
            src = dest;
        }
        if (!Directory.Exists(src)) throw new FileNotFoundException($"发布包不存在: {src}");
        foreach (var c in new[] { src }.Concat(Directory.GetDirectories(src)))
            if (File.Exists(Path.Combine(c, "OptiScaler.dll"))) return c;
        throw new FileNotFoundException("该位置没有 OptiScaler.dll,请选择完整的发布包(不是源码仓库)");
    }

    /// <summary>注入器在发布包 Injector\ 内,或在源码仓库 tools\injector 内时自动找到可用的包。</summary>
    public static string? AutodetectPackage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            if (IsPackage(dir.FullName)) return dir.FullName;
            var rel = Path.Combine(dir.FullName, "release");
            if (Directory.Exists(rel))
            {
                var best = new DirectoryInfo(rel).GetDirectories()
                    .Where(d => IsPackage(d.FullName)).OrderByDescending(d => d.LastWriteTime).FirstOrDefault();
                if (best != null) return best.FullName;
            }
        }
        return null;
    }

    static bool IsPackage(string d) =>
        File.Exists(Path.Combine(d, "OptiScaler.dll")) && File.Exists(Path.Combine(d, "OptiScaler.ini"));

    public static List<(bool Ok, string Name)> PackageReport(string pkg) => new()
    {
        (File.Exists(Path.Combine(pkg, "OptiScaler.dll")), "OptiScaler.dll"),
        (File.Exists(Path.Combine(pkg, "OptiScaler.ini")), "OptiScaler.ini"),
        (File.Exists(Path.Combine(pkg, Const.ForwarderName)), Const.ForwarderName),
        (Directory.Exists(Path.Combine(pkg, "OptiScaler")), "OptiScaler/ (后端目录)"),
    };

    // ---------------------------------------------------------------- INI
    /// <summary>只修改指定 section 下已有的 key;不存在则追加到该 section 末尾。</summary>
    public static string PatchIni(string text, Dictionary<(string Section, string Key), string> edits)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var pending = new Dictionary<(string, string), string>(edits);
        var last = new Dictionary<string, int>();
        string sec = "";
        for (int i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^\[(.+?)\]");
            if (m.Success) { sec = m.Groups[1].Value; last[sec] = i; continue; }
            var km = Regex.Match(lines[i], @"^([A-Za-z0-9_]+)=");
            if (!km.Success) continue;
            last[sec] = i;
            var key = (sec, km.Groups[1].Value);
            if (pending.Remove(key, out var v)) lines[i] = $"{key.Item2}={v}";
        }
        foreach (var ((s, k), v) in pending)
        {
            if (last.TryGetValue(s, out var at))
            {
                lines.Insert(at + 1, $"{k}={v}");
                foreach (var name in last.Keys.ToList()) if (last[name] > at) last[name]++;
                last[s] = at + 1;
            }
            else
            {
                lines.Add($"[{s}]"); lines.Add($"{k}={v}");
                last[s] = lines.Count - 1;
            }
        }
        return string.Join(nl, lines);
    }

    // ---------------------------------------------------------------- 安装 / 卸载
    static string Rel(string path, string baseDir) => Path.GetRelativePath(baseDir, path).Replace('\\', '/');

    static bool SkipItem(string name) =>
        Const.PackageSkip.Contains(name) || name.StartsWith("!!") ||
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exp", StringComparison.OrdinalIgnoreCase);

    public static void Install(string pkg, string gameDir, InstallOptions opt, Action<string> log)
    {
        if (!Directory.Exists(gameDir)) throw new DirectoryNotFoundException(gameDir);
        if (File.Exists(Path.Combine(gameDir, Const.ManifestName)))
        {
            log("检测到上次由本工具安装的内容,先卸载并还原...");
            Uninstall(gameDir, log);
        }

        var backupRoot = Path.Combine(gameDir, Const.BackupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var m = new Manifest { Proxy = opt.Proxy, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };

        void Backup(string rel)
        {
            var dst = Path.Combine(gameDir, rel);
            if (!File.Exists(dst)) return;
            var bk = Path.Combine(backupRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(bk)!);
            File.Move(dst, bk);
            m.Backups[rel] = Rel(bk, gameDir);
            log($"  备份已有文件 {rel}");
        }
        void Place(string src, string rel)
        {
            var dst = Path.Combine(gameDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            Backup(rel);
            File.Copy(src, dst);
            m.Files.Add(rel);
        }

        try
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(pkg).OrderBy(x => x))
            {
                var name = Path.GetFileName(item);
                if (SkipItem(name)) continue;
                if (Directory.Exists(item))
                {
                    foreach (var f in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories))
                        Place(f, Rel(f, pkg));
                }
                else if (name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase))
                {
                    Place(item, opt.Proxy);
                    log($"  OptiScaler.dll -> {opt.Proxy}");
                }
                else if (!name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))
                {
                    Place(item, name);
                }
            }

            var ini = PatchIni(File.ReadAllText(Path.Combine(pkg, "OptiScaler.ini")), opt.IniEdits());
            Backup("OptiScaler.ini");
            File.WriteAllText(Path.Combine(gameDir, "OptiScaler.ini"), ini, Utf8NoBom);   // 无 BOM
            m.Files.Add("OptiScaler.ini");

            if (!string.IsNullOrEmpty(opt.RuntimePath))
            {
                log($"  复制运行库 {Const.RuntimeName} (约 165MB)...");
                Place(opt.RuntimePath, Const.RuntimeName);
            }
            else log("  ⚠ 未提供 nvngx_dlssnr.dll,Neural Rendering 将无法初始化");
        }
        catch
        {
            log("安装失败,回滚...");
            WriteManifest(gameDir, m);
            Uninstall(gameDir, log);
            throw;
        }
        WriteManifest(gameDir, m);
        log($"完成: {m.Files.Count} 个文件 -> {gameDir}");
    }

    static void WriteManifest(string gameDir, Manifest m) =>
        File.WriteAllText(Path.Combine(gameDir, Const.ManifestName),
            JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            Utf8NoBom);

    public static void Uninstall(string gameDir, Action<string> log)
    {
        var mf = Path.Combine(gameDir, Const.ManifestName);
        if (!File.Exists(mf)) throw new FileNotFoundException("该目录没有本工具的安装记录(.optiscaler_injector.json)");
        var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(mf)) ?? new Manifest();

        int removed = 0;
        foreach (var rel in m.Files)
        {
            var p = Path.Combine(gameDir, rel);
            try { if (File.Exists(p)) { File.Delete(p); removed++; } }
            catch (Exception e) { log($"  无法删除 {rel}: {e.Message}(游戏是否仍在运行?)"); }
        }
        try { File.Delete(Path.Combine(gameDir, "OptiScaler.log")); } catch { }

        foreach (var (rel, bk) in m.Backups)
        {
            var src = Path.Combine(gameDir, bk);
            var dst = Path.Combine(gameDir, rel);
            if (!File.Exists(src)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Move(src, dst, true);
            log($"  还原 {rel}");
        }

        foreach (var rel in m.Files.Select(r => Path.GetDirectoryName(r.Replace('/', '\\')) ?? "")
                     .Where(r => r != "").Distinct().OrderByDescending(r => r.Length))
        {
            var d = Path.Combine(gameDir, rel);
            while (!string.Equals(d.TrimEnd('\\'), gameDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && Directory.Exists(d))
            {
                try { Directory.Delete(d, false); } catch { break; }
                d = Path.GetDirectoryName(d)!;
            }
        }
        try { Directory.Delete(Path.Combine(gameDir, Const.BackupDir), true); } catch { }
        File.Delete(mf);
        log($"已卸载,删除 {removed} 个文件");
    }
}
