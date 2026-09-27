using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using TA = Mono.Cecil.TypeAttributes;

internal static class UpdatePatcher
{
    internal const string HelperName = "Terraria.NXUpdateProfile56";
    internal sealed record Receipt(string Method, string Kind, int ScopeId, int[] OriginalIndices, int OriginalLocals,
        int OriginalHandlers, bool OriginalInitLocals, int OriginalMaxStack, int[] ReturnIndices,
        string BeforeHash, string AfterHash);
    internal sealed record Target(int Token, int Instructions, int Scope, string Kind, string Name);

    // Exact untouched52 methods, pinned by the source-backed inventory.
    internal static readonly Target[] Targets = {
        new(0x06000269,431,8,"scope","System.Void Terraria.Liquid::UpdateLiquid()"),
        new(0x06000e9a,8,18,"scope","System.Void Terraria.Main::ConsumeAllMainThreadActions()"),
        new(0x06000e9b,35,0,"frame","System.Void Terraria.Main::Update(Microsoft.Xna.Framework.GameTime)"),
        new(0x06000e9e,888,1,"scope","System.Void Terraria.Main::DoUpdate(Microsoft.Xna.Framework.GameTime&)"),
        new(0x06000ea6,18,15,"scope","System.Void Terraria.Main::UpdateUIStates(Microsoft.Xna.Framework.GameTime)"),
        new(0x06000eae,24,2,"scope","System.Void Terraria.Main::DoUpdateInWorld()"),
        new(0x06000eb0,115,10,"scope","System.Void Terraria.Main::UpdateWorld_Players()"),
        new(0x06000eb2,191,11,"scope","System.Void Terraria.Main::UpdateWorld_NPCs()"),
        new(0x06000eb4,43,12,"scope","System.Void Terraria.Main::UpdateWorld_Projectiles()"),
        new(0x06000eb5,37,13,"scope","System.Void Terraria.Main::UpdateWorld_Items()"),
        new(0x06000eb9,18,3,"scope","System.Void Terraria.Main::UpdateWorld_WorldGenAndInvasion()"),
        new(0x06000ec5,18,14,"scope","System.Void Terraria.Main::DoUpdate_HandleInput()"),
        new(0x06000ecc,2946,16,"scope","System.Void Terraria.Main::AnimateTiles()"),
        new(0x06000ece,681,17,"scope","System.Void Terraria.Main::DoUpdate_AnimateWalls()"),
        new(0x060012f9,300,5,"scope","System.Void Terraria.Wiring::UpdateMech()"),
        new(0x060015aa,208,7,"scope","System.Void Terraria.WorldGen::CountTiles(System.Int32)"),
        new(0x060015ae,485,4,"scope","System.Void Terraria.WorldGen::UpdateWorld()"),
        new(0x060015af,52,9,"scope","System.Void Terraria.WorldGen::UpdatePrioritizedTownNPC()"),
        new(0x060015b0,102,9,"scope","System.Void Terraria.WorldGen::CheckForHousesNearAPlayer()"),
        new(0x06004fcf,18,6,"scope","System.Void Terraria.DataStructures.TileEntity::PerformUpdates()"),
        new(0x06000db5,56,-1,"flush","System.Void Terraria.Program::RunGame()"),
    };
    internal static bool IsAdded(TypeDefinition type) => type.FullName == HelperName || type.DeclaringType != null && IsAdded(type.DeclaringType);
    internal static IAssemblyResolver Resolver(string input) => new PinnedResolver(Path.GetDirectoryName(Path.GetFullPath(input))!);

    internal static Receipt[] Inject(ModuleDefinition module)
    {
        Require(module.Mvid == Guid.Parse(Program.InputMvid), "update56 requires exact untouched52 module");
        Require(!Types(module).Any(IsAdded), "update56 already injected");
        var originalMethods = Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
        var targets = Targets.ToDictionary(t => {
            var method = (MethodDefinition)module.LookupToken(t.Token);
            Require(method.FullName == t.Name && method.Body.Instructions.Count == t.Instructions, "pinned update target changed: " + t.Token.ToString("x8"));
            return method;
        });
        var before = originalMethods.ToDictionary(m => m, Fingerprint);
        var helper = MapRuntime(module);
        var receipts = new List<Receipt>();
        foreach (var method in originalMethods) {
            if (!targets.TryGetValue(method, out var target)) continue;
            Receipt receipt;
            if (target.Kind == "flush") {
                receipt = IdentityReceipt(method, "flush", -1);
                var instructions = method.Body.Instructions.ToArray();
                var gameRun = instructions.Single(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.DeclaringType.FullName == "Microsoft.Xna.Framework.Game" && r.Name == "Run" && r.Parameters.Count == 0);
                method.Body.GetILProcessor().InsertAfter(gameRun, Instruction.Create(OpCodes.Call, helper.Methods.Single(m => m.Name == "Flush")));
                Widen(method);
                receipt = receipt with { OriginalIndices = instructions.Select(i => method.Body.Instructions.IndexOf(i)).ToArray() };
            } else receipt = Envelope(method, helper, target.Scope, target.Kind);
            receipts.Add(receipt with { BeforeHash = before[method], AfterHash = Fingerprint(method) });
        }
        Require(receipts.Count == 21, "exact update plan requires twenty-one existing bodies");
        return receipts.ToArray();
    }

