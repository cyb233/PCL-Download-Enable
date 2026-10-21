# PCL Download Enable

这是一个同目录启动器，用于启动 `Plain Craft Launcher 2.exe` 时将 [`RestrictMcDownload`](https://github.com/Meloong-Git/PCL/blob/6b9a50df911c9d68b69b24c979efba477fb14b67/Plain%20Craft%20Launcher%202/Modules/ModSecret.vb#L18) 改为 `False`，并恢复下载页使用正常的 Minecraft 列表加载器。

https://github.com/Meloong-Git/PCL/discussions/9512

> 当前确定适配版本 `2.99.0.0`

![PCL2](./image.png)

## 使用

1. 将 `PCL-Download-Enable.exe` 复制到 `Plain Craft Launcher 2.exe` 所在目录。
2. 双击 `PCL-Download-Enable.exe`。

也可以将 `Plain Craft Launcher 2.exe` 直接拖到 `PCL-Download-Enable.exe` 上，或在命令行中指定目标程序：

```powershell
PCL-Download-Enable.exe "D:\PCL\Plain Craft Launcher 2.exe"
PCL-Download-Enable.exe --target "D:\PCL\Plain Craft Launcher 2.exe"
```

启动器会在原目录生成 `<原文件名>.Patch.exe`，以原目录作为工作目录启动 PCL2。补丁副本会保留，之后可以直接双击它继续使用；原始 PCL 文件不会被修改。

## 构建

### 开发环境

- .NET Framework 4.8
  > [与 PCL2 需求一致](https://shimo.im/docs/qKPttVvXKqPD8YDC#:~:text=%E4%B8%8B%E8%BD%BD%E5%B9%B6%E5%AE%89%E8%A3%85%20.NET%20Framework%204.8)

```powershell
dotnet build PclDownloadEnable.csproj -c Release
```

构建会生成单个 .NET Framework 4.8 单文件：`PCL-Download-Enable.exe`。`Mono.Cecil` 已在 Release 构建时合并进 exe，运行时只依赖系统已安装的 .NET Framework 4.8。
