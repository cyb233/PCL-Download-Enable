using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string ProjectUrl = "https://github.com/cyb233/PCL-Download-Enable";

    private static int Main(string[] args)
    {
        var patchStarted = false;
        try
        {
            var profiles = PatchProfiles.Load();
            var source = ResolveSource(args, profiles);
            var output = Path.Combine(Path.GetDirectoryName(source)!,
                Path.GetFileNameWithoutExtension(source) + ".Patch.exe");
            if (Path.GetFileNameWithoutExtension(source).EndsWith(".Patch", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("目标程序不能是补丁副本。");

            patchStarted = true;
            var target = profiles.Identify(source);
            var temporary = output + ".tmp";
            try
            {
                if (target.Bundle != null)
                {
                    PclCePatch.Apply(target);
                    File.WriteAllBytes(temporary, target.Bundle.Data);
                }
                else
                {
                    PclPatch.Apply(target);
                    target.Assembly.Write(temporary);
                }
                profiles.Verify(temporary, target);
                if (File.Exists(output)) File.Delete(output);
                File.Move(temporary, output);
            }
            finally
            {
                target.Dispose();
                TryDelete(temporary);
            }

            if (args.Any(a => a.Equals("--patch-only", StringComparison.OrdinalIgnoreCase))) return 0;
            using (var process = Process.Start(new ProcessStartInfo(output)
                   { WorkingDirectory = Path.GetDirectoryName(source)!, UseShellExecute = true })
                   ?? throw new InvalidOperationException("无法启动补丁副本。"))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("PCL_PATCH_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            return patchStarted ? FailPatch(ex.Message) : Fail(ex.Message);
        }
    }

    private static string ResolveSource(string[] args, PatchProfiles profiles)
    {
        string? source = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--patch-only", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg.Equals("--target", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length) throw new ArgumentException("--target 后缺少目标程序路径。");
                arg = args[i];
            }
            else if (arg.StartsWith("--target=", StringComparison.OrdinalIgnoreCase))
            {
                arg = arg.Substring(9);
            }
            if (source != null) throw new ArgumentException("只能指定一个目标程序。");
            source = arg;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            var candidates = profiles.DefaultFileNames
                .Select(name => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name))
                .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count != 1)
                throw new FileNotFoundException(candidates.Count == 0
                    ? "未找到受支持的程序。请把启动器放在 PCL/PCL-CE 同目录，或传入目标 exe 路径。"
                    : "当前目录有多个受支持的程序，请拖拽目标 exe 或用 --target 指定。");
            source = candidates[0];
        }
        var path = Path.GetFullPath(source);
        if (!File.Exists(path)) throw new FileNotFoundException("未找到目标程序：" + path);
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标程序必须是 exe 文件。" );
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static int FailPatch(string message)
    {
        var choice = MessageBox(IntPtr.Zero,
            "补丁失败：" + message + "\n\n请到 GitHub 检查更新或反馈问题：\n" + ProjectUrl +
            "\n\n点击“确定”打开项目主页；点击“取消”或关闭窗口退出。",
            "PCL Download Enable", 0x11);
        if (choice == 1)
        {
            try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Fail("无法打开项目主页：" + ex.Message + "\n" + ProjectUrl); }
        }
        return 1;
    }

    private static int Fail(string message)
    {
        MessageBox(IntPtr.Zero, message, "PCL Download Enable", 0x10);
        return 1;
    }
}
