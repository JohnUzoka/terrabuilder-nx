using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class CostPatcher
{
    internal const string HelperName = "NXTileCost45", Accessor = "NXCost45Current";
    internal sealed record Plan(string Method, int[] OriginalIndices, Dictionary<int, int> Entries, int OriginalLocals, int OriginalHandlers, int[] ReturnsChanged);
    internal static bool IsAdded(TypeDefinition type) => type.Name == HelperName || type.DeclaringType?.Name == HelperName;
    internal static bool IsAccessor(MethodDefinition method) => method.Name == Accessor && method.DeclaringType.FullName is "Terraria.TimeLogger/TimeLogData" or "Terraria.TimeLogger/DataSeries";

    internal static void Run(string input, string fna, string output, AssemblyDefinition original, bool accept)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run acceptance inside the Linux monobuild container");
        var resolverType = Support.GetType("ReferenceAudit+PinnedResolver", true)!;
        using var resolver = (IAssemblyResolver)Activator.CreateInstance(resolverType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Path.GetDirectoryName(input)! }, null)!;
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(input)), new ReaderParameters { AssemblyResolver = resolver });
        var plans = Inject(game.MainModule);
        game.MainModule.Mvid = Guid.Empty;
        using (var identity = new MemoryStream()) { game.Write(identity, new WriterParameters { Timestamp = 0 }); game.MainModule.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(identity.ToArray()).AsSpan(0, 16)); }
        byte[] bytes;
        using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); bytes = stream.ToArray(); }
        using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        Json(Path.Combine(output, "injection-plan.json"), plans);
        if (!accept)
        {
            File.WriteAllBytes(Path.Combine(output, "Terraria.unaccepted.exe"), bytes);
            Json(Path.Combine(output, "emission.json"), new { accepted = false, sha256 = Sha(bytes), mvid = changed.MainModule.Mvid });
            Console.WriteLine("EMITTED UNACCEPTED " + output); return;
        }
        var preservation = CostAudit.Preservation(original, changed, plans);
        var resources = PeResources.Fingerprints(File.ReadAllBytes(input));
        Require(resources.SequenceEqual(PeResources.Fingerprints(bytes)), "native resources changed");
        var signatures = CostAudit.Signatures(bytes);
        var sdk = CostAudit.Sdk(bytes, original, input);
        Json(Path.Combine(output, "raw-signatures.json"), signatures); Json(Path.Combine(output, "sdk-references.json"), sdk);
        using var framework = AssemblyDefinition.ReadAssembly(fna);
        using var build42 = AssemblyDefinition.ReadAssembly("/build/aot42-windows/runtime-romfs/Terraria.exe");
        var prior43 = Invoke("Probe", "Verify", build42, changed, framework, output)!;
        var proof = CostProof.Verify(original, changed, output);
        Require(Sha(File.ReadAllBytes(input)) == Program.InputHash, "input changed during acceptance");
        string destination = Path.Combine(output, "Terraria.exe");
        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Json(Path.Combine(output, "acceptance.json"), new { accepted = true, inputSha256 = Program.InputHash, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid, changedMethods = plans.Select(p => p.Method), addedAccessors = Types(changed.MainModule).SelectMany(t => t.Methods).Where(IsAccessor).Select(m => m.FullName), newMetricRows = 28, expectedFullRegistryCount = 138, preservation, resources, signatures, sdk, prior43, proof });
        Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
    }

    internal static Plan[] Inject(ModuleDefinition module)
    {
        Require(!Types(module).Any(IsAdded), "already instrumented");
        var logger = Types(module).Single(t => t.FullName == "Terraria.TimeLogger");
        var metric = logger.NestedTypes.Single(t => t.Name == "TimeLogData");
        var series = logger.NestedTypes.Single(t => t.Name == "DataSeries");
        AddAccessors(module, logger, metric, series);
        var helper = new TypeDefinition("", HelperName, TA.NestedAssembly | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        logger.NestedTypes.Add(helper);
        var valueType = new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary);
        var context = new TypeDefinition("", "Context", TA.NestedAssembly | TA.SequentialLayout | TA.Sealed | TA.BeforeFieldInit, valueType);
        var sample = new TypeDefinition("", "Sample", TA.NestedAssembly | TA.SequentialLayout | TA.Sealed | TA.BeforeFieldInit, valueType);
        helper.NestedTypes.Add(context); helper.NestedTypes.Add(sample);
        using var template = AssemblyDefinition.ReadAssembly(typeof(CostTemplate).Assembly.Location);
        var source = template.MainModule.Types.Single(t => t.Name == nameof(CostTemplate));
        var contextSource = template.MainModule.Types.Single(t => t.Name == nameof(ContextShape));
        var sampleSource = template.MainModule.Types.Single(t => t.Name == nameof(SampleShape));
        var map = new Dictionary<string, TypeReference> { [nameof(CostTemplate)] = helper, [nameof(ContextShape)] = context, [nameof(SampleShape)] = sample, [nameof(CostMetricShape)] = metric, [nameof(CostLoggerShape)] = logger };
        var mapperType = Support.GetType("Patcher+Mapper", true)!;
        var mapper = Activator.CreateInstance(mapperType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { module, map }, null)!;
        TypeReference Type(TypeReference t) => (TypeReference)mapperType.GetMethod("Type", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mapper, new object[] { t })!;
        object Member(object o) => mapperType.GetMethod("Member", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mapper, new[] { o })!;
        foreach (var (from, to) in new[] { (source, helper), (contextSource, context), (sampleSource, sample) })
        {
            foreach (var field in from.Fields)
            {
                var added = new FieldDefinition(field.Name, field.Attributes, Type(field.FieldType));
                if (field.HasConstant) added.Constant = field.Constant;
                if (field.CustomAttributes.Any(a => a.AttributeType.FullName == "System.ThreadStaticAttribute"))
                {
                    var attribute = new TypeReference("System", "ThreadStaticAttribute", module, module.TypeSystem.CoreLibrary);
                    added.CustomAttributes.Add(new CustomAttribute(new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true }));
                }
                to.Fields.Add(added);
            }
            foreach (var method in from.Methods) Invoke("Patcher", "Define", method, to, (Func<TypeReference, TypeReference>)Type);
        }
        foreach (var method in source.Methods) Copy(method, helper.Methods.Single(m => m.Name == method.Name && m.Parameters.Count == method.Parameters.Count), Type, Member);
        MethodDefinition Hook(string name) => helper.Methods.Single(m => m.Name == name);
        return new[] { PatchDraw(Program.Draw(module), context, Hook), PatchSingle(Program.Single(module), sample, Hook) };
    }

    static void AddAccessors(ModuleDefinition module, TypeDefinition logger, TypeDefinition metric, TypeDefinition series)
    {
        Require(!metric.Methods.Any(m => m.Name == Accessor) && !series.Methods.Any(m => m.Name == Accessor), "existing observation accessor");
        var readSeries = new MethodDefinition(Accessor, MA.Assembly | MA.HideBySig, module.TypeSystem.Int32); series.Methods.Add(readSeries);
        foreach (var i in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, series.Fields.Single(f => f.Name == "values")), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, series.Fields.Single(f => f.Name == "next")), Instruction.Create(OpCodes.Ldelem_I4), Instruction.Create(OpCodes.Ret) }) readSeries.Body.Instructions.Add(i);
        readSeries.Body.MaxStackSize = 2;
        var readMetric = new MethodDefinition(Accessor, MA.Assembly | MA.HideBySig, module.TypeSystem.Int32); metric.Methods.Add(readMetric);
        foreach (var i in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, metric.Fields.Single(f => f.Name == "data")), Instruction.Create(OpCodes.Ldsfld, logger.Fields.Single(f => f.Name == "activeDataSeries")), Instruction.Create(OpCodes.Ldelem_Ref), Instruction.Create(OpCodes.Callvirt, readSeries), Instruction.Create(OpCodes.Ret) }) readMetric.Body.Instructions.Add(i);
        readMetric.Body.MaxStackSize = 2;
    }

    sealed class Splice
    {
        internal readonly MethodDefinition Method;
        internal readonly Instruction[] Original;
        readonly Dictionary<Instruction, Instruction> redirects = new();
        readonly int locals, handlers;
        internal readonly List<Instruction> Returns = new();
        internal Splice(MethodDefinition method) { Method = method; Original = method.Body.Instructions.ToArray(); locals = method.Body.Variables.Count; handlers = method.Body.ExceptionHandlers.Count; }
        internal void Before(Instruction target, bool redirect, params Instruction[] inserted)
        {
            foreach (var i in inserted) Method.Body.GetILProcessor().InsertBefore(target, i);
            if (!redirect) return;
            redirects.Add(inserted[0], target);
            foreach (var i in Original)
            {
                if (ReferenceEquals(i.Operand, target)) i.Operand = inserted[0];
                else if (i.Operand is Instruction[] targets) for (int n = 0; n < targets.Length; n++) if (targets[n] == target) targets[n] = inserted[0];
            }
        }
        internal Plan Finish()
        {
            Widen(Method); Method.Body.MaxStackSize += 8;
            return new Plan(Method.FullName, Original.Select(i => Method.Body.Instructions.IndexOf(i)).ToArray(), redirects.ToDictionary(p => Method.Body.Instructions.IndexOf(p.Key), p => Method.Body.Instructions.IndexOf(p.Value)), locals, handlers, Returns.Select(i => Method.Body.Instructions.IndexOf(i)).ToArray());
        }
    }
    static Plan PatchDraw(MethodDefinition method, TypeDefinition context, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method);
        var begin = edit.Original.Single(i => i.Operand is MethodReference m && m.DeclaringType.Name == "NXTileProfile44" && m.Name == "Begin");
        Require(begin.Previous.OpCode.Code is Code.Ldloca or Code.Ldloca_S, "44 Begin pass argument changed");
        var pass = (VariableDefinition)begin.Previous.Operand;
        var passType = pass.VariableType.Resolve();
        var saved = new VariableDefinition(context); method.Body.Variables.Add(saved);
        var first = begin.Next;
        edit.Before(first, false, Instruction.Create(OpCodes.Ldloca, saved), Instruction.Create(OpCodes.Ldloca, pass), Instruction.Create(OpCodes.Ldfld, passType.Fields.Single(f => f.Name == "Phase")), Instruction.Create(OpCodes.Ldloca, pass), Instruction.Create(OpCodes.Ldfld, passType.Fields.Single(f => f.Name == "Offset")), Instruction.Create(OpCodes.Ldc_I4, 10), Instruction.Create(OpCodes.Div), Instruction.Create(OpCodes.Ldc_I4, 14), Instruction.Create(OpCodes.Mul), Instruction.Create(OpCodes.Call, hook("Enter")));
        var ret = edit.Original.Single(i => i.OpCode == OpCodes.Ret);
        var finalRet = Instruction.Create(OpCodes.Ret); ret.OpCode = OpCodes.Leave; ret.Operand = finalRet; edit.Returns.Add(ret);
        var finallyStart = Instruction.Create(OpCodes.Ldloc, saved);
        method.Body.Instructions.Add(finallyStart); method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, hook("Finish"))); method.Body.Instructions.Add(Instruction.Create(OpCodes.Endfinally)); method.Body.Instructions.Add(finalRet);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = finalRet });
        return edit.Finish();
    }
    static Plan PatchSingle(MethodDefinition method, TypeDefinition sample, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method);
        Require(method.Body.ExceptionHandlers.Count == 0, "unexpected original Single handlers");
        var state = new VariableDefinition(sample); method.Body.Variables.Add(state);
        var selected = sample.Fields.Single(f => f.Name == "Selected");
        edit.Before(edit.Original[0], false, Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Call, hook("BeginSample")));
        int Group(Instruction instruction)
        {
            if (instruction.OpCode == OpCodes.Newobj && instruction.Operand is MethodReference ctor && ctor.DeclaringType.FullName == "Terraria.DataStructures.TileDrawInfo") return 0;
            if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) || instruction.Operand is not MethodReference called) return -1;
            if (called.DeclaringType.FullName == "Terraria.Lighting" && called.Name == "GetColor" && called.Parameters.Count == 2) return 1;
            if (called.DeclaringType.FullName != "Terraria.GameContent.Drawing.TileDrawing") return -1;
            return called.Name switch { "GetTileDrawData" or "GetTileOutlineInfo" or "DrawTiles_GetLightOverride" or "GetFinalLight" => 1, "GetTileDrawTexture" => 2, "DrawBasicTile" or "DrawTile_MinecartTrack" or "DrawXmasTree" => 3, _ => -1 };
        }
        var counts = new int[4];
        foreach (var call in edit.Original)
        {
            int group = Group(call); if (group < 0) continue;
            counts[group]++;
            var next = call.Next;
            edit.Before(call, true, Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, selected), Instruction.Create(OpCodes.Brfalse, call), Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldc_I4, group), Instruction.Create(OpCodes.Call, hook("BeginOp")));
            edit.Before(next, false, Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, selected), Instruction.Create(OpCodes.Brfalse, next), Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldc_I4, group), Instruction.Create(OpCodes.Call, hook("EndOp")));
        }
        Require(counts.SequenceEqual(new[] { 1, 6, 4, 3 }), "unexpected grouped callsite inventory: " + string.Join(',', counts));
        var returns = edit.Original.Where(i => i.OpCode == OpCodes.Ret).ToArray();
        Require(returns.Length == 5, "unexpected Single return count");
        foreach (var ret in returns) edit.Before(ret, true, Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, selected), Instruction.Create(OpCodes.Brfalse, ret), Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Call, hook("EndSample")));
        return edit.Finish();
    }
}
