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
using ModuleDefinition = Mono.Cecil.ModuleDefinition;

internal static class TileHelperAudit
{
    internal static object Preservation(AssemblyDefinition original, AssemblyDefinition changed, TileHelperReceipt[] receipts)
    {
        var a = original.MainModule; var b = changed.MainModule;
        Require(a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName && original.Name.FullName == changed.Name.FullName, "assembly metadata changed");
        Require(a.AssemblyReferences.Select(r => r.FullName).SequenceEqual(b.AssemblyReferences.Select(r => r.FullName)) && a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "assembly/module references changed");
        Require(a.Mvid != b.Mvid && b.Mvid != Guid.Empty, "candidate MVID must be new");
        string Meta(string name, object value) => (string)Invoke("Preservation", name, value)!;
        foreach (string name in new[] { "Attributes", "Security" }) Require(Meta(name, original) == Meta(name, changed), "assembly attributes/security changed");
        Require(Meta("Attributes", a) == Meta("Attributes", b), "module attributes changed");
        var oldTypes = Types(a).ToArray(); var newTypes = Types(b).Where(t => !TileHelperPatcher.IsAdded(t)).ToArray();
        Require(oldTypes.Select(t => t.FullName).SequenceEqual(newTypes.Select(t => t.FullName)), "original type set/order changed");
        Require(receipts.Select(r => r.Method).Order().SequenceEqual(Program.ChangedNames.Order()) && receipts.Length == 4, "exactly four changed originals required");
        int bodies = 0, fields = 0;
        foreach (var before in oldTypes)
        {
            var after = newTypes.Single(t => t.FullName == before.FullName);
            Require(Meta("TypeMetadata", before) == Meta("TypeMetadata", after), "type metadata changed " + before.FullName);
            Require(before.Fields.Select(f => f.FullName).SequenceEqual(after.Fields.Select(f => f.FullName)) && before.Methods.Select(m => m.FullName).SequenceEqual(after.Methods.Select(m => m.FullName)), "original member set/order changed");
            foreach (var field in before.Fields) { Require(Meta("FieldMetadata", field) == Meta("FieldMetadata", after.Fields.Single(f => f.Name == field.Name)), "original field changed " + field.FullName); fields++; }
            foreach (var method in before.Methods)
            {
                var target = after.Methods.Single(m => m.FullName == method.FullName);
                Require(Meta("MethodMetadata", method) == Meta("MethodMetadata", target), "method metadata changed " + method.FullName);
                if (!Program.ChangedNames.Contains(method.FullName)) { Require(Body(method) == Body(target), "unrelated method body changed " + method.FullName); bodies++; }
            }
        }
        Require(a.Resources.Select(r => Meta("ResourceMetadata", r)).SequenceEqual(b.Resources.Select(r => Meta("ResourceMetadata", r))), "managed resources changed");
        var restoration = new List<object>();
        foreach (var receipt in receipts)
        {
            var before = Program.Method(a, receipt.Method); var after = Program.Method(b, receipt.Method);
            Require(receipt.OriginalIndices.Length == before.Body.Instructions.Count && receipt.OriginalIndices.Distinct().Count() == receipt.OriginalIndices.Length && receipt.OriginalIndices.SequenceEqual(receipt.OriginalIndices.Order()), "original instruction receipt malformed");
            Require(after.Body.Variables.Count == receipt.OriginalLocals + receipt.AddedLocals && receipt.OriginalLocals == before.Body.Variables.Count, "local receipt differs");
            Require(receipt.AddedLocals == (receipt.Method == Program.SingleName ? 1 : receipt.Method == Program.RunGameName ? 0 : 2), "unexpected observer locals");
            Require(receipt.AddedHandlers == (receipt.Method is Program.DrawName or Program.MainDrawName ? 1 : 0) && after.Body.ExceptionHandlers.Count == receipt.OriginalHandlers + receipt.AddedHandlers && receipt.OriginalHandlers == before.Body.ExceptionHandlers.Count, "handler receipt differs");
            MethodDefinition Shell(MethodDefinition source)
            {
                var shell = new MethodDefinition(source.Name, source.Attributes, source.ReturnType) { ImplAttributes = source.ImplAttributes };
                foreach (var parameter in source.Parameters) shell.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
                Copy(source, shell, t => t, o => o); return shell;
            }
            var restored = Shell(after); var all = restored.Body.Instructions.ToArray(); var keep = receipt.OriginalIndices.Select(i => all[i]).ToHashSet();
            foreach (var instruction in keep)
            {
                if (instruction.Operand is Instruction target && receipt.Entries.TryGetValue(Array.IndexOf(all, target), out int destination)) instruction.Operand = all[destination];
                else if (instruction.Operand is Instruction[] targets) for (int n = 0; n < targets.Length; n++) if (receipt.Entries.TryGetValue(Array.IndexOf(all, targets[n]), out int at)) targets[n] = all[at];
            }
            Require(receipt.ReturnsChanged.Length == (receipt.Method == Program.DrawName ? 1 : 0), "unexpected changed returns");
            foreach (int at in receipt.ReturnsChanged) { Require(all[at].OpCode == OpCodes.Leave, "unexpected return replacement"); all[at].OpCode = OpCodes.Ret; all[at].Operand = null; }
            foreach (var instruction in all) if (!keep.Contains(instruction)) restored.Body.Instructions.Remove(instruction);
            while (restored.Body.ExceptionHandlers.Count > receipt.OriginalHandlers) restored.Body.ExceptionHandlers.RemoveAt(restored.Body.ExceptionHandlers.Count - 1);
            while (restored.Body.Variables.Count > receipt.OriginalLocals) restored.Body.Variables.RemoveAt(restored.Body.Variables.Count - 1);
            var normalized = Shell(before); Widen(normalized);
            Require(Body(restored) == Body(normalized), "reverse splice does not reconstruct original instructions/locals/handlers " + receipt.Method);
            restoration.Add(new { method = receipt.Method, originalInstructions = before.Body.Instructions.Count, originalLocals = receipt.OriginalLocals, originalHandlers = receipt.OriginalHandlers, hookRemovalExact = true });
        }
        var helper = Types(b).Single(t => t.FullName == "Terraria." + TileHelperPatcher.HelperName);
        Require(!helper.IsBeforeFieldInit && !helper.Methods.Any(m => m.IsConstructor), "runtime helper must initialize lazily");
        using var template = AssemblyDefinition.ReadAssembly(typeof(Tile49Template).Assembly.Location);
        var source = template.MainModule.Types.Single(t => t.Name == nameof(Tile49Template));
        var expectedThreadStatic = source.Fields.Where(f => f.CustomAttributes.Any(c => c.AttributeType.FullName == "System.ThreadStaticAttribute")).Select(f => f.Name).Order().ToArray();
        var actualThreadStatic = helper.Fields.Where(f => f.CustomAttributes.Any(c => c.AttributeType.FullName == "System.ThreadStaticAttribute")).Select(f => f.Name).Order().ToArray();
        Require(expectedThreadStatic.SequenceEqual(actualThreadStatic) && expectedThreadStatic.Contains("current") && expectedThreadStatic.Contains("Pending"), "ThreadStatic attributes lost");
        Require((string)helper.Fields.Single(f => f.Name == "SourceHash").Constant == Program.SourceHash(), "embedded source hash differs");
        return new { unchangedBodies = bodies, unchangedFields = fields, unchangedTypes = oldTypes.Length, managedResources = a.Resources.Count, restoration, threadStaticFields = actualThreadStatic, originalSignaturesAndFlagsPreserved = true, sourceHashVerified = true };
    }

