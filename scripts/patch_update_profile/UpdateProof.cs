using System.Reflection;
using System.Text.Json;
using static Common;

internal static class UpdateProof
{
    internal static object Run(string baselinePath, string candidatePath, string fnaPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        byte[] baseline = File.ReadAllBytes(baselinePath), candidate = File.ReadAllBytes(candidatePath);
        var receipts = new List<object>();
        byte[] bytes = UpdateProbe.Build(baselinePath, candidatePath, receipts);
        string path = Path.Combine(outputDirectory, "Update56ExactBehaviorProbe.dll"); File.WriteAllBytes(path, bytes);
        Json(Path.Combine(outputDirectory, "clone-bindings.json"), receipts);
        var assembly = Assembly.Load(bytes); var runtime = assembly.GetType("Probe.Runtime", true)!;
        var roots = UpdateBehaviorRoots.Run(assembly, runtime);
        Json(Path.Combine(outputDirectory, "behavior-roots.json"), roots);
        var world = UpdateBehaviorWorld.Run(assembly, runtime);
        Json(Path.Combine(outputDirectory, "behavior-world.json"), world);
        var entities = UpdateBehaviorEntities.Run(assembly, runtime);
        Json(Path.Combine(outputDirectory, "behavior-entities.json"), entities);
        var sections = new[] { roots, world, entities }.Select(x => JsonSerializer.SerializeToElement(x)).ToArray();
        var exercised = sections.SelectMany(s => s.GetProperty("scenarios").EnumerateArray()).Select(s => s.GetProperty("token").GetString()!).Distinct().Order().ToArray();
        using var original = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(baseline));
        var expected = UpdatePatcher.Targets.Select(t => Types(original.MainModule).SelectMany(x => x.Methods).Single(m => m.FullName == t.Name).MetadataToken.ToInt32().ToString("x8")).Order().ToArray();
        Require(expected.Length == 21 && exercised.SequenceEqual(expected), "all twenty-one actual original/candidate targets must execute");
        var result = new { passed = true, proofVersion = 56, baselineSha256 = Sha(baseline), candidateSha256 = Sha(candidate), fnaSha256 = Sha(File.ReadAllBytes(fnaPath)), probeSha256 = Sha(bytes), executable = Path.GetFileName(path), exercisedTargets = exercised, roots, world, entities,
            contract = "All21 exact baseline and actual serialized candidate bodies execute whole under source-qualified typed projection. Actual emitted helper executes; no surrogate target algorithm and no inverse audit is presented as executed behavior. Receipts bind source assembly/MVID/token/body, every projected member, branch/switch/local/ref parameter and exception region. Every executable body fingerprint survives serialization. Boundary ref/struct receiver writes survive original exceptions via projection finally. Baseline/candidate field/array graphs, return/ref arguments, callback order and original exceptions are compared with named branch oracles and destructive executed mutants.",
            limitations = new[] { "External callees are explicit deterministic fixtures, individually declared in section bounded lists, not real whole-game implementations. Each target's actual IL is retained; the corresponding downstream method may be bounded in its caller and separately executed as its own target.", "Observer scene snapshots are separate fixed RuntimeMain fixture state, not an assertion of scene capture equivalence during these bounded game-state fixtures; actual emitted state/timing behavior has the independent runtime proof.", "Only enumerated paths and bounded object/array sizes execute. Real game startup, rendering/input devices, unbounded world workloads, native/AOT execution and Switch hardware/performance equivalence are not claimed.", "Injected original exceptions are compared by object identity in branch oracles; generated CLR failures compare type/message. Stack traces and instruction addresses necessarily differ in projected host assemblies." } };
        Json(Path.Combine(outputDirectory, "behavior.json"), result); return result;
    }
}
