using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OptiScalerInjector.Core;

public static class Const
{
    public static readonly string[] ProxyChoices =
        { "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll", "OptiScaler.asi" };
    public static readonly string[] ProxyDlls = ProxyChoices.Where(p => p.EndsWith(".dll")).ToArray();

    public const string HashStock = "E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E";
    public const string HashCrossGen = "E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A";
    public const string RuntimeName = "nvngx_dlssnr.dll";
    public const string ForwarderName = "nvngx.dll_dlssnr.dll";
    public const string ManifestName = ".optiscaler_injector.json";
    public const string BackupDir = ".optiscaler_injector_backup";

    /// <summary>发布包里不应复制进游戏目录的内容。</summary>
    public static readonly HashSet<string> PackageSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup_windows.bat", "setup_linux.sh", "get_streamline.ps1", "readme.md", "install-dlssnr.md",
        "license", "sha256sums.txt", "docs", "redist", "injector",
    };

    public static readonly string[] AntiCheat =
    {
        "EasyAntiCheat", "BattlEye", "BEService", "vgk.sys", "EAAntiCheat", "XIGNCODE", "nProtect", "GameGuard", "Ricochet", "FACEIT",
    };
}

public static class Hashing
{
    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}

public enum Level { None, Ok, Warn, Bad }

public static class GpuInfo
{
    public static (string Name, string Driver) Detect()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,driver_version --format=csv,noheader")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(8000)) { p.Kill(); return ("", ""); }
            if (!string.IsNullOrWhiteSpace(line))
            {
                var parts = line.Split(',', 2);
                return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "");
            }
        }
        catch { /* 没有 NVIDIA 驱动 */ }
        return ("", "");
    }

    /// <summary>"50" / "40" / "30" / "20" / ""</summary>
    public static string Generation(string name)
    {
        var m = Regex.Match(name, @"RTX\s*(?:PRO\s*)?(\d)0\d{2}", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value + "0" : "";
    }

    public static (Level, string) RuntimeVerdict(string? path, string gen)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (Level.None, "未选择 nvngx_dlssnr.dll(约 165MB,需自行获取)");
        var h = Hashing.Sha256(path);
        if (h == Const.HashCrossGen) return (Level.Ok, "ShortFuse 跨代 310.8 运行库(RTX 20/30/40/50 通用)");
        if (h == Const.HashStock)
        {
            if (gen is "" or "50") return (Level.Ok, "NVIDIA 原版 310.8(仅适用 RTX 50)");
            return (Level.Bad, $"这是 RTX 50 专用原版;当前 RTX {gen}80 级别显卡需要跨代版 (E67DEE...)");
        }
        return (Level.Warn, $"哈希 {h[..12]}... 不在文档记录内,无法确认来源与 GPU 兼容性");
    }
}
