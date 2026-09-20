using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class TilePatcher
{
    internal const string DrawHash = "35e4b1654ab1c9113ac57c043310aaf82a40fab5406178dbe9e3e8c1f09bcb80";
    internal const string HelperName = "NXTileProfile44";
    internal sealed record Splice(int[] OriginalIndices, Dictionary<int, int> Entries, int OriginalLocals);
    internal static bool IsAdded(TypeDefinition t) => t.Name == HelperName || t.DeclaringType?.Name == HelperName;
    internal static void Accept(string input, string fna, string output, AssemblyDefinition original)
    {
        Require(Fingerprint(Program.Draw(original.MainModule)) == DrawHash, "unsupported Draw body/signature/locals/EH");
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(fna)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(input)), new ReaderParameters { AssemblyResolver = resolver });
        var plan = Inject(game.MainModule);
        game.MainModule.Mvid = Guid.Empty;
        using (var identity = new MemoryStream()) { game.Write(identity, new WriterParameters { Timestamp = 0 }); game.MainModule.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(identity.ToArray()).AsSpan(0, 16)); }
        using var stream = new MemoryStream(); game.Write(stream, new WriterParameters { Timestamp = 0 }); byte[] bytes = stream.ToArray();
        Require(Sha(bytes) == "e909fee83e8664b431cc21fbd980909ca99c8765c2c90ea2e19dedc73c940611", "output differs from frozen observer44");
        using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        Require(Body(Program.Draw(game.MainModule)) == Body(Program.Draw(changed.MainModule)), "serialized Draw differs");
        foreach (var m in Types(game.MainModule).Where(IsAdded).SelectMany(t => t.Methods))
            Require(Body(m) == Body(Types(changed.MainModule).SelectMany(t => t.Methods).Single(n => n.FullName == m.FullName)), "helper serialized body differs: " + m.FullName);
        var preservation = Audit.Preservation(original, changed, plan);
        var resources = PeResources.Fingerprints(File.ReadAllBytes(input));
        Require(resources.SequenceEqual(PeResources.Fingerprints(bytes)), "native resources changed");
        var signatures = Audit.Signatures(bytes);
        var malformedSignatureRejection = Audit.MalformedSignatureRejection(output);
        var sdk = Audit.Sdk(bytes, original, input);
        Json(Path.Combine(output, "raw-signatures.json"), signatures); Json(Path.Combine(output, "sdk-references.json"), sdk);
        using var framework = AssemblyDefinition.ReadAssembly(fna);
        using var build42 = AssemblyDefinition.ReadAssembly("/build/aot42-windows/runtime-romfs/Terraria.exe");
        var prior = Invoke("Probe", "Verify", build42, changed, framework, output)!;
        var proof = TileProof.Verify(original, changed, output);
        Require(Sha(File.ReadAllBytes(input)) == Program.InputHash, "input changed during acceptance");
        string destination = Path.Combine(output, "Terraria.exe");
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Json(Path.Combine(output, "acceptance.json"), new {
            accepted = true, inputSha256 = Program.InputHash, inputMvid = original.MainModule.Mvid, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid,
            changedMethods = new[] { new { name = Program.DrawName, before = DrawHash, after = Fingerprint(Program.Draw(changed.MainModule)) } },
            addedTypes = Types(changed.MainModule).Where(IsAdded).Select(t => t.FullName),
            addedMethods = Types(changed.MainModule).Where(IsAdded).SelectMany(t => t.Methods).Select(m => new { m.FullName, hash = Fingerprint(m) }),
            addedFields = Types(changed.MainModule).Where(IsAdded).SelectMany(t => t.Fields).Select(f => f.FullName),
            addedAssemblyReferences = changed.MainModule.AssemblyReferences.Select(r => r.FullName).Except(original.MainModule.AssemblyReferences.Select(r => r.FullName)),
            metricCount = 20, previousRegistryCount = 90, resultingRegistryCount = 110, registryCapacity = 512,
            boundaries = new { setup = "0000..01ee", loop = "01ef..090d (including initialization/checks/backedges)", post = "0912..09cd" },
            semantics = "Completed-region wall time includes in-region observer overhead; region publication excluded by restarting after publication. Counts publish in finally for partial passes; started at entry, completed only normal return. Visited counts attempted tile fetches including null creation paths. Eligible counts after active/layer filtering before debug-wall and special-type filtering. Calls count attempted DrawSingleTile invocations. Only completed selected calls contribute sampled count/time, never extrapolated. Per-pass phase rotates independently solid/non-solid through 0..31; select zero-based invocation (index+phase)&31 == 0. Empty/throwing passes also rotate. No RNG. First helper cctor on first Begin after original TimeLogger.Start. Int32 TimeLogger storage retains43 overflow limitations. No liquid sampling. No FPS, Switch or AOT performance claim.",
            preservation, resources, signatures, malformedSignatureRejection, sdk, priorObserverProof = prior, tileProof = proof
        });
        Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
    }
    internal static Splice Inject(ModuleDefinition module)
    {
        Require(!Types(module).Any(IsAdded), "already instrumented");
        var logger = Types(module).Single(t => t.FullName == "Terraria.TimeLogger");
        var helper = new TypeDefinition("", HelperName, TA.NestedAssembly | TA.Abstract | TA.Sealed, module.TypeSystem.Object); logger.NestedTypes.Add(helper);
        var valueType = new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary);
        var pass = new TypeDefinition("", "Pass", TA.NestedAssembly | TA.SequentialLayout | TA.Sealed | TA.BeforeFieldInit, valueType); helper.NestedTypes.Add(pass);
        using var template = AssemblyDefinition.ReadAssembly(typeof(TileTemplate).Assembly.Location);
        var sourceHelper = template.MainModule.Types.Single(t => t.Name == nameof(TileTemplate));
        var sourcePass = template.MainModule.Types.Single(t => t.Name == nameof(PassShape));
        var mapTypes = new Dictionary<string, TypeReference> { [nameof(TileTemplate)] = helper, [nameof(PassShape)] = pass, [nameof(LoggerShape)] = logger, [nameof(MetricShape)] = logger.NestedTypes.Single(t => t.Name == "TimeLogData") };
        var mapperType = Support.GetType("Patcher+Mapper", true)!;
        var mapper = Activator.CreateInstance(mapperType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { module, mapTypes }, null)!;
        TypeReference Type(TypeReference t) => (TypeReference)mapperType.GetMethod("Type", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mapper, new object[] { t })!;
        object Member(object o) => mapperType.GetMethod("Member", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mapper, new[] { o })!;
        foreach (var (source, target) in new[] { (sourceHelper, helper), (sourcePass, pass) }) {
            foreach (var f in source.Fields) { var n = new FieldDefinition(f.Name, f.Attributes, Type(f.FieldType)); if (f.HasConstant) n.Constant = f.Constant; target.Fields.Add(n); }
            foreach (var m in source.Methods) Invoke("Patcher", "Define", m, target, (Func<TypeReference, TypeReference>)Type);
        }
        foreach (var m in sourceHelper.Methods) Copy(m, helper.Methods.Single(n => n.Name == m.Name), Type, Member);
        var draw = Program.Draw(module); var originals = draw.Body.Instructions.ToArray(); int locals = draw.Body.Variables.Count;
        Require(draw.Body.ExceptionHandlers.Count == 0 && originals.Count(i => i.OpCode == OpCodes.Ret) == 1, "unexpected Draw handlers/returns");
        Instruction At(int offset) => originals.Single(i => i.Offset == offset);
        Require(At(0x01ef).OpCode == OpCodes.Ldloc_S && At(0x0912).OpCode == OpCodes.Ldarg_0 && At(0x09cd).OpCode == OpCodes.Ret, "region boundaries differ");
        Require(At(0x0203).OpCode == OpCodes.Ldsfld && At(0x025a).OpCode == OpCodes.Bne_Un && ((MethodReference)At(0x08eb).Operand).Name == "DrawSingleTile", "counter/sample boundaries differ");
        var state = new VariableDefinition(pass); var sample = new VariableDefinition(module.TypeSystem.Boolean); var sampleStart = new VariableDefinition(module.TypeSystem.Int64);
        draw.Body.Variables.Add(state); draw.Body.Variables.Add(sample); draw.Body.Variables.Add(sampleStart);
        MethodDefinition Hook(string name) => helper.Methods.Single(m => m.Name == name);
        FieldDefinition Field(string name) => pass.Fields.Single(f => f.Name == name);
        var clock = (MethodReference)Member(template.MainModule.ImportReference(typeof(System.Diagnostics.Stopwatch).GetMethod("GetTimestamp")!));
        var redirects = new Dictionary<Instruction, Instruction>();
        void Before(Instruction at, bool redirect, params Instruction[] added) {
            foreach (var i in added) draw.Body.GetILProcessor().InsertBefore(at, i);
            if (redirect) { redirects.Add(added[0], at); foreach (var i in originals) { if (ReferenceEquals(i.Operand, at)) i.Operand = added[0]; else if (i.Operand is Instruction[] targets) for (int j = 0; j < targets.Length; j++) if (targets[j] == at) targets[j] = added[0]; } }
        }
        Instruction[] Increment(string name) => new[] { Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldfld, Field(name)), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Stfld, Field(name)) };
        Before(originals[0], false, Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Call, Hook("Begin")));
        void Region(int offset, int region) => Before(At(offset), true, Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldc_I4, region), Instruction.Create(OpCodes.Call, Hook("Region")));
        Region(0x01ef, 0); Region(0x0912, 1); Region(0x09cd, 2);
        Before(At(0x0203), true, Increment("Visited")); Before(At(0x025f), true, Increment("Eligible"));
        var call = At(0x08eb);
        // Arguments are already on the evaluation stack. All inserted paths leave them
        // untouched and converge on the one original call instruction.
        var pre = new List<Instruction> {
            Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, Field("Calls")),
            Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, Field("Phase")),
            Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ldc_I4, 31), Instruction.Create(OpCodes.And),
            Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ceq), Instruction.Create(OpCodes.Stloc, sample)
        };
        pre.AddRange(Increment("Calls")); pre.AddRange(new[] { Instruction.Create(OpCodes.Ldloc, sample), Instruction.Create(OpCodes.Brfalse, call), Instruction.Create(OpCodes.Call, clock), Instruction.Create(OpCodes.Stloc, sampleStart) });
        Before(call, false, pre.ToArray());
        var next = call.Next; var post = new List<Instruction> { Instruction.Create(OpCodes.Ldloc, sample), Instruction.Create(OpCodes.Brfalse, next), Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldfld, Field("SampleTicks")), Instruction.Create(OpCodes.Call, clock), Instruction.Create(OpCodes.Ldloc, sampleStart), Instruction.Create(OpCodes.Sub), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Stfld, Field("SampleTicks")) };
        post.AddRange(Increment("Samples")); Before(next, false, post.ToArray());
        var ret = At(0x09cd); var finalRet = Instruction.Create(OpCodes.Ret); ret.OpCode = OpCodes.Leave; ret.Operand = finalRet;
        var finallyStart = Instruction.Create(OpCodes.Ldloca, state);
        draw.Body.Instructions.Add(finallyStart); draw.Body.Instructions.Add(Instruction.Create(OpCodes.Call, Hook("Finish"))); draw.Body.Instructions.Add(Instruction.Create(OpCodes.Endfinally)); draw.Body.Instructions.Add(finalRet);
        draw.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = originals[0], TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = finalRet });
        Widen(draw); draw.Body.MaxStackSize += 8;
        return new Splice(originals.Select(i => draw.Body.Instructions.IndexOf(i)).ToArray(), redirects.ToDictionary(p => draw.Body.Instructions.IndexOf(p.Key), p => draw.Body.Instructions.IndexOf(p.Value)), locals);
    }
}
