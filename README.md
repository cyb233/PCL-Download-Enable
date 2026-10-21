# PCL Download Enable

这是一个同目录启动器，用于启动 `Plain Craft Launcher 2.exe` 时将 `RestrictMcDownload` 改为 `False`，并恢复下载页使用正常的 Minecraft 列表加载器。

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

- .NET 8

```powershell
dotnet publish -c Release -r win-x86 --self-contained true
```

发布配置启用单文件、自包含和 x86，适配当前 PCL2 的 .NET Framework x86 程序。
