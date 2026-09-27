using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Contracts;

internal static class Program
{
    internal static void Require(bool value, string message) => Contracts.Require(value, message);
    internal static string Sha(byte[] bytes) => Contracts.Sha(bytes);
    static Dictionary<string, string> Sources()
    {
        string path = Environment.GetEnvironmentVariable("LIGHT57_SOURCE_MANIFEST") ?? throw new InvalidDataException("LIGHT57_SOURCE_MANIFEST required");
        byte[] bytes = File.ReadAllBytes(path);
        string built = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Light57SourceManifest").Value!;
        Require(Sha(bytes) == built, "source manifest differs from compiled tool");
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)!;
        foreach (string name in new[] { "Program.cs", "Contracts.cs", "LightAudit.cs", "AuditQualified.cs", "PatchLightValue.csproj", "run.py", "proof/Program.cs", "proof/Projection.cs", "proof/Fixture.cs", "proof/ColorProof.csproj", "proof/run.py" })
            Require(sources.Keys.Count(key => key.EndsWith("/patch_light_value/" + name, StringComparison.Ordinal)) == 1, "incomplete light57 source manifest: " + name);
        foreach (string helper in new[] { "patch_tile_stack_state/AuditCanonical.cs", "patch_render_profile/RenderRawSignatures.cs", "patch_tile_profile/Common.cs", "patch_time_logger/PeResources.cs", "patch_frame_profile/PatchFrameProfile.csproj" })
            Require(sources.Keys.Count(key => key.EndsWith("/" + helper, StringComparison.Ordinal)) == 1, "shared source missing: " + helper);
        foreach (var (file, hash) in sources) Require(Sha(File.ReadAllBytes(file)) == hash, "source changed since build: " + file);
        return sources;
    }
    static string SourceHash() { _ = Sources(); return Sha(File.ReadAllBytes(Environment.GetEnvironmentVariable("LIGHT57_SOURCE_MANIFEST")!)); }
    static string ArtifactHash()
    {
        string path = Environment.GetEnvironmentVariable("LIGHT57_BUILD_ARTIFACTS") ?? throw new InvalidDataException("LIGHT57_BUILD_ARTIFACTS required");
        var artifacts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))!;
        string support = Environment.GetEnvironmentVariable("FRAME_PROFILE_TOOL") ?? throw new InvalidDataException("FRAME_PROFILE_TOOL required");
        string compiledSupport = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Light57SupportArtifact").Value!;
        Require(Sha(File.ReadAllBytes(support)) == compiledSupport, "support artifact differs from compiled dependency");
        Require(artifacts.ContainsKey(typeof(Program).Assembly.Location) && artifacts.ContainsKey(Common.Support.Location) && artifacts.ContainsKey(typeof(AssemblyDefinition).Assembly.Location), "incomplete build artifact manifest");
        foreach (var (file, hash) in artifacts) Require(Sha(File.ReadAllBytes(file)) == hash, "built artifact changed: " + file);
        return Sha(File.ReadAllBytes(path));
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
    static void CheckImage(string path, string hash) => Require(Sha(File.ReadAllBytes(path)) == hash && (File.GetUnixFileMode(path) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "proof input changed or writable: " + path);
    // Exact validated color-local/Emit.cs transformation and two-pass MVID derivation.
    internal static byte[] Emit(AssemblyDefinition game)
    {
        var target = (MethodDefinition)game.MainModule.LookupToken(TargetToken);
        Require(target.FullName == "Microsoft.Xna.Framework.Color Terraria.Lighting::GetColor(System.Int32,System.Int32)", "target differs");
        Require(target.Body.Instructions.Count == 65 && target.Body.Variables.Count == 5 && target.Body.ExceptionHandlers.Count == 0 && target.Body.InitLocals, "target shape differs");
        var body = target.Body; var il = body.GetILProcessor(); var source = body.Instructions.ToArray();
        Instruction At(int offset) => source.Single(i => i.Offset == offset);
        var engineCall = At(0x1c); var firstDup = At(0x2d); var secondDup = At(0x37); var finalField = At(0x41);
        Require(engineCall.OpCode == OpCodes.Callvirt && engineCall.Operand is MethodReference engine && engine.FullName == "Microsoft.Xna.Framework.Vector3 Terraria.Graphics.Light.ILightingEngine::GetColor(System.Int32,System.Int32)", "engine call differs");
        Require(firstDup.OpCode == OpCodes.Dup && secondDup.OpCode == OpCodes.Dup, "stack dup shape differs");
        Require(firstDup.Next.Operand is FieldReference xField && xField.Name == "X" && secondDup.Next.Operand is FieldReference yField && yField.Name == "Y" && finalField.Operand is FieldReference zField && zField.Name == "Z", "component sequence differs");
        var targets = source.SelectMany(i => i.Operand is Instruction one ? new[] { one } : i.Operand is Instruction[] many ? many : Array.Empty<Instruction>()).ToHashSet();
        Require(!targets.Contains(engineCall.Next) && !targets.Contains(firstDup) && !targets.Contains(secondDup) && !targets.Contains(finalField), "branch enters the scalarization region");
        var vector = new VariableDefinition(game.MainModule.ImportReference(((MethodReference)engineCall.Operand).ReturnType));
        Require(vector.VariableType.IsValueType && vector.VariableType.FullName == "Microsoft.Xna.Framework.Vector3", "vector kind differs");
        body.Variables.Add(vector);
        var save = Instruction.Create(OpCodes.Stloc, vector); il.InsertAfter(engineCall, save);
        firstDup.OpCode = OpCodes.Ldloca; firstDup.Operand = vector;
        secondDup.OpCode = OpCodes.Ldloca; secondDup.Operand = vector;
        var finalReceiver = Instruction.Create(OpCodes.Ldloca, vector); il.InsertBefore(finalField, finalReceiver);
        Require(body.Instructions.Count == 67 && body.Variables.Count == 6, "unexpected edit size");
        var inserted = new[] { body.Instructions.IndexOf(save), body.Instructions.IndexOf(finalReceiver) };
        var replaced = new[] { body.Instructions.IndexOf(firstDup), body.Instructions.IndexOf(secondDup) };
        game.MainModule.Mvid = Guid.Empty;
        using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); game.MainModule.Mvid = new Guid(SHA256.HashData(stream.ToArray()).AsSpan(0, 16)); }
        byte[] candidate;
        using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); candidate = stream.ToArray(); }
        Require(Sha(candidate) == CandidateHash && game.MainModule.Mvid == Guid.Parse(CandidateMvid), "current-source emission differs from frozen value-local candidate");
        return candidate;
    }
    static JsonElement Proof(string original, string candidate, string fna, string output)
    {
        var sources = Sources();
        string script = sources.Keys.Single(p => p.EndsWith("/patch_light_value/proof/run.py", StringComparison.Ordinal));
        var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { script, "--input", original, "--candidate", candidate, "--fna", fna, "--output", output }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidDataException("proof process did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.WaitForExit();
        File.WriteAllText(output + ".log", stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        Require(process.ExitCode == 0, "mandatory real behavior proof failed: " + output + ".log");
        var receipt = JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(Path.Combine(output, "proof-results.json")));
        Require(receipt.GetProperty("passed").GetBoolean() && receipt.GetProperty("inputSha256").GetString() == InputHash && receipt.GetProperty("candidateSha256").GetString() == CandidateHash && receipt.GetProperty("fnaSha256").GetString() == FnaHash, "behavior receipt identity differs");
        string proofSource = Path.GetDirectoryName(script)!;
        var proofSources = receipt.GetProperty("sourceHashes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var expectedProofSources = sources.Where(p => Path.GetDirectoryName(p.Key) == proofSource).ToDictionary(p => Path.GetFileName(p.Key), p => p.Value);
        Require(proofSources.Count == expectedProofSources.Count && proofSources.All(p => expectedProofSources.TryGetValue(p.Key, out var hash) && hash == p.Value), "behavior source binding differs");
        string proofHash = Sha(Encoding.UTF8.GetBytes(string.Concat(proofSources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\0" + p.Value + "\n"))));
        Require(receipt.GetProperty("sourceHash").GetString() == proofHash, "behavior source hash differs");
        int artifacts = 0;
        foreach (var artifact in receipt.GetProperty("artifacts").EnumerateObject())
        {
            string path = Path.GetFullPath(Path.Combine(output, artifact.Name));
            Require(path.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Sha(File.ReadAllBytes(path)) == artifact.Value.GetString(), "behavior artifact binding differs"); artifacts++;
        }
        Require(artifacts > 0 && receipt.GetProperty("caseCount").GetInt32() == 17350 && receipt.GetProperty("lanes").GetInt32() == 2 && receipt.GetProperty("repetitions").GetInt32() == 3 && receipt.GetProperty("pairedComparisons").GetInt32() == 104100, "behavior execution coverage differs");
        var controls = receipt.GetProperty("negativeControls"); var allocation = receipt.GetProperty("zeroAllocation");
        Require(controls.GetProperty("passed").GetBoolean() && controls.GetProperty("count").GetInt32() == 5 && controls.GetProperty("detected").GetInt32() == 5 && allocation.GetProperty("passed").GetBoolean() && allocation.GetProperty("measuredBytes").GetInt64() == 0, "behavior controls/allocation failed");
        Json(Path.Combine(output, "proof.json"), new { passed = true, baselineSha256 = InputHash, candidateSha256 = CandidateHash, behavior = receipt });
        return receipt;
    }
    static int Main(string[] args)
    {
        try
        {
            Require(OperatingSystem.IsLinux(), "use pinned Linux monobuild");
            bool supplied = args.Length == 5 && args[0] == "prove";
            Require(supplied || args.Length == 4 && args[0] is "emit" or "verify", "Usage: PatchLightValue <emit|verify> <exact52> <FNA> <fresh-output>, or prove <exact52> <candidate-pair> <FNA> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[supplied ? 3 : 2]), output = Path.GetFullPath(args[supplied ? 4 : 3]);
            string relogic = Path.Combine(Path.GetDirectoryName(input)!, "ReLogic.dll");
            FreshOutput(output);
            void Inputs()
            {
                Require(Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(relogic)) == ReLogicHash && Sha(File.ReadAllBytes(fna)) == FnaHash, "pinned52 game/ReLogic/FNA required");
                string adjacentFna = Path.Combine(Path.GetDirectoryName(input)!, "FNA.dll");
                Require(!File.Exists(adjacentFna) || Sha(File.ReadAllBytes(adjacentFna)) == FnaHash, "resolver FNA differs from pinned dependency");
                Require(Sha(File.ReadAllBytes(CorePath)) == CoreHash, "pinned CoreLib changed");
                Require(Sha(File.ReadAllBytes(DotnetPath)) == DotnetHash && Sha(File.ReadAllBytes(typeof(AssemblyDefinition).Assembly.Location)) == CecilHash, "pinned dotnet/Cecil changed");
            }
            Inputs(); string sourceHash = SourceHash(), artifactHash = ArtifactHash();
            byte[] original = File.ReadAllBytes(input), library = File.ReadAllBytes(relogic);
            using var resolver = Resolver(input, fna);
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(original), new ReaderParameters { AssemblyResolver = resolver });
            Require(game.MainModule.Mvid == Guid.Parse(InputMvid), "input MVID differs");
            byte[] expected = Emit(game);
            string? suppliedGame = supplied ? Path.Combine(Path.GetFullPath(args[2]), "Terraria.exe") : null;
            string? suppliedLibrary = supplied ? Path.Combine(Path.GetFullPath(args[2]), "ReLogic.dll") : null;
            byte[] candidate = supplied ? File.ReadAllBytes(suppliedGame!) : expected;
            if (supplied)
            {
                Require(candidate.SequenceEqual(expected), "supplied image differs from exact current-source emission");
                Require(Sha(File.ReadAllBytes(suppliedLibrary!)) == ReLogicHash, "supplied ReLogic is not byte-identical52");
            }
            var audit = LightAudit.Verify(original, candidate, expected, input, fna);
            Directory.CreateDirectory(output);
            Json(Path.Combine(output, "preparation.json"), audit);
            string pending = Path.Combine(output, "proof-pair"); Directory.CreateDirectory(pending);
            string pendingGame = Path.Combine(pending, "Terraria.exe"), pendingLibrary = Path.Combine(pending, "ReLogic.dll");
            ReadonlyImage(pendingGame, candidate); ReadonlyImage(pendingLibrary, library);
            void FinalGuards()
            {
                Inputs(); Require(SourceHash() == sourceHash && ArtifactHash() == artifactHash, "source/build changed during verification");
                CheckImage(pendingGame, CandidateHash); CheckImage(pendingLibrary, ReLogicHash);
                if (supplied) Require(Sha(File.ReadAllBytes(suppliedGame!)) == CandidateHash && Sha(File.ReadAllBytes(suppliedLibrary!)) == ReLogicHash, "supplied pair changed during proof");
            }
            if (args[0] == "emit")
            {
                FinalGuards(); Json(Path.Combine(output, "inspection-only.json"), new { passed = true, accepted = false, sourceHash, artifactHash, inputSha256 = InputHash, outputSha256 = CandidateHash, outputMvid = CandidateMvid, reLogicSha256 = ReLogicHash, fnaSha256 = FnaHash, audit, hardwarePending = true, performanceAdopted = false, optimizationAdopted = false, activePerformanceBaseline = 52 });
                Console.WriteLine("INSPECTION ONLY " + pendingGame + " " + CandidateHash); return 0;
            }
            var negatives = LightAudit.Negatives(original, candidate, input, fna, Path.Combine(output, "static-negative"));
            var behavior = Proof(input, pendingGame, fna, Path.Combine(output, "proof"));
            FinalGuards();
            Json(Path.Combine(output, "validation.json"), new { passed = true, accepted = false, sourceHash, artifactHash, inputSha256 = InputHash, outputSha256 = CandidateHash, outputMvid = CandidateMvid, reLogicSha256 = ReLogicHash, fnaSha256 = FnaHash, targetCorelibSha256 = CoreHash, audit, negatives, proof = new { passed = true, behavior }, unchangedCalls = 4, changedOriginalBodies = 1, hardwarePending = true, performanceAdopted = false, optimizationAdopted = false, activePerformanceBaseline = 52 });
            Console.WriteLine("VERIFIED PENDING RUNNER GATES " + output); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
