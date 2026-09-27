using Mono.Cecil;
using static Common;

internal static class QualifiedBindings
{
    static string Scope(IMetadataScope? scope) => scope switch
    {
        AssemblyNameReference assembly => "assembly:" + assembly.FullName,
        ModuleDefinition module => "module:" + module.Name + "@" + module.Assembly.Name.FullName,
        ModuleReference module => "module-ref:" + module.Name,
        null => "none",
        _ => throw new InvalidDataException("unsupported metadata scope " + scope.GetType().Name)
    };

    static string Type(TypeReference? type) => type switch
    {
        null => "none",
        GenericParameter parameter => (parameter.Type == GenericParameterType.Method ? "!!" : "!") + parameter.Position,
        GenericInstanceType instance => "generic:" + Type(instance.ElementType) + "<" + string.Join(",", instance.GenericArguments.Select(Type)) + ">",
        ArrayType array => "array:" + array.Rank + ":" + array.IsVector + "[" + string.Join(",", array.Dimensions.Select(d => d.LowerBound + ":" + d.UpperBound)) + "](" + Type(array.ElementType) + ")",
        OptionalModifierType optional => "optional:" + Type(optional.ModifierType) + "(" + Type(optional.ElementType) + ")",
        RequiredModifierType required => "required:" + Type(required.ModifierType) + "(" + Type(required.ElementType) + ")",
        FunctionPointerType pointer => "function:" + Signature(pointer),
        TypeSpecification specification => specification.GetType().Name + "(" + Type(specification.ElementType) + ")",
        _ => type.FullName + "@" + (type.DeclaringType == null ? Scope(type.Scope) : Type(type.DeclaringType))
    };

    static string Signature(IMethodSignature method) => method.CallingConvention + ":" + method.HasThis + ":" + method.ExplicitThis + ":" + Type(method.ReturnType) + "(" + string.Join(",", method.Parameters.Select(p => Type(p.ParameterType))) + ")";
    static string Method(MethodReference method) => method is GenericInstanceMethod generic
        ? "method-spec:" + Method(generic.ElementMethod) + "<" + string.Join(",", generic.GenericArguments.Select(Type)) + ">"
        : Type(method.DeclaringType) + "::" + method.Name + "``" + method.GenericParameters.Count + ":" + Signature(method);
    static string Member(MemberReference member) => member switch
    {
        TypeReference type => Type(type),
        MethodReference method => Method(method),
        FieldReference field => Type(field.DeclaringType) + "::" + field.Name + ":" + Type(field.FieldType),
        _ => throw new InvalidDataException("unsupported qualified member " + member.GetType().Name)
    };

    // Existing preservation verifies attribute blobs verbatim; qualify their constructor bindings without resolving host enums.
    static IEnumerable<string> Attributes(ICustomAttributeProvider owner) => owner.CustomAttributes.Select(attribute => Method(attribute.Constructor));

