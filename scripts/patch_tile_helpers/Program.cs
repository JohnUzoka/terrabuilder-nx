using System.Text.Json;
using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string DrawName = "System.Void Terraria.GameContent.Drawing.TileDrawing::Draw(System.Boolean,System.Boolean,System.Int32)";
    internal const string SingleName = "System.Void Terraria.GameContent.Drawing.TileDrawing::DrawSingleTile(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2,System.Int32,System.Int32)";
    internal const string MainDrawName = "System.Void Terraria.Main::Draw(Microsoft.Xna.Framework.GameTime)";
    internal const string RunGameName = "System.Void Terraria.Program::RunGame()";
    internal static readonly string[] ChangedNames = { MainDrawName, DrawName, SingleName, RunGameName };
    internal static void Require(bool ok, string message) => Common.Require(ok, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);
    internal static MethodDefinition Method(ModuleDefinition module, string name) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == name);
    internal static MethodDefinition Draw(ModuleDefinition module) => Method(module, DrawName);
    internal static MethodDefinition Single(ModuleDefinition module) => Method(module, SingleName);
    internal static string SourceHash()
    {
        var path = Environment.GetEnvironmentVariable("TILE49_SOURCE_MANIFEST") ?? throw new InvalidDataException("TILE49_SOURCE_MANIFEST required");
        byte[] manifest = File.ReadAllBytes(path);
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
        Require(sources.Count >= 10 && sources.Keys.Any(p => p.EndsWith("/patch_tile_helpers/TileHelperPatcher.cs", StringComparison.Ordinal)) && sources.Keys.Any(p => p.EndsWith("/patch_tile_helpers/TimingTemplate.cs", StringComparison.Ordinal)) && sources.Keys.Any(p => p.EndsWith("/patch_tile_helpers/StateTemplate.cs", StringComparison.Ordinal)), "incomplete source manifest");
        foreach (var (file, hash) in sources) Require(Sha(File.ReadAllBytes(file)) == hash, "source changed since build manifest: " + file);
        return Sha(manifest);
    }
    static void FreshOutput(string output)
    {
        Require(!Path.Exists(output) && new FileInfo(output).LinkTarget == null && new DirectoryInfo(output).LinkTarget == null, "existing/alias output forbidden");
        for (var parent = new DirectoryInfo(output).Parent; parent != null; parent = parent.Parent)
            Require(parent.LinkTarget == null, "symlink output ancestor forbidden");
    }
    static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 4 && args[0] is "inspect" or "emit" or "accept", "Usage: PatchTileHelpers <inspect|emit|accept> <clean42-Terraria.exe> <FNA.dll> <fresh-output>");
            Require(!OperatingSystem.IsWindows(), "Run under Linux monobuild container");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            FreshOutput(output);
            Require(Sha(File.ReadAllBytes(input)) == InputHash, "unsupported or already-patched input");
            Require(Sha(File.ReadAllBytes(fna)) == FnaHash, "FNA differs");
            using var original = AssemblyDefinition.ReadAssembly(input);
            Require(original.MainModule.Mvid == Guid.Parse("2a9040da-3f4b-844f-a14b-3056cdda95ba"), "input MVID differs");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { accepted = false, inputSha256 = InputHash, methods = ChangedNames.Select(n => new { name = n, hash = Fingerprint(Method(original.MainModule, n)), il = Body(Method(original.MainModule, n)) }) });
                return 0;
            }
            string sourceHash = SourceHash();
            using var resolver = TileHelperPatcher.Resolver(input);
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(input)), new ReaderParameters { AssemblyResolver = resolver });
            var receipts = TileHelperPatcher.Inject(game.MainModule);
            game.MainModule.Mvid = Guid.Empty;
            using (var identity = new MemoryStream()) { game.Write(identity, new WriterParameters { Timestamp = 0 }); game.MainModule.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(identity.ToArray()).AsSpan(0, 16)); }
            byte[] bytes;
            using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); bytes = stream.ToArray(); }
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
            foreach (var method in Types(game.MainModule).SelectMany(t => t.Methods).Where(m => TileHelperPatcher.IsAdded(m.DeclaringType) || ChangedNames.Contains(m.FullName)))
                Require(Body(method) == Body(Method(changed.MainModule, method.FullName)), "serialized body differs: " + method.FullName);
            Json(Path.Combine(output, "injection-receipts.json"), receipts);
            string candidate = Path.Combine(output, args[0] == "emit" ? "candidate-inspection-only.exe" : "candidate-proof-only.exe");
            using (var file = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var preservation = TileHelperAudit.Preservation(original, changed, receipts);
            var unsampled = TileHelperAudit.UnsampledGuard(changed.MainModule);
            Json(Path.Combine(output, "unsampled-guard.json"), unsampled);
            var resources = PeResources.Fingerprints(File.ReadAllBytes(input));
            Require(resources.SequenceEqual(PeResources.Fingerprints(bytes)), "native resources changed");
            var signatures = TileHelperAudit.Signatures(bytes);
            var malformedSignatures = Invoke("RawSignatures", "VerifyMalformedRejection", output)!;
            var sdk = TileHelperAudit.Sdk(bytes, original, input);
            Json(Path.Combine(output, "preservation.json"), preservation); Json(Path.Combine(output, "raw-signatures.json"), signatures); Json(Path.Combine(output, "sdk-references.json"), sdk);
            if (args[0] == "emit")
            {
                Json(Path.Combine(output, "inspection-only.json"), new { accepted = false, hardwarePending = true, sourceHash, outputSha256 = Sha(bytes), preservation, resources, signatures, malformedSignatures, sdk });
                Console.WriteLine("INSPECTION ONLY " + candidate); return 0;
            }
            var proof = TileHelperProof.Run(input, candidate, output);
            Require(Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(fna)) == FnaHash && Sha(File.ReadAllBytes(candidate)) == Sha(bytes) && SourceHash() == sourceHash, "input/candidate/source changed during acceptance");
            string destination = Path.Combine(output, "Terraria.exe");
            using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Json(Path.Combine(output, "acceptance.json"), new { accepted = true, hardwarePending = true, sourceHash, inputSha256 = InputHash, fnaSha256 = FnaHash, inputMvid = original.MainModule.Mvid, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid, changedMethods = ChangedNames, addedTypes = Types(changed.MainModule).Where(TileHelperPatcher.IsAdded).Select(t => t.FullName), addedAssemblyReferences = changed.MainModule.AssemblyReferences.Select(r => r.FullName).Except(original.MainModule.AssemblyReferences.Select(r => r.FullName)), preservation, resources, signatures, malformedSignatures, sdk, proof });
            Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
