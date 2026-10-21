using System.Diagnostics;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private const string Target = "Plain Craft Launcher 2.exe";
    private const string Patched = "Plain Craft Launcher 2.pcl-download.exe";
    private const string Message = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";

    private static int Main(string[] args)
    {
        string source;
        try { source = ResolveSource(args); }
        catch (ArgumentException ex) { return Fail(ex.Message); }

        var dir = Path.GetDirectoryName(source)!;
        var output = Path.Combine(dir, Patched);
        if (!File.Exists(source)) return Fail($"未找到目标程序：{source}");
        try
        {
            Patch(source, output);
            Verify(output);
            if (args.Contains("--patch-only", StringComparer.OrdinalIgnoreCase)) return 0;
            using var process = Process.Start(new ProcessStartInfo(output) { WorkingDirectory = dir, UseShellExecute = true }) ?? throw new InvalidOperationException("无法启动 PCL。");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) { return Fail("启动 PCL 失败。请确认当前目录可写，并关闭正在运行的 PCL 后重试。", ex); }
        finally { TryDelete(output); }
    }

    private static string ResolveSource(string[] args)
    {
        string? source = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--patch-only", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg.Equals("--target", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length) throw new ArgumentException("--target 后缺少目标程序路径。");
                source = args[i];
                continue;
            }
            if (arg.StartsWith("--target=", StringComparison.OrdinalIgnoreCase))
            {
                source = arg[9..];
                if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("--target= 后缺少目标程序路径。");
                continue;
            }
            if (source is not null) throw new ArgumentException("只能指定一个目标程序。");
            source = arg;
        }

        source ??= Path.Combine(AppContext.BaseDirectory, Target);
        return Path.GetFullPath(source);
    }

    private static void Patch(string source, string output)
    {
        TryDelete(output);
        File.Copy(source, output);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(output)!);
        using var assembly = AssemblyDefinition.ReadAssembly(output, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });
        var types = assembly.MainModule.Types.SelectMany(Flatten).ToList();
        var secret = types.First(t => t.Name == "ModSecret");
        secret.Fields.First(f => f.Name == "RestrictMcDownload").Constant = false;
        PatchLoader(types);
        PatchModify(types);
        PatchResourceDetail(types);
        PatchForm(types);
        PatchModpack(types);
        foreach (var method in types.SelectMany(t => t.Methods))
            foreach (var parameter in method.Parameters)
                if (parameter.HasConstant) parameter.Constant = null;
        var temp = output + ".tmp";
        TryDelete(temp);
        assembly.Write(temp, new WriterParameters { WriteSymbols = false });
        assembly.Dispose();
        File.Move(temp, output, true);
    }

    private static void PatchLoader(IReadOnlyCollection<TypeDefinition> types)
    {
        var type = types.First(t => t.Name == "PageDownloadInstall");
        var method = type.Methods.First(m => m.Name == "LoaderInit" && m.Parameters.Count == 0);
        var loader = types.First(t => t.Name == "ModDownload").Fields.First(f => f.Name == "DlClientListLoader");
        var disabled = method.Body.Instructions.First(i => i.OpCode == OpCodes.Ldstr && (string?)i.Operand == "关闭下载");
        var start = method.Body.Instructions.IndexOf(disabled);
        var end = method.Body.Instructions.Skip(start).First(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference r && r.DeclaringType.Name.StartsWith("LoaderTask", StringComparison.Ordinal));
        var endIndex = method.Body.Instructions.IndexOf(end);
        for (var i = endIndex; i >= start; i--) method.Body.Instructions.RemoveAt(i);
        method.Body.Instructions.Insert(start, Instruction.Create(OpCodes.Ldsfld, loader));
    }

    private static void PatchModify(IReadOnlyCollection<TypeDefinition> types)
    {
        var type = types.First(t => t.Name == "PageDownloadModify");
        var method = type.Methods.First(m => m.Name == "LoadInstanceFinished" && m.Parameters.Count == 1);
        for (var i = 0; i + 1 < method.Body.Instructions.Count; i++)
            if (method.Body.Instructions[i].OpCode == OpCodes.Ldc_I4_2 && method.Body.Instructions[i + 1].Operand is MethodReference r && r.Name == "set_Visibility")
            { method.Body.Instructions[i].OpCode = OpCodes.Ldc_I4_0; return; }
    }

    private static void PatchResourceDetail(IReadOnlyCollection<TypeDefinition> types)
    {
        var type = types.First(t => t.Name == "PageDownloadResourceDetail");
        var method = type.Methods.First(m => m.Name == "InitializeStackResourceVersions" && m.Parameters.Count == 1);
        var normal = method.Body.Instructions.Last(i => i.OpCode == OpCodes.Ldarg_1 &&
            method.Body.Instructions[method.Body.Instructions.IndexOf(i) + 1].Operand is MethodReference r && r.Name == "get_Children");
        var normalIndex = method.Body.Instructions.IndexOf(normal);
        var add = method.Body.Instructions.Skip(normalIndex).First(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "Add" && r.DeclaringType.Name == "UIElementCollection");
        var resourceType = type.Fields.First(f => f.Name == "TargetResourceType");
        var install = type.Methods.First(m => m.Name == "Install_Click" && m.Parameters.Count == 2);
        var save = type.Methods.First(m => m.Name == "Save_Click" && m.Parameters.Count == 2);
        var versionType = types.First(t => t.Name == "ResourceVersion");
        var toList = versionType.Methods.First(m => m.Name == "ToListItem" && m.Parameters.Count == 3);
        var listClick = types.First(t => t.Name == "ClickEventHandler" && t.DeclaringType?.Name == "MyListItem").Methods.First(m => m.IsConstructor && m.Parameters.Count == 2);
        var iconClick = types.First(t => t.Name == "ClickEventHandler" && t.DeclaringType?.Name == "MyIconButton").Methods.First(m => m.IsConstructor && m.Parameters.Count == 2);
        var versionLocal = method.Body.Variables.First(v => v.VariableType.FullName.Contains("ResourceVersion", StringComparison.Ordinal));
        var branchNormal = Instruction.Create(OpCodes.Bne_Un_S, normal);
        var branchAdd = Instruction.Create(OpCodes.Br_S, add);
        var injected = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, resourceType), Instruction.Create(OpCodes.Ldc_I4_2), branchNormal,
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Callvirt, (MethodReference)method.Body.Instructions[normalIndex + 1].Operand),
            Instruction.Create(OpCodes.Ldloc, versionLocal), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldftn, install),
            Instruction.Create(OpCodes.Newobj, listClick), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldftn, save),
            Instruction.Create(OpCodes.Newobj, iconClick), Instruction.Create(OpCodes.Ldloc_1), Instruction.Create(OpCodes.Callvirt, toList), branchAdd
        };
        foreach (var instruction in injected.AsEnumerable().Reverse()) method.Body.Instructions.Insert(normalIndex, instruction);
    }

    private static void PatchModpack(IReadOnlyCollection<TypeDefinition> types)
    {
        var type = types.First(t => t.Name == "ModModpack");
        var method = type.Methods.First(m => m.Name == "ModpackInstall" && m.Parameters.Count == 3);
        var restriction = method.Body.Instructions.First(i => i.OpCode == OpCodes.Ldstr && (string?)i.Operand == Message);
        var start = method.Body.Instructions.IndexOf(restriction);
        var finallyStart = method.Body.Instructions.Skip(start).First(i => i.OpCode == OpCodes.Ldloc_1);
        var finallyIndex = method.Body.Instructions.IndexOf(finallyStart);
        var result = Instruction.Create(OpCodes.Ldloc_0);
        var dispatch = new List<Instruction>();
        var cases = new[] { (0, "InstallPackCurseForge", 5), (1, "InstallPackHMCL", 3), (2, "InstallPackMMC", 3), (3, "InstallPackMCBBS", 4), (4, "InstallPackModrinth", 5), (9, "InstallPackLauncherPack", 3) };
        var labels = cases.Select(_ => Instruction.Create(OpCodes.Nop)).ToArray();
        for (var i = 0; i < cases.Length; i++)
        {
            dispatch.Add(Instruction.Create(OpCodes.Ldloc, method.Body.Variables[4]));
            dispatch.Add(Instruction.Create(OpCodes.Ldc_I4, cases[i].Item1));
            dispatch.Add(Instruction.Create(OpCodes.Beq, labels[i]));
        }
        var fallback = Instruction.Create(OpCodes.Nop);
        dispatch.Add(Instruction.Create(OpCodes.Br, fallback));
        for (var i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            dispatch.Add(labels[i]);
            dispatch.Add(Instruction.Create(OpCodes.Ldarg_0));
            dispatch.Add(Instruction.Create(OpCodes.Ldloc_1));
            dispatch.Add(Instruction.Create(OpCodes.Ldloc_2));
            if (item.Item3 >= 4) dispatch.Add(Instruction.Create(OpCodes.Ldarg_1));
            if (item.Item3 >= 5) dispatch.Add(Instruction.Create(OpCodes.Ldarg_2));
            var target = type.Methods.First(m => m.Name == item.Item2 && m.Parameters.Count == item.Item3);
            dispatch.Add(Instruction.Create(OpCodes.Call, target));
            dispatch.Add(Instruction.Create(OpCodes.Stloc_0));
            dispatch.Add(Instruction.Create(OpCodes.Leave, result));
        }
        var compress = type.Methods.First(m => m.Name == "InstallPackCompress" && m.Parameters.Count == 2);
        dispatch.Add(fallback);
        dispatch.Add(Instruction.Create(OpCodes.Ldarg_0));
        dispatch.Add(Instruction.Create(OpCodes.Ldloc_1));
        dispatch.Add(Instruction.Create(OpCodes.Call, compress));
        dispatch.Add(Instruction.Create(OpCodes.Stloc_0));
        dispatch.Add(Instruction.Create(OpCodes.Leave, result));
        for (var i = finallyIndex - 1; i >= start; i--) method.Body.Instructions.RemoveAt(i);
        var anchor = method.Body.Instructions.IndexOf(finallyStart);
        foreach (var instruction in dispatch.AsEnumerable().Reverse()) method.Body.Instructions.Insert(anchor, instruction);
        method.Body.Instructions.Add(result);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    private static void Verify(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
        var types = assembly.MainModule.Types.SelectMany(Flatten).ToList();
        var secret = types.First(t => t.Name == "ModSecret");
        if (!Equals(secret.Fields.First(f => f.Name == "RestrictMcDownload").Constant, false)) throw new InvalidDataException("RestrictMcDownload 未改为 False。");
        var loader = types.First(t => t.Name == "PageDownloadInstall").Methods.First(m => m.Name == "LoaderInit" && m.Parameters.Count == 0);
        if (!loader.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.Name == "DlClientListLoader")) throw new InvalidDataException("下载加载器未恢复。");
        var detail = types.First(t => t.Name == "PageDownloadResourceDetail").Methods.First(m => m.Name == "InitializeStackResourceVersions" && m.Parameters.Count == 1);
        if (!detail.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldftn && i.Operand is MethodReference m && m.Name == "Install_Click")) throw new InvalidDataException("整合包安装按钮未恢复。");
        var modpack = types.First(t => t.Name == "ModModpack").Methods.First(m => m.Name == "ModpackInstall" && m.Parameters.Count == 3);
        if (!modpack.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "InstallPackCurseForge")) throw new InvalidDataException("整合包分发逻辑未恢复。");
    }

    private static void PatchForm(IReadOnlyCollection<TypeDefinition> types)
    {
        var method = types.SelectMany(t => t.Methods).First(m => m.HasBody && m.DeclaringType.FullName.Contains("FormMain", StringComparison.Ordinal) && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string?)i.Operand == Message) && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "CompareString"));
        var index = method.Body.Instructions.ToList().FindIndex(i => i.OpCode == OpCodes.Ldstr && (string?)i.Operand == Message);
        var branch = method.Body.Instructions[index + 3];
        if (branch.OpCode == OpCodes.Brtrue_S || branch.OpCode == OpCodes.Brtrue) branch.OpCode = OpCodes.Br_S;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    { yield return type; foreach (var nested in type.NestedTypes.SelectMany(Flatten)) yield return nested; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint hWnd, string text, string caption, uint type);
    private static int Fail(string text, Exception? error = null) { try { MessageBox(0, text, "PCL Download Enable", 0x10); } catch { Console.Error.WriteLine(text); if (error is not null) Console.Error.WriteLine(error); } return 1; }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
