using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string ReLogicHash = "856438fe15b91b2bc1972f8ebd710cb3ef0ba2d6d7900e65d33bbf2e3c8d68c8";
    internal const string CoreHash = "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd";
    internal const string MainDrawName = "System.Void Terraria.Main::Draw(Microsoft.Xna.Framework.GameTime)";
    internal const string DrawName = "System.Void Terraria.GameContent.Drawing.TileDrawing::Draw(System.Boolean,System.Boolean,System.Int32)";
    internal const string SingleName = "System.Void Terraria.GameContent.Drawing.TileDrawing::DrawSingleTile(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2,System.Int32,System.Int32)";
    internal const string RunGameName = "System.Void Terraria.Program::RunGame()";
    internal const string BatchEndName = "System.Int32 Terraria.Graphics.TileBatch::End()";
    internal const string RenderBatchName = "System.Void Terraria.Graphics.TileBatch::RenderBatch(Microsoft.Xna.Framework.Graphics.Texture2D,Terraria.Graphics.TileBatch/SpriteData[],System.Int32,System.Int32)";
    internal const string FlushLayeredName = "System.Void Terraria.Graphics.TileBatch::FlushLayered()";
    internal static readonly string[] ChangedNames = { MainDrawName, DrawName, SingleName, RunGameName, BatchEndName, RenderBatchName, FlushLayeredName };
    internal static void Require(bool condition, string message) => Common.Require(condition, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);
    internal static MethodDefinition Method(ModuleDefinition module, string name) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == name);
    internal static MethodDefinition Draw(ModuleDefinition module) => Method(module, DrawName);
    internal static MethodDefinition Single(ModuleDefinition module) => Method(module, SingleName);

    internal static string SourceHash()
    {
        string path = Environment.GetEnvironmentVariable("RENDER53_SOURCE_MANIFEST") ?? throw new InvalidDataException("RENDER53_SOURCE_MANIFEST required");
        byte[] bytes = File.ReadAllBytes(path);
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)!;
        string[] required = { "Contracts.cs", "TimingTemplate.cs", "StateTemplate.cs", "BatchTemplate.cs", "RenderPatcher.cs", "RenderAudit.cs", "RenderProof.cs", "RenderProbe.cs", "RenderRuntimeProof.cs", "RenderFixture.cs", "Program.cs", "PatchRenderProfile.csproj", "render53-schema.json", "run.py" };
        Require(required.All(name => sources.Keys.Any(key => key.EndsWith("/patch_render_profile/" + name, StringComparison.Ordinal))) && sources.Keys.Any(key => key.EndsWith("/scripts/analyze_render_profile.py", StringComparison.Ordinal)), "incomplete render53 source manifest");
        foreach (var (file, hash) in sources) Require(Sha(File.ReadAllBytes(file)) == hash, "source changed since build: " + file);
        return Sha(bytes);
    }

    static void FreshOutput(string output)
    {
        Require(!Path.Exists(output) && new FileInfo(output).LinkTarget == null && new DirectoryInfo(output).LinkTarget == null, "fresh non-alias output required");
        for (var parent = new DirectoryInfo(output).Parent; parent != null; parent = parent.Parent) Require(parent.LinkTarget == null, "symlink output ancestor forbidden");
    }

    static void Inputs(string input, string fna)
    {
        string directory = Path.GetDirectoryName(input)!;
        Require(Sha(File.ReadAllBytes(input)) == InputHash, "unsupported/already-profiled input; accepted52 required");
        Require(Sha(File.ReadAllBytes(fna)) == FnaHash && Sha(File.ReadAllBytes(Path.Combine(directory, "FNA.dll"))) == FnaHash, "supplied/resolved FNA differs");
        Require(Sha(File.ReadAllBytes(Path.Combine(directory, "ReLogic.dll"))) == ReLogicHash, "accepted52 ReLogic required");
        Require(Sha(File.ReadAllBytes("/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll")) == CoreHash, "target CoreLib changed");
    }

    static object VerifyReports(string directory, string candidateSha)
    {
        string analyzer = Environment.GetEnvironmentVariable("RENDER53_ANALYZER") ?? throw new InvalidDataException("RENDER53_ANALYZER required");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "report-manifest.json")));
        Require(manifest.RootElement.GetProperty("candidateSha256").GetString() == candidateSha, "report proof belongs to another candidate");
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var receipts = new List<object>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        int accepted = 0, rejected = 0, index = 0;
        foreach (string kind in new[] { "accepted", "rejected" })
        foreach (var item in manifest.RootElement.GetProperty(kind).EnumerateArray())
        {
            string relative = item.GetProperty("file").GetString()!;
            string input = Path.GetFullPath(Path.Combine(directory, relative));
            Require(input.StartsWith(root, StringComparison.Ordinal) && seen.Add(input) && File.Exists(input), "report fixture missing/aliased/duplicated");
            string parsed = Path.Combine(directory, "parser-" + index + ".json");
            var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in new[] { analyzer, input, "--output", parsed }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start analyzer");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("report analyzer timed out"); }
            string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(directory, "parser-" + index + ".log"), log);
            if (kind == "accepted")
            {
                Require(process.ExitCode == 0, "actual runtime report rejected: " + relative + "\n" + log);
                using var result = JsonDocument.Parse(File.ReadAllBytes(parsed));
                Require(result.RootElement.GetProperty("measurement_invalid").GetBoolean() == item.GetProperty("measurement_invalid").GetBoolean(), "report invalidity differs");
                Require(result.RootElement.GetProperty("final_seen").GetBoolean() == item.GetProperty("final_seen").GetBoolean(), "report final status differs");
                accepted++;
            }
            else { Require(process.ExitCode != 0 && !File.Exists(parsed), "malformed report accepted: " + relative); rejected++; }
            receipts.Add(new { file = relative, expected = kind, exitCode = process.ExitCode, inputSha256 = Sha(File.ReadAllBytes(input)), log = "parser-" + index + ".log" });
            index++;
        }
        Require(accepted > 0 && rejected > 0, "positive and negative actual-report proof required");
        var proof = new { passed = true, candidateSha256 = candidateSha, analyzerSha256 = Sha(File.ReadAllBytes(analyzer)), accepted, rejected, receipts };
        Json(Path.Combine(directory, "parser-proof.json"), proof); return proof;
    }

    static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("use pinned Linux monobuild");
            bool supplied = args.Length == 5 && args[0] == "prove";
            Require(supplied || args.Length == 4 && args[0] is "inspect" or "emit" or "accept", "Usage: PatchRenderProfile <inspect|emit|accept> <build52> <FNA> <fresh-output>, or prove <build52> <exact-candidate> <FNA> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[supplied ? 3 : 2]), output = Path.GetFullPath(args[supplied ? 4 : 3]);
            FreshOutput(output); Inputs(input, fna);
            byte[] originalBytes = File.ReadAllBytes(input);
            using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes));
            Require(original.MainModule.Mvid == Guid.Parse("df61a8d1-0622-9555-82c2-a2118b6c9bc4"), "baseline52 MVID differs");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { accepted = false, inputSha256 = InputHash, roots = ChangedNames.Select(name => new { name, fingerprint = Fingerprint(Method(original.MainModule, name)), il = Body(Method(original.MainModule, name)) }) });
                return 0;
            }
            string sourceHash = SourceHash();
            using var resolver = RenderPatcher.Resolver(input);
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
            var receipts = RenderPatcher.Inject(game.MainModule);
            Require(receipts.Length == ChangedNames.Length && receipts.Select(r => r.Method).OrderBy(n => n).SequenceEqual(ChangedNames.OrderBy(n => n)), "unexpected original method changes");
            game.MainModule.Mvid = Guid.Empty;
            using (var identity = new MemoryStream()) { game.Write(identity, new WriterParameters { Timestamp = 0 }); game.MainModule.Mvid = new Guid(SHA256.HashData(identity.ToArray()).AsSpan(0, 16)); }
            byte[] bytes;
            using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); bytes = stream.ToArray(); }
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
            if (supplied) Require(Sha(File.ReadAllBytes(Path.GetFullPath(args[2]))) == Sha(bytes), "supplied candidate differs from current-source emission");
            var audit = RenderAudit.Verify(originalBytes, bytes, receipts, input);
            Json(Path.Combine(output, "receipts.json"), receipts); Json(Path.Combine(output, "preservation.json"), audit);
            string pending = Path.Combine(output, "candidate-proof-only.exe");
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            if (args[0] == "emit")
            {
                Json(Path.Combine(output, "inspection-only.json"), new { accepted = false, hardwarePending = true, measurementOnly = true, sourceHash, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid, audit });
                Console.WriteLine("INSPECTION ONLY " + pending); return 0;
            }
            var staticNegatives = RenderAudit.NegativeProof(originalBytes, bytes, receipts, input, Path.Combine(output, "static-negative"));
            string proofDirectory = Path.Combine(output, "proof");
            var behavior = RenderProof.Run(input, pending, fna, proofDirectory);
            var parser = VerifyReports(proofDirectory, Sha(bytes));
            Inputs(input, fna); Require(SourceHash() == sourceHash && Sha(File.ReadAllBytes(pending)) == Sha(bytes), "source/candidate changed during proof");
            string destination = Path.Combine(output, "Terraria.exe");
            using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Json(Path.Combine(output, "acceptance.json"), new { accepted = true, hardwarePending = true, measurementOnly = true, performanceAdopted = false, activePerformanceBaseline = 52, sourceHash, inputSha256 = InputHash, fnaSha256 = FnaHash, reLogicSha256 = ReLogicHash, targetCorelibSha256 = CoreHash, inputMvid = original.MainModule.Mvid, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid, changedMethods = ChangedNames, addedTypes = Types(changed.MainModule).Where(RenderPatcher.IsAdded).Select(t => t.FullName), audit, staticNegatives, behavior, parser });
            Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
