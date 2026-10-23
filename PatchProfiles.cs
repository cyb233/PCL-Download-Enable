using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Mono.Cecil;

internal sealed class PatchProfiles
{
    private readonly Dictionary<string, Dictionary<string, PatchProfile>> programs;

    private PatchProfiles(Dictionary<string, Dictionary<string, PatchProfile>> programs)
    {
        this.programs = programs;
    }

    internal IEnumerable<string> DefaultFileNames => programs.Values.SelectMany(r => r.Values)
        .SelectMany(p => p.FileNames ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase);

    internal static PatchProfiles Load()
    {
        using (var stream = typeof(PatchProfiles).Assembly.GetManifestResourceStream("PatchProfiles.json")
               ?? throw new InvalidDataException("缺少内置补丁配置。"))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, Dictionary<string, PatchProfile>>>(reader.ReadToEnd());
            if (data == null || data.Count == 0) throw new InvalidDataException("补丁配置为空。");
            foreach (var program in data)
                foreach (var pair in program.Value)
                {
                    var profile = pair.Value;
                    InRange(new Version(0, 0, 0, 0), pair.Key);
                    if (profile.Signature == null || string.IsNullOrWhiteSpace(profile.Signature.Type) ||
                        string.IsNullOrWhiteSpace(profile.Signature.Member) ||
                        string.IsNullOrWhiteSpace(profile.Assembly) ||
                        (profile.Format != "assembly" && profile.Format != "bundle") ||
                        (profile.Format == "bundle" && string.IsNullOrWhiteSpace(profile.BundleEntry)) ||
                        profile.Actions == null || profile.Actions.Length == 0)
                        throw new InvalidDataException($"补丁配置无效：{program.Key} {pair.Key}");
                }
            return new PatchProfiles(data);
        }
    }

    internal PatchTarget Identify(string path)
    {
        var bundle = BundleFile.TryLoad(path);
        var bundleEntry = programs.Values.SelectMany(r => r.Values)
            .Where(p => p.Format == "bundle").Select(p => p.BundleEntry)
            .Distinct(StringComparer.Ordinal).FirstOrDefault(name => bundle?.GetEntry(name) != null);
        var bytes = bundleEntry == null ? null : bundle?.GetEntry(bundleEntry);
        if (bundle != null && bytes == null)
            throw new InvalidDataException("无法在单文件程序中找到主程序集。" );
        AssemblyDefinition assembly;
        try
        {
            assembly = bytes == null
                ? AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false })
                : AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { ReadSymbols = false });
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException("无法识别目标程序：不是受支持的 .NET 程序集。", ex);
        }
        try
        {
            var version = assembly.Name.Version;
            foreach (var program in programs)
            {
                var matches = program.Value.Where(pair => MatchesIdentity(pair.Value, assembly, bundle != null)).ToList();
                if (matches.Count == 0) continue;
                var supported = matches.Where(pair => InRange(version, pair.Key)).ToList();
                if (supported.Count > 1)
                    throw new InvalidDataException($"{program.Key} {version} 命中多个版本区间，请检查补丁配置。" );
                if (supported.Count == 0)
                    throw new InvalidDataException($"{program.Key} {version} 暂不支持。已配置区间：{string.Join("、", matches.Select(x => x.Key))}");
                return new PatchTarget(program.Key, version, supported[0].Value, assembly, bundle);
            }
            throw new InvalidDataException("无法识别目标程序。仅支持配置中的 PCL 与 PCL-CE 构建。" );
        }
        catch
        {
            assembly.Dispose();
            throw;
        }
    }

    internal void Verify(string path, PatchTarget original)
    {
        using (var patched = Identify(path))
        {
            if (patched.Program != original.Program || patched.Version != original.Version)
                throw new InvalidDataException("补丁副本的程序或版本发生变化。" );
            if (patched.Bundle != null) PclCePatch.Verify(patched);
            else PclPatch.Verify(patched);
        }
    }

    private static bool MatchesIdentity(PatchProfile profile, AssemblyDefinition assembly, bool isBundle)
    {
        if (!string.Equals(profile.Format, isBundle ? "bundle" : "assembly", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(profile.Assembly, assembly.Name.Name, StringComparison.Ordinal)) return false;
        var type = PatchTarget.Types(assembly).FirstOrDefault(t => t.FullName == profile.Signature.Type);
        return type != null && (type.Fields.Any(f => f.Name == profile.Signature.Member) ||
                                type.Methods.Any(m => m.Name == profile.Signature.Member));
    }

    private static bool InRange(Version version, string range)
    {
        if (range.Length < 5 || (range[0] != '[' && range[0] != '(') ||
            (range[range.Length - 1] != ']' && range[range.Length - 1] != ')'))
            throw new InvalidDataException("版本区间格式错误：" + range);
        var bounds = range.Substring(1, range.Length - 2).Split(',');
        if (bounds.Length != 2 || !Version.TryParse(bounds[0].Trim(), out var minimum) ||
            !Version.TryParse(bounds[1].Trim(), out var maximum))
            throw new InvalidDataException("版本区间格式错误：" + range);
        var lower = version.CompareTo(minimum);
        var upper = version.CompareTo(maximum);
        return (range[0] == '[' ? lower >= 0 : lower > 0) &&
               (range[range.Length - 1] == ']' ? upper <= 0 : upper < 0);
    }
}

internal sealed class PatchTarget : IDisposable
{
    internal string Program { get; }
    internal Version Version { get; }
    internal PatchProfile Profile { get; }
    internal AssemblyDefinition Assembly { get; }
    internal BundleFile? Bundle { get; }

    internal PatchTarget(string program, Version version, PatchProfile profile, AssemblyDefinition assembly, BundleFile? bundle)
    {
        Program = program;
        Version = version;
        Profile = profile;
        Assembly = assembly;
        Bundle = bundle;
    }

    internal static IEnumerable<TypeDefinition> Types(AssemblyDefinition assembly) => assembly.MainModule.Types.SelectMany(Flatten);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Flatten)) yield return nested;
    }

    public void Dispose() => Assembly.Dispose();
}

public sealed class PatchProfile
{
    public string Format { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string BundleEntry { get; set; } = "";
    public string[] FileNames { get; set; } = new string[0];
    public PatchSignature Signature { get; set; } = new PatchSignature();
    public PatchAction[] Actions { get; set; } = new PatchAction[0];
}

public sealed class PatchSignature
{
    public string Type { get; set; } = "";
    public string Member { get; set; } = "";
}

public enum PatchBehavior
{
    Unknown,
    setConstant,
    restoreLoader,
    showCard,
    restoreResourceInstall,
    skipRestrictionBranch,
    restoreModpackDispatch,
    returnTrue
}

public sealed class PatchAction
{
    public PatchBehavior Behavior { get; set; }
    public Dictionary<string, string> Selectors { get; set; } = new Dictionary<string, string>();
    public PatchCase[] Cases { get; set; } = new PatchCase[0];

    public string Get(string name)
    {
        if (Selectors == null || !Selectors.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"补丁行为 {Behavior} 缺少配置项 {name}。" );
        return value;
    }
}

public sealed class PatchCase
{
    public int Value { get; set; }
    public string Method { get; set; } = "";
    public int Parameters { get; set; }
}
