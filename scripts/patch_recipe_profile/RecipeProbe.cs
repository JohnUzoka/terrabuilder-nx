using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class RecipeProbe
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static readonly string[] Mutants = { "SkipFilter", "MaterialBeforeFilter", "WrongMaterialResult", "MissingChests", "WrongCollectArgument", "WrongFocusArgument", "LostRepositionWrite", "SwallowedOriginal" };
    internal static readonly (int Token, string Name)[] Targets = {
        (0x060005f2, "Update"), (0x06000602, "Clear"), (0x060005fc, "Collect"), (0x06000601, "Guide"), (0x06000603, "Refocus"), (0x06001f36, "Reposition"), (0x060005f4, "Add") };
    internal static byte[] Build(string baselinePath, string candidatePath, List<object> receipts)
    {
        byte[] runtimeBytes = RuntimeProbe.Build(candidatePath, receipts);
        using var probe = AssemblyDefinition.ReadAssembly(new MemoryStream(runtimeBytes));
        using var baseline = AssemblyDefinition.ReadAssembly(baselinePath);
        using var candidate = AssemblyDefinition.ReadAssembly(candidatePath);
        var module = probe.MainModule;
        probe.Name.Name = "Recipe55ExactBehaviorProbe"; module.Name = probe.Name.Name;
        var runtime = module.Types.Single(t => t.FullName == "Probe.RecipeRuntime"); runtime.Name = "Runtime";
        var hooks = new TypeDefinition("Probe", "Recipes", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
        var hosts = new Dictionary<string, Type> {
            ["Terraria.Main"] = typeof(RecipeMain), ["Terraria.Recipe"] = typeof(RecipeNode), ["Terraria.Player"] = typeof(RecipePlayer), ["Terraria.Item"] = typeof(RecipeItem),
            ["Terraria.Recipe/RequiredItemEntry"] = typeof(RecipeRequired), ["Terraria.UI.CraftingUI"] = typeof(RecipeCrafting),
            ["Terraria.GameContent.UI.NewCraftingUI/RecipeFilter"] = typeof(RecipeFilter), ["Terraria.GameContent.CraftingRequests"] = typeof(RecipeRequests) };
        TypeReference MapType(TypeReference type)
        {
            if (hosts.TryGetValue(type.FullName, out var host))
            {
                Require(type.Scope is ModuleDefinition own && own.Assembly.Name.FullName == baseline.Name.FullName || type.Scope is AssemblyNameReference name && name.FullName == baseline.Name.FullName, "recipe host source scope " + Qualified(type));
                Require(type.IsValueType == host.IsValueType, "recipe host named kind " + Qualified(type)); return module.ImportReference(host);
            }
            if (type.FullName == "Terraria.NXRecipeProfile55") return runtime;
            if (type is GenericParameter) return type;
            if (type is ArrayType array) { var result = new ArrayType(MapType(array.ElementType), array.Rank); for (int i = 0; i < array.Rank; i++) result.Dimensions[i] = new ArrayDimension(array.Dimensions[i].LowerBound, array.Dimensions[i].UpperBound); return result; }
            if (type is ByReferenceType reference) return new ByReferenceType(MapType(reference.ElementType));
            if (type is GenericInstanceType generic) { var result = new GenericInstanceType(MapType(generic.ElementType)); foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(MapType(argument)); return result; }
            Require(type.Namespace == "System" || type.Namespace.StartsWith("System.", StringComparison.Ordinal), "unmapped recipe type " + Qualified(type));
            return type.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr ? Primitive(module, type.FullName) : module.ImportReference(type);
        }
        var copies = new List<(MethodDefinition source, MethodDefinition target, string prefix)>();
        var exact = new Dictionary<string, MethodDefinition>();
        foreach (string prefix in new[] { "Baseline", "Patched" }.Concat(Mutants))
        {
            var sourceAssembly = prefix == "Baseline" ? baseline : candidate;
            foreach (var (token, suffix) in Targets)
            {
                string fullName = ((MethodDefinition)baseline.MainModule.LookupToken(token)).FullName;
                var source = Types(sourceAssembly.MainModule).SelectMany(t => t.Methods).Single(m => m.FullName == fullName);
                var target = new MethodDefinition(prefix + suffix, MA.Public | MA.Static, MapType(source.ReturnType)) { ImplAttributes = source.ImplAttributes };
                if (source.HasThis) target.Parameters.Add(new ParameterDefinition("receiver", Mono.Cecil.ParameterAttributes.None, MapType(source.DeclaringType)));
                foreach (var parameter in source.Parameters) target.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, MapType(parameter.ParameterType)));
                hooks.Methods.Add(target); exact.Add(prefix + ":" + source.FullName, target); copies.Add((source, target, prefix));
            }
        }
        string current = "";
        object MapMember(object value)
        {
            if (value is TypeReference type) return MapType(type);
            if (value is FieldReference field)
            {
                if (hosts.TryGetValue(field.DeclaringType.FullName, out var host))
                {
                    MapType(field.DeclaringType);
                    var match = host.GetField(field.Name, Flags) ?? throw new InvalidDataException("missing recipe field " + Qualified(field));
                    var mapped = module.ImportReference(match);
                    Require(mapped.FieldType.FullName == MapType(field.FieldType).FullName, "recipe field signature " + Qualified(field)); return mapped;
                }
                return new FieldReference(field.Name, MapType(field.FieldType), MapType(field.DeclaringType));
            }
            if (value is GenericInstanceMethod generic) { var mapped = new GenericInstanceMethod((MethodReference)MapMember(generic.ElementMethod)); foreach (var argument in generic.GenericArguments) mapped.GenericArguments.Add(MapType(argument)); return mapped; }
            if (value is MethodReference method)
            {
                if (method.DeclaringType.FullName == "Terraria.NXRecipeProfile55")
                {
                    Require(method.DeclaringType.Scope is ModuleDefinition own && own == candidate.MainModule, "helper source scope");
                    return runtime.Methods.Single(m => m.Name == method.Name && m.HasThis == method.HasThis && m.ReturnType.FullName == MapType(method.ReturnType).FullName && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => MapType(p.ParameterType).FullName)));
                }
                // Keep callvirt itself, including its null receiver behavior. The typed bridge dispatches to the selected full instance-body clone.
                if (method.Name != "VisuallyRepositionRecipes" && exact.TryGetValue(current + ":" + method.FullName, out var clone)) return clone;
                if (hosts.TryGetValue(method.DeclaringType.FullName, out var host))
                {
                    MapType(method.DeclaringType);
                    var matches = host.GetMethods(Flags).Where(m => m.Name == method.Name && m.GetParameters().Select(p => module.ImportReference(p.ParameterType).FullName).SequenceEqual(method.Parameters.Select(p => MapType(p.ParameterType).FullName))).ToArray();
                    Require(matches.Length == 1, "recipe method boundary " + Qualified(method));
                    var mapped = module.ImportReference(matches[0]);
                    Require(mapped.HasThis == method.HasThis && mapped.ReturnType.FullName == MapType(method.ReturnType).FullName, "recipe method signature " + Qualified(method)); return mapped;
                }
                var reference = new MethodReference(method.Name, MapType(method.ReturnType), MapType(method.DeclaringType)) { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
                foreach (var parameter in method.GenericParameters) reference.GenericParameters.Add(new GenericParameter(parameter.Name, reference));
                foreach (var parameter in method.Parameters) reference.Parameters.Add(new ParameterDefinition(MapType(parameter.ParameterType))); return reference;
            }
            return value;
        }
        foreach (var (source, target, prefix) in copies)
        {
            current = prefix;
            Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)MapType, (Func<object, object>)MapMember, source.HasThis ? 1 : 0);
            target.Body.MaxStackSize = source.Body.MaxStackSize;
            Validate(source, target, MapType);
            receipts.Add(new { kind = "ExactRecipeSourceClone", source = source.FullName, sourceMvid = source.Module.Mvid, sourceToken = source.MetadataToken.ToInt32(), sourceBody = Fingerprint(source), fixture = target.FullName,
                prefix, signature = Qualified(source), parameters = source.Parameters.Select(p => new { p.Name, attributes = p.Attributes.ToString(), type = Qualified(p.ParameterType) }),
                bindings = source.Body.Instructions.Select((instruction, index) => new { instruction, index }).Where(p => p.instruction.Operand is MemberReference).Select(p => new { p.index, opcode = p.instruction.OpCode.Name, source = Qualified((MemberReference)p.instruction.Operand), mapped = Qualified((MemberReference)target.Body.Instructions[p.index].Operand) }).ToArray(),
                controlFlow = source.Body.Instructions.Select((instruction, index) => new { instruction, index }).Where(p => p.instruction.Operand is Instruction || p.instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw).Select(p => new { p.index, opcode = p.instruction.OpCode.Name, target = p.instruction.Operand is Instruction branch ? source.Body.Instructions.IndexOf(branch) : -1 }).ToArray(),
                instructions = source.Body.Instructions.Count, locals = source.Body.Variables.Count, handlers = source.Body.ExceptionHandlers.Count });
        }
        foreach (string mutant in Mutants) Mutate(mutant, hooks);
        BehaviorLarge.Add(baseline, candidate, module, runtime, receipts);
        foreach (var method in Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody)) Widen(method);
        var fingerprints = Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody).ToDictionary(m => m.FullName, Fingerprint);
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fingerprints.Select(p => p.Key + ":" + p.Value)))).AsSpan(0, 16));
        using var stream = new MemoryStream(); probe.Write(stream, new WriterParameters { Timestamp = 0 }); byte[] bytes = stream.ToArray();
        using var serialized = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var method in Types(serialized.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody)) Require(fingerprints[method.FullName] == Fingerprint(method), "serialized recipe probe differs " + method.FullName);
        receipts.Add(new { kind = "SerializedExecutableBodies", bodies = fingerprints }); return bytes;
    }
    static void Validate(MethodDefinition source, MethodDefinition target, Func<TypeReference, TypeReference> map)
    {
        Require(source.Body.InitLocals == target.Body.InitLocals && source.Body.MaxStackSize == target.Body.MaxStackSize && source.Body.Instructions.Count == target.Body.Instructions.Count && source.Body.Variables.Count == target.Body.Variables.Count && source.Body.ExceptionHandlers.Count == target.Body.ExceptionHandlers.Count, "recipe full-body shape " + source.FullName);
        for (int i = 0; i < source.Body.Instructions.Count; i++)
        {
            var a = source.Body.Instructions[i]; var b = target.Body.Instructions[i]; Require(a.OpCode == b.OpCode, "recipe opcode " + source.FullName);
            if (a.Operand is Instruction branch) Require(b.Operand is Instruction next && source.Body.Instructions.IndexOf(branch) == target.Body.Instructions.IndexOf(next), "recipe branch mapping");
            if (a.Operand is VariableDefinition variable) Require(b.Operand is VariableDefinition mapped && variable.Index == mapped.Index, "recipe local alias");
            if (a.Operand is ParameterDefinition parameter) Require(b.Operand is ParameterDefinition mapped && parameter.Index + (source.HasThis ? 1 : 0) == mapped.Index, "recipe parameter alias");
        }
        for (int i = 0; i < source.Body.Variables.Count; i++) Require(map(source.Body.Variables[i].VariableType).FullName == target.Body.Variables[i].VariableType.FullName, "recipe local type");
        int At(MethodDefinition m, Instruction? i) => i == null ? -1 : m.Body.Instructions.IndexOf(i);
        for (int i = 0; i < source.Body.ExceptionHandlers.Count; i++)
        {
            var a = source.Body.ExceptionHandlers[i]; var b = target.Body.ExceptionHandlers[i];
            Require(a.HandlerType == b.HandlerType && At(source, a.TryStart) == At(target, b.TryStart) && At(source, a.TryEnd) == At(target, b.TryEnd) && At(source, a.HandlerStart) == At(target, b.HandlerStart) && At(source, a.HandlerEnd) == At(target, b.HandlerEnd) && At(source, a.FilterStart) == At(target, b.FilterStart) && (a.CatchType == null ? b.CatchType == null : map(a.CatchType).FullName == b.CatchType?.FullName), "recipe EH mapping");
        }
    }
    internal static string Qualified(MemberReference member)
    {
        if (member is TypeReference type)
        {
            if (type is GenericParameter p) return (p.Type == GenericParameterType.Method ? "!!" : "!") + p.Position;
            if (type is GenericInstanceType g) return Qualified(g.ElementType) + "<" + string.Join(",", g.GenericArguments.Select(Qualified)) + ">";
            if (type is TypeSpecification s) return type.GetType().Name + "(" + Qualified(s.ElementType) + ")";
            return "[" + (type.Scope is ModuleDefinition m ? m.Assembly.Name.FullName + ";mvid=" + m.Mvid : type.Scope) + "]" + type.FullName + ";kind=" + (type.IsValueType ? "VALUETYPE" : "CLASS");
        }
        if (member is FieldReference f) return Qualified(f.DeclaringType) + "::" + f.Name + ":" + Qualified(f.FieldType);
        var method = (MethodReference)member;
        return Qualified(method.DeclaringType) + "::" + method.Name + ";this=" + method.HasThis + ";explicit=" + method.ExplicitThis + ";cc=" + method.CallingConvention + ";arity=" + method.GenericParameters.Count + "(" + string.Join(",", method.Parameters.Select(p => Qualified(p.ParameterType))) + ")->" + Qualified(method.ReturnType);
    }
    static void Mutate(string name, TypeDefinition hooks)
    {
        MethodDefinition M(string suffix) => hooks.Methods.Single(m => m.Name == name + suffix);
        Instruction Call(MethodDefinition m, string target) => m.Body.Instructions.Single(i => i.Operand is MethodReference r && (r.Name == target || r.Name == name + target));
        void ReplaceCall(MethodDefinition m, Instruction call, int pops, bool? result = null)
        {
            call.OpCode = OpCodes.Pop; call.Operand = null; var tail = call;
            for (int i = 1; i < pops; i++) { var next = Instruction.Create(OpCodes.Pop); m.Body.GetILProcessor().InsertAfter(tail, next); tail = next; }
            if (result.HasValue) m.Body.GetILProcessor().InsertAfter(tail, Instruction.Create(result.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
        }
        if (name == "SkipFilter") ReplaceCall(M("Update"), Call(M("Update"), "Accepts"), 2, true);
        else if (name == "MaterialBeforeFilter")
        {
            var method = M("Update"); var filter = Call(method, "Accepts"); var material = Call(method, "CollectedEnoughItemsToCraft");
            var at = filter.Previous.Previous; var il = method.Body.GetILProcessor();
            il.InsertBefore(at, Instruction.Create(OpCodes.Ldloc, method.Body.Variables[5])); il.InsertBefore(at, Instruction.Create(OpCodes.Call, (MethodReference)material.Operand)); il.InsertBefore(at, Instruction.Create(OpCodes.Pop));
            ReplaceCall(method, material, 1, true);
        }
        else if (name == "WrongMaterialResult") { var m = M("Update"); var call = Call(m, "CollectedEnoughItemsToCraft"); m.Body.GetILProcessor().InsertAfter(call, Instruction.Create(OpCodes.Pop)); m.Body.GetILProcessor().InsertAfter(call.Next, Instruction.Create(OpCodes.Ldc_I4_0)); }
        else if (name == "MissingChests") ReplaceCall(M("Collect"), Call(M("Collect"), "CollectItemsFromChests"), 1);
        else if (name == "WrongCollectArgument") { var arg = Call(M("Collect"), "CollectItems").Previous; arg.OpCode = OpCodes.Ldc_I4; arg.Operand = 57; }
        else if (name == "WrongFocusArgument") foreach (var call in M("Update").Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == name + "Refocus")) { call.Previous.OpCode = OpCodes.Ldc_I4_0; call.Previous.Operand = null; }
        else if (name == "LostRepositionWrite") { var m = M("Reposition"); var write = m.Body.Instructions.Single(i => i.OpCode == OpCodes.Stind_R4); write.OpCode = OpCodes.Pop; m.Body.GetILProcessor().InsertAfter(write, Instruction.Create(OpCodes.Pop)); }
        else if (name == "SwallowedOriginal")
        {
            var m = M("Update"); var il = m.Body.GetILProcessor(); var exit = Instruction.Create(OpCodes.Ret); var catcher = Instruction.Create(OpCodes.Pop);
            foreach (var ret in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret)) { ret.OpCode = OpCodes.Leave; ret.Operand = exit; }
            il.Append(catcher); il.Append(Instruction.Create(OpCodes.Leave, exit)); il.Append(exit);
            m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = m.Body.Instructions[0], TryEnd = catcher, HandlerStart = catcher, HandlerEnd = exit, CatchType = hooks.Module.ImportReference(typeof(Exception)) });
        }
        else throw new InvalidDataException("unknown recipe mutant " + name);
    }
}
