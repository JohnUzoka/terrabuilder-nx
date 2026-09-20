using Mono.Cecil;
using static Program;

internal static class ReferenceAudit
{
    const string Core = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
    const string Runtime = "/mono-nx/dotnet_runtime/artifacts/bin/runtime/net9.0-libnx-Debug-arm64";
    internal static object Verify(byte[] bytes, AssemblyDefinition baseline, string input)
    {
        Require(Sha(File.ReadAllBytes(Core)) == "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd", "target SDK CoreLib differs from reviewed42");
        using var resolver = new PinnedResolver(Path.GetDirectoryName(input)!);
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        var previous = Types(baseline.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MemberReference>().Select(m => m.FullName).ToHashSet();
        var references = Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && (m.DeclaringType.Name == "NXFrameProfile43" || m.Name == "NXProfileRead")).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MemberReference>().Where(m => m.DeclaringType?.Namespace.StartsWith("System", StringComparison.Ordinal) == true).GroupBy(m => m.FullName).Select(g => g.First()).ToArray();
        var rows = new List<object>();
        foreach (var r in references)
        {
            IMemberDefinition resolved = r switch { MethodReference m => m.Resolve(), FieldReference f => f.Resolve(), _ => throw new InvalidDataException("unhandled SDK member " + r.FullName) };
            Require(resolved != null, "target SDK member unresolved: " + r.FullName);
            Require(resolved is MethodDefinition { IsPublic: true } or FieldDefinition { IsPublic: true }, "non-public target BCL reference " + r.FullName);
            var owner = resolved!.DeclaringType.Module;
            rows.Add(new { reference = r.FullName, scope = r.DeclaringType.Scope.ToString(), resolved = resolved.FullName, assembly = owner.Assembly.Name.FullName, image = owner.FileName, sha256 = Sha(File.ReadAllBytes(owner.FileName)), newMemberReference = !previous.Contains(r.FullName) });
        }
        var core = resolver.Resolve(new AssemblyNameReference("System.Private.CoreLib", new Version(9, 0, 0, 0)));
        Require(core.MainModule.Mvid == Guid.Parse("2f80d797-2850-4a62-86a8-8b6a291749cb"), "target CoreLib MVID differs");
        return new { coreSha256 = Sha(File.ReadAllBytes(Core)), coreMvid = core.MainModule.Mvid, checkedMembers = rows, hostFallbackAllowed = false };
    }
    sealed class PinnedResolver : IAssemblyResolver
    {
        readonly string game;
        readonly Dictionary<string, AssemblyDefinition> loaded = new();
        internal PinnedResolver(string game) { this.game = game; }
        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (loaded.TryGetValue(name.Name, out var result)) return result;
            string path = name.Name == "System.Private.CoreLib" ? Core : Path.Combine(game, name.Name + ".dll");
            if (!File.Exists(path)) path = Path.Combine(Runtime, name.Name + ".dll");
            Require(File.Exists(path), "no pinned target resolver image: " + name.FullName);
            parameters.AssemblyResolver = this;
            result = AssemblyDefinition.ReadAssembly(path, parameters); loaded.Add(name.Name, result); return result;
        }
        public void Dispose() { foreach (var a in loaded.Values) a.Dispose(); }
    }
}