    static Dictionary<string, string> Uses(ModuleDefinition module)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, IEnumerable<string> values) => rows.Add(key, string.Join("\n", values));
        void AttributeRows(string key, ICustomAttributeProvider owner) => Add(key + ":attributes", Attributes(owner));
        void Parameters(string key, IEnumerable<GenericParameter> parameters)
        {
            foreach (var parameter in parameters)
            {
                string identity = key + ":generic:" + parameter.Position;
                Add(identity, parameter.Constraints.Select(c => Type(c.ConstraintType)));
                AttributeRows(identity, parameter);
                for (int index = 0; index < parameter.Constraints.Count; index++) AttributeRows(identity + ":constraint:" + index, parameter.Constraints[index]);
            }
        }
        AttributeRows("assembly", module.Assembly);
        AttributeRows("module", module);
        foreach (var type in Types(module))
        {
            string owner = "type:" + type.FullName;
            Add(owner, new[] { Type(type), Type(type.BaseType) }.Concat(type.Interfaces.Select(i => Type(i.InterfaceType))));
            AttributeRows(owner, type);
            Parameters(owner, type.GenericParameters);
            for (int index = 0; index < type.Interfaces.Count; index++) AttributeRows(owner + ":interface:" + index, type.Interfaces[index]);
            foreach (var field in type.Fields)
            {
                string key = "field:" + field.FullName;
                rows.Add(key, Member(field));
                AttributeRows(key, field);
            }
            foreach (var property in type.Properties)
            {
                string key = "property:" + property.FullName;
                Add(key, new[] { Type(property.PropertyType) }.Concat(property.Parameters.Select(p => Type(p.ParameterType))));
                AttributeRows(key, property);
            }
            foreach (var item in type.Events)
            {
                string key = "event:" + item.FullName;
                rows.Add(key, Type(item.EventType));
                AttributeRows(key, item);
            }
            foreach (var method in type.Methods)
            {
                string key = "method:" + method.FullName;
                rows.Add(key, Method(method));
                Add(key + ":overrides", method.Overrides.Select(Method));
                AttributeRows(key, method);
                AttributeRows(key + ":return", method.MethodReturnType);
                Parameters(key, method.GenericParameters);
                for (int index = 0; index < method.Parameters.Count; index++) AttributeRows(key + ":parameter:" + index, method.Parameters[index]);
                if (!method.HasBody) continue;
                Add(key + ":locals", method.Body.Variables.Select(v => Type(v.VariableType)));
                Add(key + ":handlers", method.Body.ExceptionHandlers.Select(h => Type(h.CatchType)));
                // Only the existing non-member gate instructions move; member use-site order stays exact.
                Add(key + ":operands", method.Body.Instructions.Select(i => i.Operand).Where(o => o is MemberReference or CallSite).Select(o => o is MemberReference member ? Member(member) : "call-site:" + Signature((CallSite)o)));
            }
        }
        return rows;
    }

    internal static object Compare(ModuleDefinition original, ModuleDefinition candidate)
    {
        var beforeTypes = original.GetTypeReferences().Select(Type).ToHashSet(StringComparer.Ordinal);
        var afterTypes = candidate.GetTypeReferences().Select(Type).ToHashSet(StringComparer.Ordinal);
        Require(beforeTypes.SetEquals(afterTypes), "qualified TypeRef resolution scope changed");
        var beforeMembers = original.GetMemberReferences().Select(Member).ToHashSet(StringComparer.Ordinal);
        var afterMembers = candidate.GetMemberReferences().Select(Member).ToHashSet(StringComparer.Ordinal);
        Require(beforeMembers.SetEquals(afterMembers), "qualified MemberRef binding changed");
        var beforeUses = Uses(original);
        var afterUses = Uses(candidate);
        Require(beforeUses.Count == afterUses.Count, "qualified metadata use-site count changed");
        foreach (var (key, value) in beforeUses)
            Require(afterUses.TryGetValue(key, out string? actual) && value == actual, "qualified binding changed at " + key);
        return new
        {
            passed = true, typeReferenceIdentities = beforeTypes.Count, memberReferenceIdentities = beforeMembers.Count, qualifiedUseSiteRows = beforeUses.Count,
            tableOrderAndDedupIndependent = true, selfModuleMvidIntentionallyExcluded = true,
            beforeProbeRemapping = true, parameterLocalOperandAndAttributeBindingsCompared = true
        };
    }

    internal static object RejectWrongScope(byte[] originalBytes, byte[] candidateBytes, string inputPath, string output)
    {
        Directory.CreateDirectory(output);
        using var resolver = (IAssemblyResolver)Activator.CreateInstance(Support.GetType("ReferenceAudit+PinnedResolver", true)!, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new object[] { Path.GetDirectoryName(inputPath)! }, null)!;
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
        var rectangle = candidate.MainModule.GetTypeReferences().Single(t => t.FullName == "Microsoft.Xna.Framework.Rectangle");
        string previous = Type(rectangle);
        var wrongScope = candidate.MainModule.AssemblyReferences.First(r => r.Name is "mscorlib" or "System.Private.CoreLib" or "System.Runtime");
        Require(rectangle.Scope.Name == "FNA" && wrongScope.Name != rectangle.Scope.Name, "wrong-scope negative requires original FNA binding and existing core reference");
        rectangle.Scope = wrongScope;
        byte[] mutated;
        using (var stream = new MemoryStream()) { candidate.Write(stream, new WriterParameters { Timestamp = 0 }); mutated = stream.ToArray(); }
        string path = Path.Combine(output, "wrong-rectangle-scope.exe");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(mutated);
        string? rejection = null;
        try { DrawGateAudit.Verify(originalBytes, mutated); }
        catch (InvalidDataException error) { rejection = error.Message; }
        Require(rejection != null && rejection.StartsWith("qualified ", StringComparison.Ordinal), "wrong-scope candidate was not rejected before fixture remapping");
        return new { passed = true, candidatePath = path, candidateSha256 = Sha(mutated), originalQualifiedRectangle = previous, wrongQualifiedRectangle = Type(rectangle), rejection, separateFnaBytesUnchanged = true };
    }
}
