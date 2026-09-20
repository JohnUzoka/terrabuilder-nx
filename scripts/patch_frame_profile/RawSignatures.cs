using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using static Program;

internal static class RawSignatures
{
    internal static readonly HashSet<string> PrimitiveNames = new(StringComparer.Ordinal) {
        "System.Void", "System.Boolean", "System.Char", "System.SByte", "System.Byte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.String", "System.IntPtr", "System.UIntPtr", "System.Object", "System.TypedReference"
    };
    internal static object Verify(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader(); var decoder = new Decoder();
        using var cecil = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var methods = Types(cecil.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && (m.DeclaringType.Name == "NXFrameProfile43" || m.Name == "NXProfileRead")).ToArray();
        Require(methods.Length > 1, "raw signature audit requires injected helpers");
        var rows = new List<object>();
        foreach (var method in methods)
        {
            var def = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            var sig = def.DecodeSignature(decoder, (object?)null);
            rows.Add(new { kind = "MethodDef", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(def.Signature)), decoded = Signature(sig) });
            var body = pe.GetMethodBody(def.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil)
            {
                var local = reader.GetStandaloneSignature(body.LocalSignature);
                rows.Add(new { kind = "Locals", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(local.Signature)), decoded = string.Join(",", local.DecodeLocalSignature(decoder, (object?)null)) });
            }
        }
        foreach (var field in Types(cecil.MainModule).Where(t => t.Name == "NXFrameProfile43").SelectMany(t => t.Fields))
        {
            var def = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)field.MetadataToken.RID));
            rows.Add(new { kind = "FieldDef", name = field.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(def.Signature)), decoded = def.DecodeSignature(decoder, (object?)null) });
        }
        var references = methods.SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<Mono.Cecil.MemberReference>().Where(m => m.MetadataToken.TokenType == TokenType.MemberRef).GroupBy(m => m.MetadataToken.ToInt32()).Select(g => g.First());
        foreach (var reference in references)
        {
            var def = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)reference.MetadataToken.RID));
            string decoded = def.GetKind() == MemberReferenceKind.Method ? Signature(def.DecodeMethodSignature(decoder, (object?)null)) : def.DecodeFieldSignature(decoder, (object?)null);
            rows.Add(new { kind = "MemberRef", name = reference.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(def.Signature)), decoded });
        }
        return new { passed = true, independentlyDecodedRawMetadata = true, primitiveClassOrValueTypeAliasesAllowed = false, rows };
    }
    internal static object VerifyMalformedRejection(string output)
    {
        using var bad = Mono.Cecil.AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("RejectedSignatureFixture", new Version(1, 0)), "RejectedSignatureFixture", ModuleKind.Dll);
        var m = bad.MainModule; m.Mvid = Guid.Parse("e21b628a-7101-4e92-bc7c-d6c869631043");
        var type = new Mono.Cecil.TypeDefinition("", "NXFrameProfile43", Mono.Cecil.TypeAttributes.Public, m.TypeSystem.Object); m.Types.Add(type);
        foreach (string name in new[] { "BadVoid", "AlsoBadVoid" })
        {
            var encodedClassVoid = new Mono.Cecil.TypeReference("System", "Void", m, m.TypeSystem.CoreLibrary, false);
            var method = new Mono.Cecil.MethodDefinition(name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, encodedClassVoid);
            method.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret)); type.Methods.Add(method);
        }
        using var buffer = new MemoryStream(); bad.Write(buffer, new WriterParameters { Timestamp = 0 });
        byte[] image = buffer.ToArray(); File.WriteAllBytes(Path.Combine(output, "RejectedSignatureFixture.dll"), image);
        try { Verify(image); }
        catch (InvalidDataException e) when (e.Message.Contains("invalid raw primitive encoding: System.Void", StringComparison.Ordinal))
        {
            return new { passed = true, malformedFixtureSha256 = Sha(image), rejection = e.Message };
        }
        throw new InvalidDataException("raw signature guard accepted a CLASS System.Void fixture");
    }
    static string Signature(MethodSignature<string> m) => m.ReturnType + "(" + string.Join(",", m.ParameterTypes) + ")";
    sealed class Decoder : ISignatureTypeProvider<string, object?>
    {
        static string Named(string ns, string name, byte kind)
        {
            string fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            Require(!PrimitiveNames.Contains(fullName), "invalid raw primitive encoding: " + fullName + " used CLASS/VALUETYPE kind=" + kind.ToString("x2"));
            return "named:" + kind.ToString("x2") + ":" + fullName;
        }
        public string GetArrayType(string type, ArrayShape shape) => type + "[rank=" + shape.Rank + "]";
        public string GetByReferenceType(string type) => type + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fn:" + Signature(signature);
        public string GetGenericInstantiation(string type, ImmutableArray<string> arguments) => type + "<" + string.Join(",", arguments) + ">";
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string type, bool required) => type + " mod:" + modifier;
        public string GetPinnedType(string type) => type + " pinned";
        public string GetPointerType(string type) => type + "*";
        public string GetPrimitiveType(PrimitiveTypeCode code) => "primitive:" + code;
        public string GetSZArrayType(string type) => type + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind) { var t = reader.GetTypeDefinition(handle); return Named(reader.GetString(t.Namespace), reader.GetString(t.Name), kind); }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind) { var t = reader.GetTypeReference(handle); return Named(reader.GetString(t.Namespace), reader.GetString(t.Name), kind); }
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte kind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}
