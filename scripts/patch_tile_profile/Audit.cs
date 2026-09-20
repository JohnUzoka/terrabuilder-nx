using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using FieldDefinition = Mono.Cecil.FieldDefinition;
using ParameterDefinition = Mono.Cecil.ParameterDefinition;
using TypeReference = Mono.Cecil.TypeReference;

internal static class Audit
{
    internal static object Preservation(AssemblyDefinition original, AssemblyDefinition changed, TilePatcher.Splice plan)
    {
        var a = original.MainModule; var b = changed.MainModule;
        Require(a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName && original.Name.FullName == changed.Name.FullName, "assembly metadata changed");
        Require(a.AssemblyReferences.Select(r => r.FullName).SequenceEqual(b.AssemblyReferences.Select(r => r.FullName)) && a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "references changed");
        string Meta(string method, object o) => (string)Invoke("Preservation", method, o)!;
        foreach (string name in new[] { "Attributes", "Security" }) Require(Meta(name, original) == Meta(name, changed), "assembly attributes/security changed");
        Require(Meta("Attributes", a) == Meta("Attributes", b), "module attributes changed");
        var oldTypes = Types(a).ToArray(); var newTypes = Types(b).Where(t => !TilePatcher.IsAdded(t)).ToArray();
        Require(oldTypes.Select(t => t.FullName).SequenceEqual(newTypes.Select(t => t.FullName)), "original types changed");
        int bodies = 0, fields = 0;
        foreach (var before in oldTypes) {
            var after = newTypes.Single(t => t.FullName == before.FullName);
            Require(Meta("TypeMetadata", before) == Meta("TypeMetadata", after), "type changed " + before.FullName);
            Require(before.Fields.Count == after.Fields.Count && before.Methods.Count == after.Methods.Count, "original member counts changed");
            foreach (var f in before.Fields) { Require(Meta("FieldMetadata", f) == Meta("FieldMetadata", after.Fields.Single(x => x.Name == f.Name)), "field changed"); fields++; }
            foreach (var m in before.Methods) {
                var n = after.Methods.Single(x => x.FullName == m.FullName);
                Require(Meta("MethodMetadata", m) == Meta("MethodMetadata", n), "method flags/metadata changed " + m.FullName);
                if (m.FullName != Program.DrawName) { Require(Body(m) == Body(n), "unrelated method changed " + m.FullName); bodies++; }
            }
        }
        Require(a.Resources.Select(r => Meta("ResourceMetadata", r)).SequenceEqual(b.Resources.Select(r => Meta("ResourceMetadata", r))), "managed resources changed");
        var baseline = Program.Draw(a); var injected = Program.Draw(b);
        var scratch = new MethodDefinition("Draw", injected.Attributes, injected.ReturnType);
        foreach (var p in injected.Parameters) scratch.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        Copy(injected, scratch, t => t, o => o);
        var all = scratch.Body.Instructions.ToArray(); var keep = plan.OriginalIndices.Select(i => all[i]).ToHashSet();
        foreach (var i in keep) {
            if (i.Operand is Instruction target && plan.Entries.TryGetValue(Array.IndexOf(all, target), out int dest)) i.Operand = all[dest];
            if (i.Operand is Instruction[] targets) for (int j = 0; j < targets.Length; j++) if (plan.Entries.TryGetValue(Array.IndexOf(all, targets[j]), out int d)) targets[j] = all[d];
        }
        var oldRet = all[plan.OriginalIndices[^1]]; Require(oldRet.OpCode == OpCodes.Leave, "return changed unexpectedly"); oldRet.OpCode = OpCodes.Ret; oldRet.Operand = null;
        foreach (var i in all) if (!keep.Contains(i)) scratch.Body.Instructions.Remove(i);
        Require(scratch.Body.ExceptionHandlers.Count == 1 && scratch.Body.ExceptionHandlers[0].HandlerType == ExceptionHandlerType.Finally, "unexpected observer EH");
        scratch.Body.ExceptionHandlers.Clear();
        while (scratch.Body.Variables.Count > plan.OriginalLocals) scratch.Body.Variables.RemoveAt(scratch.Body.Variables.Count - 1);
        var normalized = new MethodDefinition("Draw", baseline.Attributes, baseline.ReturnType);
        foreach (var p in baseline.Parameters) normalized.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        Copy(baseline, normalized, t => t, o => o); Widen(normalized);
        Require(Body(scratch) == Body(normalized), "hook removal failed to reconstruct original instructions/locals/targets/EH");
        var helper = Types(b).Single(t => t.Name == TilePatcher.HelperName);
        Require(!helper.IsBeforeFieldInit, "helper cctor could run early");
        foreach (var m in Types(b).Where(TilePatcher.IsAdded).SelectMany(t => t.Methods)) foreach (var r in m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()) {
            if (r.DeclaringType.FullName == "Terraria.TimeLogger" && r.Name.StartsWith("New")) Require(m.DeclaringType.DeclaringType?.FullName == "Terraria.TimeLogger", "private factory access not nested");
        }
        return new { unchangedBodies = bodies, unchangedFields = fields, unchangedTypes = oldTypes.Length, managedResources = a.Resources.Count, originalInstructionsRestored = baseline.Body.Instructions.Count, originalLocalsRestored = plan.OriginalLocals, originalEhRestored = baseline.Body.ExceptionHandlers.Count, hookRemovalExact = true, helperBeforeFieldInit = false, preservedNoOptimizationMethods = oldTypes.SelectMany(t => t.Methods).Where(m => (m.ImplAttributes & Mono.Cecil.MethodImplAttributes.NoOptimization) != 0).Select(m => m.FullName) };
    }
    internal static object MalformedSignatureRejection(string output)
    {
        using var bad = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("RejectedTileSignature", new Version(1, 0)), "RejectedTileSignature", ModuleKind.Dll);
        var m = bad.MainModule; m.Mvid = Guid.Parse("f36ea07b-e3c1-41f2-91e8-72d5c45704c1");
        var type = new Mono.Cecil.TypeDefinition("", TilePatcher.HelperName, Mono.Cecil.TypeAttributes.Public, m.TypeSystem.Object); m.Types.Add(type);
        var method = new MethodDefinition("Bad", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, new TypeReference("System", "Void", m, m.TypeSystem.CoreLibrary));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); type.Methods.Add(method);
        using var stream = new MemoryStream(); bad.Write(stream, new WriterParameters { Timestamp = 0 }); var bytes = stream.ToArray();
        File.WriteAllBytes(Path.Combine(output, "RejectedTileSignature.dll"), bytes);
        try { Signatures(bytes); }
        catch (InvalidDataException e) when (e.Message.Contains("invalid raw primitive encoding: System.Void")) {
            return new { passed = true, rejectedSha256 = Sha(bytes), reason = e.Message, prior43 = Invoke("RawSignatures", "VerifyMalformedRejection", output) };
        }
        throw new InvalidDataException("malformed44 CLASS Void accepted");
    }

    internal static object Signatures(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
        var decoder = (ISignatureTypeProvider<string, object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder", true)!, true)!;
        using var game = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var methods = Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && (TilePatcher.IsAdded(m.DeclaringType) || m.FullName == Program.DrawName)).ToArray();
        string Sig(MethodSignature<string> m) => m.ReturnType + "(" + string.Join(',', m.ParameterTypes) + ")";
        var rows = new List<object>();
        foreach (var m in methods) {
            var d = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)m.MetadataToken.RID));
            rows.Add(new { kind = "MethodDef", name = m.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = Sig(d.DecodeSignature(decoder, (object?)null)) });
            var body = pe.GetMethodBody(d.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil) { var l = reader.GetStandaloneSignature(body.LocalSignature); rows.Add(new { kind = "Locals", name = m.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(l.Signature)), decoded = string.Join(',', l.DecodeLocalSignature(decoder, (object?)null)) }); }
        }
        foreach (var f in Types(game.MainModule).Where(TilePatcher.IsAdded).SelectMany(t => t.Fields)) {
            var d = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)f.MetadataToken.RID)); rows.Add(new { kind = "FieldDef", name = f.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = d.DecodeSignature(decoder, (object?)null) });
        }
        foreach (var r in methods.SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r => r.MetadataToken.TokenType == TokenType.MemberRef).DistinctBy(r => r.MetadataToken.ToInt32())) {
            var d = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)r.MetadataToken.RID)); rows.Add(new { kind = "MemberRef", name = r.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = d.GetKind() == MemberReferenceKind.Method ? Sig(d.DecodeMethodSignature(decoder, (object?)null)) : d.DecodeFieldSignature(decoder, (object?)null) });
        }
        return new { passed = true, independentlyDecodedRawMetadata = true, primitiveAliasesAllowed = false, prior43 = Invoke("RawSignatures", "Verify", bytes), rows };
    }
    internal static object Sdk(byte[] bytes, Mono.Cecil.AssemblyDefinition original, string input)
    {
        var prior = Invoke("ReferenceAudit", "Verify", bytes, original, input)!;
        var resolverType = Support.GetType("ReferenceAudit+PinnedResolver", true)!;
        using var resolver = (IAssemblyResolver)Activator.CreateInstance(resolverType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Path.GetDirectoryName(input)! }, null)!;
        using var game = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        var rows = new List<object>();
        foreach (var r in Types(game.MainModule).Where(TilePatcher.IsAdded).SelectMany(t => t.Methods).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r => r.DeclaringType?.Namespace.StartsWith("System") == true).DistinctBy(r => r.FullName)) {
            IMemberDefinition d = r switch { MethodReference m => m.Resolve(), FieldReference f => f.Resolve(), _ => throw new InvalidDataException("unsupported SDK reference") };
            Require(d is MethodDefinition { IsPublic: true } or FieldDefinition { IsPublic: true }, "missing/nonpublic SDK reference " + r.FullName);
            rows.Add(new { reference = r.FullName, resolved = d.FullName, image = d.DeclaringType.Module.FileName, sha256 = Sha(File.ReadAllBytes(d.DeclaringType.Module.FileName)) });
        }
        return new { prior43 = prior, checkedMembers = rows, hostFallbackAllowed = false };
    }
}
