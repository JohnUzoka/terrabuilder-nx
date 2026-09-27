using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Contracts;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using MethodDefinition = Mono.Cecil.MethodDefinition;

internal static class LightAudit
{
    internal static object Verify(byte[] originalBytes, byte[] candidateBytes, byte[] expectedBytes, string input, string fna)
    {
        using var resolver = Resolver(input, fna);
        using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
        using var expected = AssemblyDefinition.ReadAssembly(new MemoryStream(expectedBytes), new ReaderParameters { AssemblyResolver = resolver });
        var rawUses = RenderRawSignatures.Compare(expectedBytes, candidateBytes, expected.MainModule, candidate.MainModule);
        var qualified = AuditQualified.Compare(expected.MainModule, candidate.MainModule);
        var native = PeResources.Fingerprints(originalBytes);
        Require(native.SequenceEqual(PeResources.Fingerprints(candidateBytes)), "native resource bytes changed");
        Require(AuditCanonical.ModuleMeta(original) == AuditCanonical.ModuleMeta(candidate), "module metadata/resources changed");
        var beforeTypes = Types(original.MainModule).ToArray(); var afterTypes = Types(candidate.MainModule).ToArray();
        Require(beforeTypes.Length == afterTypes.Length, "definition count changed");
        int methods = 0, fields = 0, unchangedBodies = 0;
        for (int n = 0; n < beforeTypes.Length; n++)
        {
            var a = beforeTypes[n]; var b = afterTypes[n];
            Require(a.MetadataToken == b.MetadataToken && AuditCanonical.TypeMeta(a) == AuditCanonical.TypeMeta(b), "type/field/layout/order changed: " + a.FullName);
            Require(a.Methods.Count == b.Methods.Count && a.Fields.Select(f => f.MetadataToken).SequenceEqual(b.Fields.Select(f => f.MetadataToken)), "definition/member order changed");
            fields += a.Fields.Count;
            for (int m = 0; m < a.Methods.Count; m++)
            {
                var x = a.Methods[m]; var y = b.Methods[m]; methods++;
                Require(x.MetadataToken == y.MetadataToken && AuditCanonical.Json(AuditCanonical.Signature(x)) == AuditCanonical.Json(AuditCanonical.Signature(y)), "method metadata changed: " + x.FullName);
                if (x.MetadataToken.ToInt32() == TargetToken) continue;
                Require(AuditQualified.QualifiedBody(x) == AuditQualified.QualifiedBody(y) && (!x.HasBody || x.Body.MaxStackSize == y.Body.MaxStackSize), "non-target body changed: " + x.FullName);
                unchangedBodies++;
            }
        }
        var before = (MethodDefinition)original.MainModule.LookupToken(TargetToken);
        var after = (MethodDefinition)candidate.MainModule.LookupToken(TargetToken);
        Require(before.FullName == TargetName && before.Body.Instructions.Count == 65 && before.Body.Variables.Count == 5, "original target shape differs");
        Require(after.Body.Instructions.Count == 67 && after.Body.Variables.Count == 6 && after.Body.ExceptionHandlers.Count == 0 && after.Body.InitLocals, "serialized target shape differs");
        var vector = after.Body.Variables[5].VariableType;
        Require(vector.IsValueType && vector.FullName == "Microsoft.Xna.Framework.Vector3" && vector.Scope.Name == "FNA" && vector.Resolve().Module.Assembly.Name.Name == "FNA", "added local is not pinned FNA value Vector3");
        var originalCalls = before.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => i.OpCode.Name + " " + AuditQualified.MemberIdentity((MethodReference)i.Operand)).ToArray();
        var candidateCalls = after.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => i.OpCode.Name + " " + AuditQualified.MemberIdentity((MethodReference)i.Operand)).ToArray();
        Require(originalCalls.Length == 4 && originalCalls.SequenceEqual(candidateCalls), "call identities/order changed");
        var raw = Raw(originalBytes, candidateBytes, candidate.MainModule);
        var old = before.Body.Instructions.ToArray(); var current = after.Body.Instructions.ToArray();
        int Index(int offset) => Array.FindIndex(old, i => i.Offset == offset);
        int save = Index(0x1c) + 1, receiver = Index(0x41) + 1;
        var il = after.Body.GetILProcessor();
        Require(current[save].OpCode == OpCodes.Stloc && current[save].Operand == after.Body.Variables[5] && current[receiver].OpCode == OpCodes.Ldloca && current[receiver].Operand == after.Body.Variables[5], "declared inserted instructions differ");
        foreach (int index in new[] { Index(0x2d) + 1, Index(0x37) + 1 })
        {
            Require(current[index].OpCode == OpCodes.Ldloca && current[index].Operand == after.Body.Variables[5], "declared replacement differs");
            current[index].OpCode = OpCodes.Dup; current[index].Operand = null;
        }
        il.Remove(current[receiver]); il.Remove(current[save]); after.Body.Variables.RemoveAt(5); after.Body.MaxStackSize = before.Body.MaxStackSize;
        Require(AuditQualified.QualifiedBody(before) == AuditQualified.QualifiedBody(after), "exact serialized inverse failed");
        var inverseQualified = AuditQualified.Compare(original.MainModule, candidate.MainModule);
        byte[] restored, normalized;
        using (var stream = new MemoryStream()) { candidate.Write(stream, new WriterParameters { Timestamp = 0 }); restored = stream.ToArray(); }
        using (var stream = new MemoryStream()) { original.Write(stream, new WriterParameters { Timestamp = 0 }); normalized = stream.ToArray(); }
        using var restoredImage = AssemblyDefinition.ReadAssembly(new MemoryStream(restored), new ReaderParameters { AssemblyResolver = resolver });
        using var normalizedImage = AssemblyDefinition.ReadAssembly(new MemoryStream(normalized), new ReaderParameters { AssemblyResolver = resolver });
        var inverseRaw = RenderRawSignatures.Compare(normalized, restored, normalizedImage.MainModule, restoredImage.MainModule);
        Require(candidateBytes.SequenceEqual(expectedBytes), "supplied image differs from exact current-source emission");
        Require(Sha(candidateBytes) == CandidateHash && candidate.MainModule.Mvid == Guid.Parse(CandidateMvid), "candidate identity differs");
        return new {
            passed = true, inputSha256 = InputHash, outputSha256 = CandidateHash, outputMvid = CandidateMvid,
            target = TargetName, token = TargetToken.ToString("x8"), originalInstructions = 65, candidateInstructions = 67,
            originalLocals = 5, candidateLocals = 6, unchangedCalls = 4, orderedCalls = originalCalls,
            changedOriginalBodies = 1, unchangedBodies, originalTypes = beforeTypes.Length, originalMethods = methods, originalFields = fields,
            exactSerializedInverse = true, inverseQualified, inverseRaw, metadataAndResourcesUnchanged = true,
            originalSignaturesAccessAndCallsUnchanged = true, definitionOrderUnchanged = true, raw, rawUses, qualified, nativeResources = native
        };
    }

    static object Raw(byte[] original, byte[] candidate, Mono.Cecil.ModuleDefinition module)
    {
        using var a = new PEReader(new MemoryStream(original)); using var b = new PEReader(new MemoryStream(candidate));
        var before = a.GetMetadataReader(); var after = b.GetMetadataReader();
        var bodies = new List<int>(); var locals = new List<int>();
        Require(before.MethodDefinitions.Count == after.MethodDefinitions.Count && before.FieldDefinitions.Count == after.FieldDefinitions.Count, "raw definition count differs");
        foreach (var handle in before.MethodDefinitions)
        {
            var x = before.GetMethodDefinition(handle); var y = after.GetMethodDefinition(handle); int token = MetadataTokens.GetToken(handle);
            Require(before.GetBlobBytes(x.Signature).SequenceEqual(after.GetBlobBytes(y.Signature)), "raw original method signature changed");
            Require((x.RelativeVirtualAddress == 0) == (y.RelativeVirtualAddress == 0), "raw body presence changed");
            if (x.RelativeVirtualAddress == 0) continue;
            var xb = a.GetMethodBody(x.RelativeVirtualAddress); var yb = b.GetMethodBody(y.RelativeVirtualAddress);
            if (!xb.GetILBytes()!.SequenceEqual(yb.GetILBytes()!)) bodies.Add(token);
            byte[] Local(MetadataReader md, MethodBodyBlock body) => body.LocalSignature.IsNil ? Array.Empty<byte>() : md.GetBlobBytes(md.GetStandaloneSignature(body.LocalSignature).Signature);
            if (!Local(before, xb).SequenceEqual(Local(after, yb))) locals.Add(token);
            Require(xb.LocalVariablesInitialized == yb.LocalVariablesInitialized, "raw locals initialization changed");
        }
        Require(bodies.SequenceEqual(new[] { TargetToken }) && locals.SequenceEqual(new[] { TargetToken }), "raw changed body/local set differs");
        foreach (var handle in before.FieldDefinitions)
            Require(before.GetBlobBytes(before.GetFieldDefinition(handle).Signature).SequenceEqual(after.GetBlobBytes(after.GetFieldDefinition(handle).Signature)), "raw original field signature changed");
        var decoder = new RenderRawSignatures.NamedKindDecoder(module);
        var target = after.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(TargetToken & 0xffffff));
        _ = target.DecodeSignature(decoder, (object?)null);
        var body = b.GetMethodBody(target.RelativeVirtualAddress);
        _ = after.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(decoder, (object?)null);
        int members = 0;
        foreach (var reference in ((MethodDefinition)module.LookupToken(TargetToken)).Body.Instructions.Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r => r.MetadataToken.TokenType == TokenType.MemberRef).DistinctBy(r => r.MetadataToken.ToInt32()))
        {
            var item = after.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)reference.MetadataToken.RID));
            if (item.GetKind() == MemberReferenceKind.Method) _ = item.DecodeMethodSignature(decoder, (object?)null); else _ = item.DecodeFieldSignature(decoder, (object?)null);
            members++;
        }
        return new { passed = true, changedBodyTokens = bodies, changedLocalSignatureTokens = locals, changedDefinitionSignatures = 0, resolvedNamedKindMembers = members, namedKindsResolvedAgainstDefinitions = true };
    }

    internal static object Negatives(byte[] original, byte[] candidate, string input, string fna, string output)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        void Reject(string label, byte[] bytes, string reason)
        {
            string? rejection = null;
            try { Verify(original, bytes, candidate, input, fna); } catch (InvalidDataException error) { rejection = error.Message; }
            Require(rejection != null && rejection.Contains(reason, StringComparison.Ordinal), "negative not rejected by intended static gate: " + label + " actual=" + rejection);
            File.WriteAllBytes(Path.Combine(output, label + ".exe"), bytes);
            rows.Add(new { label, sha256 = Sha(bytes), rejection });
        }
        byte[] Mutate(Action<AssemblyDefinition> mutation)
        {
            using var resolver = Resolver(input, fna);
            using var image = AssemblyDefinition.ReadAssembly(new MemoryStream(candidate), new ReaderParameters { AssemblyResolver = resolver });
            mutation(image); using var stream = new MemoryStream(); image.Write(stream, new WriterParameters { Timestamp = 0 }); return stream.ToArray();
        }
        Reject("wrong-vector-scope", Mutate(a => a.MainModule.GetTypeReferences().Single(t => t.FullName == "Microsoft.Xna.Framework.Vector3").Scope = a.MainModule.AssemblyReferences.First(r => r.Name is "mscorlib" or "System.Private.CoreLib")), "qualified");
        using (var image = AssemblyDefinition.ReadAssembly(new MemoryStream(candidate)))
        {
            int vector = image.MainModule.GetTypeReferences().Single(t => t.FullName == "Microsoft.Xna.Framework.Vector3").MetadataToken.ToInt32();
            Reject("raw-vector-class", RenderRawSignatures.FlipKindForProof(candidate, TargetToken, vector, true), "raw signature use differs");
        }
        Reject("target-scalar", Mutate(a => ((MethodDefinition)a.MainModule.LookupToken(TargetToken)).Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_R4).Operand = 254f), "qualified");
        Reject("other-body", Mutate(a => {
            var method = Types(a.MainModule).SelectMany(t => t.Methods).First(m => m.HasBody && m.MetadataToken.ToInt32() != TargetToken && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_0));
            method.Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_I4_0).OpCode = OpCodes.Ldc_I4_1;
        }), "qualified");
        Reject("field-definition-order", Mutate(a => {
            var type = Types(a.MainModule).First(t => t.Fields.Count > 1); var first = type.Fields[0]; type.Fields.RemoveAt(0); type.Fields.Insert(1, first);
        }), "raw signature use differs");
        byte[] native = (byte[])candidate.Clone();
        using (var pe = new PEReader(new MemoryStream(candidate)))
        {
            var directory = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
            var section = pe.PEHeaders.SectionHeaders.Single(s => directory.RelativeVirtualAddress >= s.VirtualAddress && directory.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            int root = section.PointerToRawData + directory.RelativeVirtualAddress - section.VirtualAddress;
            int FirstData(int offset)
            {
                uint child = BitConverter.ToUInt32(native, root + offset + 20);
                return (child & 0x80000000) != 0 ? FirstData((int)(child & 0x7fffffff)) : (int)child;
            }
            int dataRva = BitConverter.ToInt32(native, root + FirstData(0));
            var dataSection = pe.PEHeaders.SectionHeaders.Single(s => dataRva >= s.VirtualAddress && dataRva < s.VirtualAddress + s.VirtualSize);
            native[dataSection.PointerToRawData + dataRva - dataSection.VirtualAddress] ^= 1;
        }
        Reject("native-resource", native, "native resource bytes changed");
        var result = new { passed = true, count = rows.Count, detected = rows.Count, rows };
        Json(Path.Combine(output, "results.json"), result); return result;
    }
}
