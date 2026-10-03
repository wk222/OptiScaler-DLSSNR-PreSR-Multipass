using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OptiScalerInjector.Core;

public sealed record Game(string Name, string Root, string Source);

public sealed class ExeDir
{
    public string Path { get; init; } = "";
    public List<string> Exes { get; init; } = new();
    public long Size { get; init; }
    public int Score { get; init; }
    public bool Installed { get; set; }
}

public static class Scanner
{
    // ---------------------------------------------------------------- 游戏库
    public static List<Game> ScanAll()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<Game>();
        foreach (var g in ScanSteam().Concat(ScanEpic()).Concat(ScanGog()))
            if (seen.Add(g.Root)) list.Add(g);
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    static IEnumerable<string> SteamLibraries()
    {
        string? steam = null;
        foreach (var (hive, sub, val) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        })
        {
            try
            {
                using var k = hive.OpenSubKey(sub);
                steam = k?.GetValue(val) as string;
                if (!string.IsNullOrEmpty(steam)) break;
            }
            catch { }
        }
        if (string.IsNullOrEmpty(steam)) yield break;
        steam = steam.Replace('/', '\\');
        var libs = new List<string> { steam };
        var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (var l in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(System.IO.Path.Combine(l, "steamapps"))) yield return l;
    }

    static IEnumerable<Game> ScanSteam()
    {
        foreach (var lib in SteamLibraries())
        {
            var apps = System.IO.Path.Combine(lib, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList(); } catch { continue; }
            foreach (var mf in manifests)
            {
                string t;
                try { t = File.ReadAllText(mf); } catch { continue; }
                var nm = Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"");
                var di = Regex.Match(t, "\"installdir\"\\s+\"([^\"]+)\"");
                if (!nm.Success || !di.Success) continue;
                var dir = System.IO.Path.Combine(apps, "common", di.Groups[1].Value);
                if (!Directory.Exists(dir)) continue;
                if (Regex.IsMatch(nm.Groups[1].Value, "Steamworks Common|Proton|Steam Linux Runtime|Redistributables")) continue;
                yield return new Game(nm.Groups[1].Value, dir, "Steam");
            }
        }
    }

    static IEnumerable<Game> ScanEpic()
    {
        var pd = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
        var man = System.IO.Path.Combine(pd, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(man)) yield break;
        foreach (var f in Directory.EnumerateFiles(man, "*.item"))
        {
            Game? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var loc = doc.RootElement.TryGetProperty("InstallLocation", out var l) ? l.GetString() ?? "" : "";
                var name = doc.RootElement.TryGetProperty("DisplayName", out var n) ? n.GetString() ?? "" : "";
                if (Directory.Exists(loc)) g = new Game(name == "" ? System.IO.Path.GetFileName(loc) : name, loc, "Epic");
            }
            catch { }
            if (g != null) yield return g;
        }
    }

    static IEnumerable<Game> ScanGog()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) yield break;
        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(sub);
            var p = k?.GetValue("path") as string;
            var n = k?.GetValue("gameName") as string;
            if (p != null && n != null && Directory.Exists(p)) yield return new Game(n, p, "GOG");
        }
    }

    // ---------------------------------------------------------------- exe 目录定位
    static readonly Regex ExeSkip = new(
        "(unins|crash|report|redist|vcredist|dxsetup|dotnet|setup|installer|helper|eac|easyanticheat|beservice|bootstrap|cef|webview|notification|updater|7z|ue4prereq|prereq|quicksfv|handler|diag|bugreport)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DirSkip = new(
        @"^(_commonredist|redist|redistributables|__installer|directx|vcredist|thirdparty|\.git|crashreport|easyanticheat|engine)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ExeDir> FindExeDirs(string root, int maxDepth = 7)
    {
        var found = new Dictionary<string, List<(string Name, long Size)>>(StringComparer.OrdinalIgnoreCase);

        void Walk(string dir, int depth)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    var fn = System.IO.Path.GetFileName(f);
                    if (ExeSkip.IsMatch(fn)) continue;
                    long sz;
                    try { sz = new FileInfo(f).Length; } catch { continue; }
                    if (sz <= 300 * 1024) continue;
                    if (!found.TryGetValue(dir, out var l)) found[dir] = l = new();
                    l.Add((fn, sz));
                }
                if (depth >= maxDepth) return;
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var n = System.IO.Path.GetFileName(d);
                    if (DirSkip.IsMatch(n) || n == Const.BackupDir) continue;
                    Walk(d, depth + 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        Walk(root, 0);

        var result = new List<ExeDir>();
        foreach (var (dir, exes) in found)
        {
            long biggest = exes.Max(e => e.Size);
            int score = (int)Math.Min(biggest >> 20, 400);
            if (dir.Replace('/', '\\').Contains(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase)) score += 300;
            if (exes.Any(e => e.Name.Contains("shipping", StringComparison.OrdinalIgnoreCase))) score += 120;
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) score += 30;
            bool installed = File.Exists(System.IO.Path.Combine(dir, Const.ManifestName));
            if (installed) score += 50;
            result.Add(new ExeDir
            {
                Path = dir, Exes = exes.Select(e => e.Name).OrderBy(x => x).ToList(),
                Size = biggest, Score = score, Installed = installed,
            });
        }
        return result.OrderByDescending(r => r.Score).Take(12).ToList();
    }

    // ---------------------------------------------------------------- 检测
    public static List<string> DetectAntiCheat(string root)
    {
        var hit = new HashSet<string>();
        try
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
            int n = 0;
            foreach (var e in Directory.EnumerateFileSystemEntries(root, "*", opt))
            {
                if (++n > 60000) break;
                var name = System.IO.Path.GetFileName(e);
                foreach (var m in Const.AntiCheat)
                    if (name.StartsWith(m, StringComparison.OrdinalIgnoreCase)) hit.Add(m);
            }
        }
        catch { }
        return hit.ToList();
    }

    static readonly byte[] OptiSig = System.Text.Encoding.Unicode.GetBytes("OptiScaler.dll");

    /// <summary>目录里已有的 OptiScaler 代理 dll(通过 PE 资源中的原始文件名识别)。</summary>
    public static List<string> ForeignOptiScaler(string dir)
    {
        var hits = new List<string>();
        foreach (var n in Const.ProxyDlls)
        {
            var p = System.IO.Path.Combine(dir, n);
            try
            {
                if (File.Exists(p) && new FileInfo(p).Length < (80 << 20) && File.ReadAllBytes(p).AsSpan().IndexOf(OptiSig) >= 0)
                    hits.Add(n);
            }
            catch { }
        }
        return hits;
    }
}
