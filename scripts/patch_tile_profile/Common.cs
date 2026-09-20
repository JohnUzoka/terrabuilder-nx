using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Reuse the reviewed43 tooling's body copier, fingerprints and metadata comparers.
// This dependency is patch-time only, never referenced by Terraria.exe.
internal static class Common
{
    internal static readonly Assembly Support = Assembly.LoadFrom(Environment.GetEnvironmentVariable("FRAME_PROFILE_TOOL") ?? throw new InvalidDataException("FRAME_PROFILE_TOOL required"));
    internal static object? Invoke(string type, string method, params object?[] args) => Support.GetType(type, true)!.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    internal static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Body(MethodDefinition m) => (string)Invoke("Program", "Body", m)!;
    internal static string Fingerprint(MethodDefinition m) => Sha(Encoding.UTF8.GetBytes(Body(m)));
    internal static IEnumerable<TypeDefinition> Types(ModuleDefinition m) => m.Types.SelectMany(Flatten);
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));
    internal static void Copy(MethodDefinition a, MethodDefinition b, Func<TypeReference, TypeReference> type, Func<object, object> member) => Invoke("Patcher", "CopyBody", a, b, type, member, 0);
    internal static void Widen(MethodDefinition m) => Invoke("Patcher", "WidenBranches", m);
    internal static void Json(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    internal static TypeReference Primitive(ModuleDefinition m, string name) => (TypeReference)Support.GetType("Patcher+Mapper", true)!.GetMethod("Primitive", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { m, name })!;
}
