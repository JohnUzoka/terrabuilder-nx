using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "06b11beb9c83fc14140754eb5a44c7fa0525d1877ea2fd0860a75b91f3d6f4f6";
    internal const string ReLogicHash = "2e7750fe79ba48bcca8e7ea5691f87c0e5887024b58552c433efb2e2ad060a65";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string CoreHash = "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd";
    internal const string TargetName = "System.Void Terraria.Main::DrawGamepadInstructions()";
    internal const string SizeName = "Microsoft.Xna.Framework.Vector2 Terraria.UI.Chat.ChatManager::GetStringSize(ReLogic.Graphics.DynamicSpriteFont,System.String,Microsoft.Xna.Framework.Vector2,System.Single)";
    internal static void Require(bool condition, string message) => Common.Require(condition, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);
    internal static MethodDefinition Method(ModuleDefinition module, string name) => Types(module).SelectMany(type => type.Methods).Single(method => method.FullName == name);
    internal static MethodDefinition Target(ModuleDefinition module) => Method(module, TargetName);

    internal static string SourceHash()
    {
        string path = Environment.GetEnvironmentVariable("HINT52_SOURCE_MANIFEST") ?? throw new InvalidDataException("HINT52_SOURCE_MANIFEST required");
        byte[] manifest = File.ReadAllBytes(path);
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
        string[] required = { "Contracts.cs", "FontGuardTemplate.cs", "GameGuardTemplate.cs", "HintPatcher.cs", "HintAudit.cs", "HintProof.cs", "HintProbe.cs", "HintFixture.cs", "Program.cs", "PatchHintPrepass.csproj", "run.py" };
        Require(required.All(name => sources.Keys.Any(key => key.EndsWith("/patch_hint_prepass/" + name, StringComparison.Ordinal))), "incomplete hint52 source manifest");
        foreach (var (file, hash) in sources) Require(Sha(File.ReadAllBytes(file)) == hash, "source changed since build: " + file);
        return Sha(manifest);
    }

    static void FreshOutput(string output)
    {
        Require(!Path.Exists(output) && new FileInfo(output).LinkTarget == null && new DirectoryInfo(output).LinkTarget == null, "fresh non-alias output required");
        for (var parent = new DirectoryInfo(output).Parent; parent != null; parent = parent.Parent) Require(parent.LinkTarget == null, "symlink output ancestor forbidden");
    }

    static byte[] Serialize(AssemblyDefinition assembly)
    {
        assembly.MainModule.Mvid = Guid.Empty;
        using (var identity = new MemoryStream())
        {
            assembly.Write(identity, new WriterParameters { Timestamp = 0 });
            assembly.MainModule.Mvid = new Guid(SHA256.HashData(identity.ToArray()).AsSpan(0, 16));
        }
        using var result = new MemoryStream();
        assembly.Write(result, new WriterParameters { Timestamp = 0 });
        return result.ToArray();
    }

    static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("use pinned Linux monobuild");
            bool supplied = args.Length == 5 && args[0] == "prove";
            Require(supplied || args.Length == 4 && args[0] is "inspect" or "emit" or "accept", "Usage: PatchHintPrepass <inspect|emit|accept> <build50-Terraria.exe> <FNA.dll> <fresh-output>, or prove <build50> <candidate-pair-directory> <FNA> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[supplied ? 3 : 2]), output = Path.GetFullPath(args[supplied ? 4 : 3]);
            string relogic = Path.Combine(Path.GetDirectoryName(input)!, "ReLogic.dll");
            FreshOutput(output);
            byte[] originalGameBytes = File.ReadAllBytes(input), originalReLogicBytes = File.ReadAllBytes(relogic);
            Require(Sha(originalGameBytes) == InputHash && Sha(originalReLogicBytes) == ReLogicHash && Sha(File.ReadAllBytes(fna)) == FnaHash, "pinned50 game/ReLogic/FNA required");
            const string core = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
            Require(Sha(File.ReadAllBytes(core)) == CoreHash, "pinned CoreLib changed");
            using var originalGame = AssemblyDefinition.ReadAssembly(new MemoryStream(originalGameBytes));
            using var originalReLogic = AssemblyDefinition.ReadAssembly(new MemoryStream(originalReLogicBytes));
            Require(originalGame.MainModule.Mvid == Guid.Parse("18ab5d9f-3419-30c0-509e-cf5a34c42f0b") && originalReLogic.MainModule.Mvid == Guid.Parse("9192551c-5bb1-4da1-9e37-6d31c1a140c0"), "input MVID differs");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { accepted = false, inputSha256 = InputHash, reLogicSha256 = ReLogicHash, target = TargetName, body = Body(Target(originalGame.MainModule)), prepass = Body(Method(originalGame.MainModule, SizeName)) });
                return 0;
            }
            string sourceHash = SourceHash();
            using var resolver = HintPatcher.Resolver(input);
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(originalGameBytes), new ReaderParameters { AssemblyResolver = resolver });
            using var library = AssemblyDefinition.ReadAssembly(new MemoryStream(originalReLogicBytes), new ReaderParameters { AssemblyResolver = resolver });
            var receipt = HintPatcher.Inject(game.MainModule, library.MainModule);
            byte[] libraryBytes = Serialize(library), gameBytes = Serialize(game);
            if (supplied)
            {
                string directory = Path.GetFullPath(args[2]);
                Require(Sha(File.ReadAllBytes(Path.Combine(directory, "Terraria.exe"))) == Sha(gameBytes) && Sha(File.ReadAllBytes(Path.Combine(directory, "ReLogic.dll"))) == Sha(libraryBytes), "supplied pair differs from exact current production-helper emission");
            }
            var audit = HintAudit.Verify(originalGameBytes, gameBytes, originalReLogicBytes, libraryBytes, receipt, input);
            Json(Path.Combine(output, "receipt.json"), receipt);
            Json(Path.Combine(output, "preservation.json"), audit);
            string pending = Path.Combine(output, "proof-pair");
            Directory.CreateDirectory(pending);
            string pendingGame = Path.Combine(pending, "Terraria.exe"), pendingLibrary = Path.Combine(pending, "ReLogic.dll");
            foreach (var pair in new[] { (path: pendingGame, bytes: gameBytes), (path: pendingLibrary, bytes: libraryBytes) })
            {
                using (var file = new FileStream(pair.path, FileMode.CreateNew, FileAccess.Write)) file.Write(pair.bytes);
                File.SetUnixFileMode(pair.path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            if (args[0] == "emit")
            {
                Json(Path.Combine(output, "inspection-only.json"), new { accepted = false, hardwarePending = true, sourceHash, gameSha256 = Sha(gameBytes), reLogicSha256 = Sha(libraryBytes), gameMvid = game.MainModule.Mvid, reLogicMvid = library.MainModule.Mvid, audit });
                Console.WriteLine("INSPECTION ONLY " + pending);
                return 0;
            }
            var negatives = HintAudit.NegativeProof(originalGameBytes, gameBytes, originalReLogicBytes, libraryBytes, receipt, input, Path.Combine(output, "static-negative"));
            var proof = HintProof.Run(input, pendingGame, relogic, pendingLibrary, fna, Path.Combine(output, "proof"));
            Require(JsonSerializer.SerializeToElement(proof).GetProperty("passed").GetBoolean(), "guard/root proof did not pass");
            Require(SourceHash() == sourceHash && Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(relogic)) == ReLogicHash && Sha(File.ReadAllBytes(fna)) == FnaHash && Sha(File.ReadAllBytes(pendingGame)) == Sha(gameBytes) && Sha(File.ReadAllBytes(pendingLibrary)) == Sha(libraryBytes), "source/input/proof pair changed");
            if (supplied)
            {
                Json(Path.Combine(output, "supplied-proof.json"), new { passed = true, sourceHash, gameSha256 = Sha(gameBytes), reLogicSha256 = Sha(libraryBytes), audit, negatives, proof });
                Console.WriteLine("SUPPLIED HINT52 PROOF PASSED");
                return 0;
            }
            File.Move(pendingGame, Path.Combine(output, "Terraria.exe"));
            File.Move(pendingLibrary, Path.Combine(output, "ReLogic.dll"));
            Json(Path.Combine(output, "acceptance.json"), new
            {
                accepted = true, hardwarePending = true, performanceAdopted = false, sourceHash,
                inputSha256 = InputHash, inputReLogicSha256 = ReLogicHash, fnaSha256 = FnaHash, targetCorelibSha256 = CoreHash,
                outputSha256 = Sha(gameBytes), outputReLogicSha256 = Sha(libraryBytes), outputMvid = game.MainModule.Mvid, outputReLogicMvid = library.MainModule.Mvid,
                receipt, audit, negatives, proof,
                scope = "Guarded parse-preserving hint layout skip on50. Original root changes one call; parser callbacks run once; actual safe snippet/font bounds checked without cache/reflection; same-list fallback retains original layout/error path. ReLogic adds read-only metric helpers only; five other AOT objects/FNA unchanged. No profiler or hint/visual/input removal; hardware gain untested. Races/native corruption/runtime detours/resource exhaustion remain outside supported proof."
            });
            Console.WriteLine("ACCEPTED " + output + " game=" + Sha(gameBytes) + " ReLogic=" + Sha(libraryBytes));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
