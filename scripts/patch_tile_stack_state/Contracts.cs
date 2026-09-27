using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;

internal static class Contracts
{
    internal const string InputHash = "90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22";
    internal const string CandidateHash = "9782543073b4f66d629126eec2387b9c8e99fdca03bee143750b756107ea409d";
    internal const string ReLogicHash = "856438fe15b91b2bc1972f8ebd710cb3ef0ba2d6d7900e65d33bbf2e3c8d68c8";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string CoreHash = "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd";
    internal const string CecilHash = "d864ae1b39be10eaf671cd4a8a5e6bd2613740ca57b294c400776f8617dc5355";
    internal const string DotnetHash = "11e4b2ad384bfa6c1adf01b0adbbe9dfe5d673ddbdfdbde0412411ac4e027520";
    internal const string DotnetPath = "/build/runtime-source/.dotnet/dotnet";
    internal const string InputMvid = "df61a8d1-0622-9555-82c2-a2118b6c9bc4";
    internal const string CandidateMvid = "018aaf69-8e4b-014e-8437-5b97ac20037d";
    internal const string Original = "Terraria.DataStructures.TileDrawInfo";
    internal const string Owner = "Terraria.GameContent.Drawing.TileDrawing";
    internal const string Added = "Terraria.GameContent.Drawing.TileDrawState54";
    internal const int EntryToken = 0x0600453a;
    internal const string CorePath = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
    internal static readonly uint[] HelperTokens = { 0x06004541, 0x0600454d, 0x0600454f, 0x06004550, 0x06004551, 0x06004552, 0x06004555 };
    internal static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void Json(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    internal static IEnumerable<TypeDefinition> Types(ModuleDefinition module) => module.Types.SelectMany(Flatten);
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
    internal static DefaultAssemblyResolver Resolver(string input, string fna)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(fna)!);
        resolver.AddSearchDirectory("/mono-nx/dotnet_runtime/artifacts/bin/runtime/net9.0-libnx-Debug-arm64");
        resolver.AddSearchDirectory(Path.GetDirectoryName(CorePath)!);
        return resolver;
    }
}
