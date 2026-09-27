using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22";
    internal const string InputMvid = "df61a8d1-0622-9555-82c2-a2118b6c9bc4";
    internal const string ReLogicHash = "856438fe15b91b2bc1972f8ebd710cb3ef0ba2d6d7900e65d33bbf2e3c8d68c8";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string CoreHash = "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd";
    internal const string CecilHash = "d864ae1b39be10eaf671cd4a8a5e6bd2613740ca57b294c400776f8617dc5355";
    internal const string DotnetHash = "11e4b2ad384bfa6c1adf01b0adbbe9dfe5d673ddbdfdbde0412411ac4e027520";
    internal const string DotnetPath = "/build/runtime-source/.dotnet/dotnet";
    internal const string MainDrawName = "System.Void Terraria.Main::Draw(Microsoft.Xna.Framework.GameTime)";
    internal const string RunGameName = "System.Void Terraria.Program::RunGame()";
    internal const string InterfaceName = "System.Void Terraria.Main::DrawInterface(Microsoft.Xna.Framework.GameTime)";
    internal static void Require(bool condition, string message) => Common.Require(condition, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);
    internal static MethodDefinition Method(ModuleDefinition module, string name) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == name);

    internal static string SourceHash()
    {
        string path = Environment.GetEnvironmentVariable("RECIPE55_SOURCE_MANIFEST") ?? throw new InvalidDataException("RECIPE55_SOURCE_MANIFEST required");
        byte[] manifest = File.ReadAllBytes(path);
        string built = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Recipe55SourceManifest").Value!;
        Require(Sha(manifest) == built, "source manifest differs from compiled tool");
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
        string[] required = { "Program.cs", "Contracts.cs", "RecipePatcher.cs", "RecipeAudit.cs", "RecipeAuditRaw.cs", "RecipeAuditNegative.cs", "TimingTemplate.cs", "StateTemplate.cs", "PatchRecipeProfile.csproj", "run.py", "recipe55-schema.json" };
        Require(required.All(name => sources.Keys.Count(key => key.EndsWith("/patch_recipe_profile/" + name, StringComparison.Ordinal)) == 1), "incomplete recipe55 source manifest");
        foreach (string helper in new[] { "/patch_tile_profile/Common.cs", "/patch_render_profile/RenderRawSignatures.cs", "/patch_time_logger/PeResources.cs", "/patch_frame_profile/PatchFrameProfile.csproj" })
            Require(sources.Keys.Count(key => key.EndsWith(helper, StringComparison.Ordinal)) == 1, "shared helper missing from source manifest");
        foreach (var (file, expected) in sources) Require(Sha(File.ReadAllBytes(file)) == expected, "source changed since build: " + file);
        string directory = Path.GetDirectoryName(sources.Keys.Single(key => key.EndsWith("/patch_recipe_profile/Program.cs", StringComparison.Ordinal)))!;
        foreach (string file in Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p) is ".cs" or ".py" or ".json" or ".csproj"))
            Require(sources.ContainsKey(file), "unbound patch source: " + file);
        return Sha(manifest);
    }

    static string ArtifactHash()
    {
        string path = Environment.GetEnvironmentVariable("RECIPE55_BUILD_ARTIFACTS") ?? throw new InvalidDataException("RECIPE55_BUILD_ARTIFACTS required");
        byte[] manifest = File.ReadAllBytes(path);
        var artifacts = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
        Require(artifacts.ContainsKey(typeof(Program).Assembly.Location) && artifacts.ContainsKey(Support.Location) && artifacts.ContainsKey(typeof(AssemblyDefinition).Assembly.Location), "incomplete build artifact manifest");
        string builtSupport = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Recipe55SupportArtifact").Value!;
        string selectedSupport = Environment.GetEnvironmentVariable("FRAME_PROFILE_TOOL") ?? throw new InvalidDataException("FRAME_PROFILE_TOOL required");
        Require(Sha(File.ReadAllBytes(Support.Location)) == builtSupport && Sha(File.ReadAllBytes(selectedSupport)) == builtSupport, "support artifact differs from compiled dependency");
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
        Require(new FileInfo(path).LinkTarget == null && Sha(File.ReadAllBytes(path)) == hash && (File.GetUnixFileMode(path) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "proof input changed or writable: " + path);
    }

    static object CheckedProof(object proof, string name)
    {
        Require(JsonSerializer.SerializeToElement(proof).GetProperty("passed").GetBoolean(), name + " actual supplied-image proof failed");
        return proof;
    }

    static object Reports(string fixtures, string candidate, string output)
    {
        string script = Environment.GetEnvironmentVariable("RECIPE55_REPORT_PROOF") ?? throw new InvalidDataException("RECIPE55_REPORT_PROOF required");
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Environment.GetEnvironmentVariable("RECIPE55_SOURCE_MANIFEST")!))!;
        string Bound(string suffix) => sources.Keys.Single(path => path.EndsWith(suffix, StringComparison.Ordinal));
        Require(Path.GetFullPath(script) == Bound("/patch_recipe_profile/prove_recipe_reports.py"), "report proof is not the source-bound script");
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { script, "--fixtures", fixtures, "--candidate", candidate, "--output", output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start report proof");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new TimeoutException("report proof timed out"); }
        string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult(); File.WriteAllText(output + ".log", log);
        Require(process.ExitCode == 0, "actual report proof failed: " + log);
        var result = JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(output));
        Require(result.GetProperty("passed").GetBoolean() && result.GetProperty("candidate_sha256").GetString() == Sha(File.ReadAllBytes(candidate)), "parser proof candidate/passed mismatch");
        Require(result.GetProperty("analyzer_sha256").GetString() == sources[Bound("/scripts/analyze_recipe_profile.py")] && result.GetProperty("schema_sha256").GetString() == sources[Bound("/patch_recipe_profile/recipe55-schema.json")], "parser proof source/schema mismatch");
        return result;
    }

    static int Main(string[] args)
    {
        try {
            Require(OperatingSystem.IsLinux(), "use pinned Linux monobuild");
            bool supplied = args.Length == 5 && args[0] == "prove";
            Require(supplied || args.Length == 4 && args[0] is "emit" or "accept", "Usage: PatchRecipeProfile <emit|accept> <build52-Terraria.exe> <FNA.dll> <fresh-output>, or prove <build52> <candidate-pair-directory> <FNA> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[supplied ? 3 : 2]), output = Path.GetFullPath(args[supplied ? 4 : 3]);
            string relogic = Path.Combine(Path.GetDirectoryName(input)!, "ReLogic.dll");
            FreshOutput(output);
            void Inputs() {
                Require(Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(relogic)) == ReLogicHash && Sha(File.ReadAllBytes(fna)) == FnaHash, "pinned52 game/ReLogic/FNA required");
                Require(Sha(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(input)!, "FNA.dll"))) == FnaHash, "resolver FNA differs from pinned dependency");
                Require(Sha(File.ReadAllBytes(RecipePatcher.PinnedResolver.Core)) == CoreHash, "pinned CoreLib changed");
                Require(Sha(File.ReadAllBytes(DotnetPath)) == DotnetHash && Sha(File.ReadAllBytes(typeof(AssemblyDefinition).Assembly.Location)) == CecilHash, "pinned dotnet/Cecil changed");
            }
            Inputs(); string sourceHash = SourceHash(), artifactHash = ArtifactHash();
            byte[] original = File.ReadAllBytes(input), library = File.ReadAllBytes(relogic);
            using var resolver = RecipePatcher.Resolver(input);
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(original), new ReaderParameters { AssemblyResolver = resolver });
            Require(game.MainModule.Mvid == Guid.Parse(InputMvid), "input MVID differs");
            var receipts = RecipePatcher.Inject(game.MainModule);
            game.MainModule.Mvid = Guid.Empty;
            using (var identity = new MemoryStream()) { game.Write(identity, new WriterParameters { Timestamp = 0 }); game.MainModule.Mvid = new Guid(SHA256.HashData(identity.ToArray()).AsSpan(0, 16)); }
            byte[] expected;
            using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); expected = stream.ToArray(); }
            string? suppliedGame = supplied ? Path.Combine(Path.GetFullPath(args[2]), "Terraria.exe") : null;
            string? suppliedLibrary = supplied ? Path.Combine(Path.GetFullPath(args[2]), "ReLogic.dll") : null;
            byte[] candidate = supplied ? File.ReadAllBytes(suppliedGame!) : expected;
            if (supplied) Require(Sha(File.ReadAllBytes(suppliedLibrary!)) == ReLogicHash, "supplied ReLogic is not byte-identical52");
            Directory.CreateDirectory(output);
            var audit = RecipeAudit.Verify(original, candidate, receipts, input);
            Require(candidate.AsSpan().SequenceEqual(expected), "supplied image differs from exact current-source emission");
            string candidateHash = Sha(candidate), candidateMvid = game.MainModule.Mvid.ToString();
            Json(Path.Combine(output, "receipts.json"), receipts); Json(Path.Combine(output, "preservation.json"), audit);
            string pending = Path.Combine(output, "proof-pair"); Directory.CreateDirectory(pending);
            string pendingGame = Path.Combine(pending, "Terraria.exe"), pendingLibrary = Path.Combine(pending, "ReLogic.dll");
            ReadonlyImage(pendingGame, candidate); ReadonlyImage(pendingLibrary, library);
            void FinalGuards() {
                Inputs(); Require(SourceHash() == sourceHash && ArtifactHash() == artifactHash, "source/build changed during verification");
                VerifyImage(pendingGame, candidateHash); VerifyImage(pendingLibrary, ReLogicHash);
                if (supplied) Require(Sha(File.ReadAllBytes(suppliedGame!)) == candidateHash && Sha(File.ReadAllBytes(suppliedLibrary!)) == ReLogicHash, "supplied pair changed during proof");
            }
            if (args[0] == "emit") {
                FinalGuards();
                Json(Path.Combine(output, "inspection-only.json"), new { accepted = false, hardwarePending = true, measurementOnly = true, performanceAdopted = false, inputSha256 = InputHash, outputSha256 = candidateHash, outputMvid = candidateMvid, reLogicSha256 = ReLogicHash, fnaSha256 = FnaHash, targetCorelibSha256 = CoreHash, sourceHash, artifactHash, proof = (object?)null, audit });
                Console.WriteLine("INSPECTION ONLY " + pendingGame + " sha256=" + candidateHash); return 0;
            }
            var negatives = RecipeAudit.NegativeProof(original, candidate, receipts, input, Path.Combine(output, "static-negative"));
            string proofDirectory = Path.Combine(output, "proof"); Directory.CreateDirectory(proofDirectory);
            var behavior = CheckedProof(RecipeProof.Run(input, pendingGame, fna, Path.Combine(proofDirectory, "behavior")), nameof(RecipeProof));
            var runtime = CheckedProof(RecipeRuntimeProof.Run(input, pendingGame, fna, Path.Combine(proofDirectory, "runtime")), nameof(RecipeRuntimeProof));
            var parser = Reports(Path.Combine(proofDirectory, "runtime", "report-fixtures"), pendingGame, Path.Combine(proofDirectory, "parser-proof.json"));
            var proof = new { passed = true, baselineSha256 = InputHash, candidateSha256 = candidateHash, behavior, runtime, parser };
            Json(Path.Combine(proofDirectory, "proof.json"), proof);
            FinalGuards();
            var acceptance = new { passed = true, accepted = !supplied, hardwarePending = true, measurementOnly = true, performanceAdopted = false, inputSha256 = InputHash, outputSha256 = candidateHash, outputMvid = candidateMvid, reLogicSha256 = ReLogicHash, fnaSha256 = FnaHash, targetCorelibSha256 = CoreHash, sourceHash, artifactHash, audit, negatives, proof, scope = "Measurement-only recipe55 on untouched52: fourteen existing bodies, five coarse callee envelopes, zero wrappers, seven unchanged coarse callsites. Recipe exclusive includes unmeasured conditions and observer cost; no pure-scan CPU or Switch performance claim." };
            if (supplied) { Json(Path.Combine(output, "supplied-proof.json"), acceptance); Console.WriteLine("SUPPLIED RECIPE55 PROOF PASSED"); return 0; }
            File.Move(pendingGame, Path.Combine(output, "Terraria.exe")); File.Move(pendingLibrary, Path.Combine(output, "ReLogic.dll"));
            Json(Path.Combine(output, "acceptance.json"), acceptance);
            Console.WriteLine("ACCEPTED " + output + " game=" + candidateHash + " ReLogic=" + ReLogicHash); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
