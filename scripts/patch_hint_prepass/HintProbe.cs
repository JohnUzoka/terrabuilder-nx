using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class HintProbe
{
    internal static byte[] Handlers(ModuleDefinition game, ModuleDefinition library, ModuleDefinition fna)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Hint52Controlled", new Version(1, 0)), "Hint52Controlled", ModuleKind.Dll);
        var module = assembly.MainModule;
        var snippet = Types(game).Single(x => x.FullName == "Terraria.UI.Chat.TextSnippet");
        var contract = Types(game).Single(x => x.FullName == "Terraria.UI.Chat.ITagHandler");
        var handler = new TypeDefinition("Hint52Controlled", "Handler", TA.Public | TA.Sealed, module.TypeSystem.Object); module.Types.Add(handler);
        handler.Interfaces.Add(new InterfaceImplementation(module.ImportReference(contract)));
        void Constructor(TypeDefinition type, MethodReference parent, params TypeReference[] parameters)
        {
            var m = new MethodDefinition(".ctor", MA.Public | MA.SpecialName | MA.RTSpecialName | MA.HideBySig, module.TypeSystem.Void); type.Methods.Add(m);
            foreach (var p in parameters) m.Parameters.Add(new ParameterDefinition(p));
            var il = m.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0);
            foreach (var p in m.Parameters) il.Emit(OpCodes.Ldarg, p);
            il.Emit(OpCodes.Call, module.ImportReference(parent)); il.Emit(OpCodes.Ret);
        }
        Constructor(handler, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        var parseSource = contract.Methods.Single(m => m.Name == "Parse");
        var parse = new MethodDefinition("Parse", MA.Public | MA.Virtual | MA.Final | MA.NewSlot | MA.HideBySig, module.ImportReference(snippet)); handler.Methods.Add(parse);
        foreach (var p in parseSource.Parameters) parse.Parameters.Add(new ParameterDefinition(module.ImportReference(p.ParameterType)));
        var pi = parse.Body.GetILProcessor(); pi.Emit(OpCodes.Ldarg_1); pi.Emit(OpCodes.Call, module.ImportReference(typeof(HintFixture).GetMethod("Parse")!)); pi.Emit(OpCodes.Castclass, module.ImportReference(snippet)); pi.Emit(OpCodes.Ret);
        var stateful = new TypeDefinition("Hint52Controlled", "StatefulSnippet", TA.Public, module.ImportReference(snippet)); module.Types.Add(stateful);
        Constructor(stateful, snippet.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 1), module.TypeSystem.String);
        var uniqueSource = snippet.Methods.Single(m => m.Name == "UniqueDraw");
        var unique = new MethodDefinition("UniqueDraw", MA.Public | MA.Virtual | MA.HideBySig, module.TypeSystem.Boolean); stateful.Methods.Add(unique);
        foreach (var p in uniqueSource.Parameters) unique.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, module.ImportReference(p.ParameterType)));
        var ui = unique.Body.GetILProcessor();
        var vector = Types(fna).Single(t => t.FullName == "Microsoft.Xna.Framework.Vector2");
        var ctor = vector.Methods.Single(m => m.Name == ".ctor" && m.Parameters.Count == 2 && m.Parameters.All(p => p.ParameterType.MetadataType == MetadataType.Single));
        ui.Emit(OpCodes.Ldarg_2); ui.Emit(OpCodes.Ldc_R4, 37f); ui.Emit(OpCodes.Ldc_R4, 11f); ui.Emit(OpCodes.Call, module.ImportReference(ctor));
        ui.Emit(OpCodes.Ldarg_0); ui.Emit(OpCodes.Ldarg_1); ui.Emit(OpCodes.Call, module.ImportReference(typeof(HintFixture).GetMethod("Unique")!)); ui.Emit(OpCodes.Ret);
        var morph = new MethodDefinition("CopyMorph", MA.Public | MA.Virtual | MA.HideBySig, module.ImportReference(snippet)); stateful.Methods.Add(morph); morph.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var mi = morph.Body.GetILProcessor(); mi.Emit(OpCodes.Ldarg_0); mi.Emit(OpCodes.Call, module.ImportReference(typeof(HintFixture).GetMethod("Morph")!)); mi.Emit(OpCodes.Ldarg_0); mi.Emit(OpCodes.Ldarg_1); mi.Emit(OpCodes.Call, module.ImportReference(snippet.Methods.Single(m => m.Name == "CopyMorph"))); mi.Emit(OpCodes.Ret);
        var font = Types(library).Single(t => t.FullName == "ReLogic.Graphics.DynamicSpriteFont");
        var sub = new TypeDefinition("Hint52Controlled", "SubFont", TA.Public, module.ImportReference(font)); module.Types.Add(sub);
        Constructor(sub, font.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 3), module.TypeSystem.Single, module.TypeSystem.Int32, module.TypeSystem.Char);
        return Serialize(assembly);
    }

    internal static byte[] Roots(MethodDefinition before, MethodDefinition after, ModuleDefinition fna, List<object> receipts)
    {
        string identity = Sha(Encoding.UTF8.GetBytes(HintAudit.QualifiedBody(before) + "\n" + HintAudit.QualifiedBody(after)))[..24];
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Hint52Roots" + identity, new Version(1, 0)), "Hint52Roots", ModuleKind.Dll);
        var module = assembly.MainModule;
        var hooks = new TypeDefinition("Probe", "Root", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
        var primitive = new TypeDefinition("Probe", "VectorPrimitives", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(primitive);
        var sourceVector = Types(fna).Single(t => t.FullName == "Microsoft.Xna.Framework.Vector2");
        var primitives = sourceVector.Methods.Where(m => m.Name == ".cctor" || m.Name == "get_Zero" || m.Name == ".ctor" && (m.Parameters.Count is 1 or 2) && m.Parameters.All(p => p.ParameterType.FullName == "System.Single") || m.Name is "op_Multiply" or "op_Subtraction" && m.Parameters.Count == 2 && m.Parameters.All(p => p.ParameterType.FullName == "Microsoft.Xna.Framework.Vector2")).ToArray();
        Require(primitives.Length == 6, "exact six FNA primitive methods required");
        var mapped = new Dictionary<string, MethodDefinition>();
        TypeReference Type(TypeReference type)
        {
            if (type.FullName == "Microsoft.Xna.Framework.Vector2") return module.ImportReference(typeof(HintVector));
            if (type.FullName == "Microsoft.Xna.Framework.Color") return module.ImportReference(typeof(HintColor));
            if (type.FullName is "Microsoft.Xna.Framework.Graphics.SpriteBatch" or "ReLogic.Graphics.DynamicSpriteFont") return module.TypeSystem.Object;
            if (type.FullName.StartsWith("ReLogic.Content.Asset`1<", StringComparison.Ordinal)) return module.ImportReference(typeof(HintAsset));
            if (type is ByReferenceType reference) return new ByReferenceType(Type(reference.ElementType));
            if (type.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr) return Primitive(module, type.FullName);
            Require(type.Namespace.StartsWith("System", StringComparison.Ordinal), "unexpected probe type " + type.FullName);
            return module.ImportReference(type);
        }
        foreach (var field in sourceVector.Fields.Where(f => f.IsStatic)) primitive.Fields.Add(new FieldDefinition(field.Name, FA.Private | FA.Static, Type(field.FieldType)));
        foreach (var method in primitives)
        {
            string name = method.IsConstructor && !method.IsStatic ? "Ctor" + method.Parameters.Count : method.Name;
            var target = new MethodDefinition(name, method.IsConstructor && method.IsStatic ? MA.Private | MA.Static | MA.SpecialName | MA.RTSpecialName : MA.Public | MA.Static, Type(method.ReturnType));
            if (method.HasThis) target.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.ImportReference(typeof(HintVector)))));
            foreach (var parameter in method.Parameters) target.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, Type(parameter.ParameterType)));
            primitive.Methods.Add(target); mapped.Add(method.FullName, target);
        }
        foreach (var source in primitives)
        {
            var target = mapped[source.FullName];
            Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)Type, (Func<object, object>)Member, source.HasThis ? 1 : 0);
            receipts.Add(Receipt(source, target, "actual-pinned-FNA-primitive"));
        }
        var factories = new Dictionary<int, MethodDefinition>();
        foreach (int count in new[] { 1, 2 })
        {
            var factory = new MethodDefinition("New" + count, MA.Public | MA.Static, module.ImportReference(typeof(HintVector)));
            for (int index = 0; index < count; index++) factory.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
            primitive.Methods.Add(factory); factory.Body.InitLocals = true;
            var local = new VariableDefinition(module.ImportReference(typeof(HintVector))); factory.Body.Variables.Add(local);
            var il = factory.Body.GetILProcessor(); il.Emit(OpCodes.Ldloca, local);
            foreach (var parameter in factory.Parameters) il.Emit(OpCodes.Ldarg, parameter);
            il.Emit(OpCodes.Call, primitive.Methods.Single(m => m.Name == "Ctor" + count)); il.Emit(OpCodes.Ldloc, local); il.Emit(OpCodes.Ret);
            factories.Add(count, factory);
        }
        foreach (var pair in new[] { (name: "Original", source: before), (name: "Candidate", source: after) })
        {
            var target = new MethodDefinition(pair.name, MA.Public | MA.Static, module.TypeSystem.Void); hooks.Methods.Add(target);
            Copy(pair.source, target, Type, Member);
            Require(target.Body.Instructions.Count == pair.source.Body.Instructions.Count, "whole root copy changed instruction count");
            receipts.Add(Receipt(pair.source, target, "whole-actual-" + pair.name + "-root"));
        }
        foreach (var method in Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is MethodReference constructor && constructor.Name == ".ctor" && constructor.DeclaringType.FullName == typeof(HintVector).FullName)
                {
                    instruction.Operand = instruction.OpCode == OpCodes.Newobj ? factories[constructor.Parameters.Count] : primitive.Methods.Single(m => m.Name == "Ctor" + constructor.Parameters.Count);
                    instruction.OpCode = OpCodes.Call;
                }
            Widen(method);
        }
        return Serialize(assembly);
        object Member(object value)
        {
            if (value is TypeReference type) return Type(type);
            if (value is FieldReference field)
            {
                if (field.DeclaringType.FullName == sourceVector.FullName)
                {
                    var own = primitive.Fields.FirstOrDefault(f => f.Name == field.Name);
                    if (own != null) return own;
                    return module.ImportReference(typeof(HintVector).GetField(field.Name)!);
                }
                return module.ImportReference(typeof(HintFixture).GetField(field.Name) ?? throw new InvalidDataException("unmapped root field " + field.FullName));
            }
            if (value is GenericInstanceMethod generic && generic.Name == "Swap") return module.ImportReference(typeof(HintFixture).GetMethod("Swap")!);
            if (value is MethodReference method)
            {
                if (method.DeclaringType.FullName == sourceVector.FullName)
                {
                    if (method.Name == ".ctor") return module.ImportReference(typeof(HintVector).GetConstructor(Enumerable.Repeat(typeof(float), method.Parameters.Count).ToArray())!);
                    return mapped[method.FullName];
                }
                if (method.DeclaringType.FullName.StartsWith("ReLogic.Content.Asset`1<", StringComparison.Ordinal)) return module.ImportReference(typeof(HintAsset).GetProperty("Value")!.GetMethod!);
                if (method.DeclaringType.FullName == "System.String" && method.Name == "get_Length") return module.ImportReference(typeof(string).GetProperty("Length")!.GetMethod!);
                string name = method.Name == "_NXHint52Measure" ? "GetStringSize" : method.Name;
                return module.ImportReference(typeof(HintFixture).GetMethod(name) ?? throw new InvalidDataException("unmapped root call " + method.FullName));
            }
            return value;
        }
    }
    internal static byte[] ItemFixture(ModuleDefinition source, string fixturePath, List<object> receipts)
    {
        using var fixture = AssemblyDefinition.ReadAssembly(fixturePath);
        var module = fixture.MainModule;
        var targets = Types(module).ToDictionary(t => t.FullName);
        TypeReference Type(TypeReference t)
        {
            if (t is ByReferenceType b) return new ByReferenceType(Type(b.ElementType));
            if (t is ArrayType a) return new ArrayType(Type(a.ElementType), a.Rank);
            if (t is GenericInstanceType g) { var n = new GenericInstanceType(Type(g.ElementType)); foreach (var p in g.GenericArguments) n.GenericArguments.Add(Type(p)); return n; }
            if (t is GenericParameter) return t;
            return targets.TryGetValue(t.FullName, out var local) ? local : module.ImportReference(t);
        }
        MethodReference Method(MethodReference m)
        {
            if (m is GenericInstanceMethod g) { var n = new GenericInstanceMethod(Method(g.ElementMethod)); foreach (var p in g.GenericArguments) n.GenericArguments.Add(Type(p)); return n; }
            var declaring = m.DeclaringType is GenericInstanceType gi ? gi.ElementType : m.DeclaringType;
            if (!targets.TryGetValue(declaring.FullName, out var t)) return module.ImportReference(m);
            var target = t.Methods.Single(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count && x.GenericParameters.Count == m.GenericParameters.Count);
            if (m.DeclaringType is not GenericInstanceType) return target;
            var mr = new MethodReference(target.Name, target.ReturnType, Type(m.DeclaringType)) { HasThis = target.HasThis, ExplicitThis = target.ExplicitThis, CallingConvention = target.CallingConvention };
            foreach (var p in target.Parameters) mr.Parameters.Add(new ParameterDefinition(p.ParameterType));
            foreach (var p in target.GenericParameters) mr.GenericParameters.Add(new GenericParameter(p.Name, mr));
            return mr;
        }
        object Member(object o) => o switch { MethodReference m => Method(m), FieldReference f => new FieldReference(f.Name, Type(f.FieldType), Type(f.DeclaringType)), TypeReference t => Type(t), _ => o };
        foreach (string name in new[] { "Terraria.GameContent.UI.Chat.ItemTagHandler/ItemSnippet", "Terraria.Main" })
        {
            var original = Types(source).Single(t => t.FullName == name).Methods.Single(m => m.Name == (name == "Terraria.Main" ? "LoadItem" : "UniqueDraw") && (name != "Terraria.Main" || m.Parameters.Count == 1));
            var target = targets[name].Methods.Single(m => m.Name == original.Name);
            Copy(original, target, Type, Member); Widen(target);
            receipts.Add(Receipt(original, target, "exact-current-pair-item-effect-with-named-repository-boundary"));
        }
        fixture.Name.Name = "Hint52ItemEffects" + Sha(Encoding.UTF8.GetBytes(string.Join("\n", Types(source).Where(t => t.FullName is "Terraria.Main" or "Terraria.GameContent.UI.Chat.ItemTagHandler/ItemSnippet").SelectMany(t => t.Methods).Where(m => m.Name is "LoadItem" or "UniqueDraw").Select(HintAudit.QualifiedBody))))[..16];
        module.Name = fixture.Name.Name + ".dll";
        return Serialize(fixture);
    }

    internal static object Receipt(MethodDefinition source, MethodDefinition? target, string kind) => new { kind, source = source.FullName, token = source.MetadataToken.ToUInt32().ToString("x8"), sourceMvid = source.Module.Mvid, sourceBodySha256 = Fingerprint(source), qualifiedBodySha256 = Sha(Encoding.UTF8.GetBytes(HintAudit.QualifiedBody(source))), instructions = source.Body.Instructions.Count, fixture = target?.FullName, copiedInstructions = target?.Body.Instructions.Count };
    internal static byte[] Serialize(AssemblyDefinition assembly)
    {
        assembly.MainModule.Mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Types(assembly.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody).Select(Body)))).AsSpan(0, 16));
        using var output = new MemoryStream(); assembly.Write(output, new WriterParameters { Timestamp = 0 }); return output.ToArray();
    }
}
