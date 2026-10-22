using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

internal sealed class BundleFile
{
    private static readonly byte[] Signature = {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    };

    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    internal byte[] Data { get; }

    private BundleFile(byte[] data, long manifestOffset)
    {
        Data = data;
        using (var stream = new MemoryStream(data, false))
        using (var reader = new BinaryReader(stream))
        {
            stream.Position = manifestOffset;
            var major = reader.ReadUInt32();
            reader.ReadUInt32(); // minor
            var count = reader.ReadInt32();
            if (major != 6 || count <= 0 || count > 1000)
                throw new InvalidDataException("不支持的 .NET 单文件 bundle 格式。" );
            reader.ReadString(); // bundle ID
            stream.Position += 5 * sizeof(long); // deps, runtimeconfig, flags
            for (var i = 0; i < count; i++)
            {
                var offset = reader.ReadInt64();
                var size = reader.ReadInt64();
                var compressedSize = reader.ReadInt64();
                reader.ReadByte(); // file type
                var name = reader.ReadString();
                var storedSize = compressedSize > 0 ? compressedSize : size;
                if (offset < 0 || size < 0 || compressedSize < 0 || storedSize > data.LongLength - offset)
                    throw new InvalidDataException("单文件 bundle 条目越界。" );
                if (entries.ContainsKey(name)) throw new InvalidDataException("单文件 bundle 含重复条目。" );
                entries.Add(name, new Entry(offset, size, compressedSize));
            }
        }
    }

    internal static BundleFile? TryLoad(string path)
    {
        var data = File.ReadAllBytes(path);
        var limit = data.Length - Signature.Length;
        for (var i = 8; i <= limit; i++)
        {
            if (data[i] != Signature[0]) continue;
            var match = true;
            for (var j = 1; j < Signature.Length; j++)
                if (data[i + j] != Signature[j]) { match = false; break; }
            if (!match) continue;
            var offset = BitConverter.ToInt64(data, i - 8);
            if (offset > 0 && offset < data.LongLength - 64) return new BundleFile(data, offset);
        }
        return null;
    }

    internal byte[]? GetEntry(string name)
    {
        if (!entries.TryGetValue(name, out var entry)) return null;
        if (entry.Size > int.MaxValue) throw new InvalidDataException("程序集过大。" );
        if (entry.CompressedSize == 0)
        {
            var result = new byte[(int)entry.Size];
            Buffer.BlockCopy(Data, (int)entry.Offset, result, 0, result.Length);
            return result;
        }
        using (var input = new MemoryStream(Data, (int)entry.Offset, (int)entry.CompressedSize, false))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            deflate.CopyTo(output);
            if (output.Length != entry.Size) throw new InvalidDataException("bundle 条目解压大小不一致。" );
            return output.ToArray();
        }
    }

    internal void ReplaceEntry(string name, byte[] replacement)
    {
        if (!entries.TryGetValue(name, out var entry)) throw new InvalidDataException("未找到 bundle 条目：" + name);
        if (entry.CompressedSize != 0)
            throw new InvalidDataException("当前版本的主程序集已压缩，需要增加对应 bundle 补丁配置。" );
        if (replacement.LongLength != entry.Size)
            throw new InvalidDataException("补丁改变了主程序集长度，无法原位替换。" );
        Buffer.BlockCopy(replacement, 0, Data, (int)entry.Offset, replacement.Length);
    }

    private sealed class Entry
    {
        internal long Offset { get; }
        internal long Size { get; }
        internal long CompressedSize { get; }

        internal Entry(long offset, long size, long compressedSize)
        {
            Offset = offset;
            Size = size;
            CompressedSize = compressedSize;
        }
    }
}
