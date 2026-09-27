using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class UIProfileProbe
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static byte[] Build(AssemblyDefinition baseline, AssemblyDefinition candidate, AssemblyDefinition fna, List<object> receipts, string fault = "")
    {
        using var probe = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("UIProfileExactProbe" + fault, new Version(51, 0)), "UIProfileExactProbe" + fault, ModuleKind.Dll);
        using var self = AssemblyDefinition.ReadAssembly(typeof(UIProfileFixture).Assembly.Location);
        var module = probe.MainModule;
        var hooks = new TypeDefinition("Probe", "Hooks", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
        var hosts = new Dictionary<string, Type> {
            ["Terraria.Main"] = typeof(UIProofMain), ["Terraria.Entity"] = typeof(UIProofEntity), ["Terraria.Player"] = typeof(UIProofPlayer), ["Terraria.Item"] = typeof(UIProofItem),
            ["Microsoft.Xna.Framework.Vector2"] = typeof(UIProofVector), ["Microsoft.Xna.Framework.Color"] = typeof(UIProofColor), ["Microsoft.Xna.Framework.Matrix"] = typeof(UIProofMatrix), ["Microsoft.Xna.Framework.GameTime"] = typeof(UIProofTime),
            ["Terraria.UI.GameInterfaceLayer"] = typeof(UIProofLayer), ["Terraria.UI.LegacyGameInterfaceLayer"] = typeof(UIProofLayer), ["Terraria.UI.GameInterfaceDrawMethod"] = typeof(UIProofDrawMethod),
            ["System.Collections.Generic.List`1<Terraria.UI.GameInterfaceLayer>"] = typeof(UIProofLayerList), ["System.Collections.Generic.List`1/Enumerator<Terraria.UI.GameInterfaceLayer>"] = typeof(UIProofLayerList.Enumerator),
            ["Terraria.Main/MouseTextCache"] = typeof(UIProofMouseCache), ["ReLogic.Content.IAssetRepository"] = typeof(UIProofAssets),
            ["Terraria.Graphics.SpriteViewMatrix"] = typeof(UIProofView), ["Microsoft.Xna.Framework.Graphics.SpriteBatch"] = typeof(UIProofBatch),
            ["Microsoft.Xna.Framework.Graphics.BlendState"] = typeof(UIProofRenderState), ["Microsoft.Xna.Framework.Graphics.SamplerState"] = typeof(UIProofRenderState), ["Microsoft.Xna.Framework.Graphics.DepthStencilState"] = typeof(UIProofRenderState), ["Microsoft.Xna.Framework.Graphics.RasterizerState"] = typeof(UIProofRenderState), ["Microsoft.Xna.Framework.Graphics.Effect"] = typeof(object),
            ["Terraria.Testing.DebugVisualizer"] = typeof(UIProofDebug), ["Terraria.Testing.DetailedFPS"] = typeof(UIProofBoundary), ["Terraria.GameInput.PlayerInput"] = typeof(UIProofBoundary), ["Terraria.UI.CoinSlot"] = typeof(UIProofBoundary), ["Terraria.TimeLogger"] = typeof(UIProofBoundary),
            ["Terraria.Localization.LocalizedText"] = typeof(UIProofText), ["Terraria.Lang"] = typeof(UIProofLanguage), ["Terraria.Localization.Language"] = typeof(UIProofLanguage), ["Terraria.Lang/ItemPrefixCombiner"] = typeof(UIProofCombiner), ["Terraria.Localization.GameCulture"] = typeof(UIProofCulture),
            ["ReLogic.Graphics.DynamicSpriteFont"] = typeof(UIProofFont), ["ReLogic.Graphics.DynamicSpriteFontExtensionMethods"] = typeof(UIProofFontExtensions), ["ReLogic.Graphics.DynamicSpriteFont/DrawCharacter"] = typeof(UIProofDrawCharacter),
            ["Terraria.UI.Chat.TextSnippet"] = typeof(UIProofSnippet), ["Terraria.UI.Chat.PositionedSnippet"] = typeof(UIProofPositioned),
            ["Terraria.Enums.FrameSkipMode"] = typeof(int), ["Terraria.UI.InterfaceScaleType"] = typeof(int), ["Terraria.Testing.DetailedFPS/OperationCategory"] = typeof(int), ["Microsoft.Xna.Framework.Graphics.SpriteSortMode"] = typeof(int), ["Microsoft.Xna.Framework.Graphics.SpriteEffects"] = typeof(int),
            [nameof(UIProfileFixture)] = typeof(UIProfileFixture)
        };
        string prefix = "Patched";
        var types = new Dictionary<string, TypeDefinition>();
        var fullTypes = new List<(TypeDefinition source, TypeDefinition target, string prefix)>();
        var methods = new Dictionary<string, MethodDefinition>();
        var copies = new List<(MethodDefinition source, MethodDefinition target, string prefix, string kind)>();
        string Key(string name) => (name.StartsWith("Terraria.NXUIProfile51", StringComparison.Ordinal) ? "Runtime" : prefix) + ":" + name;
        void DefineType(TypeDefinition source, string name)
        {
            var target = new TypeDefinition("Probe", name, TA.Public | (source.IsValueType ? TA.SequentialLayout | TA.Sealed : source.IsAbstract ? TA.Abstract | TA.Sealed : TA.Sealed), source.IsValueType ? module.ImportReference(typeof(ValueType)) : module.TypeSystem.Object);
            module.Types.Add(target); types.Add(Key(source.FullName), target); fullTypes.Add((source, target, prefix));
        }
        var sourceRuntime = Types(candidate.MainModule).Single(t => t.FullName == "Terraria.NXUIProfile51");
        foreach (var source in new[] { sourceRuntime }.Concat(sourceRuntime.NestedTypes)) DefineType(source, source == sourceRuntime ? "UIRuntime" : "UIRuntime_" + source.Name);
        foreach (var (game, pre) in new[] { (baseline, "Baseline"), (candidate, "Patched") })
        {
            prefix = pre;
            DefineType(Types(game.MainModule).Single(t => t.FullName == "Terraria.UI.Chat.ChatManager/<LayoutSnippets>d__12"), pre + "LayoutIterator");
        }
        TypeReference TypeMap(TypeReference t)
        {
            if (types.TryGetValue(Key(t.FullName), out var exact)) return exact;
            if (hosts.TryGetValue(t.FullName, out var host)) return module.ImportReference(host);
            if (t is GenericParameter) return t;
            if (t is ArrayType a) { var n = new ArrayType(TypeMap(a.ElementType), a.Rank); for (int i = 0; i < a.Rank; i++) n.Dimensions[i] = new ArrayDimension(a.Dimensions[i].LowerBound, a.Dimensions[i].UpperBound); return n; }
            if (t is ByReferenceType b) return new ByReferenceType(TypeMap(b.ElementType));
            if (t is PointerType p) return new PointerType(TypeMap(p.ElementType));
            if (t is GenericInstanceType g) { var n = new GenericInstanceType(TypeMap(g.ElementType)); foreach (var x in g.GenericArguments) n.GenericArguments.Add(TypeMap(x)); return n; }
            Require(t.Namespace.StartsWith("System", StringComparison.Ordinal), "unmapped UI proof type " + t.FullName);
            if (t.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr) return Primitive(module, t.FullName);
            return module.ImportReference(t);
        }
        MethodDefinition Define(MethodDefinition source, TypeDefinition owner, string name, bool staticReceiver, string kind)
        {
            var attributes = (source.Attributes & ~MA.MemberAccessMask) | MA.Public;
            if (staticReceiver) attributes = MA.Public | MA.Static;
            var target = new MethodDefinition(name, attributes, TypeMap(source.ReturnType)) { ImplAttributes = source.ImplAttributes };
            if (staticReceiver && source.HasThis) target.Parameters.Add(new ParameterDefinition(TypeMap(source.DeclaringType)));
            foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, TypeMap(p.ParameterType)));
            owner.Methods.Add(target); methods.Add(Key(source.FullName), target); copies.Add((source, target, prefix, kind)); return target;
        }
        foreach (var (source, target, pre) in fullTypes)
        {
            prefix = pre;
            foreach (var field in source.Fields)
            {
                var f = new FieldDefinition(field.Name, (field.Attributes & ~FA.FieldAccessMask) | FA.Public, TypeMap(field.FieldType)); if (field.HasConstant) f.Constant = field.Constant;
                foreach (var attribute in field.CustomAttributes) { Require(attribute.AttributeType.FullName == "System.ThreadStaticAttribute", "unhandled helper field attribute"); f.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!))); }
                target.Fields.Add(f);
            }
            foreach (var iface in source.Interfaces) target.Interfaces.Add(new InterfaceImplementation(TypeMap(iface.InterfaceType)));
            foreach (var sourceMethod in source.Methods) Define(sourceMethod, target, sourceMethod.Name, false, source == sourceRuntime || source.DeclaringType == sourceRuntime ? "ActualCandidateRuntime" : "ActualDeferredIterator");
        }
        foreach (var (game, pre) in new[] { (baseline, "Baseline"), (candidate, "Patched") })
        {
            prefix = pre;
            foreach (var (token, suffix) in new[] { (0x060010c3, "Main"), (0x06000fba, "Interface"), (0x06001f68, "Layer"), (0x06002074, "LayerSelf"), (0x06000fbf, "PendingTooltip"), (0x06000ee4, "MouseText"), (0x06000ee8, "TooltipColor"), (0x0600089b, "Affix"), (0x060001ef, "Prefix"), (0x06002978, "Format"), (0x0600225e, "Layout"), (0x06002260, "LayoutSize"), (0x06002261, "AggregateSize") })
            {
                var originalMethod = (MethodDefinition)baseline.MainModule.LookupToken(token);
                var source = Types(game.MainModule).SelectMany(t => t.Methods).Single(m => m.FullName == originalMethod.FullName);
                Define(source, hooks, pre + suffix, true, "ActualOriginalCandidateBody");
            }
            var generic = Types(self.MainModule).Single(t => t.Name == nameof(UIProfileFixture)).Methods.Single(m => m.Name == "Generic");
            Define(generic, hooks, pre + "Generic", true, "GenericEnvelopeSource");
        }
        MethodReference HostMethod(Type host, MethodReference source)
        {
            var parameters = source.Parameters.Select(p => TypeMap(p.ParameterType).FullName).ToArray();
            var options = source.Name == ".ctor" ? host.GetConstructors(Flags).Cast<MethodBase>() : host.GetMethods(Flags).Where(m => m.Name == source.Name).Cast<MethodBase>();
            var matches = options.Where(m => m.GetParameters().Select(p => module.ImportReference(p.ParameterType).FullName).SequenceEqual(parameters)).ToArray();
            Require(matches.Length == 1, "missing/ambiguous typed UI host method " + source.FullName + " -> " + host);
            var result = matches[0] is ConstructorInfo c ? module.ImportReference(c) : module.ImportReference((MethodInfo)matches[0]);
            Require(result.HasThis == source.HasThis && result.ReturnType.FullName == TypeMap(source.ReturnType).FullName, "host signature mismatch " + source.FullName); return result;
        }
        object Member(object value)
        {
            if (value is TypeReference t) return TypeMap(t);
            if (value is FieldReference field)
            {
                if (types.TryGetValue(Key(field.DeclaringType.FullName), out var owner)) return owner.Fields.Single(f => f.Name == field.Name);
                if (hosts.TryGetValue(field.DeclaringType.FullName, out var host))
                {
                    var f = host.GetField(field.Name, Flags); Require(f != null, "missing typed UI field " + field.FullName);
                    var result = module.ImportReference(f!); Require(result.FieldType.FullName == TypeMap(field.FieldType).FullName, "host field signature mismatch " + field.FullName); return result;
                }
                return new FieldReference(field.Name, TypeMap(field.FieldType), TypeMap(field.DeclaringType));
            }
            if (value is GenericInstanceMethod generic) { var n = new GenericInstanceMethod((MethodReference)Member(generic.ElementMethod)); foreach (var a in generic.GenericArguments) n.GenericArguments.Add(TypeMap(a)); return n; }
            if (value is MethodReference method)
            {
                if (methods.TryGetValue(Key(method.FullName), out var exact)) return exact;
                if (method.DeclaringType.FullName == "Terraria.UI.GameInterfaceLayer" && method.Name == "DrawSelf") return hooks.Methods.Single(m => m.Name == prefix + "LayerSelf");
                if (method.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && method.Name == "GetTimestamp") return module.ImportReference(typeof(UIProfileFixture).GetMethod("Clock")!);
                if (method.DeclaringType.FullName == "System.Console" && method.Name == "WriteLine" && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.String") return module.ImportReference(typeof(UIProfileFixture).GetMethod("WriteLine")!);
                if (method.DeclaringType.FullName == "System.Console" && method.Name == "Write" && method.Parameters.Count == 3) return module.ImportReference(typeof(UIProfileFixture).GetMethod("Write")!);
                if (hosts.TryGetValue(method.DeclaringType.FullName, out var host))
                {
                    // The generic List<T>.GetEnumerator signature contains !0 until instantiated; host list owns an observable disposer.
                    if (host == typeof(UIProofLayerList) && method.Name == "GetEnumerator") return module.ImportReference(host.GetMethod("GetEnumerator")!);
                    if (host == typeof(UIProofLayerList.Enumerator)) return module.ImportReference(host.GetMethod(method.Name)!);
                    return HostMethod(host, method);
                }
                var n = new MethodReference(method.Name, TypeMap(method.ReturnType), TypeMap(method.DeclaringType)) { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
                foreach (var p in method.Parameters) n.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
                foreach (var g in method.GenericParameters) n.GenericParameters.Add(new GenericParameter(g.Name, n)); return n;
            }
            return value;
        }
        foreach (var (source, target, pre, kind) in copies)
        {
            prefix = pre;
            Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)TypeMap, (Func<object, object>)Member, source.HasThis && target.IsStatic ? 1 : 0);
            for (int i = 0; i < target.Body.Instructions.Count; i++)
            {
                var instruction = target.Body.Instructions[i];
                if (instruction.OpCode == OpCodes.Callvirt && instruction.Operand is MethodReference { HasThis: false }) instruction.OpCode = OpCodes.Call;
                if (source.Body.Instructions[i].Operand is FieldReference { Name: "Frequency", DeclaringType.FullName: "System.Diagnostics.Stopwatch" }) { instruction.OpCode = OpCodes.Call; instruction.Operand = module.ImportReference(typeof(UIProfileFixture).GetMethod("Frequency")!); }
            }
            foreach (var ov in source.Overrides) target.Overrides.Add((MethodReference)Member(ov));
            Require(target.Body.Instructions.Count == source.Body.Instructions.Count && target.Body.ExceptionHandlers.Count == source.Body.ExceptionHandlers.Count && target.Body.Variables.Count == source.Body.Variables.Count, "whole body copy shape mismatch " + source.FullName);
            receipts.Add(new { source = source.FullName, sourceMvid = source.Module.Mvid, sourceBody = Fingerprint(source), fixture = target.FullName, mappedBody = Fingerprint(target), instructions = source.Body.Instructions.Count, locals = source.Body.Variables.Count, handlers = source.Body.ExceptionHandlers.Count, kind });
        }
        var runtime = types["Runtime:" + sourceRuntime.FullName];
        var genericTarget = hooks.Methods.Single(m => m.Name == "PatchedGeneric");
        UIProfilePatcher.Envelope(genericTarget, runtime, 9, "scope");
        if (fault == "SkipCallback") SuppressCall(genericTarget, "Effect", "Generic.Enter");
        else if (fault == "MissFinally")
        {
            var originalFinally = genericTarget.Body.ExceptionHandlers.First(e => e.HandlerType == ExceptionHandlerType.Finally);
            Require(originalFinally.HandlerEnd.Previous.OpCode == OpCodes.Endfinally, "generic source finally ending changed");
            for (var instruction = originalFinally.HandlerStart; instruction != originalFinally.HandlerEnd; instruction = instruction.Next) { instruction.OpCode = OpCodes.Nop; instruction.Operand = null; }
            genericTarget.Body.ExceptionHandlers.Remove(originalFinally);
        }
        else if (fault == "MissReturn") { var ret = genericTarget.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret); ret.Previous.OpCode = OpCodes.Ldc_I4_0; ret.Previous.Operand = null; }
        else if (fault == "WrongScope") { var enter = genericTarget.Body.Instructions.Single(i => i.Operand is MethodReference { Name: "Enter" }); enter.Previous.OpCode = OpCodes.Ldc_I4; enter.Previous.Operand = 10; }
        else if (fault == "FontSkip")
        {
            foreach (var wrapper in runtime.Methods.Where(m => m.Name.StartsWith("Font", StringComparison.Ordinal)))
                foreach (var call in wrapper.Body.Instructions.Where(i => i.Operand is MethodReference { DeclaringType.FullName: nameof(UIProofFont), Name: "MeasureString" }).ToArray())
                {
                    var il = wrapper.Body.GetILProcessor(); il.InsertBefore(call, Instruction.Create(OpCodes.Pop)); call.OpCode = OpCodes.Pop; call.Operand = null;
                    var local = new VariableDefinition(module.ImportReference(typeof(UIProofVector))); wrapper.Body.Variables.Add(local); wrapper.Body.InitLocals = true;
                    var load = Instruction.Create(OpCodes.Ldloc, local); il.InsertAfter(call, load);
                }
        }
        else if (fault == "FontWrongScope")
        {
            var wrapper = runtime.Methods.First(m => m.Name.StartsWith("Font", StringComparison.Ordinal));
            var enter = wrapper.Body.Instructions.Single(i => i.Operand is MethodReference { Name: "Enter" });
            enter.Previous.OpCode = OpCodes.Ldc_I4; enter.Previous.Operand = 9;
        }
        else if (fault == "FontReturn")
        {
            var wrapper = runtime.Methods.Single(m => m.Name.StartsWith("Font", StringComparison.Ordinal) && m.ReturnType.FullName == nameof(UIProofVector));
            var ret = wrapper.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
            var zero = new VariableDefinition(wrapper.ReturnType); wrapper.Body.Variables.Add(zero); wrapper.Body.InitLocals = true;
            ret.Previous.OpCode = OpCodes.Ldloc; ret.Previous.Operand = zero;
        }
        else Require(fault == "", "unknown UI proof mutant " + fault);
        foreach (var m in Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody)) Widen(m);
        module.Mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Types(module).SelectMany(t => t.Methods).Select(Body)))).AsSpan(0, 16));
        using var buffer = new MemoryStream(); probe.Write(buffer, new WriterParameters { Timestamp = 0 }); var bytes = buffer.ToArray();
        using var serialized = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var target in Types(module).SelectMany(t => t.Methods))
        {
            var actual = Types(serialized.MainModule).Single(t => t.FullName == target.DeclaringType.FullName).Methods.Single(m => m.Name == target.Name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(target.Parameters.Select(p => p.ParameterType.FullName)));
            Require(Fingerprint(actual) == Fingerprint(target), "serialized UI probe changed " + target.FullName);
        }
        return bytes;
    }
    static void SuppressCall(MethodDefinition method, string name, string argument)
    {
        var call = method.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == name && i.Previous.Operand as string == argument);
        call.OpCode = OpCodes.Pop; call.Operand = null;
    }
}
