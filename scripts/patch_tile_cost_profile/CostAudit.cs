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

internal static class CostAudit
{
    static bool Observed(MethodDefinition method) => CostPatcher.IsAdded(method.DeclaringType) || CostPatcher.IsAccessor(method) || method.FullName is Program.DrawName or Program.SingleName;
    internal static object Preservation(AssemblyDefinition original, AssemblyDefinition changed, CostPatcher.Plan[] plans)
    {
        var a = original.MainModule; var b = changed.MainModule;
        Require(a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName && original.Name.FullName == changed.Name.FullName, "assembly metadata changed");
        Require(a.AssemblyReferences.Select(r => r.FullName).SequenceEqual(b.AssemblyReferences.Select(r => r.FullName)) && a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "assembly/module references changed");
        string Meta(string method, object value) => (string)Invoke("Preservation", method, value)!;
        foreach (string name in new[] { "Attributes", "Security" }) Require(Meta(name, original) == Meta(name, changed), "assembly attributes/security changed");
        Require(Meta("Attributes", a) == Meta("Attributes", b), "module attributes changed");
        var oldTypes = Types(a).ToArray(); var newTypes = Types(b).Where(t => !CostPatcher.IsAdded(t)).ToArray();
        Require(oldTypes.Select(t => t.FullName).SequenceEqual(newTypes.Select(t => t.FullName)), "original type set/order changed");
        int bodies = 0, fields = 0;
        foreach (var before in oldTypes)
        {
            var after = newTypes.Single(t => t.FullName == before.FullName);
            Require(Meta("TypeMetadata", before) == Meta("TypeMetadata", after), "type metadata changed " + before.FullName);
            Require(before.Fields.Count == after.Fields.Count && before.Methods.Count == after.Methods.Count(m => !CostPatcher.IsAccessor(m)), "original member count changed");
            foreach (var field in before.Fields) { Require(Meta("FieldMetadata", field) == Meta("FieldMetadata", after.Fields.Single(f => f.Name == field.Name)), "original field changed"); fields++; }
            foreach (var method in before.Methods)
            {
                var target = after.Methods.Single(m => m.FullName == method.FullName);
                Require(Meta("MethodMetadata", method) == Meta("MethodMetadata", target), "method metadata changed " + method.FullName);
                if (!plans.Any(p => p.Method == method.FullName)) { Require(Body(method) == Body(target), "unrelated method body changed " + method.FullName); bodies++; }
            }
        }
        Require(a.Resources.Select(r => Meta("ResourceMetadata", r)).SequenceEqual(b.Resources.Select(r => Meta("ResourceMetadata", r))), "managed resources changed");
        var restoration = new List<object>();
        foreach (var plan in plans)
        {
            var before = Types(a).SelectMany(t => t.Methods).Single(m => m.FullName == plan.Method);
            var after = Types(b).SelectMany(t => t.Methods).Single(m => m.FullName == plan.Method);
            MethodDefinition Shell(MethodDefinition source)
            {
                var shell = new MethodDefinition(source.Name, source.Attributes, source.ReturnType) { ImplAttributes = source.ImplAttributes };
                foreach (var parameter in source.Parameters) shell.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
                Copy(source, shell, t => t, o => o); return shell;
            }
            var reconstructed = Shell(after); var all = reconstructed.Body.Instructions.ToArray();
            var keep = plan.OriginalIndices.Select(i => all[i]).ToHashSet();
            foreach (var instruction in keep)
            {
                if (instruction.Operand is Instruction target && plan.Entries.TryGetValue(Array.IndexOf(all, target), out int destination)) instruction.Operand = all[destination];
                else if (instruction.Operand is Instruction[] targets) for (int i = 0; i < targets.Length; i++) if (plan.Entries.TryGetValue(Array.IndexOf(all, targets[i]), out int at)) targets[i] = all[at];
            }
            foreach (int at in plan.ReturnsChanged) { Require(all[at].OpCode == OpCodes.Leave, "unexpected return replacement"); all[at].OpCode = OpCodes.Ret; all[at].Operand = null; }
            foreach (var instruction in all) if (!keep.Contains(instruction)) reconstructed.Body.Instructions.Remove(instruction);
            Require(reconstructed.Body.ExceptionHandlers.Count == plan.OriginalHandlers + (plan.Method == Program.DrawName ? 1 : 0), "unexpected observer handler count");
            while (reconstructed.Body.ExceptionHandlers.Count > plan.OriginalHandlers) reconstructed.Body.ExceptionHandlers.RemoveAt(reconstructed.Body.ExceptionHandlers.Count - 1);
            while (reconstructed.Body.Variables.Count > plan.OriginalLocals) reconstructed.Body.Variables.RemoveAt(reconstructed.Body.Variables.Count - 1);
            var normalized = Shell(before); Widen(normalized);
            Require(Body(reconstructed) == Body(normalized), "observer removal does not reconstruct " + plan.Method);
            restoration.Add(new { method = plan.Method, originalInstructions = before.Body.Instructions.Count, originalLocals = plan.OriginalLocals, originalHandlers = plan.OriginalHandlers, hookRemovalExact = true });
        }
        var helper = Types(b).Single(t => t.Name == CostPatcher.HelperName);
        Require(!helper.IsBeforeFieldInit, "helper initialized before first use");
        var current = helper.Fields.Single(f => f.Name == "current");
        Require(current.IsStatic && current.CustomAttributes.Count == 1 && current.CustomAttributes[0].AttributeType.FullName == "System.ThreadStaticAttribute", "ThreadStatic context attribute lost");
        var accessors = Types(b).SelectMany(t => t.Methods).Where(CostPatcher.IsAccessor).ToArray();
        Require(accessors.Length == 2 && accessors.All(m => m.IsAssembly && !m.IsStatic && m.ReturnType.MetadataType == MetadataType.Int32 && m.Parameters.Count == 0), "unexpected read-only accessor contract");
        Require(accessors.All(m => m.Body.Instructions.All(i => i.OpCode.Code is Code.Ldarg_0 or Code.Ldfld or Code.Ldsfld or Code.Ldelem_Ref or Code.Ldelem_I4 or Code.Callvirt or Code.Ret)), "accessor is not read-only");
        return new { unchangedBodies = bodies, unchangedFields = fields, unchangedTypes = oldTypes.Length, managedResources = a.Resources.Count, restoration, threadStaticPreserved = true, originalSignaturesAndFlagsPreserved = true, observationOnlyAccessors = accessors.Select(m => m.FullName), preservedNoOptimizationMethods = oldTypes.SelectMany(t => t.Methods).Where(m => (m.ImplAttributes & Mono.Cecil.MethodImplAttributes.NoOptimization) != 0).Select(m => m.FullName) };
    }

