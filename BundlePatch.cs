using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class BundlePatch
{
    internal static void Apply(PatchTarget target)
    {
        var bytes = target.Bundle?.GetEntry(target.Profile.BundleEntry)
            ?? throw new InvalidDataException("缺少 PCL-CE 主程序集。" );
        foreach (var action in target.Profile.Actions)
        {
            RunLog.Info("Bundle 补丁动作：" + action.Behavior +
                (string.IsNullOrWhiteSpace(action.Note) ? "" : "：" + action.Note));
            if (action.Behavior != PatchBehavior.SetMethodReturnValue)
                throw new InvalidDataException("当前 bundle 写入器不支持该通用修改行为：" + action.Behavior);
            var type = PatchTarget.Types(target.Assembly).Single(t => t.FullName == action.Get("type"));
            var method = type.Methods.Single(m => m.Name == action.Get("method") &&
                m.Parameters.Count == int.Parse(action.Get("parameters")));
            if (method.Body.ExceptionHandlers.Count != 0 || method.Body.CodeSize < 2 ||
                (action.Selectors.TryGetValue("firstOpcode", out var firstOpcode) &&
                 method.Body.Instructions[0].OpCode.Code.ToString() != firstOpcode) ||
                (action.Selectors.TryGetValue("firstCall", out var firstCall) &&
                 (!(method.Body.Instructions[0].Operand is MethodReference call) || call.Name != firstCall)))
                throw new InvalidDataException("方法体结构与配置不符。" );

            var codeOffset = MethodCodeOffset(bytes, method.RVA, method.Body.CodeSize);
            bytes[codeOffset] = ReturnValueOpcode(action.Code);
            bytes[codeOffset + 1] = 0x2a; // ret
        }
        target.Bundle.ReplaceEntry(target.Profile.BundleEntry, bytes);
    }

    internal static void Verify(PatchTarget target)
    {
        foreach (var action in target.Profile.Actions)
        {
            if (action.Behavior != PatchBehavior.SetMethodReturnValue)
                throw new InvalidDataException("PCL-CE 不支持该通用修改操作：" + action.Behavior);
            var method = PatchTarget.Types(target.Assembly).Single(t => t.FullName == action.Get("type"))
                .Methods.Single(m => m.Name == action.Get("method") &&
                    m.Parameters.Count == int.Parse(action.Get("parameters")));
            if (method.Body.Instructions.Count < 2 ||
                method.Body.Instructions[0].OpCode.Code != CompactReturnCode(action.Code) ||
                method.Body.Instructions[1].OpCode != OpCodes.Ret)
                throw new InvalidDataException("方法未返回配置指定的值。" );
        }
    }

    private static int MethodCodeOffset(byte[] pe, int rva, int expectedSize)
    {
        var header = BitConverter.ToInt32(pe, 0x3c);
        if (BitConverter.ToUInt32(pe, header) != 0x4550) throw new InvalidDataException("主程序集不是 PE 文件。" );
        var sections = BitConverter.ToUInt16(pe, header + 6);
        var table = header + 24 + BitConverter.ToUInt16(pe, header + 20);
        for (var i = 0; i < sections; i++)
        {
            var section = table + i * 40;
            var virtualAddress = BitConverter.ToInt32(pe, section + 12);
            var rawSize = BitConverter.ToInt32(pe, section + 16);
            var rawOffset = BitConverter.ToInt32(pe, section + 20);
            if (rva < virtualAddress || rva - virtualAddress >= rawSize) continue;
            var offset = checked((int)(rawOffset + rva - virtualAddress));
            var kind = pe[offset] & 3;
            int codeOffset, codeSize;
            if (kind == 2)
            {
                codeOffset = offset + 1;
                codeSize = pe[offset] >> 2;
            }
            else if (kind == 3)
            {
                codeOffset = offset + ((BitConverter.ToUInt16(pe, offset) >> 12) * 4);
                codeSize = BitConverter.ToInt32(pe, offset + 4);
            }
            else throw new InvalidDataException("无法解析方法体。" );
            if (codeSize != expectedSize || codeOffset < 0 || codeOffset + codeSize > pe.Length)
                throw new InvalidDataException("方法体长度与元数据不一致。" );
            return codeOffset;
        }
        throw new InvalidDataException("方法 RVA 不在 PE 节中。" );
    }

    private static byte ReturnValueOpcode(PatchCode[] code)
    {
        if (code == null || code.Length != 2 || code[1].OpCode != "Ret")
            throw new InvalidDataException("原位方法返回值操作必须由一个常量指令和 Ret 组成。" );
        switch (code[0].OpCode)
        {
            case "Ldc_I4_M1": return 0x15; case "Ldc_I4_0": return 0x16; case "Ldc_I4_1": return 0x17;
            case "Ldc_I4_2": return 0x18; case "Ldc_I4_3": return 0x19; case "Ldc_I4_4": return 0x1a;
            case "Ldc_I4_5": return 0x1b; case "Ldc_I4_6": return 0x1c; case "Ldc_I4_7": return 0x1d; case "Ldc_I4_8": return 0x1e;
            default: throw new InvalidDataException("当前原位返回值仅支持紧凑整数常量操作码：" + code[0].OpCode);
        }
    }

    private static Code CompactReturnCode(PatchCode[] code)
    {
        switch (ReturnValueOpcode(code))
        {
            case 0x15: return Code.Ldc_I4_M1; case 0x16: return Code.Ldc_I4_0; case 0x17: return Code.Ldc_I4_1;
            case 0x18: return Code.Ldc_I4_2; case 0x19: return Code.Ldc_I4_3; case 0x1a: return Code.Ldc_I4_4;
            case 0x1b: return Code.Ldc_I4_5; case 0x1c: return Code.Ldc_I4_6; case 0x1d: return Code.Ldc_I4_7; default: return Code.Ldc_I4_8;
        }
    }
}
