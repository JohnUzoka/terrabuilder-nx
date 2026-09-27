using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;

internal static class Contracts
{
    internal const string InputHash = "90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22";
    internal const string CandidateHash = "4c02e40e6b0502d311764d1c4cb0eaebe0fa17d194c61976250378a90be7e55f";
    internal const string InputMvid = "df61a8d1-0622-9555-82c2-a2118b6c9bc4";
    internal const string CandidateMvid = "b6af8788-d1e2-ad83-4d3d-2896548ad830";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string ReLogicHash = "856438fe15b91b2bc1972f8ebd710cb3ef0ba2d6d7900e65d33bbf2e3c8d68c8";
    internal const string CoreHash = "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd";
    internal const string CecilHash = "d864ae1b39be10eaf671cd4a8a5e6bd2613740ca57b294c400776f8617dc5355";
    internal const string DotnetHash = "11e4b2ad384bfa6c1adf01b0adbbe9dfe5d673ddbdfdbde0412411ac4e027520";
    internal const string CorePath = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
    internal const string DotnetPath = "/build/runtime-source/.dotnet/dotnet";
    internal const int TargetToken = 0x06000d45;
    internal const string TargetName = "Microsoft.Xna.Framework.Color Terraria.Lighting::GetColor(System.Int32,System.Int32)";
    internal static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void Json(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    internal static IEnumerable<TypeDefinition> Types(ModuleDefinition module) => module.Types.SelectMany(Flatten);
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
    internal static IAssemblyResolver Resolver(string input, string fna) => new PinnedResolver(input, fna);
    sealed class PinnedResolver(string input, string fna) : IAssemblyResolver
    {
        readonly Dictionary<string, AssemblyDefinition> loaded = new();
        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (loaded.TryGetValue(name.Name, out var found)) return found;
            string path = name.Name switch {
                "System.Private.CoreLib" => CorePath,
                "FNA" => fna,
                "Terraria" => input,
                _ => Path.Combine(Path.GetDirectoryName(input)!, name.Name + ".dll")
            };
            if (!File.Exists(path)) path = "/mono-nx/dotnet_runtime/artifacts/bin/runtime/net9.0-libnx-Debug-arm64/" + name.Name + ".dll";
            Require(File.Exists(path), "no pinned target resolver image: " + name.FullName);
            if (name.Name == "FNA") Require(Sha(File.ReadAllBytes(path)) == FnaHash, "resolver FNA differs from pinned dependency");
            if (name.Name == "ReLogic") Require(Sha(File.ReadAllBytes(path)) == ReLogicHash, "resolver ReLogic differs from pinned dependency");
            parameters.AssemblyResolver = this;
            var result = AssemblyDefinition.ReadAssembly(path, parameters);
            Require(result.Name.FullName == name.FullName, "resolved assembly identity differs: " + name.FullName);
            loaded.Add(name.Name, result); return result;
        }
        public void Dispose() { foreach (var image in loaded.Values) image.Dispose(); }
    }
}
