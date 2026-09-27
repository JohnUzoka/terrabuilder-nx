using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class RenderNegativeProof
{
    internal static object Run(string originalPath, string candidatePath, string output)
    {
        var cases = new List<object>();
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(originalPath))!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(candidatePath))!);
        void Reject(string name, string slice, Action<ModuleDefinition> mutate)
        {
            using var original = AssemblyDefinition.ReadAssembly(originalPath, new ReaderParameters { AssemblyResolver = resolver });
            using var candidate = AssemblyDefinition.ReadAssembly(candidatePath, new ReaderParameters { AssemblyResolver = resolver });
            mutate(candidate.MainModule);
            string directory = Path.Combine(output, "semantic-negatives", name); Directory.CreateDirectory(directory);
            using var stream = new MemoryStream(); candidate.Write(stream, new WriterParameters { Timestamp = 0 });
            byte[] mutantBytes = stream.ToArray(); File.WriteAllBytes(Path.Combine(directory, "Terraria.exe"), mutantBytes);
            using var serialized = AssemblyDefinition.ReadAssembly(new MemoryStream(mutantBytes), new ReaderParameters { AssemblyResolver = resolver });
            var receipts = new List<object>(); byte[] probe = RenderProbe.Build(original, serialized, receipts);
            File.WriteAllBytes(Path.Combine(directory, "RenderExactProbe.dll"), probe);
            Json(Path.Combine(directory, "receipts.json"), receipts);
            var assembly = System.Reflection.Assembly.Load(probe); var p = new RenderProof.Session(assembly.GetType("Probe.Runtime", true)!, assembly.GetType("Probe.Hooks", true)!);
            string? rejection = null;
            try { if (slice == "tile") p.ExerciseMethods(); else RenderBatchProof.Run(p, directory); }
            catch (InvalidDataException e) when (e.Message.StartsWith("render53 proof:", StringComparison.Ordinal)) { rejection = e.Message; }
            Require(rejection != null, "semantic mutant survived behavioral assertions: " + name);
            cases.Add(new { name, slice, rejectedByBehavior = true, rejection, mutantSha256 = Sha(mutantBytes), probeSha256 = Sha(probe), checksBeforeRejection = p.Checks.Count,
                unchangedBaselineSha256 = Sha(File.ReadAllBytes(originalPath)), file = Path.GetRelativePath(output, Path.Combine(directory, "Terraria.exe")) });
        }
        TypeDefinition Helper(ModuleDefinition m) => Types(m).Single(t => t.FullName == "Terraria.NXRenderProfile53");
        MethodDefinition Wrapper(ModuleDefinition m, string name) => Helper(m).Methods.Single(method => method.Name == name);
        Reject("drop-selected-sample", "tile", m => {
            var method = Helper(m).Methods.Single(method => method.Name == "BeginSelected");
            method.Body = new MethodBody(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        Reject("end-layered-state-corruption", "batch", m => {
            var end = Program.Method(m, Program.BatchEndName);
            var store = end.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "_layeredSortingEnabled");
            Require(store.Previous.OpCode == OpCodes.Ldc_I4_0, "negative mutation expected original layered clear");
            store.Previous.OpCode = OpCodes.Ldc_I4_1;
        });
        Reject("upload-scope-misattribution", "batch", m => {
            var wrapper = Wrapper(m, "BatchUpload000");
            var begin = wrapper.Body.Instructions.Single(i => i.Operand is MethodReference call && call.Name == "BatchBegin");
            begin.Previous.OpCode = OpCodes.Ldc_I4_2; begin.Previous.Operand = null;
        });
        Reject("upload-offset-corruption", "batch", m => {
            var wrapper = Wrapper(m, "BatchUpload001");
            var argument = wrapper.Parameters[1]; Require(argument.ParameterType.MetadataType == MetadataType.Int32, "negative upload offset parameter");
            var load = wrapper.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldarg_1 || i.Operand == argument && (i.OpCode == OpCodes.Ldarg || i.OpCode == OpCodes.Ldarg_S));
            load.OpCode = OpCodes.Ldc_I4; load.Operand = 7;
        });
        Reject("submit-missing-scope-finalizer", "batch", m => {
            var wrapper = Wrapper(m, "BatchSubmit002");
            var end = wrapper.Body.Instructions.Single(i => i.Operand is MethodReference call && call.Name == "BatchEnd");
            wrapper.Body.GetILProcessor().InsertBefore(end, Instruction.Create(OpCodes.Pop)); end.OpCode = OpCodes.Pop; end.Operand = null;
        });
        var result = new { passed = true, cases, criterion = "Each full mutated candidate is serialized, cloned with unchanged original52 baseline, and fails an observable behavior/timing assertion. Audit rejection, load failures, and arbitrary exceptions are not accepted as semantic proof." };
        Json(Path.Combine(output, "semantic-negatives.json"), result); return result;
    }
}