    internal static object UnsampledGuard(ModuleDefinition module)
    {
        var method = Program.Single(module); var body = method.Body.Instructions;
        var selected = method.Body.Variables.Last();
        Require(selected.VariableType.MetadataType == MetadataType.Boolean && body[0].OpCode == OpCodes.Ldsfld && body[0].Operand is FieldReference { Name: "Pending", DeclaringType.FullName: "Terraria.NXTileProfile49" } && body[1].OpCode == OpCodes.Stloc && body[1].Operand == selected, "Single must load Pending once into its only new bool");
        Require(body.Count(i => i.OpCode == OpCodes.Stloc && i.Operand == selected) == 1, "selected flag can change during Single");
        var hooks = body.Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Terraria.NXTileProfile49").ToArray();
        Require(hooks.Length == 18, "unexpected Single observer hook count");
        foreach (var call in hooks)
        {
            string name = ((MethodReference)call.Operand).Name;
            Require(name is "ConsumePending" or "BeginOp" or "EndOp" or "EndSample", "unexpected Single timing helper " + name);
            var branch = name is "BeginOp" or "EndOp" ? call.Previous.Previous : call.Previous;
            Require(branch.OpCode == OpCodes.Brfalse && branch.Operand is Instruction target && body.IndexOf(target) > body.IndexOf(call) && branch.Previous.OpCode == OpCodes.Ldloc && branch.Previous.Operand == selected, "unguarded Single timing helper " + name);
            foreach (var instruction in body)
            {
                bool Bypasses(Instruction target) => body.IndexOf(target) > body.IndexOf(branch) && body.IndexOf(target) <= body.IndexOf(call);
                Require(!(instruction.Operand is Instruction one && Bypasses(one)) && !(instruction.Operand is Instruction[] many && many.Any(Bypasses)), "branch bypasses selected guard");
            }
        }
        return new { passed = true, selectedOnlyCalls = hooks.Length, unsampledTimingCalls = 0, selectedFlagLoadsPendingOnce = true, newSingleLocals = 1, newSingleHandlers = 0, residualObserverWork = "Pending field load, bool local, guarded branches; caller inline Int64 counters and phase selection" };
    }

