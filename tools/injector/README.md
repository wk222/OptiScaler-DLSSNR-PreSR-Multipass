# OptiScaler Injector (GUI)

一个原生 Windows 桌面程序(C# / WPF,.NET 8),用来把这套 OptiScaler DLSS-NR 注入到所选游戏里。
替代手动解压 + 运行 `setup_windows.bat`。

## 使用

1. 运行 `OptiScalerInjector.exe`(需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0))。
2. **发布包**:点"从 GitHub 下载…"(先查本 fork,再查上游 `wilsjo2/...`,下载后校验 SHA-256),
   或选择本地解压目录 / zip。程序放在发布包的 `Injector\` 里或源码仓库 `tools\injector` 里时会自动找到包。
3. **NR 运行库**:默认使用 ShortFuse `310.8.SF-v2`
   ([RankFTW/rhi-repo `dlssnr-310.8.SF-v2`](https://github.com/RankFTW/rhi-repo/releases/tag/dlssnr-310.8.SF-v2),
   SHA-256 `6EB209E7…3927`)。点"下载默认 SF-v2"自动下载并校验,或"选择 dll / zip…"指向已下载的文件(zip 会自动解压)。
   运行库不随发布包分发,只在用户机器上获取。也可换成其它版本:文档记录的哈希
   (原版 `E16BCF15…` 仅 RTX 50;跨代版 `E67DEE20…`)会被识别,其余哈希给出警告;原版放在非 RTX 50 上会被拦截。
4. 左侧选游戏(自动扫 Steam / Epic / GOG,也可手动添加),右侧确认 exe 目录,点 **注入**。
5. 游戏内按 `Insert` 打开菜单。**卸载并还原** 按安装记录删除文件并还原被备份的原文件。

## 行为约定

- 安装记录 `.optiscaler_injector.json` 与备份 `.optiscaler_injector_backup\` 都放在游戏 exe 目录。
- `OptiScaler.dll` 改名为所选代理名(默认 `dxgi.dll`);已有同名文件(如 ReShade)先备份,卸载还原。
- `OptiScaler.ini` 只修改指定 key,`TargetProcessName` 强制为 `auto`,UTF-8 无 BOM。
- 不复制:`setup_*`、`*.md`、`docs\`、`redist\`、`LICENSE`、`Injector\`。
- 检测到反作弊文件名特征(EasyAntiCheat / BattlEye / Vanguard 等)会弹窗确认;这只是启发式。

## 构建

```powershell
# 开发
dotnet build tools\injector\OptiScalerInjector.csproj -c Release
# 自检(假包 + 假游戏目录,验证安装 / 备份 / 卸载还原 / INI 补丁)
dotnet run --project tools\injector\SelfTest -c Release
# 发布单文件 exe(~250 KB,框架依赖)
dotnet publish tools\injector\OptiScalerInjector.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o tools\injector\publish
```

`package_release.ps1` 会自动发布并把 exe 放进发布包的 `Injector\` 目录。

## 已知限制 / 下一步

- 不探测游戏用的是 DX11 / DX12 / Vulkan,相关 `Dx11Upscaler` / `Dx12Upscaler` 要手动勾选。
- 未扫描 Xbox / MS Store 游戏(这类游戏建议 `winmm.dll` / `version.dll` 代理)。
- exe 目录是启发式判定(体积 + UE `Binaries\Win64`),判错时用"浏览…"手选。
- 自包含发布(无需安装运行时)需要下载约 70MB 的 WindowsDesktop runtime pack,
  `dotnet publish ... --self-contained true` 即可,网络慢时可能很久。
