using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string CoreHash = "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd";
    internal const string SingleName = "System.Void Terraria.GameContent.Drawing.TileDrawing::DrawSingleTile(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2,System.Int32,System.Int32)";
    internal const string DrawName = "System.Void Terraria.GameContent.Drawing.TileDrawing::Draw(System.Boolean,System.Boolean,System.Int32)";
    // The existing single-target metadata/signature verifier uses these entry points.
    internal const string TargetName = SingleName;
    internal static MethodDefinition Method(ModuleDefinition module, string name) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == name);
    internal static MethodDefinition Single(ModuleDefinition module) => Method(module, SingleName);
    internal static MethodDefinition Draw(ModuleDefinition module) => Method(module, DrawName);
    internal static MethodDefinition Target(ModuleDefinition module) => Single(module);
    internal static void Require(bool condition, string message) => Common.Require(condition, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);

    internal static string SourceHash()
    {
        string path = Environment.GetEnvironmentVariable("GATE50_SOURCE_MANIFEST") ?? throw new InvalidDataException("GATE50_SOURCE_MANIFEST required");
        byte[] manifest = File.ReadAllBytes(path);
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
        Require(sources.Count >= 8 && new[] { "Program.cs", "DrawGatePatcher.cs", "DrawGateAudit.cs", "QualifiedBindings.cs", "DrawGateProof.cs", "DrawGateProbe.cs", "PatchDrawGate.csproj", "run.py" }.All(name => sources.Keys.Any(p => p.EndsWith("/patch_draw_gate/" + name, StringComparison.Ordinal))), "incomplete gate50 source manifest");
        foreach (var (file, hash) in sources) Require(Sha(File.ReadAllBytes(file)) == hash, "source changed since build: " + file);
        return Sha(manifest);
    }

    static void FreshOutput(string output)
    {
        Require(!Path.Exists(output) && new FileInfo(output).LinkTarget == null && new DirectoryInfo(output).LinkTarget == null, "existing/aliased output forbidden");
        for (var parent = new DirectoryInfo(output).Parent; parent != null; parent = parent.Parent)
            Require(parent.LinkTarget == null, "symlink output ancestor forbidden");
    }

    static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("run under the pinned Linux monobuild environment");
            bool supplied = args.Length == 5 && args[0] == "prove";
            Require(supplied || args.Length == 4 && args[0] is "inspect" or "emit" or "accept", "Usage: PatchDrawGate <inspect|emit|accept> <clean42-Terraria.exe> <FNA.dll> <fresh-output>, or prove <clean42> <candidate> <FNA> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[supplied ? 3 : 2]), output = Path.GetFullPath(args[supplied ? 4 : 3]);
            FreshOutput(output);
            byte[] originalBytes = File.ReadAllBytes(input);
            Require(Sha(originalBytes) == InputHash, "unsupported or already-patched input");
            Require(Sha(File.ReadAllBytes(fna)) == FnaHash, "unsupported FNA");
            const string core = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
            Require(Sha(File.ReadAllBytes(core)) == CoreHash, "target CoreLib changed");
            using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes));
            Require(original.MainModule.Mvid == Guid.Parse("2a9040da-3f4b-844f-a14b-3056cdda95ba"), "input MVID differs");
            DrawGatePatcher.CheckOriginal(Single(original.MainModule));
            Directory.CreateDirectory(output);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { accepted = false, inputSha256 = InputHash, target = SingleName, il = Body(Single(original.MainModule)), nativeWorkAndSafetyRequireSeparatePinnedEvidence = true });
                return 0;
            }
            string sourceHash = SourceHash();
            if (supplied)
            {
                string candidate = Path.GetFullPath(args[2]);
                byte[] bytes = File.ReadAllBytes(candidate);
                var audit = DrawGateAudit.Verify(originalBytes, bytes);
                var proof = DrawGateProof.Run(input, candidate, fna, output);
                Require(Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(fna)) == FnaHash && Sha(File.ReadAllBytes(candidate)) == Sha(bytes) && SourceHash() == sourceHash, "proof inputs/source changed");
                Json(Path.Combine(output, "supplied-proof.json"), new { passed = true, sourceHash, inputSha256 = InputHash, outputSha256 = Sha(bytes), audit, proof });
                Console.WriteLine("SUPPLIED GATE50 PROOF PASSED");
                return 0;
            }
            using var resolver = (IAssemblyResolver)Activator.CreateInstance(Support.GetType("ReferenceAudit+PinnedResolver", true)!, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Path.GetDirectoryName(input)! }, null)!;
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
            var relocation = DrawGatePatcher.Relocate(Single(game.MainModule));
            game.MainModule.Mvid = Guid.Empty;
            using (var identity = new MemoryStream())
            {
                game.Write(identity, new WriterParameters { Timestamp = 0 });
                game.MainModule.Mvid = new Guid(SHA256.HashData(identity.ToArray()).AsSpan(0, 16));
            }
            byte[] changedBytes;
            using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); changedBytes = stream.ToArray(); }
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(changedBytes));
            Require(Body(Single(game.MainModule)) == Body(Single(changed.MainModule)), "serialized target differs");
            var verification = DrawGateAudit.Verify(originalBytes, changedBytes);
            Json(Path.Combine(output, "relocation.json"), relocation);
            Json(Path.Combine(output, "preservation.json"), verification);
            string pending = Path.Combine(output, "candidate-proof-only.exe");
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write)) file.Write(changedBytes);
            File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            if (args[0] == "emit")
            {
                Json(Path.Combine(output, "inspection-only.json"), new { accepted = false, hardwarePending = true, sourceHash, inputSha256 = InputHash, outputSha256 = Sha(changedBytes), outputMvid = changed.MainModule.Mvid, relocation, verification });
                Console.WriteLine("INSPECTION ONLY " + pending);
                return 0;
            }
            var bindingNegative = QualifiedBindings.RejectWrongScope(originalBytes, changedBytes, input, Path.Combine(output, "binding-negative"));
            var proofResult = DrawGateProof.Run(input, pending, fna, Path.Combine(output, "proof"));
            Require(Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(fna)) == FnaHash && Sha(File.ReadAllBytes(pending)) == Sha(changedBytes) && SourceHash() == sourceHash, "input/candidate/source changed during acceptance");
            string destination = Path.Combine(output, "Terraria.exe");
            File.Move(pending, destination);
            Json(Path.Combine(output, "acceptance.json"), new
            {
                accepted = true, hardwarePending = true, performanceAdopted = false, sourceHash, inputSha256 = InputHash, fnaSha256 = FnaHash, targetCorelibSha256 = CoreHash,
                outputSha256 = Sha(changedBytes), outputMvid = changed.MainModule.Mvid, changedMethods = new[] { SingleName }, movedInstructions = 2, redirectedBranches = 2,
                addedMethods = 0, addedFields = 0, addedTypes = 0, addedLocals = 0, addedAssemblyReferences = 0, relocation, verification, bindingNegative, proof = proofResult,
                scope = "Pinned unhooked clean42/FNA rendering with intact built-in tile-set initialization. Preserves prior particles/RNG/black/cache/shroom-cap work and original scratch allocation; no lighting-quality change or profiler. Arbitrary reflection/native corruption and patched primitive callbacks are not an equivalence claim. Hardware benefit remains untested."
            });
            Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(changedBytes) + " mvid=" + changed.MainModule.Mvid);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
