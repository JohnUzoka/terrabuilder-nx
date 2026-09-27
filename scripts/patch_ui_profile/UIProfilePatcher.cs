using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class UIProfilePatcher
{
    internal const string HelperName = "Terraria.NXUIProfile51";
    internal sealed record CallReplacement(int OriginalIndex, string Opcode, string Target, string Wrapper);
    internal sealed record Receipt(string Method, string Kind, int ScopeId, int[] OriginalIndices, int OriginalLocals,
        int OriginalHandlers, bool OriginalInitLocals, int OriginalMaxStack, int[] ReturnIndices,
        CallReplacement[] Calls, string BeforeHash, string AfterHash);
    internal sealed record Target(int Token, int Instructions, int Scope, string Kind, string Name);

    // Tokens, counts and names are from the complete exact50 inventory, not decompiler guesses.
    internal static readonly Target[] Targets = {
        new(0x060010c3,27,0,"frame","Draw"), new(0x06000fba,46,1,"scope","DrawInterface"),
        new(0x06001f68,40,6,"layer","Draw"),
        new(0x0600211f,18,7,"scope","Draw"), new(0x06002120,2562,7,"scope","Draw"),
        new(0x06000fbf,28,8,"scope","DrawPendingMouseText"), new(0x06000ee5,565,8,"scope","MouseTextInner"),
        new(0x06000ee6,463,8,"scope","MouseText_DrawItemTooltip"),
        new(0x06000ee7,425,8,"scope","MouseText_DrawItemTooltip_AddShopLines"),
        new(0x06000ee8,152,8,"scope","MouseText_DrawItemTooltip_GetItemNameColor"),
        new(0x06000ee9,3131,8,"scope","MouseText_DrawItemTooltip_GetLinesInfo"),
        new(0x06000eea,482,8,"scope","MouseText_DrawBuffTooltip"),
        new(0x0600089b,19,9,"scope","AffixName"), new(0x060001ef,35,9,"scope","GetPrefixedItemName"),
        new(0x06002971,10,9,"scope","FormatWith"), new(0x06002972,10,9,"scope","FormatWith"),
        new(0x06002978,5,9,"scope","Format"), new(0x06002979,6,9,"scope","Format"),
        new(0x0600297a,7,9,"scope","Format"), new(0x0600297b,5,9,"scope","Format"),
        new(0x060012b4,75,10,"scope","DrawBorderStringFourWay"), new(0x060012b5,34,10,"scope","DrawBorderStringMeasured"),
        new(0x060012b6,39,10,"scope","DrawBorderString"), new(0x060012b7,85,10,"scope","DrawBorderStringBig"),
        new(0x06002262,30,10,"scope","DrawColorCodedStringShadow"), new(0x06002263,22,10,"scope","DrawColorCodedString"),
        new(0x06002264,17,10,"scope","DrawColorCodedString"), new(0x06002265,86,10,"scope","DrawColorCodedString"),
        new(0x06002266,30,10,"scope","DrawColorCodedStringWithShadow"), new(0x06002267,31,10,"scope","DrawColorCodedStringWithShadow"),
        new(0x06002268,31,10,"scope","DrawColorCodedStringShadow"), new(0x06002269,253,10,"scope","DrawColorCodedString"),
        new(0x0600226a,91,10,"scope","DrawColorCodedStringWithShadow"), new(0x0600226b,39,10,"scope","DrawStringWithShadowFast"),
        new(0x0600120f,55,11,"scope","WordwrapStringSmart"), new(0x06001210,32,11,"scope","WordwrapString"),
        new(0x06001211,237,11,"scope","WordwrapStringLegacy"), new(0x0600225c,115,11,"scope","ParseMessage"),
        new(0x0600225e,15,11,"scope","LayoutSnippets"), new(0x0600225f,8,11,"scope","GetStringSize"),
        new(0x06002260,7,11,"scope","GetStringSize"), new(0x06002261,44,11,"scope","GetStringSize"),
        new(0x0600226f,17,11,"scope","System.IDisposable.Dispose"), new(0x06002270,296,11,"scope","MoveNext"),
        new(0x06002275,36,11,"scope","System.Collections.Generic.IEnumerable<Terraria.UI.Chat.PositionedSnippet>.GetEnumerator"),
        new(0x06002276,3,11,"scope","System.Collections.IEnumerable.GetEnumerator")
    };

    internal static bool IsAdded(TypeDefinition type) => type.FullName == HelperName || type.DeclaringType != null && IsAdded(type.DeclaringType);
    internal static IAssemblyResolver Resolver(string input) => new PinnedResolver(Path.GetDirectoryName(Path.GetFullPath(input))!);

    internal static Receipt[] Inject(ModuleDefinition module)
    {
        Require(module.Mvid == Guid.Parse("18ab5d9f-3419-30c0-509e-cf5a34c42f0b"), "UI51 requires exact adopted50 module");
        Require(!Types(module).Any(IsAdded), "UI51 already injected");
        var originalMethods = Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
        var plan = Targets.ToDictionary(t => {
            var m = (MethodDefinition)module.LookupToken(t.Token);
            Require(m.Name == t.Name && m.Body.Instructions.Count == t.Instructions, "pinned UI target changed: " + t.Token.ToString("x8"));
            return m;
        });
        var helper = MapRuntime(module);
        var fonts = originalMethods.SelectMany(m => m.Body.Instructions.Select((i, n) => (Method: m, Instruction: i, Index: n)))
            .Where(x => x.Instruction.Operand is MethodReference r && FontMetric(r) >= 0 && x.Instruction.OpCode.Code is Code.Call or Code.Callvirt).ToArray();
        Require(fonts.Length == 230 && fonts.Select(x => x.Method).Distinct().Count() == 101, "whole-game external font coverage differs from exact50");
        Require(!originalMethods.SelectMany(m => m.Body.Instructions).Any(i => i.Operand is MethodReference r &&
            (r.DeclaringType.FullName == "Microsoft.Xna.Framework.Graphics.SpriteFont" && r.Name == "MeasureString" ||
             r.DeclaringType.FullName == "Microsoft.Xna.Framework.Graphics.SpriteBatch" && r.Name == "DrawString")), "unexpected direct FNA font callsite");
        var wrappers = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        var replacements = new Dictionary<MethodDefinition, List<CallReplacement>>();
        var before = originalMethods.ToDictionary(m => m, Fingerprint);
        foreach (var site in fonts)
        {
            var call = (MethodReference)site.Instruction.Operand;
            Require(site.Instruction.Previous == null || site.Instruction.Previous.OpCode.OpCodeType != OpCodeType.Prefix, "unsupported prefixed font call");
            string key = site.Instruction.OpCode.Name + " " + UIProfileAudit.MemberIdentity(call);
            if (!wrappers.TryGetValue(key, out var wrapper)) {
                wrapper = FontWrapper(helper, "Font" + wrappers.Count.ToString("D3"), call, site.Instruction.OpCode, FontMetric(call));
                wrappers.Add(key, wrapper);
            }
            if (!replacements.TryGetValue(site.Method, out var rows)) replacements.Add(site.Method, rows = new());
            rows.Add(new(site.Index, site.Instruction.OpCode.Name, UIProfileAudit.MemberIdentity(call), wrapper.FullName));
            site.Instruction.OpCode = OpCodes.Call; site.Instruction.Operand = wrapper;
        }
        Require(wrappers.Count == 8, "exact50 must have eight typed font wrapper signatures");
        var receipts = new List<Receipt>();
        foreach (var method in originalMethods)
        {
            replacements.TryGetValue(method, out var calls);
            Receipt receipt;
            if (plan.TryGetValue(method, out var target)) receipt = Envelope(method, helper, target.Scope, target.Kind);
            else if (calls != null) receipt = IdentityReceipt(method, "calls", -1);
            else continue;
            receipts.Add(receipt with { Calls = calls?.ToArray() ?? Array.Empty<CallReplacement>(), BeforeHash = before[method], AfterHash = Fingerprint(method) });
        }
        var run = Program.Method(module, Program.RunGameName);
        Require(!receipts.Any(r => r.Method == run.FullName), "RunGame unexpectedly already changed");
        var flush = IdentityReceipt(run, "flush", -1);
        var instructions = run.Body.Instructions.ToArray();
        var gameRun = instructions.Single(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.DeclaringType.FullName == "Microsoft.Xna.Framework.Game" && r.Name == "Run" && r.Parameters.Count == 0);
        run.Body.GetILProcessor().InsertAfter(gameRun, Instruction.Create(OpCodes.Call, helper.Methods.Single(m => m.Name == "Flush")));
        Widen(run);
        receipts.Add(flush with { OriginalIndices = instructions.Select(i => run.Body.Instructions.IndexOf(i)).ToArray(), AfterHash = Fingerprint(run) });
        return receipts.ToArray();
    }

    static Receipt IdentityReceipt(MethodDefinition method, string kind, int id) => new(method.FullName, kind, id,
        Enumerable.Range(0, method.Body.Instructions.Count).ToArray(), method.Body.Variables.Count, method.Body.ExceptionHandlers.Count,
        method.Body.InitLocals, method.Body.MaxStackSize, method.Body.Instructions.Select((i, n) => (i, n)).Where(x => x.i.OpCode == OpCodes.Ret).Select(x => x.n).ToArray(),
        Array.Empty<CallReplacement>(), Fingerprint(method), Fingerprint(method));

    internal static Receipt Envelope(MethodDefinition method, TypeDefinition helper, int scopeId, string kind)
    {
        Require(method.HasBody && !method.HasGenericParameters && !method.Body.Instructions.Any(i => i.OpCode.Code is Code.Jmp or Code.Tail), "unsupported scope body " + method.FullName);
        var receipt = IdentityReceipt(method, kind, scopeId);
        var original = method.Body.Instructions.ToArray(); var body = method.Body; var il = body.GetILProcessor();
        MethodDefinition Hook(string name) => helper.Methods.Single(m => m.Name == name);
        var cookie = new VariableDefinition(method.Module.TypeSystem.Int32);
        var completed = new VariableDefinition(method.Module.TypeSystem.Boolean);
        body.Variables.Add(cookie); body.Variables.Add(completed);
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void) { result = new VariableDefinition(method.ReturnType); body.Variables.Add(result); }
        if (kind == "layer") Require(method.HasThis && method.ReturnType.MetadataType == MetadataType.Boolean, "layer envelope requires instance bool");
        var preamble = new List<Instruction> { Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, completed) };
        if (kind == "layer") { preamble.Add(Instruction.Create(OpCodes.Ldc_I4_0)); preamble.Add(Instruction.Create(OpCodes.Stloc, result!)); }
        if (kind == "frame") preamble.Add(Instruction.Create(OpCodes.Call, Hook("FrameBegin")));
        else {
            preamble.Add(kind == "layer" ? Instruction.Create(OpCodes.Ldarg_0) : Instruction.Create(OpCodes.Ldc_I4, scopeId));
            preamble.Add(Instruction.Create(OpCodes.Call, Hook(kind == "layer" ? "EnterLayer" : "Enter")));
        }
        preamble.Add(Instruction.Create(OpCodes.Stloc, cookie));
        foreach (var instruction in preamble) il.InsertBefore(original[0], instruction);
        var finalReturn = Instruction.Create(OpCodes.Ret);
        var epilogue = result == null ? finalReturn : Instruction.Create(OpCodes.Ldloc, result);
        foreach (var index in receipt.ReturnIndices)
        {
            var ret = original[index];
            // Keep this original instruction object as the return prologue: every original
            // branch/switch/EH boundary targeting ret still enters with the original stack.
            ret.OpCode = result == null ? OpCodes.Ldc_I4_1 : OpCodes.Stloc; ret.Operand = result;
            var tail = new List<Instruction>();
            if (result != null) tail.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            tail.Add(Instruction.Create(OpCodes.Stloc, completed)); tail.Add(Instruction.Create(OpCodes.Leave, epilogue));
            var at = ret; foreach (var instruction in tail) { il.InsertAfter(at, instruction); at = instruction; }
        }
        var finallyStart = Instruction.Create(OpCodes.Ldloc, cookie);
        // Null original EH ends denote the old end-of-method, not the added epilogue.
        foreach (var handler in body.ExceptionHandlers) { handler.TryEnd ??= finallyStart; handler.HandlerEnd ??= finallyStart; }
        body.Instructions.Add(finallyStart); body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, completed));
        if (kind == "layer") body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, result!));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, Hook(kind == "frame" ? "FrameEnd" : kind == "layer" ? "ExitLayer" : "Exit")));
        body.Instructions.Add(Instruction.Create(OpCodes.Endfinally)); body.Instructions.Add(epilogue);
        if (result != null) body.Instructions.Add(finalReturn);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = original[0], TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = epilogue });
        Widen(method); body.MaxStackSize = Math.Max(body.MaxStackSize, 4);
        return receipt with { OriginalIndices = original.Select(i => body.Instructions.IndexOf(i)).ToArray(), AfterHash = Fingerprint(method) };
    }

    internal static int FontMetric(MethodReference method)
    {
        if (method.DeclaringType.FullName == "ReLogic.Graphics.DynamicSpriteFontExtensionMethods" && method.Name == "DrawString") return 10;
        if (method.DeclaringType.FullName != "ReLogic.Graphics.DynamicSpriteFont") return -1;
        return method.Name switch { "DrawCustomFast" => 10, "MeasureString" or "CreateWrappedText" or "CreateCroppedText" => 11, _ => -1 };
    }

    internal static MethodDefinition FontWrapper(TypeDefinition helper, string name, MethodReference target, OpCode opcode, int scope)
    {
        Require(!target.HasGenericParameters && target is not GenericInstanceMethod && opcode.Code is Code.Call or Code.Callvirt, "unsupported typed wrapper");
        Require(!target.HasThis || !target.DeclaringType.IsValueType, "value receiver needs explicit managed-reference wrapper");
        var wrapper = new MethodDefinition(name, MA.Assembly | MA.Static | MA.HideBySig, target.ReturnType);
        if (target.HasThis) wrapper.Parameters.Add(new ParameterDefinition("receiver", Mono.Cecil.ParameterAttributes.None, target.DeclaringType));
        foreach (var parameter in target.Parameters) wrapper.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        helper.Methods.Add(wrapper);
        foreach (var parameter in wrapper.Parameters) wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, parameter));
        wrapper.Body.Instructions.Add(Instruction.Create(opcode, target)); wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        Envelope(wrapper, helper, scope, "scope");
        var selected = wrapper.Body.Instructions[0]; var il = wrapper.Body.GetILProcessor();
        var fast = new List<Instruction> {
            Instruction.Create(OpCodes.Ldsfld, helper.Fields.Single(f => f.Name == "Active")), Instruction.Create(OpCodes.Brtrue, selected)
        };
        foreach (var parameter in wrapper.Parameters) fast.Add(Instruction.Create(OpCodes.Ldarg, parameter));
        fast.Add(Instruction.Create(opcode, target)); fast.Add(Instruction.Create(OpCodes.Ret));
        foreach (var instruction in fast) il.InsertBefore(selected, instruction);
        wrapper.Body.MaxStackSize = Math.Max(wrapper.Parameters.Count, 4);
        return wrapper;
    }

    static TypeDefinition MapRuntime(ModuleDefinition module)
    {
        using var source = AssemblyDefinition.ReadAssembly(typeof(UI51Template).Assembly.Location);
        var helper = new TypeDefinition("Terraria", "NXUIProfile51", TA.NotPublic | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        module.Types.Add(helper);
        var sourceHelper = source.MainModule.Types.Single(t => t.Name == nameof(UI51Template));
        Require(!sourceHelper.Methods.Any(m => m.IsConstructor), "UI51 runtime must not allocate through a cctor");
        var data = source.MainModule.Types.Where(t => t.Name.StartsWith("UI51", StringComparison.Ordinal) && t.IsValueType && !t.Name.EndsWith("Shape", StringComparison.Ordinal)).ToArray();
        var pairs = new List<(TypeDefinition Source, TypeDefinition Destination)> { (sourceHelper, helper) };
        var mapped = new Dictionary<string, TypeReference> { [sourceHelper.FullName] = helper };
        foreach (var type in data) {
            var dest = new TypeDefinition("", type.Name[4..], TA.NestedAssembly | TA.SequentialLayout | TA.Sealed | TA.BeforeFieldInit,
                new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary));
            helper.NestedTypes.Add(dest); mapped.Add(type.FullName, dest); pairs.Add((type, dest));
        }
        foreach (var pair in new[] { ("UI51MainShape", "Terraria.Main"), ("UI51EntityShape", "Terraria.Entity"), ("UI51PlayerShape", "Terraria.Player"), ("UI51ItemShape", "Terraria.Item"), ("UI51LayerShape", "Terraria.UI.GameInterfaceLayer") })
            mapped.Add(pair.Item1, Types(module).Single(t => t.FullName == pair.Item2));
        var vector = module.GetTypeReferences().Single(t => t.FullName == "Microsoft.Xna.Framework.Vector2");
        Require(vector.Scope.Name == "FNA", "Vector2 target not bound to pinned FNA"); mapped.Add("UI51VectorShape", vector.Resolve());
        var mapper = new RuntimeMapper(module, mapped);
        foreach (var (from, to) in pairs) {
            foreach (var field in from.Fields) {
                var dest = new FieldDefinition(field.Name, field.Attributes, mapper.Type(field.FieldType));
                if (field.HasConstant) dest.Constant = field.Constant;
                foreach (var attribute in field.CustomAttributes.Where(a => a.AttributeType.FullName == "System.ThreadStaticAttribute"))
                    dest.CustomAttributes.Add(new CustomAttribute((MethodReference)mapper.Member(attribute.Constructor)));
                to.Fields.Add(dest);
            }
            foreach (var method in from.Methods) {
                Require(!method.HasGenericParameters, "generic runtime helper not supported");
                var dest = new MethodDefinition(method.Name, method.Attributes, mapper.Type(method.ReturnType)) { ImplAttributes = method.ImplAttributes, CallingConvention = method.CallingConvention };
                foreach (var parameter in method.Parameters) dest.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, mapper.Type(parameter.ParameterType)));
                to.Methods.Add(dest);
            }
        }
        foreach (var (from, to) in pairs) foreach (var method in from.Methods)
            CopyBody(method, to.Methods.Single(m => mapper.SignatureMatches(method, m)), mapper.Type, mapper.Member);
        helper.Fields.Add(new FieldDefinition("SourceHash", Mono.Cecil.FieldAttributes.Assembly | Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Literal | Mono.Cecil.FieldAttributes.HasDefault, module.TypeSystem.String) { Constant = Program.SourceHash() });
        return helper;
    }

    internal static void CopyBody(MethodDefinition source, MethodDefinition target, Func<TypeReference, TypeReference> type, Func<object, object> member)
    {
        if (!source.HasBody) return;
        target.Body = new Mono.Cecil.Cil.MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
        foreach (var local in source.Body.Variables) target.Body.Variables.Add(new VariableDefinition(type(local.VariableType)));
        var map = source.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
        foreach (var instruction in source.Body.Instructions) {
            var dest = map[instruction]; dest.OpCode = instruction.OpCode;
            dest.Operand = instruction.Operand switch {
                null => null, Instruction branch => map[branch], Instruction[] branches => branches.Select(b => map[b]).ToArray(),
                VariableDefinition local => target.Body.Variables[local.Index], ParameterDefinition parameter => target.Parameters[parameter.Index],
                var operand => member(operand)
            };
            target.Body.Instructions.Add(dest);
        }
        foreach (var handler in source.Body.ExceptionHandlers) target.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType) {
            TryStart = map[handler.TryStart], TryEnd = handler.TryEnd == null ? null : map[handler.TryEnd], HandlerStart = map[handler.HandlerStart],
            HandlerEnd = handler.HandlerEnd == null ? null : map[handler.HandlerEnd], FilterStart = handler.FilterStart == null ? null : map[handler.FilterStart],
            CatchType = handler.CatchType == null ? null : type(handler.CatchType)
        });
    }

    sealed class RuntimeMapper
    {
        readonly ModuleDefinition module;
        readonly Dictionary<string, TypeReference> mapped;
        internal RuntimeMapper(ModuleDefinition module, Dictionary<string, TypeReference> mapped) { this.module = module; this.mapped = mapped; }
        internal TypeReference Type(TypeReference source)
        {
            if (mapped.TryGetValue(source.FullName, out var target)) return module.ImportReference(target);
            if (source is ByReferenceType reference) return new ByReferenceType(Type(reference.ElementType));
            if (source is ArrayType array) { Require(array.IsVector, "runtime multidimensional array not approved"); return new ArrayType(Type(array.ElementType)); }
            if (source is GenericInstanceType generic) { var result = new GenericInstanceType(Type(generic.ElementType)); foreach (var arg in generic.GenericArguments) result.GenericArguments.Add(Type(arg)); return result; }
            if (source is GenericParameter) return source;
            if (source.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.IntPtr or MetadataType.UIntPtr or MetadataType.Object or MetadataType.TypedByReference) return Primitive(module, source.FullName);
            Require(source.Namespace.StartsWith("System", StringComparison.Ordinal) && source.DeclaringType == null, "unmapped runtime type " + source.FullName);
            var scope = module.AssemblyReferences.Single(r => r.Name == (source.FullName == "System.Diagnostics.Stopwatch" ? "System" : "mscorlib"));
            return new TypeReference(source.Namespace, source.Name, module, scope, source.IsValueType);
        }
        internal bool SignatureMatches(MethodReference source, MethodReference target) => source.Name == target.Name && source.HasThis == target.HasThis &&
            Type(source.ReturnType).FullName == target.ReturnType.FullName && source.Parameters.Select(p => Type(p.ParameterType).FullName).SequenceEqual(target.Parameters.Select(p => p.ParameterType.FullName));
        internal object Member(object value)
        {
            if (value is TypeReference type) return Type(type);
            if (value is FieldReference field) {
                if (mapped.TryGetValue(field.DeclaringType.FullName, out var owner)) {
                    var dest = owner.Resolve().Fields.Single(f => f.Name == field.Name);
                    bool frameSkip = field.DeclaringType.FullName == "UI51MainShape" && field.Name == "FrameSkipMode" && field.FieldType.MetadataType == MetadataType.Int32;
                    if (frameSkip) Require(dest.FieldType.Resolve() is { IsEnum: true } enumType && enumType.Fields.Single(f => f.Name == "value__").FieldType.MetadataType == MetadataType.Int32, "FrameSkipMode must retain its original Int32-backed enum field");
                    Require(frameSkip || Type(field.FieldType).FullName == dest.FieldType.FullName, "mapped view field type mismatch: " + field.FullName);
                    return module.ImportReference(dest);
                }
                return new FieldReference(field.Name, Type(field.FieldType), Type(field.DeclaringType));
            }
            if (value is MethodReference method) {
                if (mapped.TryGetValue(method.DeclaringType.FullName, out var owner)) return module.ImportReference(owner.Resolve().Methods.Single(m => SignatureMatches(method, m)));
                Require(!method.HasGenericParameters && method is not GenericInstanceMethod, "generic runtime BCL method not approved: " + method.FullName);
                var dest = new MethodReference(method.Name, Type(method.ReturnType), Type(method.DeclaringType)) { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
                foreach (var parameter in method.Parameters) dest.Parameters.Add(new ParameterDefinition(Type(parameter.ParameterType)));
                return dest;
            }
            Require(value is not CallSite, "runtime calli not approved");
            return value;
        }
    }

    internal sealed class PinnedResolver : IAssemblyResolver
    {
        internal const string Core = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
        internal const string Runtime = "/mono-nx/dotnet_runtime/artifacts/bin/runtime/net9.0-libnx-Debug-arm64";
        readonly string game; readonly Dictionary<string, AssemblyDefinition> loaded = new(StringComparer.Ordinal);
        internal PinnedResolver(string game) { this.game = game; }
        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (loaded.TryGetValue(name.FullName, out var assembly)) return assembly;
            bool bcl = name.Name == "mscorlib" || name.Name == "System" || name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name == "Microsoft.CSharp";
            string path = name.Name == "System.Private.CoreLib" ? Core : Path.Combine(bcl ? Runtime : game, name.Name + ".dll");
            Require(File.Exists(path), "no pinned target resolver image: " + name.FullName);
            parameters.AssemblyResolver = this;
            assembly = AssemblyDefinition.ReadAssembly(path, parameters); loaded.Add(name.FullName, assembly); return assembly;
        }
        public void Dispose() { foreach (var assembly in loaded.Values) assembly.Dispose(); }
    }
}
