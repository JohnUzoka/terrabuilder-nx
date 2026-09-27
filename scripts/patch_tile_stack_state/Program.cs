using System.Reflection;
using System.Text.Json;
using Mono.Cecil;
using static Contracts;

internal static class Program
{
    internal static string SourceHash()
    {
        string path = Environment.GetEnvironmentVariable("STACK54_SOURCE_MANIFEST") ?? throw new InvalidDataException("STACK54_SOURCE_MANIFEST required");
        byte[] manifest = File.ReadAllBytes(path); string hash = Sha(manifest);
        string built = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Stack54SourceManifest").Value!;
        Require(hash == built, "source manifest differs from compiled tool");
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
        string[] required = { "Program.cs", "Contracts.cs", "StackStatePatcher.cs", "StackStateAudit.cs", "AuditCanonical.cs", "AuditClosure.cs", "AuditRaw.cs", "AuditNegative.cs", "StackStateBehaviorProof.cs", "StackStateProbe.cs", "StackStateFixture.cs", "StackStateProofBehavior.cs", "StackStateProofFieldFixture.cs", "StackStateProofGcFixture.cs", "StackStateProofExtraFixture.cs", "StackStateProofSourceAudit.cs", "StackStateProofCommon.cs", "PatchTileStackState.csproj", "run.py" };
        Require(required.All(name => sources.Keys.Count(key => key.EndsWith("/patch_tile_stack_state/" + name, StringComparison.Ordinal)) == 1), "incomplete stack54 source manifest");
        foreach (string helper in new[] { "/patch_tile_profile/Common.cs", "/patch_render_profile/RenderRawSignatures.cs", "/patch_frame_profile/PatchFrameProfile.csproj" })
            Require(sources.Keys.Count(key => key.EndsWith(helper, StringComparison.Ordinal)) == 1, "shared helper missing from source manifest");
        foreach (var (file, expected) in sources) Require(Sha(File.ReadAllBytes(file)) == expected, "source changed since build: " + file);
        return hash;
    }

    static string ArtifactHash()
    {
        string path = Environment.GetEnvironmentVariable("STACK54_BUILD_ARTIFACTS") ?? throw new InvalidDataException("STACK54_BUILD_ARTIFACTS required");
        byte[] manifest = File.ReadAllBytes(path);
        var artifacts = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
        Require(artifacts.ContainsKey(typeof(Program).Assembly.Location) && artifacts.ContainsKey(Common.Support.Location) && artifacts.ContainsKey(typeof(AssemblyDefinition).Assembly.Location), "incomplete build artifact manifest");
        foreach (var (file, hash) in artifacts) Require(Sha(File.ReadAllBytes(file)) == hash, "built artifact changed: " + file);
        return Sha(manifest);
    }

    static void FreshOutput(string output)
    {
        Require(!Path.Exists(output) && new FileInfo(output).LinkTarget == null && new DirectoryInfo(output).LinkTarget == null, "fresh non-alias output required");
        for (var parent = new DirectoryInfo(output).Parent; parent != null; parent = parent.Parent) Require(parent.LinkTarget == null, "symlink output ancestor forbidden");
    }

