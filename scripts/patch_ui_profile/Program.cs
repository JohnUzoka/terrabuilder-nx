using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "06b11beb9c83fc14140754eb5a44c7fa0525d1877ea2fd0860a75b91f3d6f4f6";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string ReLogicHash = "2e7750fe79ba48bcca8e7ea5691f87c0e5887024b58552c433efb2e2ad060a65";
    internal const string CoreHash = "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd";
    internal const string MainDrawName = "System.Void Terraria.Main::Draw(Microsoft.Xna.Framework.GameTime)";
    internal const string RunGameName = "System.Void Terraria.Program::RunGame()";
    internal const string InterfaceName = "System.Void Terraria.Main::DrawInterface(Microsoft.Xna.Framework.GameTime)";
    internal const string LayerDrawName = "System.Boolean Terraria.UI.GameInterfaceLayer::Draw()";
    internal static void Require(bool condition, string message) => Common.Require(condition, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);
    internal static MethodDefinition Method(ModuleDefinition module, string name) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == name);

    internal static string SourceHash()
    {
        string path = Environment.GetEnvironmentVariable("UI51_SOURCE_MANIFEST") ?? throw new InvalidDataException("UI51_SOURCE_MANIFEST required");
        byte[] bytes = File.ReadAllBytes(path);
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)!;
        string[] required = { "Contracts.cs", "TimingTemplate.cs", "StateTemplate.cs", "UIProfilePatcher.cs", "UIProfileAudit.cs", "UIProfileProof.cs", "UIProfileProbe.cs", "UIProfileRuntimeProof.cs", "UIProfileFixture.cs", "Program.cs", "PatchUIProfile.csproj", "ui51-schema.json", "run.py" };
        Require(required.All(name => sources.Keys.Any(key => key.EndsWith("/patch_ui_profile/" + name, StringComparison.Ordinal))) && sources.Keys.Any(key => key.EndsWith("/scripts/analyze_ui_profile.py", StringComparison.Ordinal)), "incomplete UI51 source manifest");
        foreach (var (file, hash) in sources) Require(Sha(File.ReadAllBytes(file)) == hash, "source changed since build: " + file);
        return Sha(bytes);
    }

    static void FreshOutput(string output)
    {
        Require(!Path.Exists(output) && new FileInfo(output).LinkTarget == null && new DirectoryInfo(output).LinkTarget == null, "fresh non-alias output required");
        for (var parent = new DirectoryInfo(output).Parent; parent != null; parent = parent.Parent) Require(parent.LinkTarget == null, "symlink output ancestor forbidden");
    }

    static object VerifyReports(string directory, string candidateSha)
    {
        string analyzer = Environment.GetEnvironmentVariable("UI51_ANALYZER") ?? throw new InvalidDataException("UI51_ANALYZER required");
        string manifestPath = Path.Combine(directory, "report-manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        Require(manifest.RootElement.GetProperty("candidateSha256").GetString() == candidateSha, "report proof belongs to another candidate");
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var receipts = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int accepted = 0, rejected = 0, index = 0;
        foreach (string kind in new[] { "accepted", "rejected" })
        foreach (var item in manifest.RootElement.GetProperty(kind).EnumerateArray())
        {
            string relative = item.GetProperty("file").GetString()!;
            string input = Path.GetFullPath(Path.Combine(directory, relative));
            Require(input.StartsWith(root, StringComparison.Ordinal) && seen.Add(input) && File.Exists(input), "report proof file missing/aliased/duplicated");
            string parsed = Path.Combine(directory, "parser-" + index + ".json");
            var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in new[] { analyzer, input, "--output", parsed }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start report analyzer");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("report analyzer timed out"); }
            string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(directory, "parser-" + index + ".log"), log);
            if (kind == "accepted")
            {
                Require(process.ExitCode == 0, "actual runtime report rejected: " + relative + "\n" + log);
                using var result = JsonDocument.Parse(File.ReadAllBytes(parsed));
                Require(result.RootElement.GetProperty("measurement_invalid").GetBoolean() == item.GetProperty("measurement_invalid").GetBoolean(), "report invalidity differs: " + relative);
                Require(result.RootElement.GetProperty("final_seen").GetBoolean() == item.GetProperty("final_seen").GetBoolean(), "report final status differs: " + relative);
                accepted++;
            }
            else
            {
                Require(process.ExitCode != 0 && !File.Exists(parsed), "malformed report incorrectly accepted: " + relative);
                rejected++;
            }
            receipts.Add(new { file = relative, expected = kind, exitCode = process.ExitCode, inputSha256 = Sha(File.ReadAllBytes(input)), log = "parser-" + index + ".log" });
            index++;
        }
        Require(accepted > 0 && rejected > 0, "positive and negative actual-report proof required");
        var proof = new { passed = true, candidateSha256 = candidateSha, analyzerSha256 = Sha(File.ReadAllBytes(analyzer)), accepted, rejected, receipts };
        Json(Path.Combine(directory, "parser-proof.json"), proof);
        return proof;
    }

    static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("use the pinned Linux monobuild environment");
            bool supplied = args.Length == 5 && args[0] == "prove";
            Require(supplied || args.Length == 4 && args[0] is "inspect" or "emit" or "accept", "Usage: PatchUIProfile <inspect|emit|accept> <build50-Terraria.exe> <FNA.dll> <fresh-output>, or prove <build50> <exact-candidate> <FNA> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[supplied ? 3 : 2]), output = Path.GetFullPath(args[supplied ? 4 : 3]);
            FreshOutput(output);
            byte[] originalBytes = File.ReadAllBytes(input);
            Require(Sha(originalBytes) == InputHash, "unsupported/already-profiled input; build50 required");
            Require(Sha(File.ReadAllBytes(fna)) == FnaHash, "unsupported FNA");
            string relogic = Path.Combine(Path.GetDirectoryName(input)!, "ReLogic.dll");
            Require(Sha(File.ReadAllBytes(relogic)) == ReLogicHash, "unsupported ReLogic");
            const string core = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
            Require(Sha(File.ReadAllBytes(core)) == CoreHash, "target CoreLib changed");
            using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes));
            Require(original.MainModule.Mvid == Guid.Parse("18ab5d9f-3419-30c0-509e-cf5a34c42f0b"), "baseline MVID differs");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { accepted = false, inputSha256 = InputHash, roots = new[] { MainDrawName, InterfaceName, LayerDrawName, RunGameName }.Select(name => new { name, fingerprint = Fingerprint(Method(original.MainModule, name)), il = Body(Method(original.MainModule, name)) }) });
                return 0;
            }
            string sourceHash = SourceHash();
            using var resolver = UIProfilePatcher.Resolver(input);
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
            var receipts = UIProfilePatcher.Inject(game.MainModule);
            game.MainModule.Mvid = Guid.Empty;
            using (var identity = new MemoryStream()) { game.Write(identity, new WriterParameters { Timestamp = 0 }); game.MainModule.Mvid = new Guid(SHA256.HashData(identity.ToArray()).AsSpan(0, 16)); }
            byte[] bytes;
            using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); bytes = stream.ToArray(); }
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
            if (supplied) Require(Sha(File.ReadAllBytes(Path.GetFullPath(args[2]))) == Sha(bytes), "supplied candidate differs from exact current-source emission");
            var audit = UIProfileAudit.Verify(originalBytes, bytes, receipts, input);
            Json(Path.Combine(output, "receipts.json"), receipts);
            Json(Path.Combine(output, "preservation.json"), audit);
            string pending = Path.Combine(output, "candidate-proof-only.exe");
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            if (args[0] == "emit")
            {
                Json(Path.Combine(output, "inspection-only.json"), new { accepted = false, hardwarePending = true, measurementOnly = true, sourceHash, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid, audit });
                Console.WriteLine("INSPECTION ONLY " + pending);
                return 0;
            }
            string proofDirectory = Path.Combine(output, "proof");
            var staticNegatives = UIProfileAudit.NegativeProof(originalBytes, bytes, receipts, input, Path.Combine(output, "static-negative"));
            var behavior = UIProfileProof.Run(input, pending, fna, proofDirectory);
            Require(JsonSerializer.SerializeToElement(behavior).GetProperty("passed").GetBoolean(), "behavior/runtime proof did not pass");
            var parserProof = VerifyReports(proofDirectory, Sha(bytes));
            Require(Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(fna)) == FnaHash && Sha(File.ReadAllBytes(relogic)) == ReLogicHash && Sha(File.ReadAllBytes(pending)) == Sha(bytes) && SourceHash() == sourceHash, "source/input/candidate changed during acceptance");
            if (supplied)
            {
                Json(Path.Combine(output, "supplied-proof.json"), new { passed = true, sourceHash, candidateSha256 = Sha(bytes), audit, staticNegatives, behavior, parserProof });
                Console.WriteLine("SUPPLIED UI51 PROOF PASSED");
                return 0;
            }
            string destination = Path.Combine(output, "Terraria.exe");
            File.Move(pending, destination);
            Json(Path.Combine(output, "acceptance.json"), new { accepted = true, hardwarePending = true, measurementOnly = true, performanceAdopted = false, sourceHash, inputSha256 = InputHash, fnaSha256 = FnaHash, reLogicSha256 = ReLogicHash, targetCorelibSha256 = CoreHash, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid, audit, staticNegatives, behavior, parserProof, scope = "Measurement-only on50; nested observer-inclusive UI costs and raw boundary state. Six non-game AOT/framework objects remain baseline. Host boundary proof does not establish Switch overhead/FPS/GPU fidelity or arbitrary capture/mod compatibility." });
            Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
