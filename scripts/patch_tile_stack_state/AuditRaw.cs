using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using static Contracts;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;

internal static class AuditRaw
{
    internal static object Changes(byte[] original, byte[] candidate)
    {
        using var beforePe = new PEReader(new MemoryStream(original));
        using var afterPe = new PEReader(new MemoryStream(candidate));
        var beforeMd = beforePe.GetMetadataReader(); var afterMd = afterPe.GetMetadataReader();
        var bodies = new List<int>(); var signatures = new List<int>();
        Require(beforeMd.MethodDefinitions.Count == afterMd.MethodDefinitions.Count, "raw method definition count differs");
        foreach (var handle in beforeMd.MethodDefinitions)
        {
            int token = MetadataTokens.GetToken(handle);
            var before = beforeMd.GetMethodDefinition(handle); var after = afterMd.GetMethodDefinition(handle);
            if (!beforeMd.GetBlobBytes(before.Signature).SequenceEqual(afterMd.GetBlobBytes(after.Signature))) signatures.Add(token);
            Require((before.RelativeVirtualAddress == 0) == (after.RelativeVirtualAddress == 0), "raw body presence differs");
            if (before.RelativeVirtualAddress != 0 && !beforePe.GetMethodBody(before.RelativeVirtualAddress).GetILBytes()!.SequenceEqual(afterPe.GetMethodBody(after.RelativeVirtualAddress).GetILBytes()!)) bodies.Add(token);
        }
        Require(bodies.ToHashSet().SetEquals(HelperTokens.Select(t => (int)t).Append(EntryToken)), "raw changed IL body set differs");
        Require(signatures.ToHashSet().SetEquals(HelperTokens.Select(t => (int)t)), "raw changed signature set differs");
        return new { passed = true, rawChangedILBodyCount = bodies.Count, rawChangedSignatureCount = signatures.Count, rawChangedILBodyTokens = bodies, rawChangedSignatureTokens = signatures, publicScratchCtorILBytesIdentical = !bodies.Contains(0x06004fb6), publicScratchCtorSignatureBytesIdentical = !signatures.Contains(0x06004fb6) };
    }

    internal static object NamedKinds(byte[] image, ModuleDefinition module)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader(); var decoder = new RenderRawSignatures.NamedKindDecoder(module);
        int definitions = 0, locals = 0, references = 0, fields = 0;
        var seen = new HashSet<int>();
        foreach (uint token in HelperTokens.Append((uint)EntryToken))
        {
            var method = (Mono.Cecil.MethodDefinition)module.LookupToken((int)token);
            var definition = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            _ = definition.DecodeSignature(decoder, (object?)null); definitions++;
            var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil) { _ = reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(decoder, (object?)null); locals++; }
            foreach (var reference in method.Body.Instructions.Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>())
            {
                if (reference.MetadataToken.TokenType != TokenType.MemberRef || !seen.Add(reference.MetadataToken.ToInt32())) continue;
                var item = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)reference.MetadataToken.RID));
                if (item.GetKind() == MemberReferenceKind.Method) _ = item.DecodeMethodSignature(decoder, (object?)null);
                else _ = item.DecodeFieldSignature(decoder, (object?)null);
                if (item.Parent.Kind == HandleKind.TypeSpecification) _ = reader.GetTypeSpecification((TypeSpecificationHandle)item.Parent).DecodeSignature(decoder, (object?)null);
                references++;
            }
        }
        foreach (var field in Types(module).Single(t => t.FullName == Added).Fields)
        {
            _ = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)field.MetadataToken.RID)).DecodeSignature(decoder, (object?)null); fields++;
        }
        return new { passed = true, definitions, locals, references, fields, namedKindsResolvedAgainstDefinitions = true, primitiveAliasesRejected = true };
    }
}