    internal static object Signatures(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
        var decoder = (ISignatureTypeProvider<string, object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder", true)!, true)!;
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var methods = Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && Observed(m)).ToArray();
        string Sig(MethodSignature<string> m) => m.ReturnType + "(" + string.Join(',', m.ParameterTypes) + ")";
        var rows = new List<object>();
        foreach (var method in methods)
        {
            var definition = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            rows.Add(new { kind = "MethodDef", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(definition.Signature)), decoded = Sig(definition.DecodeSignature(decoder, (object?)null)) });
            var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil) { var locals = reader.GetStandaloneSignature(body.LocalSignature); rows.Add(new { kind = "Locals", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(locals.Signature)), decoded = string.Join(',', locals.DecodeLocalSignature(decoder, (object?)null)) }); }
        }
        foreach (var field in Types(game.MainModule).Where(CostPatcher.IsAdded).SelectMany(t => t.Fields))
        {
            var definition = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)field.MetadataToken.RID));
            rows.Add(new { kind = "FieldDef", name = field.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(definition.Signature)), decoded = definition.DecodeSignature(decoder, (object?)null) });
        }
        foreach (var member in methods.SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r => r.MetadataToken.TokenType == TokenType.MemberRef).DistinctBy(r => r.MetadataToken.ToInt32()))
        {
            var definition = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)member.MetadataToken.RID));
            rows.Add(new { kind = "MemberRef", name = member.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(definition.Signature)), decoded = definition.GetKind() == MemberReferenceKind.Method ? Sig(definition.DecodeMethodSignature(decoder, (object?)null)) : definition.DecodeFieldSignature(decoder, (object?)null) });
        }
        return new { passed = true, independentlyDecodedRawMetadata = true, primitiveAliasesAllowed = false, prior43 = Invoke("RawSignatures", "Verify", bytes), rows };
    }
    internal static object Sdk(byte[] bytes, AssemblyDefinition original, string input)
    {
        var prior = Invoke("ReferenceAudit", "Verify", bytes, original, input)!;
        var resolverType = Support.GetType("ReferenceAudit+PinnedResolver", true)!;
        using var resolver = (IAssemblyResolver)Activator.CreateInstance(resolverType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Path.GetDirectoryName(input)! }, null)!;
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        var references = Types(game.MainModule).Where(CostPatcher.IsAdded).SelectMany(t => t.Methods).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r => r.DeclaringType?.Namespace.StartsWith("System") == true);
        references = references.Concat(Types(game.MainModule).Where(CostPatcher.IsAdded).SelectMany(t => t.Fields).SelectMany(f => f.CustomAttributes).Select(a => a.Constructor));
        var rows = new List<object>();
        foreach (var reference in references.DistinctBy(r => r.FullName))
        {
            IMemberDefinition member = reference switch { MethodReference m => m.Resolve(), FieldReference f => f.Resolve(), _ => throw new InvalidDataException("unsupported SDK member") };
            Require(member is MethodDefinition { IsPublic: true } or FieldDefinition { IsPublic: true }, "missing/nonpublic SDK reference " + reference.FullName);
            rows.Add(new { reference = reference.FullName, resolved = member.FullName, image = member.DeclaringType.Module.FileName, sha256 = Sha(File.ReadAllBytes(member.DeclaringType.Module.FileName)) });
        }
        return new { prior43 = prior, checkedMembers = rows, hostFallbackAllowed = false };
    }
}