    static void ReadonlyImage(string path, byte[] bytes)
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    static void VerifyImage(string path, string hash)
    {
        Require(Sha(File.ReadAllBytes(path)) == hash && (File.GetUnixFileMode(path) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "proof input changed or writable: " + path);
    }

    static int Main(string[] args)
    {
        try
        {
            Require(OperatingSystem.IsLinux(), "use pinned Linux monobuild");
            bool supplied = args.Length == 5 && args[0] == "prove";
            Require(supplied || args.Length == 4 && args[0] is "inspect" or "emit" or "accept", "Usage: PatchTileStackState <inspect|emit|accept> <build52-Terraria.exe> <FNA.dll> <fresh-output>, or prove <build52> <candidate-pair-directory> <FNA> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[supplied ? 3 : 2]), output = Path.GetFullPath(args[supplied ? 4 : 3]);
            string relogic = Path.Combine(Path.GetDirectoryName(input)!, "ReLogic.dll");
            FreshOutput(output);
            void Inputs()
            {
                Require(Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(relogic)) == ReLogicHash && Sha(File.ReadAllBytes(fna)) == FnaHash, "pinned52 game/ReLogic/FNA required");
                Require(Sha(File.ReadAllBytes(CorePath)) == CoreHash, "pinned CoreLib changed");
                Require(Sha(File.ReadAllBytes(DotnetPath)) == DotnetHash && Sha(File.ReadAllBytes(typeof(AssemblyDefinition).Assembly.Location)) == CecilHash, "pinned dotnet/Cecil changed");
            }
            Inputs(); string sourceHash = SourceHash(), artifactHash = ArtifactHash();
            byte[] original = File.ReadAllBytes(input), library = File.ReadAllBytes(relogic);
            using var resolver = Resolver(input, fna);
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(original), new ReaderParameters { AssemblyResolver = resolver });
            Require(game.MainModule.Mvid == Guid.Parse(InputMvid), "input MVID differs");
            Directory.CreateDirectory(output);
            var closure = AuditClosure.Verify(game, Path.Combine(output, "closure"));
            if (args[0] == "inspect")
            {
                Inputs(); Require(SourceHash() == sourceHash && ArtifactHash() == artifactHash, "source/build changed during inspection");
                Json(Path.Combine(output, "inspection.json"), new { accepted = false, sourceHash, artifactHash, closure });
                return 0;
            }
            byte[] expected = StackStatePatcher.Emit(game);
            Require(Sha(expected) == CandidateHash && game.MainModule.Mvid == Guid.Parse(CandidateMvid), "production emission differs from frozen candidate03");
            string? suppliedGame = supplied ? Path.Combine(Path.GetFullPath(args[2]), "Terraria.exe") : null;
            string? suppliedLibrary = supplied ? Path.Combine(Path.GetFullPath(args[2]), "ReLogic.dll") : null;
            byte[] candidate = supplied ? File.ReadAllBytes(suppliedGame!) : expected;
            if (supplied) Require(Sha(File.ReadAllBytes(suppliedLibrary!)) == ReLogicHash, "supplied ReLogic is not byte-identical52");
            var audit = StackStateAudit.Verify(original, candidate, expected, input, fna, Path.Combine(output, "audit"));
            string pending = Path.Combine(output, "proof-pair"); Directory.CreateDirectory(pending);
            string pendingGame = Path.Combine(pending, "Terraria.exe"), pendingLibrary = Path.Combine(pending, "ReLogic.dll");
            ReadonlyImage(pendingGame, candidate); ReadonlyImage(pendingLibrary, library);
            void FinalGuards()
            {
                Inputs(); Require(SourceHash() == sourceHash && ArtifactHash() == artifactHash, "source/build changed during verification");
                VerifyImage(pendingGame, CandidateHash); VerifyImage(pendingLibrary, ReLogicHash);
                if (supplied) Require(Sha(File.ReadAllBytes(suppliedGame!)) == CandidateHash && Sha(File.ReadAllBytes(suppliedLibrary!)) == ReLogicHash, "supplied pair changed during proof");
            }
            if (args[0] == "emit")
            {
                FinalGuards();
                Json(Path.Combine(output, "inspection-only.json"), new { accepted = false, hardwarePending = true, performanceAdopted = false, sourceHash, artifactHash, gameSha256 = CandidateHash, reLogicSha256 = ReLogicHash, closure, audit });
                Console.WriteLine("INSPECTION ONLY " + pending); return 0;
            }
            var negatives = AuditNegative.Run(original, candidate, input, fna, Path.Combine(output, "static-negative"));
            var proof = StackStateBehaviorProof.Run(input, pendingGame, fna, Path.Combine(output, "proof"));
            Require(JsonSerializer.SerializeToElement(proof).GetProperty("passed").GetBoolean(), "actual state behavior/GC proof did not pass");
            FinalGuards();
            if (supplied)
            {
                Json(Path.Combine(output, "supplied-proof.json"), new { passed = true, accepted = false, hardwarePending = true, performanceAdopted = false, sourceHash, artifactHash, gameSha256 = CandidateHash, reLogicSha256 = ReLogicHash, closure, audit, negatives, proof });
                Console.WriteLine("SUPPLIED STACK54 PROOF PASSED"); return 0;
            }
            File.Move(pendingGame, Path.Combine(output, "Terraria.exe")); File.Move(pendingLibrary, Path.Combine(output, "ReLogic.dll"));
            Json(Path.Combine(output, "acceptance.json"), new { accepted = true, hardwarePending = true, performanceAdopted = false, sourceHash, artifactHash, inputSha256 = InputHash, outputSha256 = CandidateHash, outputMvid = CandidateMvid, inputReLogicSha256 = ReLogicHash, outputReLogicSha256 = ReLogicHash, reLogicSha256 = ReLogicHash, fnaSha256 = FnaHash, targetCorelibSha256 = CoreHash, closure, audit, negatives, proof, scope = "Vanilla reversible52/54 A/B only. Public TileDrawInfo constructor, existing definition tokens, other runtime/compiler workarounds and ReLogic/FNA are unchanged. Seven private helper signatures intentionally change. No Switch performance adoption." });
            Console.WriteLine("ACCEPTED " + output + " game=" + CandidateHash + " ReLogic=" + ReLogicHash); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
