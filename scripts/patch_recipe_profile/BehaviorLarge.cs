using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class BehaviorLarge
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly (int Token, string Name, int Metric)[] Targets = { (0x06000f84, "Inventory", 2), (0x06001f3a, "List", 9), (0x06001f42, "Grid", 9), (0x06002120, "Slot", 10) };
    internal static readonly Dictionary<string, string> HostNames = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, string> BoundaryNames = new(StringComparer.Ordinal);

    internal static void Add(AssemblyDefinition baseline, AssemblyDefinition candidate, ModuleDefinition probe, TypeDefinition runtime, List<object> receipts)
    {
        HostNames.Clear(); BoundaryNames.Clear();
        var large = new TypeDefinition("Probe", "Large", TA.Public | TA.Abstract | TA.Sealed, probe.TypeSystem.Object);
        probe.Types.Add(large);
        using var mapper = new Mapper(baseline, candidate, probe, runtime);
        foreach (var (token, name, _) in Targets)
        {
            var original = (MethodDefinition)baseline.MainModule.LookupToken(token);
            foreach (var (assembly, prefix) in new[] { (baseline, "Baseline"), (candidate, "Patched") })
            {
                var source = Types(assembly.MainModule).SelectMany(t => t.Methods).Single(m => m.FullName == original.FullName);
                var target = new MethodDefinition(prefix + name, MA.Public | MA.Static, mapper.Type(source.ReturnType));
                if (source.HasThis) target.Parameters.Add(new ParameterDefinition("receiver", Mono.Cecil.ParameterAttributes.None, mapper.Type(source.DeclaringType)));
                foreach (var parameter in source.Parameters)
                    target.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, mapper.Type(parameter.ParameterType)));
                large.Methods.Add(target);
                Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)mapper.Type, (Func<object, object>)mapper.Member, source.HasThis ? 1 : 0);
                Widen(target);
                Require(source.Body.Instructions.Count == target.Body.Instructions.Count && source.Body.Variables.Count == target.Body.Variables.Count && source.Body.ExceptionHandlers.Count == target.Body.ExceptionHandlers.Count, "large full-body clone shape " + source.FullName);
                ValidateCopy(source, target, mapper);
                var bindings = source.Body.Instructions.Select((instruction, index) => new { instruction, index })
                    .Where(x => x.instruction.Operand is MemberReference)
                    .Select(x => new { index = x.index, opcode = x.instruction.OpCode.Name, source = Mapper.Qualified((MemberReference)x.instruction.Operand), fixture = Mapper.Qualified((MemberReference)target.Body.Instructions[x.index].Operand) }).ToArray();
                receipts.Add(new { source = source.FullName, sourceMvid = source.Module.Mvid, sourceToken = source.MetadataToken.ToInt32(), sourceBody = Fingerprint(source), fixture = target.FullName, mappedBody = Fingerprint(target), instructions = source.Body.Instructions.Count, locals = source.Body.Variables.Count, handlers = source.Body.ExceptionHandlers.Count, kind = "ActualLargeDrawingBody", bindings });
                if (prefix == "Patched")
                {
                    var mutant = new MethodDefinition("MissingExit" + name, MA.Public | MA.Static, target.ReturnType);
                    foreach (var p in target.Parameters) mutant.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                    large.Methods.Add(mutant);
                    Copy(target, mutant, t => t, value => value);
                    var exit = mutant.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType == runtime && m.Name == "Exit");
                    exit.OpCode = OpCodes.Pop; exit.Operand = null;
                    mutant.Body.GetILProcessor().InsertAfter(exit, Instruction.Create(OpCodes.Pop));
                    Widen(mutant);
                    receipts.Add(new { source = source.FullName, sourceMvid = source.Module.Mvid, sourceBody = Fingerprint(source), fixture = mutant.FullName, mappedBody = Fingerprint(mutant), instructions = mutant.Body.Instructions.Count, locals = mutant.Body.Variables.Count, handlers = mutant.Body.ExceptionHandlers.Count, kind = "MissingActualEnvelopeExitMutant" });
                }
            }
        }
        BehaviorRoots.Add(baseline, candidate, probe, runtime, receipts, mapper);
    }

    internal static void ValidateCopy(MethodDefinition source, MethodDefinition target, Mapper mapper)
    {
        int SourceIndex(Instruction? instruction) => instruction == null ? -1 : source.Body.Instructions.IndexOf(instruction);
        int TargetIndex(Instruction? instruction) => instruction == null ? -1 : target.Body.Instructions.IndexOf(instruction);
        for (int index = 0; index < source.Body.Instructions.Count; index++)
        {
            object? before = source.Body.Instructions[index].Operand, after = target.Body.Instructions[index].Operand;
            if (before is Instruction branch) Require(after is Instruction mapped && SourceIndex(branch) == TargetIndex(mapped), "large branch target clone " + source.FullName);
            if (before is Instruction[] branches) Require(after is Instruction[] mapped && branches.Select(SourceIndex).SequenceEqual(mapped.Select(TargetIndex)), "large switch target clone " + source.FullName);
            if (before is VariableDefinition variable) Require(after is VariableDefinition mapped && variable.Index == mapped.Index, "large local alias clone " + source.FullName);
            if (before is ParameterDefinition parameter) Require(after is ParameterDefinition mapped && parameter.Index + (source.HasThis ? 1 : 0) == mapped.Index, "large parameter alias clone " + source.FullName);
        }
        for (int index = 0; index < source.Body.Variables.Count; index++)
            Require(mapper.Type(source.Body.Variables[index].VariableType).FullName == target.Body.Variables[index].VariableType.FullName, "large local type clone " + source.FullName);
        for (int index = 0; index < source.Body.ExceptionHandlers.Count; index++)
        {
            var before = source.Body.ExceptionHandlers[index]; var after = target.Body.ExceptionHandlers[index];
            Require(before.HandlerType == after.HandlerType && SourceIndex(before.TryStart) == TargetIndex(after.TryStart) && SourceIndex(before.TryEnd) == TargetIndex(after.TryEnd) && SourceIndex(before.HandlerStart) == TargetIndex(after.HandlerStart) && SourceIndex(before.HandlerEnd) == TargetIndex(after.HandlerEnd) && SourceIndex(before.FilterStart) == TargetIndex(after.FilterStart) && (before.CatchType == null ? after.CatchType == null : mapper.Type(before.CatchType).FullName == after.CatchType?.FullName), "large exception region clone " + source.FullName);
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
            dispatch = module.ImportReference(typeof(BehaviorLargeFixture).GetMethod(nameof(BehaviorLargeFixture.Dispatch))!);
        }
        public void Dispose() { foreach (var dependency in dependencies.Values) dependency.Dispose(); }
        internal TypeDefinition FixtureType(TypeReference source, bool valueType)
        {
            string key = Qualified(source);
            Require(!hosts.ContainsKey(key), "duplicate explicit root fixture type " + key);
            var target = new TypeDefinition("Probe.LargeHost", "T" + Sha(System.Text.Encoding.UTF8.GetBytes(key))[..20], TA.Public | (valueType ? TA.Sealed | TA.SequentialLayout : TA.Class), valueType ? module.ImportReference(typeof(ValueType)) : module.TypeSystem.Object);
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
                Require(path != null, "large fixture needs pinned dependency metadata " + assemblyName);
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
            mapped = new TypeDefinition("Probe.LargeHost", name, attributes, module.TypeSystem.Object);
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
                if (SystemType(field.DeclaringType)) return new FieldReference(field.Name, Type(field.FieldType), Type(field.DeclaringType));
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
                if (method.DeclaringType.FullName == "Terraria.NXRecipeProfile55")
                {
                    Require(Scope(method.DeclaringType) == candidate.Name.FullName, "large helper source assembly identity " + Qualified(method));
                    var matches = runtime.Methods.Where(m => m.Name == method.Name && m.HasThis == method.HasThis && m.ExplicitThis == method.ExplicitThis && m.CallingConvention == method.CallingConvention && m.GenericParameters.Count == method.GenericParameters.Count && m.ReturnType.FullName == Type(method.ReturnType).FullName && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => Type(p.ParameterType).FullName))).ToArray();
                    Require(matches.Length == 1, "large exact helper signature " + Qualified(method)); return matches[0];
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
                Require(!target.ReturnType.ContainsGenericParameter && target.Parameters.All(p => !p.ParameterType.ContainsGenericParameter), "large open boundary signature " + key);
                owner.Methods.Add(target); methods.Add(key, target); BoundaryNames[method.FullName] = key;
                EmitBoundary(target, key);
                return target;
            }
            return source;
        }
        void EmitBoundary(MethodDefinition method, string key)
        {
            var il = method.Body.GetILProcessor();
            il.Append(Instruction.Create(OpCodes.Ldstr, key));
            int receiver = method.HasThis ? 1 : 0;
            il.Append(Instruction.Create(OpCodes.Ldc_I4, method.Parameters.Count + receiver));
            il.Append(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
            for (int index = 0; index < method.Parameters.Count + receiver; index++)
            {
                il.Append(Instruction.Create(OpCodes.Dup)); il.Append(Instruction.Create(OpCodes.Ldc_I4, index));
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
                Require(argumentType is not PointerType, "large pointer boundary cannot be boxed " + key);
                if (argumentType.IsValueType || argumentType is GenericParameter) il.Append(Instruction.Create(OpCodes.Box, argumentType));
                il.Append(Instruction.Create(OpCodes.Stelem_Ref));
            }
            il.Append(Instruction.Create(OpCodes.Call, dispatch));
            if (method.ReturnType.MetadataType == MetadataType.Void) il.Append(Instruction.Create(OpCodes.Pop));
            else il.Append(Instruction.Create(method.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, method.ReturnType));
            il.Append(Instruction.Create(OpCodes.Ret));
        }
    }

    internal static object Run(Assembly probeAssembly, System.Type runtime)
    {
        var session = new Session(probeAssembly, runtime);
        var large = session.Run();
        var roots = BehaviorRoots.Run(probeAssembly, runtime);
        return new { passed = true, large, roots };
    }

    sealed class Session
    {
        readonly Assembly assembly;
        readonly System.Type runtime, large;
        readonly List<object> scenarios = new();
        readonly List<object> mutants = new();
        readonly Dictionary<string, object> instances = new(StringComparer.Ordinal);
        internal Session(Assembly assembly, System.Type runtime) { this.assembly = assembly; this.runtime = runtime; large = assembly.GetType("Probe.Large", true)!; }
        object? R(string name, params object?[] args) => Call(runtime.GetMethod(name, Flags)!, args);
        object? Get(string name) => runtime.GetField(name, Flags)!.GetValue(null);
        void Set(string name, object? value) => runtime.GetField(name, Flags)!.SetValue(null, value);
        static object? Call(MethodInfo method, object?[] args)
        {
            try { return method.Invoke(null, args); }
            catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        System.Type Host(string name) => assembly.GetType(HostNames[name], true)!;
        object Instance(string name)
        {
            if (!instances.TryGetValue(name, out var value)) instances.Add(name, value = RuntimeHelpers.GetUninitializedObject(Host(name)));
            return value;
        }
        void Field(string type, string name, object? value) => Host(type).GetField(name, Flags)!.SetValue(null, value);
        object? Field(string type, string name) => Host(type).GetField(name, Flags)!.GetValue(null);
        string Boundary(string signature) => BoundaryNames[signature];
        void Configure(string signature, Func<object?[], object?> implementation) => BehaviorLargeFixture.Configured.Add(Boundary(signature), implementation);
        long Calls(int id)
        {
            var metric = ((Array)Get("FrameMetrics")!).GetValue(id)!;
            return (long)metric.GetType().GetField("Calls", Flags)!.GetValue(metric)!;
        }
        void Reset()
        {
            foreach (var field in runtime.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral && !f.IsInitOnly)) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
            foreach (var type in assembly.GetTypes().Where(t => t.Namespace == "Probe.LargeHost"))
                foreach (var field in type.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral)) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
            instances.Clear(); BehaviorLargeFixture.Reset();
            Field("Terraria.Main", "screenWidth", 1000); Field("Terraria.Main", "screenHeight", 800);
            Field("Terraria.Recipe", "maxRecipes", 0);
            var players = Array.CreateInstance(Host("Terraria.Player"), 1); players.SetValue(Instance("Terraria.Player"), 0); Field("Terraria.Main", "player", players);
        }
        object?[] Arguments(MethodInfo method) => method.GetParameters().Select(p => p.ParameterType.IsArray ? Array.CreateInstance(p.ParameterType.GetElementType()!, 1) : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : p.Name == "receiver" ? Instance(p.ParameterType == Host("Terraria.Main") ? "Terraria.Main" : "Terraria.UI.CraftingUI") : null).ToArray();
        string State() => string.Join("|", new[] { "CRAFT_CurrentRecipeBig", "CRAFT_CurrentRecipeSmall", "CRAFT_IconsPerRow", "CRAFT_IconsPerColumn" }.Select(name => Field("Terraria.UI.Gamepad.UILinkPointNavigator/Shortcuts", name))) + "|" + Field("Terraria.Main", "inventoryScale");
        void Setup(string name, string mode)
        {
            if (name == "Inventory") Configure("System.Void Terraria.Main::DrawPVPIcons()", _ => throw BehaviorLargeFixture.Failure);
            else if (name == "Slot") Configure("Microsoft.Xna.Framework.Color Microsoft.Xna.Framework.Color::get_White()", _ => throw BehaviorLargeFixture.Failure);
            else
            {
                Configure("System.Int32 Terraria.UI.CraftingUI::get_numAvailableRecipes()", _ => mode == "first-throw" && name == "List" ? throw BehaviorLargeFixture.Failure : 0);
                Configure("System.Void Terraria.UI.CraftingUI::set_inventoryScale(System.Single)", args =>
                {
                    Field("Terraria.Main", "inventoryScale", args[0]);
                    if (mode == "first-throw" && name == "Grid") throw BehaviorLargeFixture.Failure;
                    return null;
                });
                if (name == "List") Configure("System.Void Terraria.UI.CraftingUI::AdjustRecipeOffsets()", args =>
                {
                    Require(ReferenceEquals(args[0], Instance("Terraria.UI.CraftingUI")), "large list receiver identity");
                    if (mode == "later-throw") throw BehaviorLargeFixture.Failure;
                    return null;
                });
                else
                {
                    Configure("System.Int32 Terraria.UI.CraftingUI::get_recStart()", _ => 0);
                    Configure("System.Void Terraria.UI.CraftingUI::set_recStart(System.Int32)", _ => mode == "later-throw" ? throw BehaviorLargeFixture.Failure : null);
                }
            }
        }
        (string Trace, bool Rejected, int Boundaries) One(string prefix, string name, int metric, string mode, bool mutant)
        {
            Reset(); Setup(name, mode);
            bool patched = prefix != "Baseline", throws = mode != "empty-return";
            int entered = 0;
            R("InitializeTiming"); Set("SampleSelected", true); R("BeginSample"); int cookie = (int)R("Enter", 1)!;
            Require(cookie > 0 && (bool)Get("Active")! && (int)Get("timingDepth")! == 2, "large actual runtime activation");
            BehaviorLargeFixture.Observe = (_, _) =>
            {
                Require((bool)Get("Active")! && (int)Get("timingDepth")! == (patched ? 3 : 2), "large envelope active at external boundary " + prefix + name);
                entered++;
            };
            Exception? error = null;
            try { Call(large.GetMethod(prefix + name, Flags)!, Arguments(large.GetMethod(prefix + name, Flags)!)); }
            catch (Exception failure) { error = failure; }
            Require(throws ? ReferenceEquals(error, BehaviorLargeFixture.Failure) : error == null, "large original failure identity/return " + prefix + name + ": " + error);
            Require(entered > 0, "large scenario executed no original boundary " + name);
            bool clean = (int)Get("timingDepth")! == 2;
            if (!mutant) Require(clean, "large actual envelope finally cleanup " + name);
            bool aborted = (bool)Get("FrameScopeAborted")!;
            if (!mutant) Require(aborted == (patched && throws), "large completion flag/abort preservation " + name);
            R("Exit", cookie, true); R("EndSample", true);
            Require((int)Get("timingDepth")! == 0 && !(bool)Get("Active")!, "large final no active scope leak");
            bool valid = (bool)R("ValidateSample")!;
            if (!mutant)
            {
                Require(!(bool)Get("FrameTimingInvalid")!, "large no timing corruption " + name);
                Require(valid == !(patched && throws), "large aborted sample cannot validate " + name);
                Require(Calls(metric) == (patched && !throws ? 1 : 0), "large actual metric completion calls " + name);
            }
            return (State() + "\n" + string.Join("\n", BehaviorLargeFixture.Events), !clean && !valid, entered);
        }
        internal object Run()
        {
            foreach (var (_, name, metric) in Targets)
            {
                var modes = name is "List" or "Grid" ? new[] { "first-throw", "later-throw", "empty-return" } : new[] { "first-throw" };
                foreach (string mode in modes)
                {
                    var baseline = One("Baseline", name, metric, mode, false);
                    var patched = One("Patched", name, metric, mode, false);
                    Require(baseline.Trace == patched.Trace, "large baseline/candidate exact ordered effects and state " + name + "/" + mode);
                    scenarios.Add(new { method = name, scenario = mode, passed = true, observedBoundaryCount = baseline.Boundaries, exactOriginalExceptionIdentity = mode != "empty-return", runtimeScopeActivated = true, finallyBalanced = true, trace = baseline.Trace });
                }
                var mutant = One("MissingExit", name, metric, "first-throw", true);
                Require(mutant.Rejected, "large missing finally mutant escaped " + name);
                mutants.Add(new { method = name, mutation = "suppress actual candidate Exit call while consuming identical arguments", rejected = true, reason = "scope depth leaked before root finish and sample failed validation" });
            }
            BehaviorLargeFixture.Reset();
            return new { passed = true, scenarios, mutants, fixture = "Complete serialized baseline52 and candidate55 drawing IL; named game/graphics host types preserve value/reference kind and source-qualified signature bindings. All unconfigured calls fail with an explicit boundary error. Configured boundaries are deterministic host behavior, not rendering implementations.", untested = new[] { "Inventory after DrawPVPIcons and Slot after Color.White", "Nonempty recipe drawing, texture/font/GPU operations, mouse/gamepad interactions and item-slot rendering", "Original internal branches and exception paths beyond the listed boundary traces", "Hardware/Switch timing, allocation, visual equivalence and performance" } };
        }
    }
}
