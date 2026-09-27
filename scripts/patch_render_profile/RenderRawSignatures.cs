using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using static Common;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using TypeReference = Mono.Cecil.TypeReference;
using MemberReference = Mono.Cecil.MemberReference;

internal static class RenderRawSignatures
{
    // This independent stream retains encoded kind at each signature use. Cecil can
    // share TypeRef objects between CLASS and VALUETYPE uses of the same metadata row.
    internal static object Compare(byte[] expectedBytes, byte[] candidateBytes, ModuleDefinition expected, ModuleDefinition candidate)
    {
        using var leftStream = new MemoryStream(expectedBytes); using var leftPe = new PEReader(leftStream);
        using var rightStream = new MemoryStream(candidateBytes); using var rightPe = new PEReader(rightStream);
        using var left = Rows(leftPe, expected).GetEnumerator(); using var right = Rows(rightPe, candidate).GetEnumerator();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0;
        while (left.MoveNext()) {
            Require(right.MoveNext(), "raw signature use stream truncated");
            Require(left.Current.Key == right.Current.Key && left.Current.Value == right.Current.Value, "raw signature use differs: " + left.Current.Key);
            digest.AppendData(Encoding.UTF8.GetBytes(left.Current.Key + "\0" + left.Current.Value + "\n")); count++;
        }
        Require(!right.MoveNext(), "raw signature use stream has extra records");
        return new { passed = true, uses = count, sha256 = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), namedClassValueKindsPreservedPerUse = true, arrayBoundsAndRequiredModifiersPreserved = true, allOriginalDefinitionSignaturesAndLocalsCovered = true, beforeFixtureRemapping = true };
    }

    static string Signature(MethodSignature<string> value) => value.Header.RawValue + ":" + value.GenericParameterCount + ":" + value.RequiredParameterCount + ":" + value.ReturnType + "(" + string.Join(',', value.ParameterTypes) + ")";

    static IEnumerable<KeyValuePair<string, string>> Rows(PEReader pe, ModuleDefinition module)
    {
        var reader = pe.GetMetadataReader(); var decoder = new ShapeDecoder(); var references = new Dictionary<int, string>();
        string Reference(MemberReference member) {
            int token = member.MetadataToken.ToInt32();
            if (references.TryGetValue(token, out var known)) return known;
            string value;
            if (member.MetadataToken.TokenType == TokenType.MethodSpec) {
                var item = reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle((int)member.MetadataToken.RID));
                value = "spec:" + string.Join(',', item.DecodeSignature(decoder, (object?)null)) + ":element:" + Reference(((GenericInstanceMethod)member).ElementMethod);
            } else if (member.MetadataToken.TokenType == TokenType.MemberRef) {
                var item = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)member.MetadataToken.RID));
                value = item.GetKind() == MemberReferenceKind.Method ? "method:" + Signature(item.DecodeMethodSignature(decoder, (object?)null)) : "field:" + item.DecodeFieldSignature(decoder, (object?)null);
                if (item.Parent.Kind == HandleKind.TypeSpecification) value += ":owner:" + reader.GetTypeSpecification((TypeSpecificationHandle)item.Parent).DecodeSignature(decoder, (object?)null);
            } else if (member.MetadataToken.TokenType == TokenType.TypeSpec) {
                value = "type:" + reader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle((int)member.MetadataToken.RID)).DecodeSignature(decoder, (object?)null);
            } else if (member.MetadataToken.TokenType == TokenType.Method) {
                value = "definition:" + Signature(reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)member.MetadataToken.RID)).DecodeSignature(decoder, (object?)null));
            } else if (member.MetadataToken.TokenType == TokenType.Field) {
                value = "definition-field:" + reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)member.MetadataToken.RID)).DecodeSignature(decoder, (object?)null);
            } else value = "no-signature";
            references.Add(token, value); return value;
        }
        int typeIndex = 0;
        foreach (var type in Types(module)) {
            string owner = "type:" + typeIndex++ + ":" + type.FullName;
            for (int n = 0; n < type.Fields.Count; n++) {
                var field = type.Fields[n]; var item = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)field.MetadataToken.RID));
                yield return new(owner + ":field:" + n, item.DecodeSignature(decoder, (object?)null));
            }
            for (int n = 0; n < type.Properties.Count; n++) {
                var property = type.Properties[n]; var item = reader.GetPropertyDefinition(MetadataTokens.PropertyDefinitionHandle((int)property.MetadataToken.RID));
                yield return new(owner + ":property:" + n, Signature(item.DecodeSignature(decoder, (object?)null)));
            }
            for (int n = 0; n < type.Methods.Count; n++) {
                var method = type.Methods[n]; string key = owner + ":method:" + n;
                var item = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
                yield return new(key, Signature(item.DecodeSignature(decoder, (object?)null)));
                for (int o = 0; o < method.Overrides.Count; o++) yield return new(key + ":override:" + o, Reference(method.Overrides[o]));
                if (!method.HasBody) continue;
                var body = pe.GetMethodBody(item.RelativeVirtualAddress);
                yield return new(key + ":locals", body.LocalSignature.IsNil ? "none" : string.Join(',', reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(decoder, (object?)null)));
                for (int i = 0; i < method.Body.Instructions.Count; i++) if (method.Body.Instructions[i].Operand is MemberReference reference) yield return new(key + ":il:" + i, Reference(reference));
            }
        }
    }

    internal sealed class NamedKindDecoder : ISignatureTypeProvider<string, object?>
    {
        readonly ISignatureTypeProvider<string, object?> inner;
        readonly ModuleDefinition module;
        readonly Dictionary<int, bool> resolvedKinds = new();
        internal NamedKindDecoder(ModuleDefinition module) {
            this.module = module;
            inner = (ISignatureTypeProvider<string, object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder", true)!, true)!;
        }
        void Check(int token, byte kind, string name) {
            if (kind is not (0x11 or 0x12)) return;
            if (!resolvedKinds.TryGetValue(token, out bool valueType)) {
                var type = (TypeReference)module.LookupToken(token); var resolved = type.Resolve();
                Require(resolved != null, "unresolved raw named type: " + name);
                // Use defining inheritance, not a flag inferred from a shared signature use.
                string? parent = resolved!.BaseType?.FullName;
                valueType = parent == "System.Enum" || parent == "System.ValueType" && resolved.FullName != "System.Enum";
                resolvedKinds.Add(token, valueType);
            }
            Require(valueType == (kind == 0x11), "invalid raw named type kind: " + name + " encoded=" + kind.ToString("x2") + " expected=" + (valueType ? "11" : "12"));
        }
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind) { string text = inner.GetTypeFromDefinition(reader, handle, kind); Check(MetadataTokens.GetToken(handle), kind, text); return text; }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind) { string text = inner.GetTypeFromReference(reader, handle, kind); Check(MetadataTokens.GetToken(handle), kind, text); return text; }
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte kind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
        public string GetArrayType(string type, ArrayShape shape) => inner.GetArrayType(type, shape);
        public string GetByReferenceType(string type) => inner.GetByReferenceType(type);
        public string GetFunctionPointerType(MethodSignature<string> signature) => inner.GetFunctionPointerType(signature);
        public string GetGenericInstantiation(string type, ImmutableArray<string> arguments) => inner.GetGenericInstantiation(type, arguments);
        public string GetGenericMethodParameter(object? context, int index) => inner.GetGenericMethodParameter(context, index);
        public string GetGenericTypeParameter(object? context, int index) => inner.GetGenericTypeParameter(context, index);
        public string GetModifiedType(string modifier, string type, bool required) => inner.GetModifiedType(modifier, type, required);
        public string GetPinnedType(string type) => inner.GetPinnedType(type);
        public string GetPointerType(string type) => inner.GetPointerType(type);
        public string GetPrimitiveType(PrimitiveTypeCode code) => inner.GetPrimitiveType(code);
        public string GetSZArrayType(string type) => inner.GetSZArrayType(type);
    }

    internal static byte[] FlipKindForProof(byte[] bytes, int memberToken, int namedToken, bool locals)
    {
        using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader(); BlobHandle handle;
        if (locals) {
            var method = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(memberToken & 0x00ffffff));
            handle = reader.GetStandaloneSignature(pe.GetMethodBody(method.RelativeVirtualAddress).LocalSignature).Signature;
        } else handle = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(memberToken & 0x00ffffff)).Signature;
        int tag = (namedToken & unchecked((int)0xff000000)) switch { 0x02000000 => 0, 0x01000000 => 1, 0x1b000000 => 2, _ => throw new InvalidDataException("unexpected named type token") };
        var patternBuilder = new BlobBuilder(); patternBuilder.WriteByte(0x11); patternBuilder.WriteCompressedInteger(((namedToken & 0x00ffffff) << 2) | tag);
        byte[] pattern = patternBuilder.ToArray(), signature = reader.GetBlobBytes(handle);
        int at = -1;
        for (int n = 0; n <= signature.Length - pattern.Length; n++) if (signature.AsSpan(n, pattern.Length).SequenceEqual(pattern)) { Require(at == -1, "ambiguous raw kind mutation target"); at = n; }
        Require(at >= 0, "raw named kind mutation target missing");
        int heap = pe.PEHeaders.MetadataStartOffset + reader.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(handle);
        int prefix = bytes[heap] < 0x80 ? 1 : (bytes[heap] & 0xc0) == 0x80 ? 2 : 4;
        Require(bytes.AsSpan(heap + prefix, signature.Length).SequenceEqual(signature), "raw blob offset does not match metadata reader");
        byte[] changed = (byte[])bytes.Clone(); changed[heap + prefix + at] = 0x12;
        return changed;
    }

    sealed class ShapeDecoder : ISignatureTypeProvider<string, object?>
    {
        static string Named(string name, byte kind) => "named:" + kind.ToString("x2") + ":" + name;
        static string DefinitionName(MetadataReader reader, TypeDefinitionHandle handle) {
            var type = reader.GetTypeDefinition(handle); var parent = type.GetDeclaringType();
            return parent.IsNil ? reader.GetString(type.Namespace) + "." + reader.GetString(type.Name) : DefinitionName(reader, parent) + "/" + reader.GetString(type.Name);
        }
        static string ReferenceName(MetadataReader reader, TypeReferenceHandle handle) {
            var type = reader.GetTypeReference(handle);
            return type.ResolutionScope.Kind == HandleKind.TypeReference ? ReferenceName(reader, (TypeReferenceHandle)type.ResolutionScope) + "/" + reader.GetString(type.Name) : reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }
        public string GetArrayType(string type, ArrayShape shape) => type + "[rank=" + shape.Rank + ";sizes=" + string.Join(',', shape.Sizes) + ";lower=" + string.Join(',', shape.LowerBounds) + "]";
        public string GetByReferenceType(string type) => type + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fn:" + Signature(signature);
        public string GetGenericInstantiation(string type, ImmutableArray<string> arguments) => type + "<" + string.Join(',', arguments) + ">";
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string type, bool required) => type + (required ? " modreq:" : " modopt:") + modifier;
        public string GetPinnedType(string type) => type + " pinned";
        public string GetPointerType(string type) => type + "*";
        public string GetPrimitiveType(PrimitiveTypeCode code) => "primitive:" + code;
        public string GetSZArrayType(string type) => type + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind) => Named(DefinitionName(reader, handle), kind);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind) => Named(ReferenceName(reader, handle), kind);
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte kind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}
