using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class AssemblyPatch
{
    private static readonly PatchBehavior[] KnownBehaviors = {
        PatchBehavior.SetFieldConstant, PatchBehavior.ReplaceInstructionRange,
        PatchBehavior.ReplaceConstant, PatchBehavior.InsertInstructionSequence,
        PatchBehavior.ReplaceBranch, PatchBehavior.ReplaceMethodBody,
        PatchBehavior.ClearParameterConstants
    };

    internal static void Apply(PatchTarget target)
    {
        var types = PatchTarget.Types(target.Assembly).ToList();
        foreach (var action in target.Profile.Actions)
        {
            RunLog.Info("PCL 补丁动作：" + action.Behavior + (string.IsNullOrWhiteSpace(action.Note) ? "" : "：" + action.Note));
            switch (action.Behavior)
            {
                case PatchBehavior.SetFieldConstant: SetFieldConstant(types, action); break;
                case PatchBehavior.ReplaceInstructionRange: ReplaceRange(types, action); break;
                case PatchBehavior.ReplaceConstant: ReplaceConstant(types, action); break;
                case PatchBehavior.InsertInstructionSequence: InsertSequence(types, action); break;
                case PatchBehavior.ReplaceBranch: ReplaceBranch(types, action); break;
                case PatchBehavior.ReplaceMethodBody: ReplaceBody(types, action); break;
                case PatchBehavior.ClearParameterConstants: ClearParameterConstants(types); break;
                default: throw new InvalidDataException("未知 PCL 补丁行为：" + action.Behavior);
            }
        }
    }

    internal static void Verify(PatchTarget target)
    {
        var types = PatchTarget.Types(target.Assembly).ToList();
        foreach (var action in target.Profile.Actions)
        {
            if (!KnownBehaviors.Contains(action.Behavior)) throw new InvalidDataException("未知 PCL 补丁行为：" + action.Behavior);
            if (action.Behavior == PatchBehavior.ClearParameterConstants) continue;
            var type = FindType(types, action.Get("type"));
            if (action.Behavior == PatchBehavior.SetFieldConstant)
            {
                var field = type.Fields.Single(f => f.Name == action.Get("field"));
                if (!Equals(field.Constant, ParseConstant(field.FieldType, action.Get("value"))))
                    throw new InvalidDataException("字段常量未改为配置值。" );
                continue;
            }
            var method = FindMethod(type, action);
            if (action.Behavior == PatchBehavior.ReplaceBranch)
            {
                var branch = FindPattern(method, action.Start) + action.Start.Offset;
                if (branch < 0 || branch >= method.Body.Instructions.Count ||
                    method.Body.Instructions[branch].OpCode.Code.ToString() != action.Code.Single().OpCode)
                    throw new InvalidDataException("分支指令未改为配置值。" );
            }
            VerifyControlFlow(method);
        }
    }

    private static void SetFieldConstant(List<TypeDefinition> types, PatchAction action)
    {
        var field = FindType(types, action.Get("type")).Fields.Single(f => f.Name == action.Get("field"));
        if (!field.IsLiteral) throw new InvalidDataException("目标字段不是常量。" );
        field.Constant = ParseConstant(field.FieldType, action.Get("value"));
    }

    private static void ReplaceConstant(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var index = FindPattern(method, action.Start);
        if (!string.IsNullOrWhiteSpace(action.End.Member))
        {
            while (index + 1 < method.Body.Instructions.Count &&
                   ((method.Body.Instructions[index + 1].Operand as MethodReference)?.Name != action.End.Member &&
                    (method.Body.Instructions[index + 1].Operand as FieldReference)?.Name != action.End.Member))
                index = FindPattern(method, action.Start, index + 1);
        }
        var replacement = action.Code.Single(c => !string.IsNullOrWhiteSpace(c.OpCode));
        var instruction = CreateInstruction(replacement, method, method.Body.Instructions, new Dictionary<string, Instruction>());
        method.Body.Instructions[index] = instruction;
    }

    private static void ReplaceBranch(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var index = FindPattern(method, action.Start) + action.Start.Offset;
        if (index < 0 || index >= method.Body.Instructions.Count) throw new InvalidDataException("找不到配置指定的分支位置。" );
        var op = GetOpCode(action.Code.Single().OpCode);
        method.Body.Instructions[index].OpCode = op;
    }

    private static void InsertSequence(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var anchor = FindPattern(method, action.Start);
        var built = BuildCode(action.Code, method, method.Body.Instructions);
        var position = action.Selectors.TryGetValue("position", out var value) && value == "after" ? anchor + 1 : anchor;
        foreach (var instruction in built) method.Body.Instructions.Insert(position++, instruction);
    }

    private static void ReplaceBody(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var built = BuildCode(action.Code, method, method.Body.Instructions);
        method.Body.Instructions.Clear();
        foreach (var instruction in built) method.Body.Instructions.Add(instruction);
        method.Body.ExceptionHandlers.Clear();
        method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 8);
    }

    private static void ReplaceRange(List<TypeDefinition> types, PatchAction action)
    {
        var method = FindMethod(FindType(types, action.Get("type")), action);
        var instructions = method.Body.Instructions;
        var start = FindPattern(method, action.Start);
        var end = FindPattern(method, action.End, start);
        var removeCount = end - start + (action.End.Inclusive ? 1 : 0);
        var removed = instructions.Skip(start).Take(removeCount).ToList();
        var labels = new Dictionary<string, Instruction>(StringComparer.Ordinal);
        var built = BuildCode(action.Code ?? new PatchCode[0], method, instructions, labels);
        var appended = BuildCode(action.Append ?? new PatchCode[0], method, instructions, labels);
        ResolveLabels(built.Concat(appended), action.Code.Concat(action.Append), labels);
        var first = built.FirstOrDefault() ?? throw new InvalidDataException("替换指令不能为空。" );
        foreach (var instruction in instructions)
            if (instruction.Operand is Instruction destination && removed.Contains(destination)) instruction.Operand = first;
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (removed.Contains(handler.TryStart)) handler.TryStart = first;
            if (removed.Contains(handler.TryEnd)) handler.TryEnd = first;
            if (removed.Contains(handler.HandlerStart)) handler.HandlerStart = first;
            if (removed.Contains(handler.HandlerEnd)) handler.HandlerEnd = first;
            if (!action.End.Inclusive && handler.HandlerStart == instructions[end] && handler.HandlerEnd == null && appended.Count > 0)
                handler.HandlerEnd = appended[0];
        }
        for (var i = 0; i < removeCount; i++) instructions.RemoveAt(start);
        for (var i = appended.Count - 1; i >= 0; i--) instructions.Add(appended[i]);
        for (var i = built.Count - 1; i >= 0; i--) instructions.Insert(start, built[i]);
    }

    private static void ClearParameterConstants(List<TypeDefinition> types)
    {
        foreach (var method in types.SelectMany(t => t.Methods))
            foreach (var parameter in method.Parameters)
                if (parameter.HasConstant) parameter.Constant = null;
    }

    private static int FindPattern(MethodDefinition method, PatchPattern pattern, int from = 0)
    {
        for (var i = from; i < method.Body.Instructions.Count; i++)
            if (Matches(method.Body.Instructions[i], pattern) &&
                (string.IsNullOrWhiteSpace(pattern.NextMember) || (i + 1 < method.Body.Instructions.Count &&
                    ((method.Body.Instructions[i + 1].Operand as MethodReference)?.Name == pattern.NextMember ||
                     (method.Body.Instructions[i + 1].Operand as FieldReference)?.Name == pattern.NextMember)))) return i;
        throw new InvalidDataException("找不到配置指定的 IL 匹配位置。" );
    }

    private static bool Matches(Instruction instruction, PatchPattern pattern)
    {
        if (!string.IsNullOrWhiteSpace(pattern.OpCode) && instruction.OpCode.Code.ToString() != pattern.OpCode) return false;
        if (!string.IsNullOrWhiteSpace(pattern.Operand))
        {
            if (pattern.Operand.StartsWith("i4:", StringComparison.Ordinal) && IntegerConstant(instruction) != int.Parse(pattern.Operand.Substring(3), CultureInfo.InvariantCulture)) return false;
            if (pattern.Operand.StartsWith("str:", StringComparison.Ordinal) && !(instruction.Operand is string s && s == pattern.Operand.Substring(4))) return false;
        }
        if (!string.IsNullOrWhiteSpace(pattern.Member))
        {
            var name = (instruction.Operand as MethodReference)?.Name ?? (instruction.Operand as FieldReference)?.Name;
            if (name != pattern.Member) return false;
        }
        if (!string.IsNullOrWhiteSpace(pattern.DeclaringType))
        {
            var declaring = (instruction.Operand as MethodReference)?.DeclaringType.FullName ?? (instruction.Operand as FieldReference)?.DeclaringType.FullName ?? "";
            if (declaring.IndexOf(pattern.DeclaringType, StringComparison.Ordinal) < 0) return false;
        }
        return true;
    }

    private static List<Instruction> BuildCode(IEnumerable<PatchCode> specs, MethodDefinition method, IList<Instruction> existing)
    {
        var labels = new Dictionary<string, Instruction>(StringComparer.Ordinal);
        var result = BuildCode(specs, method, existing, labels);
        ResolveLabels(result, specs, labels);
        return result;
    }

    private static List<Instruction> BuildCode(IEnumerable<PatchCode> specs, MethodDefinition method, IList<Instruction> existing, IDictionary<string, Instruction> labels)
    {
        var result = new List<Instruction>();
        var specIndexes = new Dictionary<PatchCode, int>();
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.OpCode) && !string.IsNullOrWhiteSpace(spec.Label))
            {
                var nop = Instruction.Create(OpCodes.Nop);
                result.Add(nop); labels[spec.Label] = nop; continue;
            }
            result.Add(CreateInstruction(spec, method, existing, labels));
            specIndexes[spec] = result.Count - 1;
            if (!string.IsNullOrWhiteSpace(spec.Label)) labels[spec.Label] = result[result.Count - 1];
        }
        return result;
    }

    private static void ResolveLabels(IEnumerable<Instruction> instructions, IEnumerable<PatchCode> specs, IDictionary<string, Instruction> labels)
    {
        var result = instructions.ToList();
        var index = 0;
        foreach (var spec in specs)
        {
            if (!string.IsNullOrWhiteSpace(spec.OpCode) && !string.IsNullOrWhiteSpace(spec.Operand) && spec.Operand.StartsWith("label:", StringComparison.Ordinal))
                result[index].Operand = labels[spec.Operand.Substring(6)];
            index++;
        }
    }

    private static Instruction CreateInstruction(PatchCode spec, MethodDefinition method, IList<Instruction> existing, IDictionary<string, Instruction> labels)
    {
        var op = GetOpCode(spec.OpCode);
        var operand = spec.Operand ?? "";
        if (operand.StartsWith("label:", StringComparison.Ordinal)) return Instruction.Create(op, Instruction.Create(OpCodes.Nop));
        if (operand.StartsWith("existing:", StringComparison.Ordinal))
        {
            var found = FindExisting(existing, operand.Substring(9));
            if (op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Cond_Branch)
                return Instruction.Create(op, found);
            if (found.Operand is MethodReference methodReference) return Instruction.Create(op, methodReference);
            if (found.Operand is FieldReference fieldReference) return Instruction.Create(op, fieldReference);
            throw new InvalidDataException("现有 IL 目标不是成员引用：" + operand);
        }
        if (operand == "null" || operand.Length == 0) return Instruction.Create(op);
        if (operand.StartsWith("i4:", StringComparison.Ordinal)) return Instruction.Create(op, int.Parse(operand.Substring(3), CultureInfo.InvariantCulture));
        if (operand.StartsWith("str:", StringComparison.Ordinal)) return Instruction.Create(op, operand.Substring(4));
        if (operand.StartsWith("local:", StringComparison.Ordinal)) return Instruction.Create(op, method.Body.Variables[int.Parse(operand.Substring(6), CultureInfo.InvariantCulture)]);
        if (operand.StartsWith("arg:", StringComparison.Ordinal)) return Instruction.Create(op, method.Parameters[int.Parse(operand.Substring(4), CultureInfo.InvariantCulture)]);
        if (operand.StartsWith("method:", StringComparison.Ordinal) || operand.StartsWith("ref:", StringComparison.Ordinal)) return Instruction.Create(op, ResolveMethod(method, operand.Substring(operand.IndexOf(':') + 1)));
        if (operand.StartsWith("field:", StringComparison.Ordinal)) return Instruction.Create(op, ResolveField(method, operand.Substring(6)));
        throw new InvalidDataException("无法解析 IL 操作数：" + operand);
    }

    private static Instruction FindExisting(IList<Instruction> instructions, string key)
    {
        var parts = key.Split(new[] { ':' }, 2);
        foreach (var instruction in instructions)
            if ((parts.Length == 1 && ((instruction.Operand as MethodReference)?.Name == parts[0] || (instruction.Operand as FieldReference)?.Name == parts[0])) ||
                (parts.Length == 2 && instruction.OpCode.Code.ToString() == parts[0] && ((instruction.Operand as MethodReference)?.Name == parts[1] || (instruction.Operand as FieldReference)?.Name == parts[1]))) return instruction;
        throw new InvalidDataException("找不到现有 IL 目标：" + key);
    }

    private static MethodReference ResolveMethod(MethodDefinition method, string token)
    {
        var split = token.Split(new[] { "::" }, StringSplitOptions.None);
        var typeName = split.Length == 2 ? split[0] : "";
        var nameAndCount = split.Length == 2 ? split[1] : split[0];
        var slash = nameAndCount.LastIndexOf('/');
        var name = slash < 0 ? nameAndCount : nameAndCount.Substring(0, slash);
        var count = slash < 0 ? -1 : int.Parse(nameAndCount.Substring(slash + 1), CultureInfo.InvariantCulture);
        var all = PatchTarget.Types(method.Module.Assembly).SelectMany(t => t.Methods).Cast<MethodReference>()
            .Concat(method.Module.Types.SelectMany(FlattenMethods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>());
        return all.First(m => m.Name == name && (count < 0 || m.Parameters.Count == count) &&
            (string.IsNullOrWhiteSpace(typeName) || m.DeclaringType.FullName.IndexOf(typeName, StringComparison.Ordinal) >= 0));
    }

    private static IEnumerable<MethodDefinition> FlattenMethods(TypeDefinition type) => type.Methods.Concat(type.NestedTypes.SelectMany(FlattenMethods));

    private static FieldReference ResolveField(MethodDefinition method, string token)
    {
        var split = token.Split(new[] { "::" }, StringSplitOptions.None);
        var typeName = split.Length == 2 ? split[0] : "";
        var name = split.Length == 2 ? split[1] : split[0];
        return PatchTarget.Types(method.Module.Assembly).SelectMany(t => t.Fields).Cast<FieldReference>()
            .Concat(method.Module.Types.SelectMany(FlattenFields).SelectMany(t => t.Fields).Cast<FieldReference>())
            .First(f => f.Name == name && (string.IsNullOrWhiteSpace(typeName) || f.DeclaringType.FullName.IndexOf(typeName, StringComparison.Ordinal) >= 0));
    }

    private static IEnumerable<TypeDefinition> FlattenFields(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(FlattenFields));

    private static TypeDefinition FindType(IEnumerable<TypeDefinition> types, string name) => types.Single(t => t.FullName == name);
    private static MethodDefinition FindMethod(TypeDefinition type, PatchAction action) => type.Methods.Single(m => m.Name == action.Get("method") && m.Parameters.Count == int.Parse(action.Get("parameters"), CultureInfo.InvariantCulture));
    private static OpCode GetOpCode(string name) => (OpCode)(typeof(OpCodes).GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? throw new InvalidDataException("未知 IL 操作码：" + name));

    private static int? IntegerConstant(Instruction instruction)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Ldc_I4_M1: return -1; case Code.Ldc_I4_0: return 0; case Code.Ldc_I4_1: return 1;
            case Code.Ldc_I4_2: return 2; case Code.Ldc_I4_3: return 3; case Code.Ldc_I4_4: return 4; case Code.Ldc_I4_5: return 5;
            case Code.Ldc_I4_6: return 6; case Code.Ldc_I4_7: return 7; case Code.Ldc_I4_8: return 8;
            case Code.Ldc_I4: return (int)instruction.Operand; case Code.Ldc_I4_S: return (sbyte)instruction.Operand; default: return null;
        }
    }

    private static object? ParseConstant(TypeReference type, string value)
    {
        switch (type.MetadataType)
        {
            case MetadataType.Boolean: return bool.Parse(value); case MetadataType.Char: return char.Parse(value); case MetadataType.String: return value;
            case MetadataType.SByte: return sbyte.Parse(value, CultureInfo.InvariantCulture); case MetadataType.Byte: return byte.Parse(value, CultureInfo.InvariantCulture);
            case MetadataType.Int16: return short.Parse(value, CultureInfo.InvariantCulture); case MetadataType.UInt16: return ushort.Parse(value, CultureInfo.InvariantCulture);
            case MetadataType.Int32: return int.Parse(value, CultureInfo.InvariantCulture); case MetadataType.UInt32: return uint.Parse(value, CultureInfo.InvariantCulture);
            case MetadataType.Int64: return long.Parse(value, CultureInfo.InvariantCulture); case MetadataType.UInt64: return ulong.Parse(value, CultureInfo.InvariantCulture);
            case MetadataType.Single: return float.Parse(value, CultureInfo.InvariantCulture); case MetadataType.Double: return double.Parse(value, CultureInfo.InvariantCulture);
            default: throw new InvalidDataException("不支持配置字段常量类型：" + type.MetadataType);
        }
    }

    private static void VerifyControlFlow(MethodDefinition method)
    {
        var instructions = new HashSet<Instruction>(method.Body.Instructions);
        foreach (var instruction in instructions)
            if (instruction.Operand is Instruction target && !instructions.Contains(target)) throw new InvalidDataException("补丁包含失效的 IL 跳转目标。" );
        foreach (var handler in method.Body.ExceptionHandlers)
            if ((handler.TryStart != null && !instructions.Contains(handler.TryStart)) || (handler.TryEnd != null && !instructions.Contains(handler.TryEnd)) ||
                (handler.HandlerStart != null && !instructions.Contains(handler.HandlerStart)) || (handler.HandlerEnd != null && !instructions.Contains(handler.HandlerEnd)))
                throw new InvalidDataException("补丁包含失效的异常处理区边界。" );
    }
}
