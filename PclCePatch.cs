using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PclCePatch
{
    internal static void Apply(PatchTarget target)
    {
        var bytes = target.Bundle?.GetEntry(target.Profile.BundleEntry)
            ?? throw new InvalidDataException("缺少 PCL-CE 主程序集。" );
        foreach (var action in target.Profile.Actions)
        {
            RunLog.Info("PCL-CE 补丁动作：" + action.Behavior);
            if (action.Behavior != "returnTrue") throw new InvalidDataException("未知 PCL-CE 补丁行为：" + action.Behavior);
            var type = PatchTarget.Types(target.Assembly).Single(t => t.FullName == action.Get("type"));
            var method = type.Methods.Single(m => m.Name == action.Get("method") &&
                m.Parameters.Count == int.Parse(action.Get("parameters")));
            if (!method.IsStatic || method.ReturnType.MetadataType != MetadataType.Boolean ||
                method.Body.ExceptionHandlers.Count != 0 || method.Body.CodeSize < 2 ||
                method.Body.Instructions[0].OpCode.Code.ToString() != action.Get("firstOpcode") ||
                !(method.Body.Instructions[0].Operand is MethodReference call) || call.Name != action.Get("firstCall"))
                throw new InvalidDataException("PCL-CE Check 方法结构与配置不符。" );

            var codeOffset = MethodCodeOffset(bytes, method.RVA, method.Body.CodeSize);
            bytes[codeOffset] = 0x17; // ldc.i4.1
            bytes[codeOffset + 1] = 0x2a; // ret
        }
        target.Bundle.ReplaceEntry(target.Profile.BundleEntry, bytes);
    }

    internal static void Verify(PatchTarget target)
    {
        foreach (var action in target.Profile.Actions)
        {
            var method = PatchTarget.Types(target.Assembly).Single(t => t.FullName == action.Get("type"))
                .Methods.Single(m => m.Name == action.Get("method") &&
                    m.Parameters.Count == int.Parse(action.Get("parameters")));
            if (method.Body.Instructions.Count < 2 ||
                method.Body.Instructions[0].OpCode != OpCodes.Ldc_I4_1 ||
                method.Body.Instructions[1].OpCode != OpCodes.Ret)
                throw new InvalidDataException("PCL-CE Check 未被改为恒定成功。" );
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
            else throw new InvalidDataException("无法解析 Check 方法体。" );
            if (codeSize != expectedSize || codeOffset < 0 || codeOffset + codeSize > pe.Length)
                throw new InvalidDataException("Check 方法体长度与元数据不一致。" );
            return codeOffset;
        }
        throw new InvalidDataException("Check 方法 RVA 不在 PE 节中。" );
    }
}
