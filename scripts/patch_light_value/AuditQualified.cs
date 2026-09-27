using System.Globalization;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Contracts;

// Source-qualified identities follow the reviewed update56 auditor, without instrumentation.
internal static class AuditQualified
{
    static string Scope(IMetadataScope? scope) => scope switch {
        AssemblyNameReference assembly => "assembly:" + assembly.FullName,
        ModuleDefinition module => "module:" + module.Name + "@" + module.Assembly.Name.FullName,
        ModuleReference module => "module-ref:" + module.Name, null => "none", _ => throw new InvalidDataException("unknown scope")
    };
    internal static string TypeIdentity(TypeReference? type) => type switch {
        null => "none", GenericParameter p => (p.Type == GenericParameterType.Method ? "!!" : "!") + p.Position,
        GenericInstanceType g => "generic:" + TypeIdentity(g.ElementType) + "<" + string.Join(',', g.GenericArguments.Select(TypeIdentity)) + ">",
        ArrayType a => "array:" + a.Rank + ":" + a.IsVector + "[" + string.Join(',', a.Dimensions.Select(d => d.LowerBound + ":" + d.UpperBound)) + "](" + TypeIdentity(a.ElementType) + ")",
        OptionalModifierType m => "optional:" + TypeIdentity(m.ModifierType) + "(" + TypeIdentity(m.ElementType) + ")",
        RequiredModifierType m => "required:" + TypeIdentity(m.ModifierType) + "(" + TypeIdentity(m.ElementType) + ")",
        FunctionPointerType f => "function:" + Signature(f), TypeSpecification s => s.GetType().Name + "(" + TypeIdentity(s.ElementType) + ")",
        _ => type.FullName + "@" + (type.DeclaringType == null ? Scope(type.Scope) : TypeIdentity(type.DeclaringType))
    };
    static string Signature(IMethodSignature method) => method.CallingConvention + ":" + method.HasThis + ":" + method.ExplicitThis + ":" + TypeIdentity(method.ReturnType) + "(" + string.Join(',', method.Parameters.Select(p => TypeIdentity(p.ParameterType))) + ")";
    internal static string MemberIdentity(MemberReference member) => member switch {
        TypeReference type => TypeIdentity(type), GenericInstanceMethod g => "method-spec:" + MemberIdentity(g.ElementMethod) + "<" + string.Join(',', g.GenericArguments.Select(TypeIdentity)) + ">",
        MethodReference m => TypeIdentity(m.DeclaringType) + "::" + m.Name + "``" + m.GenericParameters.Count + ":" + Signature(m),
        FieldReference f => TypeIdentity(f.DeclaringType) + "::" + f.Name + ":" + TypeIdentity(f.FieldType), _ => throw new InvalidDataException("unknown member")
    };
    internal static string QualifiedBody(MethodDefinition method)
    {
        if (!method.HasBody) return "none";
        var indices = method.Body.Instructions.Select((i,n) => (i,n)).ToDictionary(x => x.i, x => x.n);
        string Operand(object? value) => value switch {
            null => "", Instruction i => "@" + indices[i], Instruction[] a => string.Join(',', a.Select(i => "@" + indices[i])),
            VariableDefinition v => "local:" + v.Index, ParameterDefinition p => "arg:" + p.Index,
            MemberReference r => MemberIdentity(r), CallSite c => "call-site:" + Signature(c),
            float n => BitConverter.SingleToInt32Bits(n).ToString("x8", CultureInfo.InvariantCulture), double n => BitConverter.DoubleToInt64Bits(n).ToString("x16", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
        };
        string At(Instruction? i) => i == null ? "end" : "@" + indices[i];
        return method.Body.InitLocals + "\n" + string.Join('\n', method.Body.Variables.Select(v => TypeIdentity(v.VariableType))) + "\n" +
            string.Join('\n', method.Body.Instructions.Select(i => i.OpCode.Name + " " + Operand(i.Operand))) + "\n" +
            string.Join('\n', method.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + At(h.TryStart) + ":" + At(h.TryEnd) + ":" + At(h.HandlerStart) + ":" + At(h.HandlerEnd) + ":" + At(h.FilterStart) + ":" + TypeIdentity(h.CatchType)));
    }

