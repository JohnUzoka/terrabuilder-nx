using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

// Separate coverage replay only; the primary exact probe has no entry instrumentation.
public static class RenderCoverageFixture
{
    public static long[] Counts = Array.Empty<long>();
    public static void Hit(int id) => Interlocked.Increment(ref Counts[id]);
}

internal static class RenderCoverageProof
{
    internal static object Run(byte[] exactProbe, string output)
    {
        using var probe = AssemblyDefinition.ReadAssembly(new MemoryStream(exactProbe));
        var runtime = Types(probe.MainModule).Single(t => t.FullName == "Probe.Runtime");
        var methods = runtime.Methods.Where(m => m.HasBody).ToArray();
        var entry = probe.MainModule.ImportReference(typeof(RenderCoverageFixture).GetMethod(nameof(RenderCoverageFixture.Hit))!);
        var receipts = new List<object>();
        for (int id = 0; id < methods.Length; id++)
        {
            var method = methods[id]; string before = Fingerprint(method); var first = method.Body.Instructions[0]; var il = method.Body.GetILProcessor();
            il.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4, id)); il.InsertBefore(first, Instruction.Create(OpCodes.Call, entry));
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 1);
            receipts.Add(new { id, method = method.FullName, originalExactProbeBody = before, instrumentedBody = Fingerprint(method), entryMarker = "RenderCoverageFixture.Hit(id); original first instruction and every original branch/EH target retained" });
        }
        using var bytes = new MemoryStream(); probe.Write(bytes, new WriterParameters { Timestamp = 0 }); byte[] image = bytes.ToArray();
        string directory = Path.Combine(output, "coverage-replay"); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "RenderCoverageProbe.dll"), image);
        RenderCoverageFixture.Counts = new long[methods.Length];
        var assembly = Assembly.Load(image); var session = new RenderProof.Session(assembly.GetType("Probe.Runtime", true)!, assembly.GetType("Probe.Hooks", true)!);
        session.ExerciseMethods(); RenderBatchProof.Run(session, directory); RenderRuntimeProof.Run(session, directory);
        var coverage = methods.Select((method, id) => new { method = method.FullName, calls = RenderCoverageFixture.Counts[id] }).ToArray();
        Json(Path.Combine(directory, "coverage.json"), new { primaryProbeSha256 = Sha(exactProbe), coverageProbeSha256 = Sha(image), receipts, coverage });
        Require(coverage.All(row => row.calls > 0), "actual emitted helper coverage missing: " + string.Join(", ", coverage.Where(row => row.calls == 0).Select(row => row.method)));
        return new { passed = true, primaryProbeSha256 = Sha(exactProbe), coverageProbeSha256 = Sha(image), methods = coverage.Length, coverage,
            scope = "Ancillary replay adds an allocation-free atomic entry counter to each copied emitted runtime method. Primary behavior, allocation and report receipts come from the uninstrumented exact clone. Coverage proves entry, not exhaustive internal branches." };
    }
}
