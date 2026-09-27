using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class UpdateProbe
{
    internal static readonly Dictionary<string, string> HostNames = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, string> BoundaryNames = new(StringComparer.Ordinal);
    internal static byte[] Build(string baselinePath, string candidatePath, List<object> receipts)
    {
        HostNames.Clear(); BoundaryNames.Clear();
        using var resolver = UpdatePatcher.Resolver(baselinePath);
        using var baseline = AssemblyDefinition.ReadAssembly(baselinePath, new ReaderParameters { AssemblyResolver = resolver });
        using var candidate = AssemblyDefinition.ReadAssembly(candidatePath, new ReaderParameters { AssemblyResolver = resolver });
        using var probe = AssemblyDefinition.ReadAssembly(new MemoryStream(RuntimeProbe.Build(candidatePath, receipts)));
        var module = probe.MainModule;
        probe.Name.Name = "Update56ExactBehaviorProbe"; module.Name = probe.Name.Name;
        var runtime = module.Types.Single(t => t.FullName == "Probe.UpdateRuntime"); runtime.Name = "Runtime";
        var hooks = new TypeDefinition("Probe", "UpdateBodies", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
        using var mapper = new Mapper(baseline, candidate, module, runtime);
        UpdateBehaviorRoots.ConfigureProjection(baseline, candidate, module, runtime, mapper);
        UpdateBehaviorWorld.ConfigureProjection(baseline, candidate, module, runtime, mapper);
        foreach (var specification in UpdatePatcher.Targets)
        {
            var original = Types(baseline.MainModule).SelectMany(t => t.Methods).Single(m => m.FullName == specification.Name);
            foreach (var (assembly, prefix) in new[] { (baseline, "Baseline"), (candidate, "Patched") })
            {
                var source = Types(assembly.MainModule).SelectMany(t => t.Methods).Single(m => m.FullName == original.FullName);
                var target = new MethodDefinition(prefix + original.MetadataToken.ToInt32().ToString("x8"), MA.Public | MA.Static, mapper.Type(source.ReturnType));
                if (source.HasThis) target.Parameters.Add(new ParameterDefinition("receiver", Mono.Cecil.ParameterAttributes.None, mapper.Type(source.DeclaringType)));
                foreach (var parameter in source.Parameters) target.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, mapper.Type(parameter.ParameterType)));
                hooks.Methods.Add(target);
                Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)mapper.Type, (Func<object, object>)mapper.Member, source.HasThis ? 1 : 0);
                Widen(target);
                Require(source.Body.Instructions.Count == target.Body.Instructions.Count && source.Body.Variables.Count == target.Body.Variables.Count && source.Body.ExceptionHandlers.Count == target.Body.ExceptionHandlers.Count, "update full body clone shape " + source.FullName);
                ValidateCopy(source, target, mapper);
                var bindings = source.Body.Instructions.Select((instruction, index) => new { instruction, index }).Where(x => x.instruction.Operand is MemberReference)
                    .Select(x => new { x.index, opcode = x.instruction.OpCode.Name, source = Mapper.Qualified((MemberReference)x.instruction.Operand), fixture = Mapper.Qualified((MemberReference)target.Body.Instructions[x.index].Operand) }).ToArray();
                receipts.Add(new { kind = "ActualUpdateBody", source = source.FullName, sourceMvid = source.Module.Mvid, sourceToken = source.MetadataToken.ToInt32(), sourceBody = Fingerprint(source), fixture = target.FullName, mappedBody = Fingerprint(target), instructions = source.Body.Instructions.Count, locals = source.Body.Variables.Count, handlers = source.Body.ExceptionHandlers.Count, bindings });
                if (prefix != "Patched") continue;
                var mutant = new MethodDefinition("MissingObserver" + original.MetadataToken.ToInt32().ToString("x8"), MA.Public | MA.Static, target.ReturnType);
                foreach (var parameter in target.Parameters) mutant.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
                hooks.Methods.Add(mutant); Copy(target, mutant, t => t, value => value);
                var exit = mutant.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType == runtime && m.Name is "Exit" or "FrameEnd" or "Flush");
                bool flush = ((MethodReference)exit.Operand).Name == "Flush";
                exit.OpCode = flush ? OpCodes.Nop : OpCodes.Pop; exit.Operand = null;
                if (!flush) mutant.Body.GetILProcessor().InsertAfter(exit, Instruction.Create(OpCodes.Pop));
                Widen(mutant);
                receipts.Add(new { kind = "MissingActualObserverMutant", source = source.FullName, fixture = mutant.FullName, mappedBody = Fingerprint(mutant) });
                UpdateBehaviorRoots.AddMutants(source, target, runtime, receipts, mapper);
                UpdateBehaviorWorld.AddMutants(source, target, runtime, receipts, mapper);
                UpdateBehaviorEntities.AddMutants(source, target, runtime, receipts, mapper);
            }
        }
        foreach (var method in hooks.Methods.ToArray()) AddInvocation(method, module);
        foreach (var method in Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody)) Widen(method);
        var fingerprints = Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody).ToDictionary(m => m.FullName, Fingerprint);
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fingerprints.Select(p => p.Key + ":" + p.Value)))).AsSpan(0, 16));
        using var stream = new MemoryStream(); probe.Write(stream, new WriterParameters { Timestamp = 0 }); byte[] bytes = stream.ToArray();
        using var serialized = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var method in Types(serialized.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody)) Require(fingerprints[method.FullName] == Fingerprint(method), "serialized update body differs " + method.FullName);
        receipts.Add(new { kind = "SerializedExecutableBodies", bodies = fingerprints }); return bytes;
    }

    // Reflection's own ref-argument copyback is skipped when its target throws.
    // This host-only adapter calls the untouched body directly, then exposes the
    // actual ref locals from finally on both normal and exceptional completion.
    static void AddInvocation(MethodDefinition target, ModuleDefinition module)
    {
        Require(!target.HasThis && target.ReturnType.MetadataType == MetadataType.Void, "void static projected target invocation");
        var adapter = new MethodDefinition(target.Name + "Invoke", MA.Public | MA.Static, module.TypeSystem.Object);
        adapter.Parameters.Add(new ParameterDefinition("arguments", Mono.Cecil.ParameterAttributes.None, new ArrayType(module.TypeSystem.Object)));
        target.DeclaringType.Methods.Add(adapter); adapter.Body.InitLocals = true;
        var il = adapter.Body.GetILProcessor();
        foreach (var parameter in target.Parameters)
        {
            var type = parameter.ParameterType is ByReferenceType reference ? reference.ElementType : parameter.ParameterType;
            var local = new VariableDefinition(type); adapter.Body.Variables.Add(local);
            il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Ldc_I4, parameter.Index)); il.Append(Instruction.Create(OpCodes.Ldelem_Ref));
            il.Append(Instruction.Create(type.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, type)); il.Append(Instruction.Create(OpCodes.Stloc, local));
        }
        var start = Instruction.Create(OpCodes.Nop); il.Append(start);
        foreach (var parameter in target.Parameters) il.Append(Instruction.Create(parameter.ParameterType is ByReferenceType ? OpCodes.Ldloca : OpCodes.Ldloc, adapter.Body.Variables[parameter.Index]));
        il.Append(Instruction.Create(OpCodes.Call, target));
        var done = Instruction.Create(OpCodes.Ldnull); il.Append(Instruction.Create(OpCodes.Leave, done));
        var cleanup = Instruction.Create(OpCodes.Nop); il.Append(cleanup);
        foreach (var parameter in target.Parameters.Where(p => p.ParameterType is ByReferenceType))
        {
            var local = adapter.Body.Variables[parameter.Index];
            il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Ldc_I4, parameter.Index)); il.Append(Instruction.Create(OpCodes.Ldloc, local));
            if (local.VariableType.IsValueType) il.Append(Instruction.Create(OpCodes.Box, local.VariableType));
            il.Append(Instruction.Create(OpCodes.Stelem_Ref));
        }
        il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(done); il.Append(Instruction.Create(OpCodes.Ret));
        adapter.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = start, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = done });
    }

    internal static void ValidateCopy(MethodDefinition source, MethodDefinition target, Mapper mapper)
    {
        int SourceIndex(Instruction? instruction) => instruction == null ? -1 : source.Body.Instructions.IndexOf(instruction);
        int TargetIndex(Instruction? instruction) => instruction == null ? -1 : target.Body.Instructions.IndexOf(instruction);
        for (int index = 0; index < source.Body.Instructions.Count; index++)
        {
            object? before = source.Body.Instructions[index].Operand, after = target.Body.Instructions[index].Operand;
            if (before is Instruction branch) Require(after is Instruction mapped && SourceIndex(branch) == TargetIndex(mapped), "update branch target clone " + source.FullName);
            if (before is Instruction[] branches) Require(after is Instruction[] mapped && branches.Select(SourceIndex).SequenceEqual(mapped.Select(TargetIndex)), "update switch target clone " + source.FullName);
            if (before is VariableDefinition variable) Require(after is VariableDefinition mapped && variable.Index == mapped.Index, "update local alias clone " + source.FullName);
            if (before is ParameterDefinition parameter) Require(after is ParameterDefinition mapped && parameter.Index + (source.HasThis ? 1 : 0) == mapped.Index, "update parameter alias clone " + source.FullName);
        }
        for (int index = 0; index < source.Body.Variables.Count; index++)
            Require(mapper.Type(source.Body.Variables[index].VariableType).FullName == target.Body.Variables[index].VariableType.FullName, "update local type clone " + source.FullName);
        for (int index = 0; index < source.Body.ExceptionHandlers.Count; index++)
        {
            var before = source.Body.ExceptionHandlers[index]; var after = target.Body.ExceptionHandlers[index];
            Require(before.HandlerType == after.HandlerType && SourceIndex(before.TryStart) == TargetIndex(after.TryStart) && SourceIndex(before.TryEnd) == TargetIndex(after.TryEnd) && SourceIndex(before.HandlerStart) == TargetIndex(after.HandlerStart) && SourceIndex(before.HandlerEnd) == TargetIndex(after.HandlerEnd) && SourceIndex(before.FilterStart) == TargetIndex(after.FilterStart) && (before.CatchType == null ? after.CatchType == null : mapper.Type(before.CatchType).FullName == after.CatchType?.FullName), "update exception region clone " + source.FullName);
        }
    }

    internal sealed class Mapper : IDisposable
    {
        readonly AssemblyDefinition baseline, candidate;
        readonly ModuleDefinition module;
        readonly TypeDefinition runtime;
        readonly Dictionary<string, TypeDefinition> hosts = new(StringComparer.Ordinal);
        readonly Dictionary<string, MethodDefinition> methods = new(StringComparer.Ordinal);
        readonly Dictionary<string, FieldDefinition> fields = new(StringComparer.Ordinal);
        readonly Dictionary<string, AssemblyDefinition> dependencies = new(StringComparer.Ordinal);
        readonly MethodReference dispatch;
        internal Mapper(AssemblyDefinition baseline, AssemblyDefinition candidate, ModuleDefinition module, TypeDefinition runtime)
        {
            this.baseline = baseline; this.candidate = candidate; this.module = module; this.runtime = runtime;
            dispatch = module.ImportReference(typeof(UpdateFixture).GetMethod(nameof(UpdateFixture.Dispatch))!);
        }
        public void Dispose() { foreach (var dependency in dependencies.Values) dependency.Dispose(); }
        internal TypeDefinition FixtureType(TypeReference source, bool valueType)
        {
            string key = Qualified(source);
            Require(!hosts.ContainsKey(key), "duplicate explicit root fixture type " + key);
            var target = new TypeDefinition("Probe.UpdateHost", "T" + Sha(System.Text.Encoding.UTF8.GetBytes(key))[..20], TA.Public | (valueType ? TA.Sealed | TA.SequentialLayout : TA.Class), valueType ? module.ImportReference(typeof(ValueType)) : module.TypeSystem.Object);
            hosts.Add(key, target); HostNames[source.FullName] = target.FullName; module.Types.Add(target);
            return target;
        }
        internal void ImplementDisposable(TypeDefinition owner, MethodReference source)
        {
            owner.Interfaces.Add(new InterfaceImplementation(module.ImportReference(typeof(IDisposable))));
            var method = new MethodDefinition("Dispose", MA.Public | MA.Virtual | MA.Final | MA.NewSlot | MA.HideBySig, module.TypeSystem.Void);
            owner.Methods.Add(method);
            string key = Qualified(source); BoundaryNames[source.FullName] = key;
            EmitBoundary(method, key);
        }
        static string Scope(TypeReference type)
        {
            while (type is TypeSpecification specification) type = specification.ElementType;
            return type.Scope is ModuleDefinition m ? m.Assembly.Name.FullName : type.Scope.ToString()!;
        }
        internal static string Qualified(MemberReference member)
        {
            if (member is TypeReference type) return TypeIdentity(type);
            string owner = TypeIdentity(member.DeclaringType);
            if (member is FieldReference field) return owner + "::" + field.Name + "|type=" + TypeIdentity(field.FieldType);
            var method = (MethodReference)member;
            return owner + "::" + method.Name + "|return=" + TypeIdentity(method.ReturnType) + "|parameters=" + string.Join(";", method.Parameters.Select(p => TypeIdentity(p.ParameterType))) + "|this=" + method.HasThis + "|explicit=" + method.ExplicitThis + "|cc=" + method.CallingConvention + "|arity=" + method.GenericParameters.Count + (method is GenericInstanceMethod generic ? "|arguments=" + string.Join(";", generic.GenericArguments.Select(TypeIdentity)) : "");
        }
        static string TypeIdentity(TypeReference type)
        {
            if (type is GenericParameter parameter) return (parameter.Type == GenericParameterType.Method ? "!!" : "!") + parameter.Position;
            if (type is ArrayType array) return TypeIdentity(array.ElementType) + "[" + string.Join(",", array.Dimensions.Select(d => d.ToString())) + "]";
            if (type is ByReferenceType reference) return TypeIdentity(reference.ElementType) + "&";
            if (type is PointerType pointer) return TypeIdentity(pointer.ElementType) + "*";
            if (type is GenericInstanceType generic) return TypeIdentity(generic.ElementType) + "<" + string.Join(",", generic.GenericArguments.Select(TypeIdentity)) + ">";
            if (type is OptionalModifierType optional) return TypeIdentity(optional.ElementType) + " modopt(" + TypeIdentity(optional.ModifierType) + ")";
            if (type is RequiredModifierType required) return TypeIdentity(required.ElementType) + " modreq(" + TypeIdentity(required.ModifierType) + ")";
            return "[" + Scope(type) + "]" + type.FullName;
        }
        static bool SystemType(TypeReference type) => type.GetElementType().Namespace == "System" || type.GetElementType().Namespace.StartsWith("System.", StringComparison.Ordinal);
        TypeDefinition Definition(TypeReference reference)
        {
            var element = reference.GetElementType();
            string assemblyName = element.Scope is ModuleDefinition own ? own.Assembly.Name.Name : ((AssemblyNameReference)element.Scope).Name;
            if (assemblyName == baseline.Name.Name) return Types(baseline.MainModule).Single(t => t.FullName == element.FullName);
            if (!dependencies.TryGetValue(assemblyName, out var assembly))
            {
                var paths = new[] { Path.GetDirectoryName(baseline.MainModule.FileName), Path.GetDirectoryName(candidate.MainModule.FileName) }.Distinct().Select(dir => Path.Combine(dir!, assemblyName + ".dll"));
                string? path = paths.FirstOrDefault(File.Exists);
                Require(path != null, "update fixture needs pinned dependency metadata " + assemblyName);
                assembly = AssemblyDefinition.ReadAssembly(path!); dependencies.Add(assemblyName, assembly);
            }
            return Types(assembly.MainModule).Single(t => t.FullName == element.FullName);
        }
        TypeReference Substitute(TypeReference type, TypeReference owner, GenericInstanceMethod? method = null)
        {
            if (type is GenericParameter parameter)
            {
                if (parameter.Type == GenericParameterType.Method && method != null) return method.GenericArguments[parameter.Position];
                if (parameter.Type == GenericParameterType.Type && owner is GenericInstanceType generic) return generic.GenericArguments[parameter.Position];
                return type;
            }
            if (type is ArrayType array) return new ArrayType(Substitute(array.ElementType, owner, method), array.Rank);
            if (type is ByReferenceType reference) return new ByReferenceType(Substitute(reference.ElementType, owner, method));
            if (type is PointerType pointer) return new PointerType(Substitute(pointer.ElementType, owner, method));
            if (type is GenericInstanceType instance)
            {
                var result = new GenericInstanceType(instance.ElementType);
                foreach (var argument in instance.GenericArguments) result.GenericArguments.Add(Substitute(argument, owner, method));
                return result;
            }
            return type;
        }
        internal TypeReference Type(TypeReference source)
        {
            if (source is GenericParameter) return source;
            if (source is ArrayType array)
            {
                var result = new ArrayType(Type(array.ElementType), array.Rank);
                for (int i = 0; i < array.Rank; i++) result.Dimensions[i] = new ArrayDimension(array.Dimensions[i].LowerBound, array.Dimensions[i].UpperBound);
                return result;
            }
            if (source is ByReferenceType reference) return new ByReferenceType(Type(reference.ElementType));
            if (source is PointerType pointer) return new PointerType(Type(pointer.ElementType));
            if (source is OptionalModifierType optional) return new OptionalModifierType(Type(optional.ModifierType), Type(optional.ElementType));
            if (source is RequiredModifierType required) return new RequiredModifierType(Type(required.ModifierType), Type(required.ElementType));
            if (hosts.TryGetValue(Qualified(source), out var configured)) return configured;
            if (SystemType(source))
            {
                if (source is GenericInstanceType generic)
                {
                    var result = new GenericInstanceType(Type(generic.ElementType));
                    foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(Type(argument));
                    return result;
                }
                if (source.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr) return Primitive(module, source.FullName);
                return module.ImportReference(source);
            }
            string key = Qualified(source);
            if (hosts.TryGetValue(key, out var mapped)) return mapped;
            var definition = Definition(source);
            string name = "T" + Sha(System.Text.Encoding.UTF8.GetBytes(key))[..20];
            var attributes = TA.Public | (definition.IsEnum ? TA.Sealed : definition.IsValueType ? TA.Sealed | TA.SequentialLayout : TA.Class);
            mapped = new TypeDefinition("Probe.UpdateHost", name, attributes, module.TypeSystem.Object);
            hosts.Add(key, mapped); module.Types.Add(mapped); HostNames[source.FullName] = mapped.FullName;
            if (definition.IsEnum)
            {
                mapped.BaseType = module.ImportReference(typeof(Enum));
                mapped.Fields.Add(new FieldDefinition("value__", FA.Public | FA.SpecialName | FA.RTSpecialName, Type(definition.Fields.Single(f => f.Name == "value__").FieldType)));
            }
            else if (definition.IsValueType) mapped.BaseType = module.ImportReference(typeof(ValueType));
            else if (definition.BaseType != null && !SystemType(definition.BaseType)) mapped.BaseType = Type(Substitute(definition.BaseType, source));
            return mapped;
        }
        internal object Member(object source)
        {
            if (source is TypeReference type) return Type(type);
            if (source is FieldReference field)
            {
                if (SystemType(field.DeclaringType) && !hosts.ContainsKey(Qualified(field.DeclaringType))) return new FieldReference(field.Name, Type(field.FieldType), Type(field.DeclaringType));
                string key = Qualified(field);
                if (fields.TryGetValue(key, out var found)) return found;
                var owner = (TypeDefinition)Type(field.DeclaringType);
                var original = Definition(field.DeclaringType).Fields.Single(f => f.Name == field.Name);
                found = owner.Fields.FirstOrDefault(f => f.Name == field.Name) ?? new FieldDefinition(field.Name, FA.Public | (original.IsStatic ? FA.Static : 0), Type(Substitute(field.FieldType, field.DeclaringType)));
                if (!owner.Fields.Contains(found)) owner.Fields.Add(found);
                fields.Add(key, found); return found;
            }
            if (source is MethodReference method)
            {
                if (method.DeclaringType.FullName == "Terraria.NXUpdateProfile56")
                {
                    Require(Scope(method.DeclaringType) == candidate.Name.FullName, "update helper source assembly identity " + Qualified(method));
                    var matches = runtime.Methods.Where(m => m.Name == method.Name && m.HasThis == method.HasThis && m.ExplicitThis == method.ExplicitThis && m.CallingConvention == method.CallingConvention && m.GenericParameters.Count == method.GenericParameters.Count && m.ReturnType.FullName == Type(method.ReturnType).FullName && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => Type(p.ParameterType).FullName))).ToArray();
                    Require(matches.Length == 1, "update exact helper signature " + Qualified(method)); return matches[0];
                }
                if ((SystemType(method.DeclaringType) && !hosts.ContainsKey(Qualified(method.DeclaringType))) || method.DeclaringType is ArrayType)
                {
                    if (method is GenericInstanceMethod generic)
                    {
                        var result = new GenericInstanceMethod((MethodReference)Member(generic.ElementMethod));
                        foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(Type(argument));
                        return result;
                    }
                    var resultMethod = new MethodReference(method.Name, Type(method.ReturnType), Type(method.DeclaringType)) { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
                    foreach (var parameter in method.Parameters) resultMethod.Parameters.Add(new ParameterDefinition(Type(parameter.ParameterType)));
                    foreach (var parameter in method.GenericParameters) resultMethod.GenericParameters.Add(new GenericParameter(parameter.Name, resultMethod));
                    return resultMethod;
                }
                string key = Qualified(method);
                if (methods.TryGetValue(key, out var existing)) return existing;
                var owner = (TypeDefinition)Type(method.DeclaringType);
                string name = method.Name == ".ctor" ? ".ctor" : "M" + Sha(System.Text.Encoding.UTF8.GetBytes(key))[..20];
                var attributes = MA.Public | (method.HasThis ? 0 : MA.Static) | (method.Name == ".ctor" ? MA.SpecialName | MA.RTSpecialName : 0);
                var target = new MethodDefinition(name, attributes, Type(Substitute(method.ReturnType, method.DeclaringType, method as GenericInstanceMethod)));
                foreach (var parameter in method.Parameters) target.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, Type(Substitute(parameter.ParameterType, method.DeclaringType, method as GenericInstanceMethod))));
                Require(!target.ReturnType.ContainsGenericParameter && target.Parameters.All(p => !p.ParameterType.ContainsGenericParameter), "update open boundary signature " + key);
                owner.Methods.Add(target); methods.Add(key, target); BoundaryNames[method.FullName] = key;
                EmitBoundary(target, key);
                return target;
            }
            return source;
        }
        void EmitBoundary(MethodDefinition method, string key)
        {
            var il = method.Body.GetILProcessor();
            var arguments = new VariableDefinition(new ArrayType(module.TypeSystem.Object));
            var result = new VariableDefinition(module.TypeSystem.Object);
            method.Body.Variables.Add(arguments); method.Body.Variables.Add(result); method.Body.InitLocals = true;
            int receiver = method.HasThis ? 1 : 0;
            il.Append(Instruction.Create(OpCodes.Ldc_I4, method.Parameters.Count + receiver));
            il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
            il.Append(Instruction.Create(OpCodes.Stloc, arguments));
            for (int index = 0; index < method.Parameters.Count + receiver; index++)
            {
                il.Append(Instruction.Create(OpCodes.Ldloc, arguments)); il.Append(Instruction.Create(OpCodes.Ldc_I4, index));
                TypeReference argumentType;
                if (receiver != 0 && index == 0)
                {
                    il.Append(Instruction.Create(OpCodes.Ldarg_0)); argumentType = method.DeclaringType;
                    if (argumentType.IsValueType) il.Append(Instruction.Create(OpCodes.Ldobj, argumentType));
                }
                else
                {
                    var parameter = method.Parameters[index - receiver]; il.Append(Instruction.Create(OpCodes.Ldarg, parameter)); argumentType = parameter.ParameterType;
                    if (argumentType is ByReferenceType byReference) { argumentType = byReference.ElementType; il.Append(Instruction.Create(OpCodes.Ldobj, argumentType)); }
                }
                Require(argumentType is not PointerType, "update pointer boundary cannot be boxed " + key);
                if (argumentType.IsValueType || argumentType is GenericParameter) il.Append(Instruction.Create(OpCodes.Box, argumentType));
                il.Append(Instruction.Create(OpCodes.Stelem_Ref));
            }
            var start = Instruction.Create(OpCodes.Ldstr, key); il.Append(start);
            il.Append(Instruction.Create(OpCodes.Ldloc, arguments)); il.Append(Instruction.Create(OpCodes.Call, dispatch));
            il.Append(Instruction.Create(OpCodes.Stloc, result));
            var done = Instruction.Create(OpCodes.Ldloc, result); il.Append(Instruction.Create(OpCodes.Leave, done));
            var cleanup = Instruction.Create(OpCodes.Nop); il.Append(cleanup);
            for (int index = 0; index < method.Parameters.Count + receiver; index++)
            {
                TypeReference type;
                if (receiver != 0 && index == 0)
                {
                    if (!method.DeclaringType.IsValueType) continue;
                    type = method.DeclaringType; il.Append(Instruction.Create(OpCodes.Ldarg_0));
                }
                else
                {
                    var parameter = method.Parameters[index - receiver];
                    if (parameter.ParameterType is not ByReferenceType reference) continue;
                    type = reference.ElementType; il.Append(Instruction.Create(OpCodes.Ldarg, parameter));
                }
                il.Append(Instruction.Create(OpCodes.Ldloc, arguments)); il.Append(Instruction.Create(OpCodes.Ldc_I4, index)); il.Append(Instruction.Create(OpCodes.Ldelem_Ref));
                il.Append(Instruction.Create(type.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, type)); il.Append(Instruction.Create(OpCodes.Stobj, type));
            }
            il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(done);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = start, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = done });
            if (method.ReturnType.MetadataType == MetadataType.Void) il.Append(Instruction.Create(OpCodes.Pop));
            else il.Append(Instruction.Create(method.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, method.ReturnType));
            il.Append(Instruction.Create(OpCodes.Ret));
        }
    }

}
