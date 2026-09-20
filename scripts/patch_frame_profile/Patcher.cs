using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Program;
using MA = Mono.Cecil.MethodAttributes;
using FA = Mono.Cecil.FieldAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class Patcher
{
    internal static readonly Dictionary<string, string> Expected = new()
    {
        ["System.Void Terraria.Main::Update(Microsoft.Xna.Framework.GameTime)"] = "10bac959ef4535d0c7ceaa16026703698a4d7437deb5368fa639b13e117ee0bb",
        ["System.Void Terraria.Main::Draw(Microsoft.Xna.Framework.GameTime)"] = "df74e8c1c9f8be8de6244af43f77e99746f68fc8b2267b3714010e502d4de8bb",
        ["Terraria.TimeLogger/TimeLogData Terraria.TimeLogger::NewEntry(System.String,System.Nullable`1<System.TimeSpan>)"] = "7c77b2b0d6fc29f8f7092ce2985c97b77ed1b3716bc4cf6aee817ac05bf05e19",
        ["Terraria.TimeLogger/TimeLogData Terraria.TimeLogger::NewCounterEntry(System.String,System.Int32)"] = "edb52f35c4609a965d1479deb5060de207fc1c311f974cd007a8b46de9a61d79",
        ["System.Void Terraria.TimeLogger::StartNextFrame()"] = "51d1efc272a4990d299da66cc00fc907cf50f3c54cb4c8b1cd1bed87dddcb3a9",
        ["System.Void Terraria.TimeLogger/DataSeries::Reset()"] = "f7a6230cf9e964d5c19c0f59af063c2d70ad426127e9015e35ae61b494f091fe",
        ["System.Void Terraria.TimeLogger/DataSeries::StartNextFrame()"] = "f9d2279e5d31860fcc8003d0a59a0b0148ab231c95f0d0f9c208733ba2f37ab6",
        ["System.Void Terraria.Program::RunGame()"] = "30bc0d63aa2e30961829a895b6fca25c00819787b73fb8cddb15e6382a65adcf"
    };
    internal static void Accept(string input, string fna, string output, AssemblyDefinition original, AssemblyDefinition framework)
    {
        Require(Sha(File.ReadAllBytes(fna)) == "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f", "FNA SHA differs");
        foreach (var pair in Expected) Require(Fingerprint(Types(original.MainModule).SelectMany(t => t.Methods).Single(m => m.FullName == pair.Key)) == pair.Value, "unsupported method shape: " + pair.Key);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(fna)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(input)), new ReaderParameters { AssemblyResolver = resolver });
        Inject(candidate.MainModule);
        candidate.MainModule.Mvid = Guid.Empty;
        using (var identity = new MemoryStream())
        {
            candidate.Write(identity, new WriterParameters { Timestamp = 0 });
            candidate.MainModule.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(identity.ToArray()).AsSpan(0, 16));
        }
        using var stream = new MemoryStream();
        candidate.Write(stream, new WriterParameters { Timestamp = 0 });
        byte[] bytes = stream.ToArray();
        Require(Sha(bytes) == "87b833138e342ff8ac8378535d181bcf2e185d9505e76d612cd9918869bfb061", "injection differs from frozen ECMA-correct43 image");
        using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var preservation = Preservation.Verify(original, changed);
        var resources = PeResources.Fingerprints(File.ReadAllBytes(input));
        Require(resources.SequenceEqual(PeResources.Fingerprints(bytes)), "native resource payload changed");
        var sdkReferences = ReferenceAudit.Verify(bytes, original, input);
        Json(Path.Combine(output, "sdk-references.json"), sdkReferences);
        var rawSignatures = RawSignatures.Verify(bytes);
        Json(Path.Combine(output, "raw-signatures.json"), rawSignatures);
        var rejectedSignatureFixture = RawSignatures.VerifyMalformedRejection(output);
        var proof = Probe.Verify(original, changed, framework, output);
        Require(Sha(File.ReadAllBytes(input)) == InputHash, "input changed during verification");
        var destination = Path.Combine(output, "Terraria.exe");
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Json(Path.Combine(output, "acceptance.json"), new {
            accepted = true, inputSha256 = InputHash, inputMvid = original.MainModule.Mvid, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid,
            changedMethods = Types(changed.MainModule).SelectMany(t => t.Methods).Where(m => Expected.ContainsKey(m.FullName)).Select(m => new { m.FullName, before = Expected[m.FullName], after = Fingerprint(m), il = Body(m) }),
            addedTypes = Types(changed.MainModule).Where(t => t.Name == "NXFrameProfile43").Select(t => t.FullName),
            addedMethods = Types(changed.MainModule).SelectMany(t => t.Methods).Where(m => m.DeclaringType.Name == "NXFrameProfile43" || m.Name == "NXProfileRead").Select(m => new { m.FullName, hash = Fingerprint(m) }),
            addedFields = new[] { "System.Int32 Terraria.TimeLogger/TimeLogData::_nxProfileId", "System.Boolean Terraria.TimeLogger/DataSeries::_nxProfileReset" },
            addedAssemblyReferences = changed.MainModule.AssemblyReferences.Select(r => r.FullName).Except(original.MainModule.AssemblyReferences.Select(r => r.FullName)),
            preservation, resources, sdkReferences, rawSignatures, rejectedSignatureFixture, proof,
            semantics = "Only exactly-one completed Draw epochs contribute. No-draw, partial and repeated-draw epochs are explicitly excluded. Update calls are counted at wrapper entry. Current used[next]/values[next] sampled before queued callbacks, ABTest and clears. State is draw-begin; changes are sampled at stage completions/end/boundary, not continuously observed. A/B series is sample-time. Negative raw values are retained but excluded from valid averages. TimeLogger overlap is not additive CPU time. Reports run before original frame advance; final flush occurs only immediately after Game.Run returns normally. No Switch/AOT/hardware performance claim."
        });
        Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
    }
    internal static void Inject(ModuleDefinition module)
    {
        TypeDefinition T(string name) => Types(module).Single(t => t.FullName == name);
        var logger = T("Terraria.TimeLogger"); var main = T("Terraria.Main");
        var metric = T("Terraria.TimeLogger/TimeLogData"); var series = T("Terraria.TimeLogger/DataSeries");
        Require(!Types(module).Any(t => t.Name == "NXFrameProfile43") && !metric.Fields.Any(f => f.Name == "_nxProfileId"), "already patched");
        metric.Fields.Add(new FieldDefinition("_nxProfileId", FA.Assembly, module.TypeSystem.Int32));
        var resetFlag = new FieldDefinition("_nxProfileReset", FA.Private, module.TypeSystem.Boolean);
        series.Fields.Add(resetFlag);
        foreach (var (name, dirty) in new[] { ("Reset", 1), ("StartNextFrame", 0) })
        {
            var method = series.Methods.Single(m => m.Name == name);
            var ret = method.Body.Instructions.Last();
            Require(ret.OpCode == OpCodes.Ret && method.Body.Instructions.Count(i => i.OpCode == OpCodes.Ret) == 1 && !method.Body.Instructions.Any(i => ReferenceEquals(i.Operand, ret)), "series observer return shape");
            Before(method, ret, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4, dirty), Instruction.Create(OpCodes.Stfld, resetFlag));
        }
        var runtime = new TypeDefinition("", "NXFrameProfile43", TA.NestedAssembly | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        logger.NestedTypes.Add(runtime);
        using var templates = AssemblyDefinition.ReadAssembly(typeof(ProfileTemplate).Assembly.Location);
        var source = templates.MainModule.Types.Single(t => t.Name == nameof(ProfileTemplate));
        var sourceSeries = templates.MainModule.Types.Single(t => t.Name == nameof(SeriesView));
        var types = new Dictionary<string, TypeReference> {
            [nameof(ProfileTemplate)] = runtime, [nameof(MainView)] = main, [nameof(LoggerView)] = logger,
            [nameof(MetricView)] = metric, [nameof(SeriesView)] = series
        };
        var map = new Mapper(module, types);
        foreach (var field in source.Fields)
            runtime.Fields.Add(new FieldDefinition(field.Name, field.Attributes, map.Type(field.FieldType)) { Constant = field.Constant });
        foreach (var method in source.Methods) Define(method, runtime, map.Type);
        var read = sourceSeries.Methods.Single(m => m.Name == "NXProfileRead");
        Define(read, series, map.Type).Attributes = MA.Assembly | MA.HideBySig;
        foreach (var method in source.Methods) CopyBody(method, runtime.Methods.Single(m => m.Name == method.Name), map.Type, map.Member);
        CopyBody(read, series.Methods.Single(m => m.Name == read.Name), map.Type, map.Member);
        MethodDefinition Hook(string name) => runtime.Methods.Single(m => m.Name == name);
        foreach (var (name, kind) in new[] { ("NewEntry", 0), ("NewCounterEntry", 1) })
        {
            var method = logger.Methods.Single(m => m.Name == name);
            var ret = method.Body.Instructions.Last(); Require(ret.OpCode == OpCodes.Ret, "factory return shape");
            // Keep returned object on the stack; registration runs after the original List.Add.
            Before(method, ret, Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldfld, metric.Fields.Single(f => f.Name == "name")), Instruction.Create(OpCodes.Ldc_I4, kind), Instruction.Create(OpCodes.Call, Hook("Register")), Instruction.Create(OpCodes.Stfld, metric.Fields.Single(f => f.Name == "_nxProfileId")));
        }
        var advance = logger.Methods.Single(m => m.Name == "StartNextFrame");
        Before(advance, advance.Body.Instructions[0], Instruction.Create(OpCodes.Call, Hook("Boundary")));
        var update = main.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 1);
        Before(update, update.Body.Instructions[0], Instruction.Create(OpCodes.Call, Hook("Update")));
        var draw = main.Methods.Single(m => m.Name == "Draw" && m.Parameters.Count == 1);
        var stores = draw.Body.Instructions.Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "_isDrawingOrUpdating").ToArray();
        Require(stores.Length == 2 && draw.Body.ExceptionHandlers.Count == 0, "Draw guard shape");
        After(draw, stores[0], Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, Hook("Begin")));
        After(draw, stores[1], Instruction.Create(OpCodes.Call, Hook("End")));
        var calls = draw.Body.Instructions.Where(i => i.Operand is MethodReference m && (m.Name is "EnsureRenderTargetContent" or "DoDraw" or "TransferCompletedAssets" || m.Name == "Invoke")).ToArray();
        Require(calls.Length == 4, "Draw sibling call shape");
        for (int i = 0; i < calls.Length; i++)
        {
            Before(draw, calls[i], Instruction.Create(OpCodes.Call, Hook("StageBegin")));
            After(draw, calls[i], Instruction.Create(OpCodes.Ldc_I4, i), Instruction.Create(OpCodes.Call, Hook("StageEnd")));
        }
        var run = T("Terraria.Program").Methods.Single(m => m.Name == "RunGame");
        var gameRun = run.Body.Instructions.Single(i => i.Operand is MethodReference m && m.FullName == "System.Void Microsoft.Xna.Framework.Game::Run()");
        After(run, gameRun, Instruction.Create(OpCodes.Call, Hook("Flush")));
        foreach (var method in Types(module).SelectMany(t => t.Methods).Where(m => Expected.ContainsKey(m.FullName))) WidenBranches(method);
        Require(module.AssemblyReferences.All(r => !r.Name.StartsWith("PatchFrame")), "tool reference leaked");
    }
    internal static MethodDefinition Define(MethodDefinition source, TypeDefinition dest, Func<TypeReference, TypeReference> type)
    {
        var method = new MethodDefinition(source.Name, source.Attributes, type(source.ReturnType)) { ImplAttributes = source.ImplAttributes };
        foreach (var p in source.Parameters) method.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, type(p.ParameterType)));
        dest.Methods.Add(method); return method;
    }
    internal sealed class Mapper
    {
        readonly ModuleDefinition module;
        readonly Dictionary<string, TypeReference> types;
        readonly Dictionary<string, MemberReference> existing;
        internal Mapper(ModuleDefinition module, Dictionary<string, TypeReference> types)
        {
            this.module = module; this.types = types;
            existing = Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MemberReference>().GroupBy(m => m.FullName).ToDictionary(g => g.Key, g => g.First());
        }
        internal static TypeReference Primitive(ModuleDefinition module, string name) => name switch {
            "System.Void" => module.TypeSystem.Void, "System.Boolean" => module.TypeSystem.Boolean,
            "System.Char" => module.TypeSystem.Char, "System.SByte" => module.TypeSystem.SByte,
            "System.Byte" => module.TypeSystem.Byte, "System.Int16" => module.TypeSystem.Int16,
            "System.UInt16" => module.TypeSystem.UInt16, "System.Int32" => module.TypeSystem.Int32,
            "System.UInt32" => module.TypeSystem.UInt32, "System.Int64" => module.TypeSystem.Int64,
            "System.UInt64" => module.TypeSystem.UInt64, "System.Single" => module.TypeSystem.Single,
            "System.Double" => module.TypeSystem.Double, "System.String" => module.TypeSystem.String,
            "System.IntPtr" => module.TypeSystem.IntPtr, "System.UIntPtr" => module.TypeSystem.UIntPtr,
            "System.Object" => module.TypeSystem.Object, "System.TypedReference" => module.TypeSystem.TypedReference,
            _ => throw new InvalidDataException("unknown primitive " + name)
        };
        internal TypeReference Type(TypeReference t)
        {
            if (types.TryGetValue(t.FullName, out var mapped)) return mapped;
            if (t is GenericParameter) return t;
            if (t is ArrayType a) return new ArrayType(Type(a.ElementType), a.Rank);
            if (t is ByReferenceType b) return new ByReferenceType(Type(b.ElementType));
            if (t is GenericInstanceType g) { var copy = new GenericInstanceType(Type(g.ElementType)); foreach (var arg in g.GenericArguments) copy.GenericArguments.Add(Type(arg)); return copy; }
            if (RawSignatures.PrimitiveNames.Contains(t.FullName)) return Primitive(module, t.FullName);
            Require(t.Namespace.StartsWith("System"), "unmapped template type " + t.FullName);
            var scope = module.AssemblyReferences.Single(r => r.Name == (t.FullName == "System.Diagnostics.Stopwatch" ? "System" : "mscorlib"));
            return new TypeReference(t.Namespace, t.Name, module, scope, t.IsValueType);
        }
        internal object Member(object o)
        {
            if (o is TypeReference t) return Type(t);
            if (o is FieldReference f)
            {
                if (types.TryGetValue(f.DeclaringType.FullName, out var target)) return ((TypeDefinition)target).Fields.Single(x => x.Name == f.Name);
                if (existing.TryGetValue(f.FullName, out var old)) return old;
                return new FieldReference(f.Name, Type(f.FieldType), Type(f.DeclaringType));
            }
            if (o is MethodReference m)
            {
                if (m.DeclaringType.FullName == nameof(MainView) && m.Name == "get_IsActive")
                    return existing.Values.OfType<MethodReference>().First(x => x.Name == "get_IsActive" && x.DeclaringType.FullName == "Microsoft.Xna.Framework.Game");
                if (types.TryGetValue(m.DeclaringType.FullName, out var target)) return ((TypeDefinition)target).Methods.Single(x => x.Name == m.Name);
                if (existing.TryGetValue(m.FullName, out var old)) return old;
                var copy = new MethodReference(m.Name, Type(m.ReturnType), Type(m.DeclaringType)) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention };
                foreach (var p in m.Parameters) copy.Parameters.Add(new ParameterDefinition(Type(p.ParameterType)));
                Require(!m.HasGenericParameters && m is not GenericInstanceMethod, "generic template method not approved");
                return copy;
            }
            return o;
        }
    }
    internal static void CopyBody(MethodDefinition source, MethodDefinition target, Func<TypeReference, TypeReference> type, Func<object, object> map, int parameterOffset = 0)
    {
        target.Body = new Mono.Cecil.Cil.MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize + 8 };
        foreach (var local in source.Body.Variables) target.Body.Variables.Add(new VariableDefinition(type(local.VariableType)));
        var instructions = source.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
        foreach (var i in source.Body.Instructions)
        {
            var copy = instructions[i]; copy.OpCode = i.OpCode;
            copy.Operand = i.Operand switch {
                null => null, Instruction b => instructions[b], Instruction[] a => a.Select(b => instructions[b]).ToArray(),
                VariableDefinition v => target.Body.Variables[v.Index], ParameterDefinition p => target.Parameters[p.Index + parameterOffset], var o => map(o)
            };
            if (copy.OpCode == OpCodes.Callvirt && copy.Operand is MethodReference m && !m.HasThis) copy.OpCode = OpCodes.Call;
            target.Body.Instructions.Add(copy);
        }
        foreach (var e in source.Body.ExceptionHandlers) target.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType) {
            TryStart = instructions[e.TryStart], TryEnd = e.TryEnd == null ? null : instructions[e.TryEnd], HandlerStart = instructions[e.HandlerStart], HandlerEnd = e.HandlerEnd == null ? null : instructions[e.HandlerEnd], FilterStart = e.FilterStart == null ? null : instructions[e.FilterStart], CatchType = e.CatchType == null ? null : type(e.CatchType)
        });
    }
    internal static void Before(MethodDefinition m, Instruction at, params Instruction[] additions) { foreach (var i in additions) m.Body.GetILProcessor().InsertBefore(at, i); }
    internal static void After(MethodDefinition m, Instruction at, params Instruction[] additions) { foreach (var i in additions) { m.Body.GetILProcessor().InsertAfter(at, i); at = i; } }
    internal static void WidenBranches(MethodDefinition m)
    {
        foreach (var i in m.Body.Instructions) if (i.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            i.OpCode = typeof(OpCodes).GetFields().Select(f => f.GetValue(null)).OfType<OpCode>().Single(o => o.Name == i.OpCode.Name[..^2]);
    }
}