    internal static object Signatures(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
        var decoder = (ISignatureTypeProvider<string, object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder", true)!, true)!;
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var methods = Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && (TileHelperPatcher.IsAdded(m.DeclaringType) || Program.ChangedNames.Contains(m.FullName))).ToArray();
        Require(methods.Length > 4, "raw signature audit requires runtime helpers");
        string Sig(MethodSignature<string> m) => m.ReturnType + "(" + string.Join(',', m.ParameterTypes) + ")";
        var rows = new List<object>();
        foreach (var method in methods)
        {
            var definition = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            rows.Add(new { kind = "MethodDef", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(definition.Signature)), decoded = Sig(definition.DecodeSignature(decoder, (object?)null)) });
            var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil) { var locals = reader.GetStandaloneSignature(body.LocalSignature); rows.Add(new { kind = "Locals", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(locals.Signature)), decoded = string.Join(',', locals.DecodeLocalSignature(decoder, (object?)null)) }); }
        }
        foreach (var field in Types(game.MainModule).Where(TileHelperPatcher.IsAdded).SelectMany(t => t.Fields))
        {
            var definition = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)field.MetadataToken.RID));
            rows.Add(new { kind = "FieldDef", name = field.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(definition.Signature)), decoded = definition.DecodeSignature(decoder, (object?)null) });
        }
        foreach (var member in methods.SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r => r.MetadataToken.TokenType == TokenType.MemberRef).DistinctBy(r => r.MetadataToken.ToInt32()))
        {
            var definition = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)member.MetadataToken.RID));
            rows.Add(new { kind = "MemberRef", name = member.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(definition.Signature)), decoded = definition.GetKind() == MemberReferenceKind.Method ? Sig(definition.DecodeMethodSignature(decoder, (object?)null)) : definition.DecodeFieldSignature(decoder, (object?)null) });
        }
        return new { passed = true, independentlyDecodedRawMetadata = true, primitiveAliasesAllowed = false, rows };
    }

    internal static object Sdk(byte[] bytes, AssemblyDefinition original, string input)
    {
        // Shared resolver verifies the exact CoreLib SHA/MVID and forbids host fallback.
        var sdkIdentity = Invoke("ReferenceAudit", "Verify", bytes, original, input)!;
        using var resolver = TileHelperPatcher.Resolver(input);
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        var observerInstructions = Types(game.MainModule).Where(TileHelperPatcher.IsAdded).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
        foreach (var instruction in observerInstructions)
        {
            if (instruction.Operand is not Mono.Cecil.MemberReference reference || reference.DeclaringType == null || TileHelperPatcher.IsAdded(reference.DeclaringType.Resolve())) continue;
            if (!reference.DeclaringType.Namespace.StartsWith("Terraria", StringComparison.Ordinal) && !reference.DeclaringType.Namespace.StartsWith("Microsoft.Xna", StringComparison.Ordinal)) continue;
            bool read = instruction.OpCode.Code is Code.Ldfld or Code.Ldsfld || instruction.OpCode.Code is Code.Ldflda or Code.Ldsflda && instruction.Next.OpCode == OpCodes.Ldfld;
            Require(reference is FieldReference && read, "observer game access is not a field read: " + instruction);
        }
        var references = Types(game.MainModule).Where(TileHelperPatcher.IsAdded).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r => r.DeclaringType != null && !TileHelperPatcher.IsAdded(r.DeclaringType.Resolve()));
        references = references.Concat(Types(game.MainModule).Where(TileHelperPatcher.IsAdded).SelectMany(t => t.Fields).SelectMany(f => f.CustomAttributes).Select(a => a.Constructor));
        var rows = new List<object>();
        foreach (var reference in references.DistinctBy(r => r.FullName))
        {
            IMemberDefinition member = reference switch { MethodReference m => m.Resolve(), FieldReference f => f.Resolve(), _ => throw new InvalidDataException("unsupported runtime reference " + reference.FullName) };
            Require(member is MethodDefinition { IsPublic: true } or FieldDefinition { IsPublic: true }, "missing/nonpublic runtime reference " + reference.FullName);
            var owner = member.DeclaringType.Module;
            string image = owner == game.MainModule ? input : owner.FileName;
            rows.Add(new { reference = reference.FullName, resolved = member.FullName, image, sha256 = Sha(File.ReadAllBytes(image)) });
        }
        return new { sdkIdentity, checkedMembers = rows, hostFallbackAllowed = false, gameAccessesAreFieldReadsOnly = true };
    }
}
