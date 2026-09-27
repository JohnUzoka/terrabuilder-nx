using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using static Common;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using MemberReference = Mono.Cecil.MemberReference;

internal static class PropertyDiagnosticAudit
{
    internal static object Preservation(AssemblyDefinition original, AssemblyDefinition changed)
    {
        var a = original.MainModule;
        var b = changed.MainModule;
        string Meta(string method, object value) => (string)Invoke("Preservation", method, value)!;
        string Reference(AssemblyNameReference r) => $"{r.FullName}|{r.Attributes}|{r.HashAlgorithm}|{Convert.ToHexString(r.PublicKey)}|{Convert.ToHexString(r.PublicKeyToken)}|{Convert.ToHexString(r.Hash)}";
        Require(a.Mvid != b.Mvid && b.Mvid != Guid.Empty, "candidate MVID must change");
        Require(original.Modules.Count == 1 && changed.Modules.Count == 1 && a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.Characteristics == b.Characteristics && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName && Reference(original.Name) == Reference(changed.Name), "assembly identity/flags changed");
        Require(a.AssemblyReferences.Select(Reference).SequenceEqual(b.AssemblyReferences.Select(Reference)) && a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "assembly/module references changed");
        foreach (string name in new[] { "Attributes", "Security" }) Require(Meta(name, original) == Meta(name, changed), "assembly attributes/security changed");
        Require(Meta("Attributes", a) == Meta("Attributes", b), "module attributes changed");
        string[] References(ModuleDefinition module) => module.GetTypeReferences().Select(t => t.FullName + "@" + t.Scope).Order(StringComparer.Ordinal).ToArray();
        Require(References(a).SequenceEqual(References(b)), "type references changed");
        string[] Exports(ModuleDefinition module) => module.ExportedTypes.Select(t => $"{t.FullName}|{t.Attributes}|{t.Identifier}|{t.Scope}|{t.DeclaringType?.FullName}").ToArray();
        Require(Exports(a).SequenceEqual(Exports(b)), "exported types changed");
        var oldTypes = Types(a).ToArray();
        var newTypes = Types(b).ToArray();
        Require(oldTypes.Select(t => t.FullName).SequenceEqual(newTypes.Select(t => t.FullName)), "type set changed");
        int bodies = 0, fields = 0;
        for (int index = 0; index < oldTypes.Length; index++)
        {
            var before = oldTypes[index];
            var after = newTypes[index];
            Require(Meta("TypeMetadata", before) == Meta("TypeMetadata", after), "type metadata changed: " + before.FullName);
            Require(before.Fields.Select(f => f.FullName).SequenceEqual(after.Fields.Select(f => f.FullName)) && before.Methods.Select(m => m.FullName).SequenceEqual(after.Methods.Select(m => m.FullName)), "member set/order changed: " + before.FullName);
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
                Require(Body(method) == Body(target) && (!method.HasBody || method.Body.MaxStackSize == target.Body.MaxStackSize), "unrelated method body changed: " + method.FullName);
                bodies++;
            }
        }
        Require(a.Resources.Select(r => Meta("ResourceMetadata", r)).SequenceEqual(b.Resources.Select(r => Meta("ResourceMetadata", r))), "managed resources changed");
        Require(!newTypes.Any(t => t.Name is "NXFrameProfile43" or "NXTileProfile44" or "NXTileCost45" or "NXTileReuse46" or "LightLookupSlices" or "DescriptorMethods"), "an experiment or proof leaked into the candidate");
        return new { unchangedBodies = bodies, unchangedFields = fields, unchangedTypes = oldTypes.Length, managedResources = a.Resources.Count, unchangedAssemblyReferences = a.AssemblyReferences.Count, unchangedTypeReferences = References(a).Length, addedMethods = 0, addedFields = 0, addedTypes = 0, addedAssemblyReferences = 0, changedMethod = Program.TargetName };
    }

    sealed record SignatureRow(string Key, string Decoded, string Raw);

    internal static object Signatures(byte[] original, byte[] changed)
    {
        SignatureRow[] Decode(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var decoder = (ISignatureTypeProvider<string, object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder", true)!, true)!;
            using var image = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
            string Name(EntityHandle handle)
            {
                var value = image.MainModule.LookupToken(MetadataTokens.GetToken(handle));
                return value is MemberReference m ? m.FullName + "@" + m.DeclaringType?.Scope : value.ToString()!;
            }
            string Sig(MethodSignature<string> value) => value.Header.RawValue + ":" + value.GenericParameterCount + ":" + value.RequiredParameterCount + ":" + value.ReturnType + "(" + string.Join(',', value.ParameterTypes) + ")";
            var rows = new List<SignatureRow>();
            void Add(string key, string decoded, BlobHandle blob) => rows.Add(new(key, decoded, Convert.ToHexString(reader.GetBlobBytes(blob))));
            foreach (var handle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(handle);
                Add("MethodDef:" + Name(handle), Sig(method.DecodeSignature(decoder, (object?)null)), method.Signature);
                if (method.RelativeVirtualAddress == 0) continue;
                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                if (!body.LocalSignature.IsNil)
                {
                    var locals = reader.GetStandaloneSignature(body.LocalSignature);
                    Add("Locals:" + Name(handle), string.Join(',', locals.DecodeLocalSignature(decoder, (object?)null)), locals.Signature);
                }
            }
            foreach (var handle in reader.FieldDefinitions)
            {
                var field = reader.GetFieldDefinition(handle);
                Add("FieldDef:" + Name(handle), field.DecodeSignature(decoder, (object?)null), field.Signature);
            }
            foreach (var type in Types(image.MainModule))
            foreach (var property in type.Properties)
            {
                var definition = reader.GetPropertyDefinition(MetadataTokens.PropertyDefinitionHandle((int)property.MetadataToken.RID));
                Add("Property:" + property.FullName, Sig(definition.DecodeSignature(decoder, (object?)null)), definition.Signature);
            }
            foreach (var handle in reader.MemberReferences)
            {
                var reference = reader.GetMemberReference(handle);
                Add("MemberRef:" + Name(handle), reference.GetKind() == MemberReferenceKind.Method ? Sig(reference.DecodeMethodSignature(decoder, (object?)null)) : reference.DecodeFieldSignature(decoder, (object?)null), reference.Signature);
            }
            for (int rid = 1; rid <= reader.GetTableRowCount(TableIndex.TypeSpec); rid++)
            {
                var definition = reader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(rid));
                string decoded = definition.DecodeSignature(decoder, (object?)null);
                Add("TypeSpec:" + decoded, decoded, definition.Signature);
            }
            for (int rid = 1; rid <= reader.GetTableRowCount(TableIndex.MethodSpec); rid++)
            {
                var definition = reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(rid));
                string decoded = string.Join(',', definition.DecodeSignature(decoder, (object?)null));
                Add("MethodSpec:" + Name(definition.Method) + "<" + decoded + ">", decoded, definition.Signature);
            }
            for (int rid = 1; rid <= reader.GetTableRowCount(TableIndex.StandAloneSig); rid++)
            {
                var definition = reader.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(rid));
                string decoded = definition.GetKind() == StandaloneSignatureKind.LocalVariables ? string.Join(',', definition.DecodeLocalSignature(decoder, (object?)null)) : Sig(definition.DecodeMethodSignature(decoder, (object?)null));
                Add("Standalone:" + definition.GetKind() + ":" + decoded, decoded, definition.Signature);
            }
            return rows.OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Decoded, StringComparer.Ordinal).ThenBy(r => r.Raw, StringComparer.Ordinal).ToArray();
        }
        var before = Decode(original);
        var after = Decode(changed);
        Require(before.Select(r => (r.Key, r.Decoded)).SequenceEqual(after.Select(r => (r.Key, r.Decoded))), "raw decoded signatures/reference rows changed");
        Require(before.SequenceEqual(after), "raw signature blob encoding changed");
        return new { passed = true, independentlyDecodedRawMetadata = true, primitiveAliasesAllowed = false, unchangedDecodedRows = after.Length, unchangedRawBlobs = after.Length, note = "Every enumerated signature blob is byte-for-byte unchanged and independently decoded with the reviewed strict primitive decoder.", rows = after };
    }

    internal sealed record SigningState(bool StrongNameSignedFlag, int StrongNameSignatureSize, bool SignatureSlotAllZero, string SignatureSlotSha256, int AuthenticodeCertificateSize, string PublicKey, string Token, string Identity, int CorFlags, int CoffCharacteristics, int DllCharacteristics);

    internal static SigningState Signing(byte[] bytes)
    {
        using var pe = new PEReader(new MemoryStream(bytes));
        using var image = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var cor = pe.PEHeaders.CorHeader!;
        var directory = cor.StrongNameSignatureDirectory;
        byte[] slot = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).ToArray();
        var state = new SigningState((cor.Flags & CorFlags.StrongNameSigned) != 0, directory.Size, slot.All(value => value == 0), Sha(slot), pe.PEHeaders.PEHeader!.CertificateTableDirectory.Size, Convert.ToHexString(image.Name.PublicKey), Convert.ToHexString(image.Name.PublicKeyToken), image.Name.FullName, (int)cor.Flags, (int)pe.PEHeaders.CoffHeader.Characteristics, (int)pe.PEHeaders.PEHeader.DllCharacteristics);
        Require(state.StrongNameSignedFlag && state.StrongNameSignatureSize == 128 && state.SignatureSlotAllZero && state.AuthenticodeCertificateSize == 0 && state.PublicKey.Length != 0 && state.Token == "B03F5F7F11D50A3A", "unsupported signing shape: expected flagged, public-key-present, all-zero 128-byte strong-name slot and no Authenticode");
        return state;
    }

    internal static object SigningPreservation(byte[] original, byte[] changed)
    {
        var before = Signing(original);
        var after = Signing(changed);
        Require(before == after, "binding identity, key, PE flags or zero signature slot changed");
        return new { passed = true, cryptographicSignatureValid = false, strongNameSigningPerformed = false, before, after, note = "The input and candidate contain a public key and StrongNameSigned flag but an all-zero 128-byte signature slot. This preserves identity and slot shape; it is not a valid cryptographic signature or a loader acceptance claim." };
    }
}
