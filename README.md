# PCL Download Enable

这是一个 PCL / PCL-CE 的补丁启动器。它为原程序生成同目录的 `<原文件名>.Patch.exe`，原文件保持不变。

### [PCL](https://github.com/Meloong-Git/PCL)

<!-- prettier-ignore -->
| 已验证版本 | 补丁行为 |
| --- | --- |
| [2.99.0.0](https://github.com/Meloong-Git/PCL/raw/6b9a50df911c9d68b69b24c979efba477fb14b67/%E6%9C%80%E6%96%B0%E6%AD%A3%E5%BC%8F%E7%89%88.zip)([Commit 6b9a50d](https://github.com/Meloong-Git/PCL/commit/6b9a50df911c9d68b69b24c979efba477fb14b67))([Tag 2.99.0.0](https://github.com/Meloong-Git/PCL/releases/tag/2.99.0.0)) | 将 `RestrictMcDownload` 改为 `False`，恢复下载页和整合包安装入口 |
| [2.99.0.1](https://github.com/Meloong-Git/PCL/raw/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/%E6%9C%80%E6%96%B0%E6%AD%A3%E5%BC%8F%E7%89%88.zip)([Commit 93ede5d](https://github.com/Meloong-Git/PCL/commit/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5)) | 恢复下载侧栏、游戏列表、资源安装按钮和手动整合包安装入口 |

### [PCL-CE](https://github.com/PCL-Community/PCL-CE)

<!-- prettier-ignore -->
| 已验证版本 | 补丁行为 |
| --- | --- |
| [2.15.1.0](https://github.com/PCL-Community/PCL-CE/releases/download/v2.15.1/PCL2_CE_Release_x64.exe)([Release 2.15.1](https://github.com/PCL-Community/PCL-CE/releases/tag/v2.15.1)) | 使 `RestrictedFeaturePolicy.Check` 直接返回 `true` |

程序识别、版本区间和 IL 匹配规则位于 [`patch-profiles.json`](./patch-profiles.json)。配置随 exe 一起嵌入；适配新版本后需重新编译。

https://github.com/Meloong-Git/PCL/discussions/9512  
https://github.com/PCL-Community/PCL-CE/discussions/3670

![PCL2](./image.png)

## 使用

### 环境依赖

> 本项目需要 .NET Framework 4.8 运行环境  
> PCL 需要 [.NET Framework 4.8](https://shimo.im/docs/qKPttVvXKqPD8YDC#:~:text=%E4%B8%8B%E8%BD%BD%E5%B9%B6%E5%AE%89%E8%A3%85%20.NET%20Framework%204.8) 运行环境  
> PCL-CE 需要 [.NET 10 Desktop Runtime](https://github.com/PCL-Community/PCL-CE#-%E6%94%AF%E6%8C%81%E5%B9%B3%E5%8F%B0) 运行环境。

> 注：[.NET Framework 4.8](https://dotnet.microsoft.com/zh-cn/download/dotnet-framework/net48)自[2019 年 5 月 10 日更新（版本 1903）开始预装于Windows](https://learn.microsoft.com/zh-cn/dotnet/framework/install/versions-and-dependencies#net-framework-48)，更早的版本可能需要自行[下载 .NET Framework 4.8](https://dotnet.microsoft.com/zh-cn/download/dotnet-framework/thank-you/net48-web-installer)

### 使用步骤

1. 将 `PCL-Download-Enable.exe` 复制到 PCL 或 PCL-CE 所在目录。
2. 双击 `PCL-Download-Enable.exe`。

如果同目录同时存在两个目标，请将目标 exe 拖到 `PCL-Download-Enable.exe` 上，或在命令行中指定：

```powershell
.\PCL-Download-Enable.exe "D:\PCL\Plain Craft Launcher 2.exe"
.\PCL-Download-Enable.exe --target "D:\PCL-CE\PCL2_CE_Release_x64.exe"
```

启动器以目标所在目录作为工作目录生成并启动补丁副本。补丁副本会保留，之后可以直接双击它继续使用。你也依然可以通过本程序启动，每次会覆盖生成补丁副本。

执行过程会输出带时间戳的控制台日志。只有启动器发生异常时，才会把本次完整日志和异常堆栈写入启动器所在目录的 `PCL-Download-Enable-*.log`；若该目录不可写，则写入系统临时目录。错误窗口会显示日志路径，正常执行不生成日志文件。

更新目标程序后，可先用 `--patch-only --target <exe路径>` 验证。配置中的上界 `99.99.99.99` 允许新版本尝试现有规则，但不保证兼容；IL 结构变化时补丁会失败并提示前往 [GitHub 项目主页](https://github.com/cyb233/PCL-Download-Enable)检查更新或反馈。适配新版本时，在 `patch-profiles.json` 的对应程序下调整版本区间及匹配行为，再重新编译启动器。

## 构建

### 开发环境

- [.NET Framework 4.8](https://dotnet.microsoft.com/zh-cn/download/dotnet-framework/net48)

```powershell
dotnet build PclDownloadEnable.csproj -c Release
```

本地构建的窗口标题使用 `dev`；可通过 `-p:ReleaseTag=v0.0.5` 指定版本标识。GitHub Release 构建会自动写入发布 tag。

构建会生成单个 .NET Framework 4.8 单文件：`PCL-Download-Enable.exe`。`Mono.Cecil` 已在 Release 构建时合并进 exe，运行时只依赖系统已安装的 .NET Framework 4.8。
