using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PclPatch
{
    private static readonly PatchBehavior[] KnownBehaviors = {
        PatchBehavior.setConstant, PatchBehavior.restoreLoader, PatchBehavior.showCard,
        PatchBehavior.restoreResourceInstall, PatchBehavior.skipRestrictionBranch,
        PatchBehavior.restoreModpackDispatch, PatchBehavior.restoreDownloadSidebar,
        PatchBehavior.restoreManualModpackInstall
    };

    internal static void Apply(PatchTarget target)
    {
        var types = PatchTarget.Types(target.Assembly).ToList();
        foreach (var action in target.Profile.Actions)
        {
            RunLog.Info("PCL 补丁动作：" + action.Behavior);
            switch (action.Behavior)
            {
                case PatchBehavior.setConstant: SetConstant(types, action); break;
                case PatchBehavior.restoreLoader: RestoreLoader(types, action); break;
                case PatchBehavior.showCard: ShowCard(types, action); break;
                case PatchBehavior.restoreResourceInstall: RestoreResourceInstall(types, action); break;
                case PatchBehavior.skipRestrictionBranch: SkipRestrictionBranch(types, action); break;
                case PatchBehavior.restoreModpackDispatch: RestoreModpackDispatch(types, action); break;
                case PatchBehavior.restoreDownloadSidebar: RestoreDownloadSidebar(types, action); break;
                case PatchBehavior.restoreManualModpackInstall: RestoreManualModpackInstall(types, action); break;
                default: throw new InvalidDataException("未知 PCL 补丁行为：" + action.Behavior);
            }
        }

        // Older PCL builds contain optional enum defaults whose dependencies are absent from the single exe.
        foreach (var method in types.SelectMany(t => t.Methods))
            foreach (var parameter in method.Parameters)
                if (parameter.HasConstant) parameter.Constant = null;
    }

    internal static void Verify(PatchTarget target)
    {
        var types = PatchTarget.Types(target.Assembly).ToList();
        foreach (var action in target.Profile.Actions)
        {
            if (!KnownBehaviors.Contains(action.Behavior)) throw new InvalidDataException("未知 PCL 补丁行为：" + action.Behavior);
            var type = FindType(types, action.Get("type"));
            if (action.Behavior == PatchBehavior.setConstant)
            {
                if (!Equals(type.Fields.Single(f => f.Name == action.Get("field")).Constant, bool.Parse(action.Get("value"))))
                    throw new InvalidDataException("RestrictMcDownload 未改为 False。" );
                continue;
            }
            var method = FindMethod(type, action);
            if (action.Behavior == PatchBehavior.restoreLoader && !method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.Name == action.Get("sourceField")))
                throw new InvalidDataException("下载加载器未恢复。" );
            if (action.Behavior == PatchBehavior.restoreDownloadSidebar)
            {
                var instructions = method.Body.Instructions;
                if (instructions.Count != 8 || instructions[instructions.Count - 1].OpCode != OpCodes.Ret ||
                    IntegerConstant(instructions[3]) != 0 ||
                    !instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.Name == "InitializeComponent"))
                    throw new InvalidDataException("下载侧栏入口未恢复。" );
            }
            if (action.Behavior == PatchBehavior.restoreManualModpackInstall)
            {
                var instructions = method.Body.Instructions;
                if (!instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.Name == "SelectFile") ||
                    !instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.Name == "RunInNewThread") ||
                    !instructions.Any(i => i.OpCode == OpCodes.Ldftn && i.Operand is MethodReference m && m.Name == action.Get("workerMethod")) ||
                    !instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == action.Get("fileField")) ||
                    !instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == action.Get("filter")) ||
                    !instructions.Any(i => (i.OpCode == OpCodes.Brtrue || i.OpCode == OpCodes.Brtrue_S) &&
                        i.Operand is Instruction destination && destination.OpCode == OpCodes.Ret))
                    throw new InvalidDataException("手动整合包安装入口未恢复。" );
                VerifyControlFlow(method);
            }
            if (action.Behavior == PatchBehavior.restoreResourceInstall && !method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldftn && i.Operand is MethodReference m && m.Name == action.Get("installMethod")))
                throw new InvalidDataException("整合包安装按钮未恢复。" );
            if (action.Behavior == PatchBehavior.restoreModpackDispatch && (action.Cases == null || action.Cases.Any(c => !method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == c.Method))))
                throw new InvalidDataException("整合包分发逻辑未恢复。" );
            if (action.Behavior == PatchBehavior.restoreModpackDispatch)
            {
                VerifyControlFlow(method);
                var instructions = method.Body.Instructions;
                if (!method.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Finally &&
                    h.HandlerEnd == instructions[instructions.Count - 2] &&
                    instructions[instructions.Count - 1].OpCode == OpCodes.Ret))
                    throw new InvalidDataException("整合包分发方法的 finally 边界不正确。" );
            }
            if (action.Behavior == PatchBehavior.showCard && !method.Body.Instructions.Any(i =>
                    IntegerConstant(i) == int.Parse(action.Get("toValue")) &&
                    i.Next(method)?.Operand is MethodReference m && m.Name == action.Get("setter")))
                throw new InvalidDataException("版本修改卡片未恢复。" );
            if (action.Behavior == PatchBehavior.skipRestrictionBranch)
            {
                var instructions = method.Body.Instructions;
                var marker = instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == action.Get("marker"));
                if (instructions[instructions.IndexOf(marker) + int.Parse(action.Get("branchOffset"))].OpCode != OpCodes.Br_S)
                    throw new InvalidDataException("主页限制判断未恢复。" );
            }
        }
    }

    private static TypeDefinition FindType(IEnumerable<TypeDefinition> types, string name) =>
        types.Single(t => t.FullName == name);

    private static MethodDefinition FindMethod(TypeDefinition type, PatchAction action) =>
        type.Methods.Single(m => m.Name == action.Get("method") && m.Parameters.Count == int.Parse(action.Get("parameters")));

    private static void SetConstant(List<TypeDefinition> types, PatchAction action)
    {
        var field = FindType(types, action.Get("type")).Fields.Single(f => f.Name == action.Get("field"));
        if (!field.IsLiteral || field.FieldType.MetadataType != MetadataType.Boolean)
            throw new InvalidDataException("限制字段不是布尔常量。" );
        field.Constant = bool.Parse(action.Get("value"));
    }

    private static void RestoreLoader(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var loader = FindType(types, action.Get("sourceType")).Fields.Single(f => f.Name == action.Get("sourceField"));
        var instructions = method.Body.Instructions;
        var start = instructions.IndexOf(instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == action.Get("marker")));
        var end = instructions.Skip(start).First(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference r && r.DeclaringType.Name.StartsWith(action.Get("constructorType"), StringComparison.Ordinal));
        for (var i = instructions.IndexOf(end); i >= start; i--) instructions.RemoveAt(i);
        instructions.Insert(start, Instruction.Create(OpCodes.Ldsfld, loader));
    }

    private static void ShowCard(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var instructions = method.Body.Instructions;
        for (var i = 0; i + 1 < instructions.Count; i++)
            if (IntegerConstant(instructions[i]) == int.Parse(action.Get("fromValue")) &&
                instructions[i + 1].Operand is MethodReference r && r.Name == action.Get("setter"))
            { instructions[i].OpCode = OpCodes.Ldc_I4; instructions[i].Operand = int.Parse(action.Get("toValue")); return; }
        throw new InvalidDataException("未找到版本修改卡片的限制指令。" );
    }

    private static void RestoreResourceInstall(List<TypeDefinition> types, PatchAction action)
    {
        var type = FindType(types, action.Get("type"));
        var method = FindMethod(type, action);
        var instructions = method.Body.Instructions;
        var toList = FindType(types, action.Get("versionType")).Methods.Single(m => m.Name == action.Get("toListMethod") && m.Parameters.Count == 3);
        var toListCall = instructions.Single(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == toList.Name && r.DeclaringType.FullName == toList.DeclaringType.FullName);
        var normal = instructions.Take(instructions.IndexOf(toListCall)).Last(i => i.OpCode == OpCodes.Ldarg_1 &&
            instructions.IndexOf(i) + 1 < instructions.Count &&
            instructions[instructions.IndexOf(i) + 1].Operand is MethodReference r && r.Name == action.Get("childrenGetter"));
        var normalIndex = instructions.IndexOf(normal);
        var add = instructions.Skip(normalIndex).First(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == action.Get("addMethod"));
        var resourceType = type.Fields.Single(f => f.Name == action.Get("resourceField"));
        var install = type.Methods.Single(m => m.Name == action.Get("installMethod") && m.Parameters.Count == 2);
        var save = type.Methods.Single(m => m.Name == action.Get("saveMethod") && m.Parameters.Count == 2);
        var listClick = FindType(types, action.Get("listClickType")).Methods.Single(m => m.IsConstructor && m.Parameters.Count == 2);
        var iconClick = FindType(types, action.Get("iconClickType")).Methods.Single(m => m.IsConstructor && m.Parameters.Count == 2);
        var versionLocal = method.Body.Variables.Single(v => v.VariableType.FullName == action.Get("versionLocalType"));
        var duplicatesLocal = method.Body.Variables[int.Parse(action.Get("hasDuplicatesLocal"))];
        var injected = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, resourceType), Instruction.Create(OpCodes.Ldc_I4, int.Parse(action.Get("resourceValue"))), Instruction.Create(OpCodes.Bne_Un_S, normal),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Callvirt, (MethodReference)instructions[normalIndex + 1].Operand),
            Instruction.Create(OpCodes.Ldloc, versionLocal), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldftn, install),
            Instruction.Create(OpCodes.Newobj, listClick), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldftn, save),
            Instruction.Create(OpCodes.Newobj, iconClick), Instruction.Create(OpCodes.Ldloc, duplicatesLocal), Instruction.Create(OpCodes.Callvirt, toList), Instruction.Create(OpCodes.Br_S, add)
        };
        foreach (var instruction in injected.AsEnumerable().Reverse()) instructions.Insert(normalIndex, instruction);
    }

    private static void SkipRestrictionBranch(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var instructions = method.Body.Instructions;
        var index = instructions.IndexOf(instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == action.Get("marker")));
        var branch = instructions[index + int.Parse(action.Get("branchOffset"))];
        if (branch.OpCode != OpCodes.Brtrue_S && branch.OpCode != OpCodes.Brtrue)
            throw new InvalidDataException("主页限制判断与配置不符。" );
        branch.OpCode = OpCodes.Br_S;
    }

    private static void RestoreDownloadSidebar(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var instructions = method.Body.Instructions;
        if (instructions.Count != 23 || instructions[6].OpCode != OpCodes.Call ||
            !(instructions[6].Operand is MethodReference init) || init.Name != "InitializeComponent" ||
            instructions[7].OpCode != OpCodes.Ldarg_0 ||
            instructions[8].OpCode != OpCodes.Callvirt || !(instructions[8].Operand is MethodReference install) || install.Name != "get_ItemInstall" ||
            IntegerConstant(instructions[9]) != 2 ||
            instructions[10].OpCode != OpCodes.Callvirt || !(instructions[10].Operand is MethodReference installHide) || installHide.Name != "set_Visibility" ||
            instructions[11].OpCode != OpCodes.Ldarg_0 ||
            instructions[12].OpCode != OpCodes.Callvirt || !(instructions[12].Operand is MethodReference resource) || resource.Name != "get_LabResource" ||
            IntegerConstant(instructions[13]) != 2 ||
            instructions[14].OpCode != OpCodes.Callvirt || !(instructions[14].Operand is MethodReference resourceHide) || resourceHide.Name != "set_Visibility" ||
            instructions[15].OpCode != OpCodes.Ldarg_0 ||
            IntegerConstant(instructions[16]) != 2 ||
            instructions[17].OpCode != OpCodes.Stfld || !(instructions[17].Operand is FieldReference page) || page.Name != "PageID" ||
            instructions[18].OpCode != OpCodes.Ldarg_0 ||
            instructions[19].OpCode != OpCodes.Callvirt || !(instructions[19].Operand is MethodReference mod) || mod.Name != "get_ItemMod" ||
            IntegerConstant(instructions[20]) != 1 || instructions[21].OpCode != OpCodes.Callvirt ||
            !(instructions[21].Operand is MethodReference check) || check.Name != "set_Checked" || instructions[22].OpCode != OpCodes.Ret)
            throw new InvalidDataException("下载侧栏限制逻辑与配置不符。" );
        for (var i = instructions.Count - 1; i > 6; i--) instructions.RemoveAt(i);
        instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    private static void RestoreManualModpackInstall(List<TypeDefinition> types, PatchAction action)
    {
        var type = FindType(types, action.Get("type"));
        var method = FindMethod(type, action);
        var instructions = method.Body.Instructions;
        if (instructions.Count != 7 || instructions[0].OpCode != OpCodes.Newobj ||
            instructions[1].OpCode != OpCodes.Stloc_0 || instructions[2].OpCode != OpCodes.Ldstr ||
            (string)instructions[2].Operand != action.Get("marker") ||
            IntegerConstant(instructions[3]) != 2 || IntegerConstant(instructions[4]) != 1 ||
            instructions[5].OpCode != OpCodes.Call || !(instructions[5].Operand is MethodReference hint) || hint.Name != "Hint" ||
            instructions[6].OpCode != OpCodes.Ret)
            throw new InvalidDataException("手动整合包安装限制逻辑与配置不符。" );

        var workerType = FindType(types, action.Get("workerType"));
        var workerCtor = workerType.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 0);
        var worker = workerType.Methods.Single(m => m.Name == action.Get("workerMethod") && m.Parameters.Count == 0);
        var file = workerType.Fields.Single(f => f.Name == action.Get("fileField") && f.FieldType.MetadataType == MetadataType.String);
        if (!worker.Body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
            m.Name == "ModpackInstall" && m.Parameters.Count == 3))
            throw new InvalidDataException("整合包安装回调与配置不符。" );
        var dialog = FindType(types, "PCL.ModJava").Methods.Single(m => m.Name == "ManuallyImportJava");
        var dialogCalls = dialog.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToList();
        var selectFile = dialogCalls.Single(m => m.Name == "SelectFile" && m.DeclaringType.FullName == "MeloongCore.Wpf.Dialogs");
        var filter = dialogCalls.Single(m => m.Name == "op_Implicit" && m.DeclaringType.FullName.StartsWith("MeloongCore.OneOf`2<", StringComparison.Ordinal));
        var nullable = dialogCalls.Single(m => m.Name == ".ctor" && m.DeclaringType.FullName.StartsWith("System.Nullable`1<MeloongCore.OneOf`2<", StringComparison.Ordinal));
        var importConfig = FindType(types, "PCL.PageInstanceExport").Methods.Single(m => m.Name == "ImportConfig");
        var first = importConfig.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .Single(m => m.Name == "FirstOrDefault" && m.DeclaringType.FullName == "System.Linq.Enumerable");
        var fileMissing = importConfig.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .Single(m => m.Name == "IsNullOrEmpty" && m.DeclaringType.FullName == "System.String");
        var drag = FindType(types, "PCL.FormMain").Methods.Single(m => m.Name == "FileDrag");
        var dragCalls = drag.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToList();
        var actionCtor = dragCalls.Single(m => m.Name == ".ctor" && m.DeclaringType.FullName == "System.Action");
        var runThread = dragCalls.Single(m => m.Name == "RunInNewThread" && m.DeclaringType.FullName == "PCL.ModBase");
        var done = Instruction.Create(OpCodes.Ret);
        var replacement = new[]
        {
            Instruction.Create(OpCodes.Newobj, workerCtor), Instruction.Create(OpCodes.Stloc_0),
            Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Ldstr, action.Get("title")),
            Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ldnull),
            Instruction.Create(OpCodes.Ldstr, action.Get("filter")), Instruction.Create(OpCodes.Call, filter),
            Instruction.Create(OpCodes.Newobj, nullable), Instruction.Create(OpCodes.Call, selectFile),
            Instruction.Create(OpCodes.Call, first), Instruction.Create(OpCodes.Stfld, file),
            Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Ldfld, file),
            Instruction.Create(OpCodes.Call, fileMissing), Instruction.Create(OpCodes.Brtrue, done),
            Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Ldftn, worker),
            Instruction.Create(OpCodes.Newobj, actionCtor), Instruction.Create(OpCodes.Ldstr, action.Get("threadName")),
            Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Call, runThread),
            Instruction.Create(OpCodes.Pop), done
        };
        instructions.Clear();
        foreach (var instruction in replacement) instructions.Add(instruction);
        method.Body.MaxStackSize = 8;
    }

    private static void RestoreModpackDispatch(List<TypeDefinition> types, PatchAction action)
    {
        var type = FindType(types, action.Get("type"));
        var method = FindMethod(type, action);
        var instructions = method.Body.Instructions;
        var start = instructions.IndexOf(instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == action.Get("marker")));
        var finallyStart = instructions.Skip(start).First(i => i.OpCode.Code.ToString() == action.Get("finallyStartOpcode"));
        var resultLocal = method.Body.Variables[int.Parse(action.Get("resultLocal"))];
        var archiveLocal = method.Body.Variables[int.Parse(action.Get("archiveLocal"))];
        var baseLocal = method.Body.Variables[int.Parse(action.Get("baseLocal"))];
        var result = Instruction.Create(OpCodes.Ldloc, resultLocal);
        var dispatch = new List<Instruction>();
        var cases = action.Cases ?? throw new InvalidDataException("缺少整合包分发表。" );
        var labels = cases.Select(_ => Instruction.Create(OpCodes.Nop)).ToArray();
        for (var i = 0; i < cases.Length; i++)
        {
            dispatch.Add(Instruction.Create(OpCodes.Ldloc, method.Body.Variables[int.Parse(action.Get("kindLocal"))]));
            dispatch.Add(Instruction.Create(OpCodes.Ldc_I4, cases[i].Value));
            dispatch.Add(Instruction.Create(OpCodes.Beq, labels[i]));
        }
        var fallback = Instruction.Create(OpCodes.Nop);
        dispatch.Add(Instruction.Create(OpCodes.Br, fallback));
        for (var i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            dispatch.Add(labels[i]);
            dispatch.Add(Instruction.Create(OpCodes.Ldarg_0));
            dispatch.Add(Instruction.Create(OpCodes.Ldloc, archiveLocal));
            dispatch.Add(Instruction.Create(OpCodes.Ldloc, baseLocal));
            if (item.Parameters >= 4) dispatch.Add(Instruction.Create(OpCodes.Ldarg_1));
            if (item.Parameters >= 5) dispatch.Add(Instruction.Create(OpCodes.Ldarg_2));
            dispatch.Add(Instruction.Create(OpCodes.Call, type.Methods.Single(m => m.Name == item.Method && m.Parameters.Count == item.Parameters)));
            dispatch.Add(Instruction.Create(OpCodes.Stloc, resultLocal));
            dispatch.Add(Instruction.Create(OpCodes.Leave, result));
        }
        dispatch.Add(fallback);
        dispatch.Add(Instruction.Create(OpCodes.Ldarg_0));
        dispatch.Add(Instruction.Create(OpCodes.Ldloc, archiveLocal));
        dispatch.Add(Instruction.Create(OpCodes.Call, type.Methods.Single(m => m.Name == action.Get("fallbackMethod") && m.Parameters.Count == 2)));
        dispatch.Add(Instruction.Create(OpCodes.Stloc, resultLocal));
        dispatch.Add(Instruction.Create(OpCodes.Leave, result));
        var removed = instructions.Skip(start).Take(instructions.IndexOf(finallyStart) - start).ToList();
        var firstDispatch = dispatch[0];
        foreach (var instruction in instructions)
        {
            if (instruction.Operand is Instruction destination && removed.Contains(destination))
                instruction.Operand = firstDispatch;
        }
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (removed.Contains(handler.TryStart)) handler.TryStart = firstDispatch;
            if (removed.Contains(handler.TryEnd)) handler.TryEnd = firstDispatch;
            if (removed.Contains(handler.HandlerStart)) handler.HandlerStart = firstDispatch;
            if (removed.Contains(handler.HandlerEnd)) handler.HandlerEnd = firstDispatch;
        }
        for (var i = instructions.IndexOf(finallyStart) - 1; i >= start; i--) instructions.RemoveAt(i);
        var anchor = instructions.IndexOf(finallyStart);
        foreach (var instruction in dispatch.AsEnumerable().Reverse()) instructions.Insert(anchor, instruction);
        instructions.Add(result);
        instructions.Add(Instruction.Create(OpCodes.Ret));
        foreach (var handler in method.Body.ExceptionHandlers.Where(h => h.HandlerType == ExceptionHandlerType.Finally &&
                     h.HandlerStart == finallyStart && h.HandlerEnd == null))
            handler.HandlerEnd = result;
    }

    private static Instruction? Next(this Instruction instruction, MethodDefinition method)
    {
        var index = method.Body.Instructions.IndexOf(instruction);
        return index + 1 < method.Body.Instructions.Count ? method.Body.Instructions[index + 1] : null;
    }

    private static int? IntegerConstant(Instruction instruction)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Ldc_I4_0: return 0;
            case Code.Ldc_I4_1: return 1;
            case Code.Ldc_I4_2: return 2;
            case Code.Ldc_I4_3: return 3;
            case Code.Ldc_I4_4: return 4;
            case Code.Ldc_I4_5: return 5;
            case Code.Ldc_I4_6: return 6;
            case Code.Ldc_I4_7: return 7;
            case Code.Ldc_I4_8: return 8;
            case Code.Ldc_I4: return (int)instruction.Operand;
            case Code.Ldc_I4_S: return (sbyte)instruction.Operand;
            default: return null;
        }
    }

    private static void VerifyControlFlow(MethodDefinition method)
    {
        var instructions = new HashSet<Instruction>(method.Body.Instructions);
        foreach (var instruction in instructions)
            if (instruction.Operand is Instruction target && !instructions.Contains(target))
                throw new InvalidDataException("补丁包含失效的 IL 跳转目标。" );
        foreach (var handler in method.Body.ExceptionHandlers)
            if ((handler.TryStart != null && !instructions.Contains(handler.TryStart)) ||
                (handler.TryEnd != null && !instructions.Contains(handler.TryEnd)) ||
                (handler.HandlerStart != null && !instructions.Contains(handler.HandlerStart)) ||
                (handler.HandlerEnd != null && !instructions.Contains(handler.HandlerEnd)))
                throw new InvalidDataException("补丁包含失效的异常处理区边界。" );
    }
}