    static string Constant(bool present, object? value) => !present ? "absent" : value switch {
        null => "null", byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
        float number => "single:" + BitConverter.SingleToInt32Bits(number).ToString("x8", CultureInfo.InvariantCulture),
        double number => "double:" + BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)
    };
    static string Marshal(MarshalInfo? info)
    {
        if (info == null) return "none";
        // Include every descriptor property, not just NativeType (array extents,
        // parameter indices, custom marshaler type/cookie, fixed strings, safearrays).
        return info.GetType().FullName + ":" + string.Join(';', info.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + (p.GetValue(info) is TypeReference type ? TypeIdentity(type) : Constant(true, p.GetValue(info)))));
    }

    internal static Dictionary<string,string> QualifiedUses(ModuleDefinition module, bool includeBodies = true)
    {
        var rows = new Dictionary<string,string>(StringComparer.Ordinal);
        void Attr(string key, Mono.Cecil.ICustomAttributeProvider owner) => rows.Add(key + ":attributes", string.Join('\n', owner.CustomAttributes.Select(a => MemberIdentity(a.Constructor) + ":" + Convert.ToHexString(a.GetBlob()))));
        void Generic(string key, IEnumerable<GenericParameter> parameters) {
            foreach (var parameter in parameters) {
                string id = key + ":generic:" + parameter.Position; Attr(id, parameter);
                rows.Add(id, parameter.Attributes + ":" + string.Join(',', parameter.Constraints.Select(c => TypeIdentity(c.ConstraintType))));
                for (int n=0;n<parameter.Constraints.Count;n++) Attr(id + ":constraint:" + n, parameter.Constraints[n]);
            }
        }
        Attr("assembly", module.Assembly); Attr("module", module);
        rows.Add("assembly:name-details", module.Assembly.Name.Attributes + ":" + module.Assembly.Name.HashAlgorithm + ":" + Convert.ToHexString(module.Assembly.Name.PublicKey));
        rows.Add("module:characteristics", module.Characteristics.ToString());
        foreach (var reference in module.AssemblyReferences) rows.Add("assembly-reference:" + reference.FullName, reference.Attributes + ":" + reference.HashAlgorithm + ":" + Convert.ToHexString(reference.PublicKey) + ":" + Convert.ToHexString(reference.Hash));
        foreach (var exported in module.ExportedTypes) rows.Add("exported:" + exported.FullName, exported.Attributes + ":" + exported.Identifier + ":" + Scope(exported.Scope) + ":" + exported.DeclaringType?.FullName);
        foreach (var type in Types(module)) {
            string key = "type:" + type.FullName; Attr(key, type); Generic(key, type.GenericParameters);
            rows.Add(key, TypeIdentity(type) + ":" + TypeIdentity(type.BaseType) + ":" + string.Join(',', type.Interfaces.Select(i => TypeIdentity(i.InterfaceType))));
            for(int n=0;n<type.Interfaces.Count;n++) Attr(key + ":interface:" + n,type.Interfaces[n]);
            foreach (var field in type.Fields) { string f = "field:" + field.FullName; rows.Add(f,MemberIdentity(field)); Attr(f,field); rows.Add(f + ":constant-marshal", Constant(field.HasConstant, field.Constant) + ":" + Marshal(field.HasMarshalInfo ? field.MarshalInfo : null)); }
            foreach (var property in type.Properties) {
                string p = "property:" + property.FullName; rows.Add(p,TypeIdentity(property.PropertyType) + ":" + string.Join(',',property.Parameters.Select(a => TypeIdentity(a.ParameterType)))); Attr(p,property);
                rows.Add(p + ":constant", Constant(property.HasConstant, property.Constant));
                for (int n = 0; n < property.Parameters.Count; n++) { var parameter = property.Parameters[n]; Attr(p + ":arg:" + n, parameter); rows.Add(p + ":arg-details:" + n, parameter.Attributes + ":" + Constant(parameter.HasConstant, parameter.Constant) + ":" + Marshal(parameter.HasMarshalInfo ? parameter.MarshalInfo : null)); }
            }
            foreach (var item in type.Events) { string e = "event:" + item.FullName; rows.Add(e,TypeIdentity(item.EventType) + ":" + string.Join(',',item.OtherMethods.Select(MemberIdentity))); Attr(e,item); }
            foreach (var method in type.Methods) {
                string m = "method:" + method.FullName; rows.Add(m,MemberIdentity(method)); Attr(m,method); Attr(m + ":return",method.MethodReturnType); Generic(m,method.GenericParameters);
                rows.Add(m + ":overrides",string.Join('\n',method.Overrides.Select(MemberIdentity)));
                rows.Add(m + ":return-details", method.MethodReturnType.Attributes + ":" + Constant(method.MethodReturnType.HasConstant, method.MethodReturnType.Constant) + ":" + Marshal(method.MethodReturnType.HasMarshalInfo ? method.MethodReturnType.MarshalInfo : null));
                for(int n=0;n<method.Parameters.Count;n++) { var parameter = method.Parameters[n]; Attr(m + ":arg:" + n,parameter); rows.Add(m + ":arg-details:" + n, parameter.Attributes + ":" + Constant(parameter.HasConstant, parameter.Constant) + ":" + Marshal(parameter.HasMarshalInfo ? parameter.MarshalInfo : null)); }
                if (includeBodies) rows.Add(m + ":body",QualifiedBody(method));
            }
        }
        return rows;
    }
    internal static object Compare(ModuleDefinition expected, ModuleDefinition candidate)
    {
        Require(expected.GetTypeReferences().Select(TypeIdentity).ToHashSet().SetEquals(candidate.GetTypeReferences().Select(TypeIdentity)), "qualified TypeRef scope changed");
        Require(expected.GetMemberReferences().Select(MemberIdentity).ToHashSet().SetEquals(candidate.GetMemberReferences().Select(MemberIdentity)), "qualified MemberRef binding changed");
        var before = QualifiedUses(expected); var after = QualifiedUses(candidate);
        Require(before.Count == after.Count, "qualified metadata/use-site count changed");
        foreach (var (key, value) in before) Require(after.TryGetValue(key, out var actual) && actual == value, "qualified candidate differs at " + key);
        return new { passed = true, qualifiedUseSiteRows = before.Count, beforeAnyFixtureRemapping = true, selfModuleMvidIntentionallyExcluded = true };
    }
}
