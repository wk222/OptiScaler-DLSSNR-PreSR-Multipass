using System.IO.Compression;
using System.Text.Json;

namespace OptiScalerInjector.Core;

/// <summary>NR 运行库(nvngx_dlssnr.dll)的获取:本地 dll / zip,或下载默认的 SF-v2。运行库不随发布包分发,只在用户机器上取。</summary>
public static class RuntimeSource
{
    static string RuntimeDir(string cacheRoot) => Path.Combine(cacheRoot, "runtime");

    /// <summary>输入可为 dll 或含 nvngx_dlssnr.dll 的 zip;zip 会解压到缓存并返回 dll 路径。</summary>
    public static string Resolve(string path, string cacheRoot)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return path;
        var dest = Path.Combine(RuntimeDir(cacheRoot), Path.GetFileNameWithoutExtension(path));
        var dll = Path.Combine(dest, Const.RuntimeName);
        if (File.Exists(dll)) return dll;
        Directory.CreateDirectory(dest);
        using var z = ZipFile.OpenRead(path);
        var e = z.Entries.FirstOrDefault(x => x.Name.Equals(Const.RuntimeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"zip 内没有 {Const.RuntimeName}");
        e.ExtractToFile(dll, true);
        return dll;
    }

    /// <summary>已缓存的默认运行库(之前下载或解压过的),没有则返回 null。</summary>
    public static string? FindCachedDefault(string cacheRoot)
    {
        var dir = RuntimeDir(cacheRoot);
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.EnumerateFiles(dir, Const.RuntimeName, SearchOption.AllDirectories))
        {
            try { if (Hashing.Sha256(f) == Const.HashSfV2) return f; } catch { }
        }
        return null;
    }

    /// <summary>从 RankFTW/rhi-repo 的 dlssnr-310.8.SF-v2 Release 下载默认运行库,并校验固定哈希。</summary>
    public static async Task<string> DownloadDefaultAsync(string cacheRoot, IProgress<(long Done, long Total)> progress, Action<string> log)
    {
        var asset = await ReleaseClient.FindTagAssetAsync(Const.DefaultRuntimeRepo, Const.DefaultRuntimeTag,
                        n => n.Contains("dlssnr", StringComparison.OrdinalIgnoreCase) &&
                             (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)), log)
                    ?? new ReleaseAsset
                    {
                        Repo = Const.DefaultRuntimeRepo, Tag = Const.DefaultRuntimeTag, Name = Const.DefaultRuntimeZip,
                        Size = 116693212,
                        Url = $"https://github.com/{Const.DefaultRuntimeRepo}/releases/download/{Const.DefaultRuntimeTag}/{Const.DefaultRuntimeZip}",
                    };
        log($"  下载运行库 {asset.Name} ({asset.Size >> 20} MB)");
        var file = await ReleaseClient.DownloadAsync(asset, Path.Combine(RuntimeDir(cacheRoot), "downloads"), progress, log);
        var dll = Resolve(file, cacheRoot);
        var got = await Task.Run(() => Hashing.Sha256(dll));
        if (got == Const.HashSfV2) log("  ✓ 运行库哈希与固定的 SF-v2 一致");
        else log($"  ⚠ 下载到的运行库哈希 {got[..12]}… 与固定的 SF-v2 ({Const.HashSfV2[..12]}…) 不一致,请自行确认来源");
        return dll;
    }
}