    static Receipt IdentityReceipt(MethodDefinition method, string kind, int id) => new(method.FullName, kind, id,
        Enumerable.Range(0, method.Body.Instructions.Count).ToArray(), method.Body.Variables.Count, method.Body.ExceptionHandlers.Count,
        method.Body.InitLocals, method.Body.MaxStackSize, method.Body.Instructions.Select((i, n) => (i, n)).Where(x => x.i.OpCode == OpCodes.Ret).Select(x => x.n).ToArray(),
        Fingerprint(method), Fingerprint(method));

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
        var preamble = new List<Instruction> { Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, completed) };
        if (kind == "frame") preamble.Add(Instruction.Create(OpCodes.Call, Hook("FrameBegin")));
        else {
            preamble.Add(Instruction.Create(OpCodes.Ldc_I4, scopeId));
            preamble.Add(Instruction.Create(OpCodes.Call, Hook("Enter")));
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
        body.Instructions.Add(Instruction.Create(OpCodes.Call, Hook(kind == "frame" ? "FrameEnd" : "Exit")));
        body.Instructions.Add(Instruction.Create(OpCodes.Endfinally)); body.Instructions.Add(epilogue);
        if (result != null) body.Instructions.Add(finalReturn);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = original[0], TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = epilogue });
        Widen(method); body.MaxStackSize = Math.Max(body.MaxStackSize, 4);
        return receipt with { OriginalIndices = original.Select(i => body.Instructions.IndexOf(i)).ToArray(), AfterHash = Fingerprint(method) };
    }

    static TypeDefinition MapRuntime(ModuleDefinition module)
    {
        using var source = AssemblyDefinition.ReadAssembly(typeof(Update56Template).Assembly.Location);
        var helper = new TypeDefinition("Terraria", "NXUpdateProfile56", TA.NotPublic | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        module.Types.Add(helper);
        var sourceHelper = source.MainModule.Types.Single(t => t.Name == nameof(Update56Template));
        Require(!sourceHelper.Methods.Any(m => m.IsConstructor), "Update56 runtime must not allocate through a cctor");
        var data = source.MainModule.Types.Where(t => t.Name.StartsWith("Update56", StringComparison.Ordinal) && t.IsValueType && !t.Name.EndsWith("Shape", StringComparison.Ordinal)).ToArray();
        var pairs = new List<(TypeDefinition Source, TypeDefinition Destination)> { (sourceHelper, helper) };
        var mapped = new Dictionary<string, TypeReference> { [sourceHelper.FullName] = helper };
        foreach (var type in data) {
            var dest = new TypeDefinition("", type.Name[8..], TA.NestedAssembly | TA.SequentialLayout | TA.Sealed | TA.BeforeFieldInit,
                new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary));
            helper.NestedTypes.Add(dest); mapped.Add(type.FullName, dest); pairs.Add((type, dest));
        }
        foreach (var pair in new[] { ("Update56MainShape", "Terraria.Main"), ("Update56EntityShape", "Terraria.Entity"), ("Update56PlayerShape", "Terraria.Player"), ("Update56ItemShape", "Terraria.Item") })
            mapped.Add(pair.Item1, Types(module).Single(t => t.FullName == pair.Item2));
        var vector = module.GetTypeReferences().Single(t => t.FullName == "Microsoft.Xna.Framework.Vector2");
        Require(vector.Scope.Name == "FNA", "Vector2 target not bound to pinned FNA"); mapped.Add("Update56VectorShape", vector.Resolve());
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
                    bool frameSkip = field.DeclaringType.FullName == "Update56MainShape" && field.Name == "FrameSkipMode" && field.FieldType.MetadataType == MetadataType.Int32;
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
