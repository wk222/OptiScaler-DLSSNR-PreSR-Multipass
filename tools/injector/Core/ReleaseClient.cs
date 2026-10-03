using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptiScalerInjector.Core;

public sealed class ReleaseAsset
{
    public string Repo { get; init; } = "";
    public string Tag { get; init; } = "";
    public bool Prerelease { get; init; }
    public string Published { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string Url { get; init; } = "";
    public string SumsUrl { get; init; } = "";

    public string Label =>
        $"{Tag}{(Prerelease ? " [预发布]" : "")}   ·   {Name}   ({Size / 1048576.0:0} MB)   ·   {Repo.Split('/')[0]}";
}

/// <summary>从 GitHub Releases 列出并下载发布包。先查本 fork,再查上游。</summary>
public static class ReleaseClient
{
    public static readonly string[] Repos =
    {
        "wk222/OptiScaler-DLSSNR-PreSR-Multipass",
        "wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass",
    };

    static readonly HttpClient Http = Create();

    static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("optiscaler-injector");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    static string GhToken()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var t = p.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(5000) && p.ExitCode == 0 ? t : "";
        }
        catch { return ""; }
    }

    public static async Task<List<ReleaseAsset>> ListAsync(Action<string> log, int limit = 8)
    {
        var token = await Task.Run(GhToken);
        var all = new List<ReleaseAsset>();
        foreach (var repo in Repos)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page={limit}");
                if (token != "") req.Headers.Authorization = new("Bearer", token);
                using var resp = await Http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                int before = all.Count;
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.GetProperty("draft").GetBoolean()) continue;
                    var tag = rel.GetProperty("tag_name").GetString() ?? "";
                    if (tag.Contains("display-filter", StringComparison.OrdinalIgnoreCase)) continue;
                    var assets = rel.GetProperty("assets").EnumerateArray().ToList();
                    string Url(JsonElement a) => a.GetProperty("browser_download_url").GetString() ?? "";
                    string Nm(JsonElement a) => a.GetProperty("name").GetString() ?? "";
                    var sums = assets.Where(a => Nm(a).EndsWith("sha256sums.txt", StringComparison.OrdinalIgnoreCase)).Select(Url).FirstOrDefault() ?? "";
                    foreach (var a in assets.Where(a => Nm(a).EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        var own = assets.Where(x => Nm(x).Equals(Nm(a) + ".sha256", StringComparison.OrdinalIgnoreCase)).Select(Url).FirstOrDefault();
                        all.Add(new ReleaseAsset
                        {
                            Repo = repo, Tag = tag, Prerelease = rel.GetProperty("prerelease").GetBoolean(),
                            Published = rel.GetProperty("published_at").GetString() ?? "",
                            Name = Nm(a), Size = a.GetProperty("size").GetInt64(), Url = Url(a),
                            SumsUrl = sums != "" ? sums : own ?? "",
                        });
                    }
                }
                if (all.Count == before) log($"  {repo}: 没有可用的 Release");
            }
            catch (Exception e) { log($"  {repo}: 获取失败 ({e.Message})"); }
        }
        return all.OrderByDescending(a => a.Published).ThenByDescending(a => a.Name).ToList();
    }

    /// <summary>取指定 tag 下第一个名字满足条件的资源;失败返回 null。</summary>
    public static async Task<ReleaseAsset?> FindTagAssetAsync(string repo, string tag, Func<string, bool> match, Action<string> log)
    {
        try
        {
            var token = await Task.Run(GhToken);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/tags/{tag}");
            if (token != "") req.Headers.Authorization = new("Bearer", token);
            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (!match(n)) continue;
                return new ReleaseAsset
                {
                    Repo = repo, Tag = tag, Name = n, Size = a.GetProperty("size").GetInt64(),
                    Url = a.GetProperty("browser_download_url").GetString() ?? "",
                };
            }
        }
        catch (Exception e) { log($"  查询 {repo}@{tag} 失败 ({e.Message}),改用预设下载地址"); }
        return null;
    }

    static async Task<string> ExpectedHashAsync(ReleaseAsset a)
    {
        if (a.SumsUrl == "") return "";
        var text = await Http.GetStringAsync(a.SumsUrl);
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9A-Fa-f]{64})\s+\*?(.+)$");
            if (m.Success && m.Groups[2].Value.Trim().EndsWith(a.Name, StringComparison.OrdinalIgnoreCase))
                return m.Groups[1].Value.ToUpperInvariant();
        }
        var single = Regex.Match(text, @"\b([0-9A-Fa-f]{64})\b");   // <zip>.sha256 只含一个哈希
        return single.Success ? single.Groups[1].Value.ToUpperInvariant() : "";
    }

    public static async Task<string> DownloadAsync(ReleaseAsset a, string destDir, IProgress<(long Done, long Total)> progress, Action<string> log)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, a.Name);
        string expect = "";
        try { expect = await ExpectedHashAsync(a); } catch (Exception e) { log($"  无法读取校验和: {e.Message}"); }

        if (File.Exists(dest) && new FileInfo(dest).Length == a.Size &&
            (expect == "" || await Task.Run(() => Hashing.Sha256(dest)) == expect))
        {
            log($"  已有完整缓存 {a.Name}");
            return dest;
        }

        var part = dest + ".part";
        using (var resp = await Http.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? a.Size;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
            var buf = new byte[1 << 20];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n));
                done += n;
                progress.Report((done, total));
            }
        }

        if (expect != "")
        {
            var got = await Task.Run(() => Hashing.Sha256(part));
            if (got != expect)
            {
                File.Delete(part);
                throw new InvalidOperationException($"SHA-256 不匹配: 期望 {expect[..12]}… 实际 {got[..12]}…");
            }
            log("  ✓ SHA-256 校验通过");
        }
        else log("  ⚠ 该 Release 无校验和文件,未校验");
        File.Move(part, dest, true);
        return dest;
    }
}
