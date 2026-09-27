using Mono.Cecil;
using Mono.Cecil.Cil;
using static Contracts;

internal static class AuditNegative
{
    internal static object Run(byte[] original, byte[] candidate, string inputPath, string fnaPath, string output)
    {
        Directory.CreateDirectory(output); var cases = new List<object>();
        void Reject(string name, string reason, Action action)
        {
            try { action(); }
            catch (InvalidDataException error) when (error.Message.Contains(reason, StringComparison.Ordinal))
            {
                cases.Add(new { name, passed = true, rejection = error.Message }); return;
            }
            throw new InvalidDataException("static guard did not reject " + name + " for " + reason);
        }
        byte[] Mutate(Action<ModuleDefinition> change)
        {
            using var resolver = Resolver(inputPath, fnaPath);
            using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(candidate), new ReaderParameters { AssemblyResolver = resolver });
            change(assembly.MainModule);
            using var stream = new MemoryStream(); assembly.Write(stream, new WriterParameters { Timestamp = 0 }); return stream.ToArray();
        }
        void CandidateCase(string name, string reason, Action<ModuleDefinition> change) => Reject(name, reason, () => StackStateAudit.Verify(original, Mutate(change), candidate, inputPath, fnaPath, Path.Combine(output, name)));
        CandidateCase("missing-initlocals", "candidate locals-init differs", module => ((MethodDefinition)module.LookupToken(EntryToken)).Body.InitLocals = false);
        CandidateCase("reused-or-wrong-array", "fresh array initialization prefix differs", module => ((MethodDefinition)module.LookupToken(EntryToken)).Body.Instructions[1].Operand = (sbyte)8);
        CandidateCase("public-state", "nonpublic value type layout differs", module => Types(module).Single(t => t.FullName == Added).IsPublic = true);
        CandidateCase("field-layout", "state field layout differs", module => Types(module).Single(t => t.FullName == Added).Fields[0].Name += "Changed");
        CandidateCase("helper-by-value", "helper is not direct state byref", module =>
        {
            var method = (MethodDefinition)module.LookupToken((int)HelperTokens[0]);
            var parameter = method.Parameters.Single(p => p.ParameterType is ByReferenceType br && br.ElementType.FullName == Added);
            parameter.ParameterType = ((ByReferenceType)parameter.ParameterType).ElementType;
        });
        CandidateCase("unrelated-method-change", "unexpected method body changes", module =>
        {
            var ctor = Types(module).Single(t => t.FullName == Original).Methods.Single();
            ctor.Body.GetILProcessor().InsertBefore(ctor.Body.Instructions[0], Instruction.Create(OpCodes.Nop));
        });
        CandidateCase("owner-load-not-address", "owner load transform differs", module =>
        {
            var entry = (MethodDefinition)module.LookupToken(EntryToken);
            var load = entry.Body.Instructions.Skip(4).First(i => i.OpCode == OpCodes.Ldloca && i.Operand == entry.Body.Variables[0]);
            load.OpCode = OpCodes.Ldloc_0; load.Operand = null;
        });
        using (var image = AssemblyDefinition.ReadAssembly(new MemoryStream(candidate)))
        {
            var state = Types(image.MainModule).Single(t => t.FullName == Added);
            byte[] wrongLocalKind = RenderRawSignatures.FlipKindForProof(candidate, EntryToken, state.MetadataToken.ToInt32(), true);
            Reject("raw-state-local-class-kind", "invalid raw named type kind", () => StackStateAudit.Verify(original, wrongLocalKind, candidate, inputPath, fnaPath, Path.Combine(output, "raw-state-local-class-kind")));
            var field = state.Fields.Single(f => f.Name == "colorSlices");
            byte[] wrongArrayKind = RenderRawSignatures.FlipKindForProof(candidate, field.MetadataToken.ToInt32(), ((ArrayType)field.FieldType).ElementType.MetadataToken.ToInt32(), false);
            Reject("raw-array-element-class-kind", "invalid raw named type kind", () => StackStateAudit.Verify(original, wrongArrayKind, candidate, inputPath, fnaPath, Path.Combine(output, "raw-array-element-class-kind")));
        }
        Reject("unrecognized-image-bytes", "image differs from frozen candidate03", () => StackStateAudit.Verify(original, candidate.Concat(new byte[] { 0 }).ToArray(), candidate, inputPath, fnaPath, Path.Combine(output, "unrecognized-image-bytes")));
        void ClosureCase(string name, string reason, Action<ModuleDefinition> change)
        {
            Reject(name, reason, () =>
            {
                using var resolver = Resolver(inputPath, fnaPath);
                using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(original), new ReaderParameters { AssemblyResolver = resolver });
                change(assembly.MainModule); AuditClosure.Verify(assembly, Path.Combine(output, name));
            });
        }
        ClosureCase("indirect-helper-call", "private helper has indirect/outside caller", module =>
        {
            var entry = (MethodDefinition)module.LookupToken(EntryToken);
            entry.Body.Instructions.First(i => i.Operand is MethodReference m && HelperTokens.Contains(m.MetadataToken.ToUInt32())).OpCode = OpCodes.Callvirt;
        });
        ClosureCase("object-identity-consumption", "unsupported tracked consumption", module =>
        {
            var entry = (MethodDefinition)module.LookupToken(EntryToken); var next = entry.Body.Instructions[2]; var il = entry.Body.GetILProcessor();
            il.InsertBefore(next, Instruction.Create(OpCodes.Ldloc_0)); il.InsertBefore(next, Instruction.Create(OpCodes.Pop));
        });
        ClosureCase("object-erased-local", "object-erased scratch store", module =>
        {
            var entry = (MethodDefinition)module.LookupToken(EntryToken); var next = entry.Body.Instructions[2]; var il = entry.Body.GetILProcessor();
            var local = new VariableDefinition(module.TypeSystem.Object); entry.Body.Variables.Add(local);
            il.InsertBefore(next, Instruction.Create(OpCodes.Ldloc_0)); il.InsertBefore(next, Instruction.Create(OpCodes.Stloc, local));
        });
        ClosureCase("field-address-escape", "unsupported tracked consumption", module =>
        {
            var entry = (MethodDefinition)module.LookupToken(EntryToken); var next = entry.Body.Instructions[2]; var il = entry.Body.GetILProcessor();
            il.InsertBefore(next, Instruction.Create(OpCodes.Ldloc_0));
            il.InsertBefore(next, Instruction.Create(OpCodes.Ldflda, Types(module).Single(t => t.FullName == Original).Fields[0]));
            il.InsertBefore(next, Instruction.Create(OpCodes.Conv_U)); il.InsertBefore(next, Instruction.Create(OpCodes.Pop));
        });
        var result = new { passed = true, cases };
        Json(Path.Combine(output, "negative-results.json"), result); return result;
    }
}
