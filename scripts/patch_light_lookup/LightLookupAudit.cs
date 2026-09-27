using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using static Common;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using MethodDefinition = Mono.Cecil.MethodDefinition;

internal static class LightLookupAudit
{
    internal static object Preservation(AssemblyDefinition original, AssemblyDefinition changed)
    {
        var a = original.MainModule;
        var b = changed.MainModule;
        string Meta(string method, object value) => (string)Invoke("Preservation", method, value)!;
        Require(a.Mvid != b.Mvid, "candidate MVID must change");
        Require(a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName && original.Name.FullName == changed.Name.FullName, "assembly metadata changed");
        Require(a.AssemblyReferences.Select(r => r.FullName).SequenceEqual(b.AssemblyReferences.Select(r => r.FullName)) && a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "assembly references changed");
        foreach (string name in new[] { "Attributes", "Security" }) Require(Meta(name, original) == Meta(name, changed), "assembly attributes/security changed");
        Require(Meta("Attributes", a) == Meta("Attributes", b), "module attributes changed");
        var oldTypes = Types(a).ToArray();
        var newTypes = Types(b).ToArray();
        Require(oldTypes.Select(t => t.FullName).SequenceEqual(newTypes.Select(t => t.FullName)), "type set changed");
        int bodies = 0, fields = 0;
        for (int index = 0; index < oldTypes.Length; index++)
        {
            var before = oldTypes[index];
            var after = newTypes[index];
            Require(Meta("TypeMetadata", before) == Meta("TypeMetadata", after), "type metadata changed: " + before.FullName);
            Require(before.Fields.Count == after.Fields.Count && before.Methods.Count == after.Methods.Count, "member counts changed: " + before.FullName);
            foreach (var field in before.Fields)
            {
                Require(Meta("FieldMetadata", field) == Meta("FieldMetadata", after.Fields.Single(f => f.Name == field.Name)), "field metadata changed: " + field.FullName);
                fields++;
            }
            foreach (var method in before.Methods)
            {
                var target = after.Methods.Single(m => m.FullName == method.FullName);
                Require(Meta("MethodMetadata", method) == Meta("MethodMetadata", target), "method metadata changed: " + method.FullName);
                if (method.FullName == Program.TargetName) continue;
                Require(Body(method) == Body(target), "unrelated method body changed: " + method.FullName);
                bodies++;
            }
        }
        Require(a.Resources.Select(r => Meta("ResourceMetadata", r)).SequenceEqual(b.Resources.Select(r => Meta("ResourceMetadata", r))), "managed resources changed");
        Require(!newTypes.Any(t => t.Name is "NXFrameProfile43" or "NXTileProfile44" or "NXTileCost45" or "NXTileReuse46"), "an earlier experiment leaked into the candidate");
        return new { unchangedBodies = bodies, unchangedFields = fields, unchangedTypes = oldTypes.Length, managedResources = a.Resources.Count, addedMethods = 0, addedFields = 0, addedTypes = 0, addedAssemblyReferences = 0, changedMethod = Program.TargetName };
    }

    internal static object Signatures(byte[] original, byte[] changed)
    {
        Dictionary<string, string> Decode(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var decoder = (ISignatureTypeProvider<string, object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder", true)!, true)!;
            using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
            var method = Program.Target(game.MainModule);
            string Sig(MethodSignature<string> value) => value.Header.RawValue + ":" + value.GenericParameterCount + ":" + value.RequiredParameterCount + ":" + value.ReturnType + "(" + string.Join(',', value.ParameterTypes) + ")";
            var definition = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            var result = new Dictionary<string, string> { ["MethodDef:" + method.FullName] = Sig(definition.DecodeSignature(decoder, (object?)null)) };
            var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil) result.Add("Locals", string.Join(',', reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(decoder, (object?)null)));
            foreach (var member in method.Body.Instructions.Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r => r.MetadataToken.TokenType == TokenType.MemberRef).DistinctBy(r => r.MetadataToken.ToInt32()))
            {
                var reference = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)member.MetadataToken.RID));
                string value = reference.GetKind() == MemberReferenceKind.Method ? Sig(reference.DecodeMethodSignature(decoder, (object?)null)) : reference.DecodeFieldSignature(decoder, (object?)null);
                string key = "MemberRef:" + member.FullName;
                if (result.TryGetValue(key, out string? previous)) Require(previous == value, "conflicting duplicate member signature: " + key);
                else result.Add(key, value);
            }
            return result;
        }
        var before = Decode(original);
        var after = Decode(changed);
        Require(before.Count == after.Count && before.All(row => after.TryGetValue(row.Key, out string? value) && row.Value == value), "raw decoded signatures changed");
        return new { passed = true, independentlyDecodedRawMetadata = true, primitiveAliasesAllowed = false, unchangedRows = after.Count, signatures = after };
    }
}
