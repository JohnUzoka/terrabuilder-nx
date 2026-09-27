using Mono.Cecil;
using Mono.Cecil.Cil;
using static Contracts;
using static AuditCanonical;

internal static class StackStateAudit
{
    internal static object Verify(byte[] input, byte[] candidate, byte[] expected, string inputPath, string fnaPath, string output)
    {
        using var resolver = Resolver(inputPath, fnaPath);
        using var before = AssemblyDefinition.ReadAssembly(new MemoryStream(input), new ReaderParameters { AssemblyResolver = resolver });
        using var after = AssemblyDefinition.ReadAssembly(new MemoryStream(candidate), new ReaderParameters { AssemblyResolver = resolver });
        var originalTypes = Types(before.MainModule).ToArray(); var afterTypes = Types(after.MainModule).ToArray();
        var originalMethods = originalTypes.SelectMany(t => t.Methods).ToArray(); var afterMethods = afterTypes.SelectMany(t => t.Methods).ToArray();
        var originalByToken = originalMethods.ToDictionary(m => m.MetadataToken.ToUInt32());
        var beforeBodies = originalMethods.ToDictionary(m => m.MetadataToken.ToUInt32(), Body);
        var beforeSigs = originalMethods.ToDictionary(m => m.MetadataToken.ToUInt32(), m => Json(Signature(m)));
        var beforeTypes = originalTypes.ToDictionary(t => t.FullName, TypeMeta);
        var old = originalTypes.Single(t => t.FullName == Original);
        var entry = (MethodDefinition)before.MainModule.LookupToken(EntryToken);
        var originalEntry = entry.Body.Instructions.ToArray();
        var changedTokens = HelperTokens.Append((uint)EntryToken).ToHashSet();
        Require(afterMethods.Length == originalMethods.Length && afterTypes.Length == originalTypes.Length + 1, "definition count differs");
        Require(afterMethods.Select(m => m.MetadataToken.ToUInt32()).ToHashSet().SetEquals(originalByToken.Keys), "existing method tokens shifted");
        Require(ModuleMeta(after) == ModuleMeta(before), "module/assembly/resources changed");
        var state = afterTypes.Single(t => t.FullName == Added); var aOld = afterTypes.Single(t => t.FullName == Original);
        var aEntry = (MethodDefinition)after.MainModule.LookupToken(EntryToken);
        Require(state.Attributes == (TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit) && !state.IsNested && state.BaseType.FullName == "System.ValueType" && state.BaseType.Scope.Name == before.MainModule.TypeSystem.CoreLibrary.Name && state.Fields.Count == 19 && state.Methods.Count == 0 && state.NestedTypes.Count == 0 && state.Interfaces.Count == 0 && state.GenericParameters.Count == 0 && !state.HasCustomAttributes && !state.HasSecurityDeclarations && state.Properties.Count == 0 && state.Events.Count == 0 && state.PackingSize == -1 && state.ClassSize == -1, "nonpublic value type layout differs");
        Require(after.MainModule.Types.Last() == state, "state not appended after original definitions");
        Require(state.Fields.Select(f => f.Name + ":" + f.FieldType.FullName).SequenceEqual(aOld.Fields.Select(f => f.Name + ":" + f.FieldType.FullName)) && state.Fields.All(f => f.Attributes == FieldAttributes.Public && !f.HasConstant && !f.HasCustomAttributes && !f.HasMarshalInfo && f.InitialValue.Length == 0 && f.Offset == -1), "state field layout differs");
        Require(aEntry.Body.InitLocals && aEntry.Body.Variables[0].VariableType.Resolve() == state, "candidate locals-init differs");
        var prefix = aEntry.Body.Instructions.Take(4).ToArray();
        Require(prefix.Length == 4 && prefix[0].OpCode == OpCodes.Ldloca && prefix[0].Operand == aEntry.Body.Variables[0] && prefix[1].OpCode == OpCodes.Ldc_I4_S && (sbyte)prefix[1].Operand == 9 && prefix[2].OpCode == OpCodes.Newarr && prefix[2].Operand is TypeReference vector && vector.FullName == "Microsoft.Xna.Framework.Vector3" && vector.Scope.Name == "FNA" && prefix[3].OpCode == OpCodes.Stfld && prefix[3].Operand is FieldReference arrayField && arrayField.Resolve() == state.Fields.Single(f => f.Name == "colorSlices"), "fresh array initialization prefix differs");
        Require(!aEntry.Body.Instructions.Any(i => i.OpCode == OpCodes.Initobj && i.Operand is TypeReference t && t.Resolve() == state), "redundant state initobj remains");
        Require(!aEntry.Body.Instructions.SelectMany(i => i.Operand is Instruction branch ? new[] { branch } : i.Operand is Instruction[] branches ? branches : Array.Empty<Instruction>()).Any(prefix.Contains), "branch enters state initialization prefix");
        foreach (uint token in HelperTokens)
        {
            var helper = (MethodDefinition)after.MainModule.LookupToken((int)token);
            var oldHelper = originalByToken[token];
            Require(helper.IsPrivate && !helper.IsVirtual && helper.DeclaringType.FullName == Owner, "private helper binding differs");
            for (int n = 0; n < oldHelper.Parameters.Count; n++)
                if (oldHelper.Parameters[n].ParameterType.Resolve() == old)
                    Require(helper.Parameters[n].ParameterType is ByReferenceType br && br.ElementType.Resolve() == state, "helper is not direct state byref");
        }
        var actualBodyChanges = afterMethods.Where(m => Body(m) != beforeBodies[m.MetadataToken.ToUInt32()]).Select(m => m.MetadataToken.ToUInt32()).ToHashSet();
        var actualSigChanges = afterMethods.Where(m => Json(Signature(m)) != beforeSigs[m.MetadataToken.ToUInt32()]).Select(m => m.MetadataToken.ToUInt32()).ToHashSet();
        Require(actualBodyChanges.SetEquals(changedTokens), "unexpected method body changes");
        Require(actualSigChanges.SetEquals(HelperTokens), "unexpected signature changes");
        foreach (var type in afterTypes.Where(t => t != state)) Require(TypeMeta(type) == beforeTypes[type.FullName], "original type metadata changed " + type.FullName);
        Require(Body(aOld.Methods.Single()) == beforeBodies[old.Methods.Single().MetadataToken.ToUInt32()], "public scratch constructor changed");
        var typeTokens = originalTypes.ToDictionary(t => t.FullName, t => t.MetadataToken);
        var fieldTokens = originalTypes.SelectMany(t => t.Fields).ToDictionary(f => f.FullName, f => f.MetadataToken);
        Require(afterTypes.Where(t => t != state).All(t => typeTokens[t.FullName] == t.MetadataToken), "existing TypeDef tokens shifted");
        Require(afterTypes.Where(t => t != state).SelectMany(t => t.Fields).All(f => fieldTokens[f.FullName] == f.MetadataToken), "existing FieldDef tokens shifted");
        Require(afterMethods.All(m => m.Name == originalByToken[m.MetadataToken.ToUInt32()].Name && m.DeclaringType.MetadataToken == originalByToken[m.MetadataToken.ToUInt32()].DeclaringType.MetadataToken), "existing MethodDef tokens shifted");
        int originalFieldOps = originalMethods.Where(m => changedTokens.Contains(m.MetadataToken.ToUInt32())).Sum(m => m.Body.Instructions.Count(i => i.Operand is FieldReference f && f.DeclaringType.FullName == Original));
        int candidateFieldOps = afterMethods.Where(m => changedTokens.Contains(m.MetadataToken.ToUInt32())).Sum(m => m.Body.Instructions.Count(i => i.Operand is FieldReference f && f.DeclaringType.FullName == Added));
        Require(originalFieldOps == 1123 && candidateFieldOps == originalFieldOps + 1, "candidate field count differs");
        var raw = AuditRaw.Changes(input, candidate);
        var named = AuditRaw.NamedKinds(candidate, after.MainModule);
        using var reference = AssemblyDefinition.ReadAssembly(new MemoryStream(expected));
        var rawUses = RenderRawSignatures.Compare(expected, candidate, reference.MainModule, after.MainModule);
        var changeReport = afterMethods.Where(m => changedTokens.Contains(m.MetadataToken.ToUInt32())).Select(m => new { token = m.MetadataToken.ToUInt32(), before = originalByToken[m.MetadataToken.ToUInt32()].FullName, after = m.FullName, instructionCount = m.Body.Instructions.Count, ilCodeSize = m.Body.CodeSize }).ToArray();
        foreach (var method in afterMethods.Where(m => changedTokens.Contains(m.MetadataToken.ToUInt32())))
        {
            foreach (var parameter in method.Parameters.Where(p => p.ParameterType is ByReferenceType br && br.ElementType.Resolve() == state)) parameter.ParameterType = aOld;
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is FieldReference field && field.DeclaringType.Resolve() == state)
                    instruction.Operand = aOld.Fields.Single(f => f.Name == field.Name);
        }
        var il = aEntry.Body.GetILProcessor(); var firstOriginal = aEntry.Body.Instructions[4];
        for (int n = 0; n < 4; n++) il.Remove(aEntry.Body.Instructions[0]);
        il.InsertBefore(firstOriginal, Instruction.Create(OpCodes.Newobj, aOld.Methods.Single()));
        il.InsertBefore(firstOriginal, Instruction.Create(OpCodes.Stloc_0)); aEntry.Body.Variables[0].VariableType = aOld;
        int ownerLoads = 0;
        for (int n = 2; n < originalEntry.Length; n++) if (Local0(originalEntry[n]))
        {
            var instruction = aEntry.Body.Instructions[n];
            Require(instruction.OpCode == OpCodes.Ldloca && instruction.Operand == aEntry.Body.Variables[0], "owner load transform differs");
            instruction.OpCode = originalEntry[n].OpCode; instruction.Operand = originalEntry[n].Operand; ownerLoads++;
        }
        Require(ownerLoads == 238, "owner load count differs");
        after.MainModule.Types.Remove(state);
        foreach (var method in afterMethods)
        {
            Require(Body(method) == beforeBodies[method.MetadataToken.ToUInt32()], "inverse body differs " + method.FullName);
            Require(Json(Signature(method)) == beforeSigs[method.MetadataToken.ToUInt32()], "inverse signature differs " + method.FullName);
        }
        foreach (var type in Types(after.MainModule)) Require(TypeMeta(type) == beforeTypes[type.FullName], "inverse type differs " + type.FullName);
        Require(Sha(candidate) == CandidateHash && Sha(expected) == CandidateHash, "image differs from frozen candidate03");
        Require(after.MainModule.Mvid == Guid.Parse(CandidateMvid), "candidate MVID differs");
        Directory.CreateDirectory(output);
        Contracts.Json(Path.Combine(output, "raw-change-summary.json"), raw);
        Contracts.Json(Path.Combine(output, "raw-signatures.json"), new { named, rawUses });
        Contracts.Json(Path.Combine(output, "token-stability.json"), new { mode = "appended-internal-top-level", addedType = Added, retokenizedTypes = 0, retokenizedFields = 0, retokenizedMethods = 0, originalTypeDefinitions = originalTypes.Length, originalFieldDefinitions = fieldTokens.Count, originalMethodDefinitions = originalMethods.Length });
        Contracts.Json(Path.Combine(output, "initialization.json"), new { originalInitLocals = true, candidateInitLocals = true, explicitScratchInitobj = false, initialization = "Fresh method-entry locals initialization only", freshVector3ArrayPerInvocation = true, arrayLength = 9, noCrossCallOrCrossFrameReuse = true });
        var result = new { passed = true, inspectionOnly = true, hardwarePending = true, performanceAdopted = false, inputSha256 = Sha(input), outputSha256 = Sha(candidate), outputMvid = CandidateMvid, changedMethods = changeReport, changedMethodBodies = 8, changedPrivateSignatures = 7, addedTypes = 1, addedFields = 19, addedMethods = 0, changedLocals = 1, ownerLoads, remappedFieldOperands = originalFieldOps, allUnlistedCanonicalMetadataAndBodiesEqual = true, fullInverseCanonicalComparisonPassed = true, publicScratchCanonicalMetadataAndCtorUnchanged = true, raw, named, rawUses, limits = "Static audit is not acceptance. Private reflection/IL hooks into seven changed signatures are outside vanilla scope. Allocation/GC/OOM behavior changes; hardware and performance adoption remain pending." };
        Contracts.Json(Path.Combine(output, "preparation.json"), result);
        return result;
    }
}
