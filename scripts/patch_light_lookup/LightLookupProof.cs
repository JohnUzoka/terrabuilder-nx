using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class LightLookupProof
{
    const string SourceHash = "d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298";
    const string SourceMvid = "2a9040da-3f4b-844f-a14b-3056cdda95ba";
    const string Drawing = "Terraria.GameContent.Drawing.TileDrawing";
    const string Lookup = "Microsoft.Xna.Framework.Color Terraria.Lighting::GetColor(System.Int32,System.Int32)";
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
    delegate LightLookupColor Prefix(LightLookupDrawing owner, int x, int y, LightLookupTile tile, ushort type,
        ref short fx, ref short fy, ref int width, ref int height, ref int top, ref int half, ref int addX, ref int addY,
        ref int effects, ref LightLookupTexture? texture, ref LightLookupRectangle rectangle, ref LightLookupColor color);
    delegate void Tail(LightLookupDrawing owner, int x, int y, LightLookupTile tile, ushort type,
        ref short fx, ref short fy, ref int width, ref int height, ref int top, ref int half, ref int addX, ref int addY,
        ref int effects, ref LightLookupTexture? texture, ref LightLookupRectangle rectangle, ref LightLookupColor color, LightLookupColor light);

#if LIGHT_LOOKUP_PROOF_STANDALONE
    public static int Main(string[] args)
    {
        try
        {
            Require(args.Length is 2 or 3, "usage: baseline output-directory [candidate-for-proof]");
            Run(args[0], args[1], args.Length == 3 ? args[2] : null);
            Console.WriteLine("LIGHT LOOKUP STRUCTURAL AND SLICE PROOF PASS; semantic approval NOT implied");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
#endif

    internal static object Run(string baselinePath, string outputDirectory, string? candidatePath = null)
    {
        byte[] baselineBytes = File.ReadAllBytes(baselinePath);
        Require(Sha(baselineBytes) == SourceHash, "unsupported baseline SHA256");
        Require(!Directory.Exists(outputDirectory) && !File.Exists(outputDirectory), "fresh proof directory required");
        Directory.CreateDirectory(outputDirectory);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(baselinePath))!);
        using var baseline = AssemblyDefinition.ReadAssembly(new MemoryStream(baselineBytes), new ReaderParameters { AssemblyResolver = resolver });
        Require(baseline.MainModule.Mvid.ToString() == SourceMvid, "unsupported baseline MVID");
        var original = Find(baseline);
        byte[] candidateBytes;
        if (candidatePath == null)
        {
            using var experiment = AssemblyDefinition.ReadAssembly(new MemoryStream(baselineBytes), new ReaderParameters { AssemblyResolver = resolver });
            InjectForProof(Find(experiment));
            using var buffer = new MemoryStream();
            experiment.Write(buffer, new WriterParameters { Timestamp = 0 }); candidateBytes = buffer.ToArray();
            File.WriteAllBytes(Path.Combine(outputDirectory, "candidate-for-proof-only.exe"), candidateBytes);
        }
        else candidateBytes = File.ReadAllBytes(candidatePath);
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
        var guarded = Find(candidate);
        var checks = new List<string>();
        void Check(bool ok, string label) { Require(ok, label); checks.Add(label); }
        var structural = Validate(original, guarded);
        checks.Add("all original serialized instructions, operands, branch/switch targets, parameters, locals, handlers and method flags are identical after removing exactly five guard instructions");
        var baselineFlow = FlowProof(original, false);
        var candidateFlow = FlowProof(guarded, true);
        checks.Add("exhaustive 65536-value conservative CFG: each light-local read is reachable only for its required type; retained lookup dominates every read");
        var adversarial = Adversarial(original, guarded);
        var receipts = new List<object>();
        byte[] probeBytes = BuildSlices(original, guarded, receipts);
        File.WriteAllBytes(Path.Combine(outputDirectory, "LightLookupSlices.dll"), probeBytes);
        var loaded = Assembly.Load(probeBytes);
        var hooks = loaded.GetType("Probe.LightLookupHooks", true)!;
        var prefixBefore = hooks.GetMethod("BaselinePrefix")!.CreateDelegate<Prefix>();
        var prefixAfter = hooks.GetMethod("CandidatePrefix")!.CreateDelegate<Prefix>();
        var dynamic = DynamicProof(hooks, prefixBefore, prefixAfter, Check);
        Check(Sha(File.ReadAllBytes(baselinePath)) == SourceHash, "baseline input remains byte-identical");
        if (candidatePath != null) Check(Sha(File.ReadAllBytes(candidatePath)) == Sha(candidateBytes), "caller candidate remains byte-identical");
        var result = new
        {
            passed = true, semanticApproval = false, promotionApproved = false,
            baselineSha256 = SourceHash, baselineMvid = SourceMvid, candidateSha256 = Sha(candidateBytes),
            candidateOrigin = candidatePath == null ? "independent in-memory five-instruction experiment, serialized and reread" : "caller-supplied serialized candidate",
            structural, baselineFlow, candidateFlow, adversarial, probeSha256 = Sha(probeBytes), receipts, dynamic,
            checkCount = checks.Count, checks,
            intentionalDifferences = new[] { "For all ushort values other than 637/638, the original lookup call and its local store are bypassed; lookup results are otherwise unobserved by this method.", "Any exception, initialization, side effect, or dependency failure occurring exclusively inside a skipped query is also bypassed. The injected-failure scenario demonstrates this difference; it is not treated as harmless." },
            limitations = new[] { "Dynamic proof executes real serialized IL slices, NOT the complete 5140-instruction GetTileDrawData method or a game scene.", "Prefix is original entry through the light store, with an observer return. Tail637/Tail638 are original consumer blocks with a supplied local0 value. Intervening tile adjustment and dispatch code are structurally analyzed but not JIT-executed here.", "Main.tileFrame, Lighting.GetColor, rectangle/colour primitives, texture and asset calls are host fixtures. Colours establish identical arguments/propagation through the original calls, not a second proof of FNA arithmetic or actual lighting engine correctness.", "No actual Lighting type initializer, BeforeFieldInit schedule, engine lifetime, rendering thread, reentrancy, gameplay, hardware performance, mod compatibility, protocol or save semantics is established." }
        };
        File.WriteAllText(Path.Combine(outputDirectory, "light-lookup-proof.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return result;
    }

    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("light proof: " + message); }
    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static MethodDefinition Find(AssemblyDefinition image) => image.MainModule.Types.Single(t => t.FullName == Drawing).Methods.Single(m => m.Name == "GetTileDrawData" && m.Parameters.Count == 16);
    static Instruction At(MethodDefinition method, int offset) => method.Body.Instructions.Single(i => i.Offset == offset);
    static void InjectForProof(MethodDefinition method)
    {
        var first = At(method, 0x44); var next = At(method, 0x4c); var il = method.Body.GetILProcessor();
        foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg, method.Parameters[3]), Instruction.Create(OpCodes.Ldc_I4, 637), Instruction.Create(OpCodes.Sub), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Bgt_Un, next) }) il.InsertBefore(first, instruction);
    }
    static int Argument(MethodDefinition method, Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3,
        Code.Ldarg or Code.Ldarg_S => ((ParameterDefinition)instruction.Operand).Index + (method.HasThis ? 1 : 0), _ => -1
    };
    static int? Constant(Instruction i) => i.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => -1, Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
        Code.Ldc_I4 => (int)i.Operand, Code.Ldc_I4_S => (sbyte)i.Operand, _ => null
    };
    static int Local(Instruction i) => i.OpCode.Code switch
    {
        Code.Ldloc_0 or Code.Stloc_0 => 0, Code.Ldloc_1 or Code.Stloc_1 => 1, Code.Ldloc_2 or Code.Stloc_2 => 2, Code.Ldloc_3 or Code.Stloc_3 => 3,
        Code.Ldloc or Code.Ldloc_S or Code.Stloc or Code.Stloc_S or Code.Ldloca or Code.Ldloca_S => ((VariableDefinition)i.Operand).Index, _ => -1
    };
    static string TypeKey(TypeReference type) => type.FullName + "@" + type.Scope;
    static string Operand(object? operand, Func<Instruction, int> index) => operand switch
    {
        null => "", Instruction instruction => "label:" + index(instruction), Instruction[] labels => "labels:" + string.Join(",", labels.Select(index)),
        VariableDefinition local => "local:" + local.Index, ParameterDefinition argument => "arg:" + argument.Index,
        MethodReference method => "method:" + method.FullName + "@" + method.DeclaringType.Scope,
        FieldReference field => "field:" + field.FullName + "@" + field.DeclaringType.Scope,
        TypeReference type => "type:" + TypeKey(type), float value => "f32:" + BitConverter.SingleToInt32Bits(value), double value => "f64:" + BitConverter.DoubleToInt64Bits(value),
        _ => operand.GetType().FullName + ":" + Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture)
    };
    static string BodyKey(MethodDefinition method, bool includeMaxStack = true)
    {
        var index = method.Body.Instructions.Select((i, n) => (i, n)).ToDictionary(p => p.i, p => p.n);
        return string.Join("\n", method.Body.Instructions.Select(i => i.OpCode.Code + " " + Operand(i.Operand, x => index[x]))) + "\nlocals:" + string.Join(";", method.Body.Variables.Select(v => TypeKey(v.VariableType))) + $"\ninit:{method.Body.InitLocals}" + (includeMaxStack ? $";stack:{method.Body.MaxStackSize}" : "");
    }
    static object Validate(MethodDefinition before, MethodDefinition after)
    {
        var a = before.Body.Instructions; var b = after.Body.Instructions; int insertion = a.IndexOf(At(before, 0x44));
        Require(a.Count == 5140 && b.Count == a.Count + 5, "exact instruction counts 5140 -> 5145");
        Require(before.FullName == after.FullName && before.Attributes == after.Attributes && before.ImplAttributes == after.ImplAttributes && before.CallingConvention == after.CallingConvention, "method signature/flags unchanged");
        Require(before.Parameters.Zip(after.Parameters).All(p => p.First.Name == p.Second.Name && p.First.Attributes == p.Second.Attributes && TypeKey(p.First.ParameterType) == TypeKey(p.Second.ParameterType)), "parameter metadata unchanged");
        Require(before.Body.InitLocals == after.Body.InitLocals && before.Body.MaxStackSize == after.Body.MaxStackSize, "local initialization and max stack unchanged");
        Require(before.Body.Variables.Select(v => TypeKey(v.VariableType)).SequenceEqual(after.Body.Variables.Select(v => TypeKey(v.VariableType))), "locals unchanged");
        Require(before.Body.ExceptionHandlers.Count == 0 && after.Body.ExceptionHandlers.Count == 0, "original and candidate have no handlers");
        Require(before.Parameters[3].ParameterType.MetadataType == MetadataType.UInt16, "guard parameter is ushort");
        Require(Argument(after, b[insertion]) == 4 && b[insertion].OpCode.Code is Code.Ldarg or Code.Ldarg_S, "guard loads typeCache");
        Require(b[insertion + 1].OpCode.Code == Code.Ldc_I4 && Constant(b[insertion + 1]) == 637 && b[insertion + 2].OpCode.Code == Code.Sub && b[insertion + 3].OpCode.Code == Code.Ldc_I4_1 && b[insertion + 4].OpCode.Code == Code.Bgt_Un, "guard is unsigned (typeCache - 637) > 1");
        var originalIndex = a.Select((i, n) => (i, n)).ToDictionary(p => p.i, p => p.n);
        var candidateIndex = b.Select((i, n) => (i, n)).ToDictionary(p => p.i, p => p.n);
        int Unsplice(Instruction i) { int n = candidateIndex[i]; Require(n < insertion || n >= insertion + 5, "an original branch must not enter guard"); return n < insertion ? n : n - 5; }
        Require(Unsplice((Instruction)b[insertion + 4].Operand) == a.IndexOf(At(before, 0x4c)), "guard lands immediately after original light store");
        for (int n = 0; n < a.Count; n++)
        {
            var changed = b[n < insertion ? n : n + 5];
            Require(a[n].OpCode.Code == changed.OpCode.Code && Operand(a[n].Operand, i => originalIndex[i]) == Operand(changed.Operand, Unsplice), "original instruction/operand/edge changed at " + a[n]);
        }
        Require(a[insertion].OpCode.Code == Code.Ldarg_1 && a[insertion + 1].OpCode.Code == Code.Ldarg_2 && a[insertion + 2].OpCode.Code == Code.Call && ((MethodReference)a[insertion + 2].Operand).FullName == Lookup && a[insertion + 3].OpCode.Code == Code.Stloc_0, "lookup remains at original point after all original prefix effects");
        Require(!a.Any(i => i.Operand is Instruction label && originalIndex[label] >= insertion && originalIndex[label] <= insertion + 3 || i.Operand is Instruction[] labels && labels.Any(label => originalIndex[label] >= insertion && originalIndex[label] <= insertion + 3)), "no original branch bypasses or enters lookup region");
        return new { baselineInstructions = a.Count, candidateInstructions = b.Count, insertedInstructions = 5, locals = before.Body.Variables.Count, handlers = 0, insertionOriginalOffset = "0044", queryOriginalOffset = "0046", skipTargetOriginalOffset = "004c", unchangedOriginalInstructions = a.Count, baselineBodySha256 = Sha(Encoding.UTF8.GetBytes(BodyKey(before))), candidateBodySha256 = Sha(Encoding.UTF8.GetBytes(BodyKey(after))) };
    }

    sealed class Flow
    {
        internal readonly MethodDefinition Method;
        internal readonly int[][] Edges;
        internal readonly Dictionary<int, Func<ushort, int>> Selected = new();
        internal readonly int[] Seen, Work;
        int stamp;
        internal Flow(MethodDefinition method)
        {
            Method = method; var ins = method.Body.Instructions; var ids = ins.Select((i, n) => (i, n)).ToDictionary(x => x.i, x => x.n);
            Edges = new int[ins.Count][]; Seen = new int[ins.Count]; Work = new int[ins.Count];
            for (int n = 0; n < ins.Count; n++)
            {
                var i = ins[n]; var edges = new List<int>();
                if (i.Operand is Instruction label) edges.Add(ids[label]);
                else if (i.Operand is Instruction[] labels) edges.AddRange(labels.Select(x => ids[x]));
                if (i.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw) && n + 1 < ins.Count) edges.Add(n + 1);
                Edges[n] = edges.Distinct().ToArray();
            }
            var predecessors = Enumerable.Range(0, ins.Count).Select(_ => new List<int>()).ToArray();
            for (int n = 0; n < ins.Count; n++) foreach (int target in Edges[n]) predecessors[target].Add(n);
            bool Straight(int first, int last) => Enumerable.Range(first + 1, last - first).All(n => predecessors[n].Count == 1 && predecessors[n][0] == n - 1);
            bool TypeExpression(int end, out int first, out int subtract)
            {
                first = end; subtract = 0;
                if (end < 0) return false;
                if (Argument(method, ins[end]) == 4) return true;
                if (end >= 2 && ins[end].OpCode.Code == Code.Sub && Constant(ins[end - 1]) is int amount && Argument(method, ins[end - 2]) == 4) { first = end - 2; subtract = amount; return true; }
                return false;
            }
            for (int n = 0; n < ins.Count; n++)
            {
                int next = n + 1;
                if (ins[n].OpCode.Code == Code.Switch && TypeExpression(n - 1, out int start, out int subtract) && Straight(start, n))
                {
                    var targets = ((Instruction[])ins[n].Operand).Select(x => ids[x]).ToArray();
                    Selected[n] = type => { uint value = unchecked((uint)(type - subtract)); return value < targets.Length ? targets[value] : next; };
                }
                else if (ins[n].Operand is Instruction target && Constant(ins[n - 1]) is int right && TypeExpression(n - 2, out start, out subtract) && Straight(start, n))
                {
                    Code code = ins[n].OpCode.Code; int destination = ids[target];
                    if (code is Code.Beq or Code.Beq_S or Code.Bne_Un or Code.Bne_Un_S or Code.Bgt or Code.Bgt_S or Code.Bgt_Un or Code.Bgt_Un_S or Code.Bge or Code.Bge_S or Code.Bge_Un or Code.Bge_Un_S or Code.Blt or Code.Blt_S or Code.Blt_Un or Code.Blt_Un_S or Code.Ble or Code.Ble_S or Code.Ble_Un or Code.Ble_Un_S)
                        Selected[n] = type => Compare(code, unchecked(type - subtract), right) ? destination : next;
                }
            }
        }
        internal void Reach(ushort type, int blocked = -1)
        {
            int top = 0; stamp++; Seen[0] = stamp; Work[top++] = 0;
            while (top > 0)
            {
                int n = Work[--top]; if (n == blocked) continue;
                if (Selected.TryGetValue(n, out var select)) Push(select(type)); else foreach (int target in Edges[n]) Push(target);
            }
            void Push(int target) { if (Seen[target] != stamp) { Seen[target] = stamp; Work[top++] = target; } }
        }
        internal bool Reached(int instruction) => Seen[instruction] == stamp;
    }
    static bool Compare(Code code, int left, int right) => code switch
    {
        Code.Beq or Code.Beq_S => left == right, Code.Bne_Un or Code.Bne_Un_S => left != right,
        Code.Bgt or Code.Bgt_S => left > right, Code.Bgt_Un or Code.Bgt_Un_S => (uint)left > (uint)right,
        Code.Bge or Code.Bge_S => left >= right, Code.Bge_Un or Code.Bge_Un_S => (uint)left >= (uint)right,
        Code.Blt or Code.Blt_S => left < right, Code.Blt_Un or Code.Blt_Un_S => (uint)left < (uint)right,
        Code.Ble or Code.Ble_S => left <= right, Code.Ble_Un or Code.Ble_Un_S => (uint)left <= (uint)right,
        _ => throw new InvalidOperationException("unsupported comparison")
    };
    static object FlowProof(MethodDefinition method, bool guarded)
    {
        var ins = method.Body.Instructions;
        Require(method.Body.ExceptionHandlers.Count == 0, "CFG requires handler-free source");
        Require(!ins.Any(i => i.OpCode.Code is Code.Starg or Code.Starg_S or Code.Ldarga or Code.Ldarga_S), "CFG typeCache value cannot be reassigned or addressed");
        var locals = ins.Where(i => Local(i) == 0).ToArray();
        Require(locals.Length == 3 && locals.Count(i => i.OpCode.Code == Code.Stloc_0) == 1 && locals.Count(i => i.OpCode.Code == Code.Ldloc_0) == 2, "exactly one local0 store and two value reads, with no local0 aliases");
        var reads = locals.Where(i => i.OpCode.Code == Code.Ldloc_0).Select(i => ins.IndexOf(i)).ToArray();
        int query = ins.IndexOf(ins.Single(i => i.OpCode.Code == Code.Call && i.Operand is MethodReference m && m.FullName == Lookup));
        var flow = new Flow(method); var owners = reads.Select(_ => new List<int>()).ToArray(); int queries = 0;
        for (int value = 0; value <= ushort.MaxValue; value++)
        {
            flow.Reach((ushort)value);
            if (flow.Reached(query)) queries++;
            Require(flow.Reached(query) == (!guarded || value is 637 or 638), "exhaustive query reachability at type " + value);
            for (int n = 0; n < reads.Length; n++) if (flow.Reached(reads[n])) owners[n].Add(value);
        }
        Require(owners[0].SequenceEqual(new[] { 637 }) && owners[1].SequenceEqual(new[] { 638 }), "light local reads have exactly types 637 and 638 respectively");
        foreach (ushort value in new ushort[] { 637, 638 }) { flow.Reach(value, query); Require(!reads.Any(flow.Reached), "lookup dominates every required light read"); }
        return new { typeValues = 65536, queryReachableTypes = queries, specializedTypePredicates = flow.Selected.Count, localReadOwners = owners, unknownPredicates = "both/all normal successors retained; exceptions may terminate execution, never re-enter (no handlers)", predicateSafety = "only immutable typeCache integer predicates with unique straight-line internal predecessors are specialized" };
    }
    static object Adversarial(MethodDefinition original, MethodDefinition candidate)
    {
        var result = new List<string>(); var b = candidate.Body.Instructions; int first = original.Body.Instructions.IndexOf(At(original, 0x44));
        void Reject(string label, Action mutate, Action restore, Action? verify = null)
        {
            bool rejected = false; mutate(); try { if (verify == null) Validate(original, candidate); else verify(); } catch (InvalidOperationException) { rejected = true; } finally { restore(); }
            Require(rejected, "negative proof did not reject " + label); result.Add(label);
        }
        var op = b[first + 1].Operand;
        Reject("guard lower bound 636", () => b[first + 1].Operand = 636, () => b[first + 1].Operand = op);
        Reject("signed rather than unsigned comparison", () => b[first + 4].OpCode = OpCodes.Bgt, () => b[first + 4].OpCode = OpCodes.Bgt_Un);
        var target = b[first + 4].Operand;
        Reject("guard branch landing on original store", () => b[first + 4].Operand = b[first + 8], () => b[first + 4].Operand = target);
        var firstSwitch = b.Single(i => i.OpCode.Code == Code.Switch && ((Instruction[])i.Operand).Length > 600); var labels = (Instruction[])firstSwitch.Operand; var old = labels[0];
        Reject("retargeted original switch edge", () => labels[0] = b[0], () => labels[0] = old);
        var load = b[first + 5];
        Reject("relocated lookup argument", () => load.OpCode = OpCodes.Ldarg_2, () => load.OpCode = OpCodes.Ldarg_1);
        Reject("changed initlocals", () => candidate.Body.InitLocals = !original.Body.InitLocals, () => candidate.Body.InitLocals = original.Body.InitLocals);
        var consumerSwitch = original.Body.Instructions.Single(i => i.Offset == 0x277f); var consumerLabels = (Instruction[])consumerSwitch.Operand; var oldConsumer = consumerLabels[2];
        Reject("CFG adversary: type636 reaches type637 local read", () => consumerLabels[2] = consumerLabels[3], () => consumerLabels[2] = oldConsumer, () => FlowProof(original, false));
        return result;
    }

    static byte[] BuildSlices(MethodDefinition original, MethodDefinition candidate, List<object> receipts)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("LightLookupExactSlices", new Version(1, 0)), "LightLookupExactSlices", ModuleKind.Dll);
        var module = assembly.MainModule;
        var hooks = new TypeDefinition("Probe", "LightLookupHooks", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
        var types = new Dictionary<string, Type>
        {
            [Drawing] = typeof(LightLookupDrawing), ["Terraria.Tile"] = typeof(LightLookupTile), ["Terraria.Main"] = typeof(LightLookupFixture),
            ["Terraria.Lighting"] = typeof(LightLookupFixture), ["Terraria.GameContent.TextureAssets"] = typeof(LightLookupFixture),
            ["Microsoft.Xna.Framework.Color"] = typeof(LightLookupColor), ["Microsoft.Xna.Framework.Rectangle"] = typeof(LightLookupRectangle),
            ["Microsoft.Xna.Framework.Graphics.SpriteEffects"] = typeof(int), ["Microsoft.Xna.Framework.Graphics.Texture2D"] = typeof(LightLookupTexture),
            ["ReLogic.Content.Asset`1<Microsoft.Xna.Framework.Graphics.Texture2D>"] = typeof(LightLookupAsset)
        };
        TypeReference Map(TypeReference type)
        {
            if (types.TryGetValue(type.FullName, out var host)) return module.ImportReference(host);
            if (type is ByReferenceType reference) return new ByReferenceType(Map(reference.ElementType));
            if (type is ArrayType array) return new ArrayType(Map(array.ElementType), array.Rank);
            var primitive = Type.GetType(type.FullName); Require(primitive != null && type.Namespace == "System", "unmapped slice type " + type.FullName); return module.ImportReference(primitive!);
        }
        object Member(object operand)
        {
            if (operand is TypeReference type) return Map(type);
            if (operand is FieldReference field)
            {
                Require(types.TryGetValue(field.DeclaringType.FullName, out var host), "unmapped slice field " + field.FullName);
                return module.ImportReference(host!.GetField(field.Name, Flags)!);
            }
            if (operand is MethodReference method)
            {
                Require(types.TryGetValue(method.DeclaringType.FullName, out var host), "unmapped slice method " + method.FullName);
                if (method.Name == ".ctor") return module.ImportReference(host!.GetConstructors().Single(c => c.GetParameters().Length == method.Parameters.Count));
                var target = host!.GetMethods(Flags).Single(m => m.Name == method.Name && m.GetParameters().Length == method.Parameters.Count);
                Require(target.IsStatic != method.HasThis, "slice receiver changed " + method.FullName); return module.ImportReference(target);
            }
            return operand;
        }
        var copies = new List<(MethodDefinition target, string fingerprint)>();
        foreach (var (source, prefix) in new[] { (original, "Baseline"), (candidate, "Candidate") })
        {
            int lookupIndex = source.Body.Instructions.IndexOf(source.Body.Instructions.Single(i => i.OpCode.Code == Code.Call && i.Operand is MethodReference m && m.FullName == Lookup));
            Add(source, prefix + "Prefix", source.Body.Instructions.Take(lookupIndex + 2).ToArray(), true);
            foreach (int type in new[] { 637, 638 })
            {
                int oldStart = type == 637 ? 0x28ea : 0x292e; int oldEnd = type == 637 ? 0x292d : 0x297c;
                int first = original.Body.Instructions.IndexOf(At(original, oldStart)); int last = original.Body.Instructions.IndexOf(At(original, oldEnd));
                if (source == candidate) { first += 5; last += 5; }
                Add(source, prefix + "Tail" + type, source.Body.Instructions.Skip(first).Take(last - first + 1).ToArray(), false);
            }
        }
        void Add(MethodDefinition source, string name, Instruction[] selected, bool isPrefix)
        {
            var target = new MethodDefinition(name, MA.Public | MA.Static, isPrefix ? Map(source.Body.Variables[0].VariableType) : module.TypeSystem.Void);
            hooks.Methods.Add(target); target.Parameters.Add(new ParameterDefinition("owner", Mono.Cecil.ParameterAttributes.None, Map(source.DeclaringType)));
            foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, Map(p.ParameterType)));
            if (!isPrefix) target.Parameters.Add(new ParameterDefinition("observedLight", Mono.Cecil.ParameterAttributes.None, Map(source.Body.Variables[0].VariableType)));
            target.Body.InitLocals = true; target.Body.MaxStackSize = source.Body.MaxStackSize;
            target.Body.Variables.Add(new VariableDefinition(Map(source.Body.Variables[0].VariableType)));
            var il = target.Body.GetILProcessor();
            if (!isPrefix) { il.Append(Instruction.Create(OpCodes.Ldarg, target.Parameters.Last())); il.Append(Instruction.Create(OpCodes.Stloc_0)); }
            var map = selected.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
            var observer = Instruction.Create(OpCodes.Ldloc_0);
            foreach (var i in selected)
            {
                var clone = map[i]; clone.OpCode = i.OpCode;
                clone.Operand = i.Operand switch
                {
                    null => null, Instruction label => map.TryGetValue(label, out var local) ? local : isPrefix && label == selected.Last().Next ? observer : throw new InvalidOperationException("slice branch escapes source boundary"),
                    Instruction[] labels => labels.Select(label => map[label]).ToArray(),
                    ParameterDefinition parameter => target.Parameters[parameter.Index + 1],
                    VariableDefinition local => target.Body.Variables[local.Index], _ => Member(i.Operand)
                };
                il.Append(clone);
            }
            if (isPrefix) { il.Append(observer); il.Append(Instruction.Create(OpCodes.Ret)); }
            Require(selected.All(i => Local(i) <= 0), "slice only uses local0");
            copies.Add((target, BodyKey(target, false)));
            receipts.Add(new { source = source.FullName, sourceMvid = source.Module.Mvid, method = name, sourceFirstOffset = selected.First().Offset.ToString("x4"), sourceLastOffset = selected.Last().Offset.ToString("x4"), copiedSourceInstructions = selected.Length, addedHarnessInstructions = 2, copiedWholeMethod = false, dependencyRemapping = "explicit fixture types and members only; source opcodes, operands and internal edges retained" });
        }
        module.Mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", copies.Select(p => p.fingerprint)))).AsSpan(0, 16));
        using var stream = new MemoryStream(); assembly.Write(stream, new WriterParameters { Timestamp = 0 }); byte[] bytes = stream.ToArray();
        using var reread = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var (target, fingerprint) in copies)
        {
            var serialized = reread.MainModule.Types.Single(t => t.Name == hooks.Name).Methods.Single(m => m.Name == target.Name);
            // Cecil computes the extracted slice's smaller MaxStack; this does not alter its instructions.
            Require(BodyKey(serialized, false) == fingerprint, "serialized dynamic slice differs: " + target.Name);
            receipts.Add(new { serializedSlice = target.Name, bodySha256 = Sha(Encoding.UTF8.GetBytes(BodyKey(serialized))), serializedMaxStack = serialized.Body.MaxStackSize });
        }
        return bytes;
    }

    sealed class State
    {
        internal short Fx = 36, Fy = 54;
        internal int Width = 91, Height = 92, Top = 93, Half = 94, AddX = 95, AddY = 96, Effects = 97;
        internal LightLookupTexture? Texture = new() { Id = -1 };
        internal LightLookupRectangle Rectangle = new() { X = 1, Y = 2, Width = 3, Height = 4 };
        internal LightLookupColor Color = new(1, 2, 3, 4);
        internal LightLookupColor Run(Prefix call, ushort type) => call(new(), 11, -9, new() { type = type }, type, ref Fx, ref Fy, ref Width, ref Height, ref Top, ref Half, ref AddX, ref AddY, ref Effects, ref Texture, ref Rectangle, ref Color);
        internal void Run(Tail call, ushort type, LightLookupColor light) => call(new(), 11, -9, new() { type = type }, type, ref Fx, ref Fy, ref Width, ref Height, ref Top, ref Half, ref AddX, ref AddY, ref Effects, ref Texture, ref Rectangle, ref Color, light);
        internal string Snapshot() => $"{Fx}|{Fy}|{Width}|{Height}|{Top}|{Half}|{AddX}|{AddY}|{Effects}|{Texture?.Id.ToString() ?? "null"}|{Rectangle}|{Color}";
    }
    static object DynamicProof(Type hooks, Prefix before, Prefix after, Action<bool, string> check)
    {
        var cases = new List<object>();
        var originalFrames = LightLookupFixture.tileFrame;
        try
        {
            for (int value = 0; value <= ushort.MaxValue; value++)
            {
                LightLookupFixture.tileFrame[value] = value % 7 - 3;
                var a = new State(); LightLookupFixture.Reset(); var oldColor = a.Run(before, (ushort)value); int oldQueries = LightLookupFixture.Queries;
                Require(LightLookupFixture.LastQueryArguments == "11:-9" && oldColor.Equals(LightLookupFixture.QueryColor), "original lookup arguments and captured colour " + value);
                var b = new State(); LightLookupFixture.Reset(); var newColor = b.Run(after, (ushort)value);
                Require(oldQueries == 1 && LightLookupFixture.Queries == (value is 637 or 638 ? 1 : 0), "JIT prefix query count " + value);
                Require(LightLookupFixture.LastQueryArguments == (value is 637 or 638 ? "11:-9" : null), "candidate retained query arguments " + value);
                Require(a.Snapshot() == b.Snapshot(), "JIT prefix by-ref outputs " + value);
                Require(value is not (637 or 638) || oldColor.Equals(newColor), "required original light preserved " + value);
                if (value is 0 or 636 or 637 or 638 or 639 or 65535) cases.Add(new { kind = "prefix-boundary", type = value, baselineQueries = oldQueries, candidateQueries = LightLookupFixture.Queries, equalByrefs = true, observedLocal0Baseline = oldColor.ToString(), observedLocal0Candidate = newColor.ToString(), note = "observer local return is not a game output; default unused candidate local is expected" });
            }
            check(true, "JIT-executed serialized prefixes for all 65536 ushort values: exactly two retained queries, identical 12 by-ref outputs");
            foreach (ushort type in new ushort[] { 0, 637, 638, 65535 })
            foreach (string failure in new[] { "Empty", "Transparent", "GetColor" })
            {
                var a = new State(); LightLookupFixture.Reset(failure); Exception? oldError = null; try { a.Run(before, type); } catch (Exception error) { oldError = error; } var trace = LightLookupFixture.Trace.ToArray();
                var b = new State(); LightLookupFixture.Reset(failure); Exception? newError = null; try { b.Run(after, type); } catch (Exception error) { newError = error; }
                bool skippedFailure = failure == "GetColor" && type is not (637 or 638);
                check(ReferenceEquals(oldError, LightLookupFixture.Failure) && (skippedFailure ? newError == null : ReferenceEquals(newError, oldError)), $"prefix exception outcome type={type} boundary={failure}");
                check(a.Snapshot() == b.Snapshot() && (skippedFailure ? LightLookupFixture.Trace.SequenceEqual(trace.Where(x => x != "GetColor")) : LightLookupFixture.Trace.SequenceEqual(trace)), $"prefix partial byrefs and call order type={type} boundary={failure}");
                cases.Add(new { kind = "boundary-failure", type, failure, baselineThrows = true, candidateThrows = newError != null, intentionalSemanticDifference = skippedFailure });
            }
            foreach (bool nullArray in new[] { false, true })
            {
                LightLookupFixture.tileFrame = nullArray ? null! : Array.Empty<int>();
                var a = new State(); LightLookupFixture.Reset("GetColor"); Exception? oldError = null; try { a.Run(before, 637); } catch (Exception error) { oldError = error; }
                var b = new State(); LightLookupFixture.Reset("GetColor"); Exception? newError = null; try { b.Run(after, 637); } catch (Exception error) { newError = error; }
                check(oldError?.GetType() == (nullArray ? typeof(NullReferenceException) : typeof(IndexOutOfRangeException)) && oldError.GetType() == newError?.GetType() && a.Snapshot() == b.Snapshot() && LightLookupFixture.Queries == 0 && LightLookupFixture.Trace.Count == 0, "original tileFrame failure precedes lookup and preserves partial byrefs: " + nullArray);
                LightLookupFixture.tileFrame = originalFrames;
            }
            foreach (ushort type in new ushort[] { 637, 638 })
            foreach (int half in new[] { 0, 8 })
            foreach (var inputColor in new[] { new LightLookupColor(0, 0, 0, 0), new LightLookupColor(24, 80, 160, 255), new LightLookupColor(255, 255, 255, 255) })
            {
                var tailBefore = hooks.GetMethod("BaselineTail" + type)!.CreateDelegate<Tail>();
                var tailAfter = hooks.GetMethod("CandidateTail" + type)!.CreateDelegate<Tail>();
                var a = new State { Half = half, AddX = 6, AddY = 12, Width = 16, Height = 16 }; LightLookupFixture.Reset(); a.Run(tailBefore, type, inputColor); var trace = LightLookupFixture.Trace.ToArray();
                var b = new State { Half = half, AddX = 6, AddY = 12, Width = 16, Height = 16 }; LightLookupFixture.Reset(); b.Run(tailAfter, type, inputColor);
                var expectedColor = new LightLookupColor((255 + 3 * inputColor.R) / 4, (255 + 3 * inputColor.G) / 4, (255 + 3 * inputColor.B) / 4, (255 + 3 * inputColor.A) / 4);
                check(b.Color.Equals(expectedColor) && (type == 638 || LightLookupFixture.LastTextureArguments == "637:11:-9"), $"consumer slice uses supplied light and original texture coordinates type={type} half={half} light={inputColor}");
                check(a.Snapshot() == b.Snapshot() && trace.SequenceEqual(LightLookupFixture.Trace) && LightLookupFixture.Queries == 0 && b.Texture?.Id == type && b.Rectangle.X == 42 && b.Rectangle.Y == 66 && b.Rectangle.Height == (type == 637 ? 16 : 16 - half), $"actual consumer IL slices preserve all byrefs/colour/texture/order type={type} half={half} light={inputColor}");
                cases.Add(new { kind = "consumer-slice", type, half, light = inputColor.ToString(), color = b.Color.ToString(), rectangle = b.Rectangle.ToString(), equalByrefs = true, trace });
                foreach (string failure in new[] { type == 637 ? "GetTileDrawTexture" : "Asset.Value", "Rectangle", "White", "Lerp" })
                {
                    a = new State { Half = half }; LightLookupFixture.Reset(failure); Exception? oldError = null; try { a.Run(tailBefore, type, inputColor); } catch (Exception error) { oldError = error; } trace = LightLookupFixture.Trace.ToArray();
                    b = new State { Half = half }; LightLookupFixture.Reset(failure); Exception? newError = null; try { b.Run(tailAfter, type, inputColor); } catch (Exception error) { newError = error; }
                    check(ReferenceEquals(oldError, LightLookupFixture.Failure) && ReferenceEquals(newError, oldError) && a.Snapshot() == b.Snapshot() && trace.SequenceEqual(LightLookupFixture.Trace), $"consumer exception identity and partial outputs type={type} half={half} light={inputColor} at={failure}");
                }
            }
        }
        finally { LightLookupFixture.tileFrame = originalFrames; LightLookupFixture.Reset(); }
        return new { completeMethodExecuted = false, actualSerializedPrefixNormalInvocations = 131072, actualSerializedPrefixFailureInvocations = 28, actualConsumerSliceNormalInvocations = 24, actualConsumerSliceFailureInvocations = 96, wholeMethodStructuralCoverage = true, cases };
    }
}
