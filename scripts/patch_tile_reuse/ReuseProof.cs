using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class ReuseProof
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    delegate object AcquireCall(CostDrawing owner, out object? lease);
#if REUSE_BASELINE_ONLY
    public static int Main(string[] args)
    {
        try
        {
            Require(args.Length is 3 or 4, "input42, frame probe, output, optional candidate46 required");
            Directory.CreateDirectory(args[2]);
            using var baseline = AssemblyDefinition.ReadAssembly(args[0]);
            File.WriteAllBytes(Path.Combine(args[2], "FrameProfileProbe.dll"), File.ReadAllBytes(args[1]));
            if (args.Length == 4) { using var candidate = AssemblyDefinition.ReadAssembly(args[3]); Verify(baseline, candidate, args[2]); }
            else
            {
                var receipts = new List<object>(); byte[] bytes = Build(baseline, baseline, File.ReadAllBytes(args[1]), receipts, true);
                File.WriteAllBytes(Path.Combine(args[2], "BaselineReuseProbe.dll"), bytes);
                var hooks = Assembly.Load(bytes).GetType("Probe.ReuseHooks", true)!;
                var single = hooks.GetMethod("BaselineSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>();
                var draw = hooks.GetMethod("BaselineDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>();
                var owner = new CostDrawing(); var cases = new List<object>();
                foreach (ushort type in new ushort[] { 0, 518, 751, 752, 323, 72, 80, 83, 129, 429, 314, 171, 725 })
                {
                    CostFixture.Reset(type: type); ReuseFixture.Reset(); CostSets.HasOutlines[type] = true;
                    if (type == 518) CostWorld.tile[0, 0]!.liquid = 1;
                    if (type is 751 or 752 or 323) CostWorld.tile[0, 0]!.frameX = 90;
                    if (type == 72) CostWorld.tile[0, 0]!.frameX = 36;
                    if (type is 129 or 429 or 725) CostWorld.tileGlowMask[type] = 0;
                    if (type == 129) CostWorld.tile[0, 0]!.frameX = 324;
                    single(owner, new(10, 20), new(3, 4), 0, 0);
                    cases.Add(new { type, trace = CostFixture.Trace.ToArray(), snapshots = ReuseFixture.Snapshots.ToArray(), state = State(owner) });
                }
                CostFixture.Reset(65); ReuseFixture.Reset(); draw(owner, true, false, 0);
                Require(CostFixture.Trace.Count(n => n == "DrawBasicTile") == 65, "clean42 actual Draw reaches 65 full Singles");
                Require(ReuseFixture.Scratch.Distinct(ReferenceEqualityComparer.Instance).Count() == 65, "baseline descriptors remain fresh");
                Json(Path.Combine(args[2], "baseline-smoke.json"), new { passed = true, hostFixtureOnly = true, probeSha256 = Sha(bytes), receipts, cases });
            }
            Console.WriteLine("REUSE FULL-IL PROOF PASS"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
#endif
    static string State(CostDrawing owner) => string.Join("|", CostFixture.Effects, CostWorld.mapTime, CostWorld.critterCage, ReuseFixture.Value(CostFixture.Dust), ReuseFixture.Value(CostWorld.tile), ReuseFixture.Value(CostWorld.player), ReuseFixture.Value(CostFixture.Texture), ReuseFixture.Snapshot(owner));

    internal static object Verify(AssemblyDefinition baseline42, AssemblyDefinition candidate46, string output)
    {
        var receipts = new List<object>(); byte[] bytes = Build(baseline42, candidate46, File.ReadAllBytes(Path.Combine(output, "FrameProfileProbe.dll")), receipts);
        File.WriteAllBytes(Path.Combine(output, "TileReuseProbe.dll"), bytes);
        var loaded = Assembly.Load(bytes); var hooks = loaded.GetType("Probe.ReuseHooks", true)!;
        var runtime = loaded.GetType("Probe.ReuseRuntime", true)!; var scopeType = loaded.GetType("Probe.ReuseScope", true)!; var scratchType = loaded.GetType("Probe.ReuseScratch", true)!;
        var before = hooks.GetMethod("BaselineSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>();
        var after = hooks.GetMethod("CandidateSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>();
        var drawBefore = hooks.GetMethod("BaselineDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>();
        var drawAfter = hooks.GetMethod("CandidateDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>();
        var enter = Bind<Func<CostDrawing, object?>>(runtime.GetMethod("Enter", Flags)!);
        var exit = Bind<Action<object?>>(runtime.GetMethod("Exit", Flags)!);
        var release = Bind<Action<object?>>(runtime.GetMethod("Release", Flags)!);
        var reset = Bind<Action<object, Array>>(scratchType.GetMethod("NXReset46", Flags)!);
        var acquire = BindAcquire(runtime.GetMethod("Acquire", Flags)!, scopeType);
        var create = NewScratch(scratchType.GetConstructor(Type.EmptyTypes)!);
        var checks = new List<string>(); var scenarios = new List<object>();
        void Check(bool ok, string label) { Require(ok, "reuse proof: " + label); checks.Add(label); }
        object? Current() => runtime.GetField("current", Flags)!.GetValue(null);
        object? Get(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value);
        void Set(object value, string name, object? item) => value.GetType().GetField(name, Flags)!.SetValue(value, item);
        bool Busy(object value) => (bool)Get(value, "Busy")!;
        var owner = new CostDrawing(); var otherOwner = new CostDrawing(); var a = new CostVec(10, 20); var b = new CostVec(3, 4);
        Check(Current() == null, "cold_thread_has_no_scope");
        var fresh = create(); var poisoned = create(); var owned = (CostVec3[])Get(poisoned, "colorSlices")!;
        Check(owned.Length == 9 && ReuseFixture.Value(owned) == ReuseFixture.Value(new CostVec3[9]), "original_constructor_all_nine_vectors_zero");
        var aliasTile = new CostCell { type = 83, frameX = 29, Active = false, Slope = 3 }; var aliasTexture = new CostTexture { Id = 311 };
        string aliasTileBefore = ReuseFixture.Value(aliasTile), aliasTextureBefore = ReuseFixture.Value(aliasTexture);
        Poison(poisoned, aliasTile, aliasTexture); var replacement = Enumerable.Range(0, 9).Select(i => new CostVec3 { X = i + 101, Y = i + 202, Z = i + 303 }).ToArray();
        for (int i = 0; i < owned.Length; i++) owned[i] = new CostVec3 { X = i + 1, Y = -i - 2, Z = i + 3 };
        Set(poisoned, "colorSlices", replacement); string sharedBefore = ReuseFixture.Value(replacement); reset(poisoned, owned);
        Check(ReuseFixture.Snapshot(poisoned) == ReuseFixture.Snapshot(fresh), "actual_reset_all19_fields_and27_vector_components_equal_fresh_constructor");
        Check(ReferenceEquals(Get(poisoned, "colorSlices"), owned) && ReuseFixture.Value(replacement) == sharedBefore, "reset_restores_owned_array_without_clearing_shared_replacement");
        Check(ReuseFixture.Value(aliasTile) == aliasTileBefore && ReuseFixture.Value(aliasTexture) == aliasTextureBefore, "reset_does_not_mutate_aliased_tile_or_texture");
        Check(scratchType.GetFields(Flags).Count(f => !f.IsStatic) == 19, "all19_descriptor_fields_have_explicit_poison_coverage");

        object? saved = enter(owner); var scope = Current()!;
        Check(saved == null && ReferenceEquals(Get(scope, "Owner"), owner) && Get(scope, "Scratch") == null && Get(scope, "Slices") == null && !Busy(scope), "Enter_creates_lazy_empty_owned_scope");
        var first = acquire(owner, out var lease); var firstSlices = Get(first, "colorSlices");
        Check(ReferenceEquals(lease, scope) && Busy(scope) && ReferenceEquals(Get(scope, "Scratch"), first) && ReferenceEquals(Get(scope, "Slices"), firstSlices), "first_Acquire_claims_scope_and_original_storage");
        var recursive = acquire(owner, out var recursiveLease);
        Check(recursiveLease == null && !ReferenceEquals(recursive, first) && !ReferenceEquals(Get(recursive, "colorSlices"), firstSlices) && Busy(scope), "Busy_Acquire_returns_fresh_without_touching_parent");
        release(recursiveLease); Check(Busy(scope), "null_Release_does_not_clear_parent_Busy"); release(lease);
        Poison(first, aliasTile, aliasTexture); Set(first, "colorSlices", replacement);
        var reused = acquire(owner, out lease);
        Check(ReferenceEquals(first, reused) && ReferenceEquals(Get(reused, "colorSlices"), firstSlices) && ReuseFixture.Snapshot(reused) == ReuseFixture.Snapshot(fresh), "subsequent_Acquire_executes_real_reset_on_same_owned_storage"); release(lease);
        var mismatch = acquire(otherOwner, out var mismatchLease);
        Check(mismatchLease == null && !ReferenceEquals(mismatch, first) && !Busy(scope), "owner_mismatch_Acquire_returns_fresh");
        exit(saved); var outside1 = acquire(owner, out var outsideLease1); var outside2 = acquire(owner, out var outsideLease2);
        Check(Current() == null && outsideLease1 == null && outsideLease2 == null && !ReferenceEquals(outside1, outside2) && !ReferenceEquals(Get(outside1, "colorSlices"), Get(outside2, "colorSlices")), "unscoped_Acquire_retains_fresh_class_and_array");

        void Scenario(string name, ushort type = 0, Action? configure = null, string? failure = null)
        {
            CostFixture.Reset(type: type); ReuseFixture.Reset(); configure?.Invoke(); CostFixture.ThrowAt = failure; var oldOwner = new CostDrawing { _isActiveAndNotPaused = owner._isActiveAndNotPaused };
            Exception? oldError = null; try { before(oldOwner, a, b, 0, 0); } catch (Exception e) { oldError = e; }
            string oldState = State(oldOwner); string[] oldTrace = CostFixture.Trace.ToArray(), oldSnapshots = ReuseFixture.Snapshots.ToArray();
            CostFixture.Reset(type: type); ReuseFixture.Reset(); configure?.Invoke(); CostFixture.ThrowAt = failure; var newOwner = new CostDrawing { _isActiveAndNotPaused = owner._isActiveAndNotPaused };
            var parent = enter(newOwner); var current = Current()!;
            // Force reuse rather than checking only the first-allocation path.
            var previousScratch = acquire(newOwner, out var previousLease); Poison(previousScratch, aliasTile, aliasTexture); release(previousLease);
            Exception? error = null; try { after(newOwner, a, b, 0, 0); } catch (Exception e) { error = e; }
            Check(!Busy(current), name + ":Single_finally_releases_lease"); exit(parent);
            Check(State(newOwner) == oldState && CostFixture.Trace.SequenceEqual(oldTrace) && ReuseFixture.Snapshots.SequenceEqual(oldSnapshots), name + ":full_field_vector_effect_order_byref_and_return_equivalence");
            Check(failure == null ? error == null && oldError == null : ReferenceEquals(error, oldError) && ReferenceEquals(error, CostFixture.Failure), name + ":original_exception_identity");
            Check(Current() == null, name + ":scope_restored");
            scenarios.Add(new { name, type, trace = oldTrace, snapshots = oldSnapshots, state = oldState, exception = oldError?.GetType().FullName });
        }
        Scenario("normal_basic");
        Check(CostFixture.Trace.Contains("DrawBasicTile") && (int)Get(CostFixture.LastScratch!, "tileWidth")! == 16, "frame_width_byref_reaches_real_Single_base_boundary");
        Scenario("liquid518_early_return", 518, () => CostWorld.tile[0, 0]!.liquid = 1);
        Check(!CostFixture.Trace.Contains("GetTileDrawData"), "liquid518_returns_before_frame_data");
        Scenario("liquid518_no_liquid", 518);
        Scenario("outline", configure: () => CostSets.HasOutlines[0] = true);
        Check(CostFixture.Trace.Contains("GetTileOutlineInfo") && CostFixture.Trace.Contains("Graphics.Draw"), "outline_byref_texture_color_consumed");
        Scenario("minecart", 314); Scenario("tree", 171); Scenario("cactus_flags", 80); Scenario("plant_reload", 83);
        foreach (ushort type in new ushort[] { 751, 752, 323 })
        { Scenario("frame_early_return_" + type, type, () => CostWorld.tile[0, 0]!.frameX = 90); Check(!CostFixture.Trace.Contains("DrawBasicTile"), "early_frame_branch_" + type); }
        Scenario("shroom_lighting", 72, () => CostWorld.tile[0, 0]!.frameX = 36);
        Scenario("glow_texture_reload", 429, () => CostWorld.tileGlowMask[429] = 0);
        Scenario("crystal_texture_reload", 129, () => { CostWorld.tileGlowMask[129] = 0; CostWorld.tile[0, 0]!.frameX = 324; });
        Scenario("closed_generic_filter_return", 725, () => CostWorld.tileGlowMask[725] = 0);
        Scenario("glow_texture", configure: () => CostFixture.Glow = true);
        Scenario("hidden_dark", configure: () => { CostFixture.Dark = true; CostWorld.tile[0, 0]!.Hidden = true; });
        Scenario("layer_over", configure: () => CostFixture.TileTop = -2); Scenario("layer_behind", configure: () => CostFixture.TileHeight = 24);
        Scenario("slope_half_inactive", configure: () => { CostWorld.tile[0, 0]!.Slope = 2; CostWorld.tile[0, 0]!.Half = true; CostWorld.tile[0, 0]!.Inactive = true; });
        owner._isActiveAndNotPaused = true;
        Scenario("senses_rng_particles", configure: () => CostWorld.player[0].dangerSense = CostWorld.player[0].findTreasure = CostWorld.player[0].biomeSight = true);
        Check(CostFixture.Trace.Contains("NewDust") && CostFixture.Trace.Contains("DrawTiles_EmitParticles") && CostFixture.Trace.Contains("Random.Next"), "original_RNG_and_particle_paths_execute");
        Scenario("fast_random_value_return", configure: () => CostFixture.UpdateEveryFrame = true); owner._isActiveAndNotPaused = false;
        foreach (string boundary in new[] { "GetColor", "GetTileDrawData", "GetTileOutlineInfo", "GetTileDrawTexture", "DrawTiles_GetLightOverride", "GetFinalLight", "CacheSpecialDraws_Part2", "DrawBasicTile", "Graphics.Draw" })
            Scenario("throw_" + boundary, configure: () => CostSets.HasOutlines[0] = true, failure: boundary);
        Scenario("throw_minecart", 314, failure: "DrawTile_MinecartTrack"); Scenario("throw_tree", 171, failure: "DrawXmasTree");
        CostFixture.Reset(); ReuseFixture.Reset(); CostWorld.tile[0, 0] = null;
        Exception? nullBefore = null, nullAfter = null; try { before(owner, a, b, 0, 0); } catch (Exception e) { nullBefore = e; }
        saved = enter(owner); scope = Current()!; try { after(owner, a, b, 0, 0); } catch (Exception e) { nullAfter = e; }
        Check(nullBefore is NullReferenceException && nullAfter is NullReferenceException && !Busy(scope), "null_tile_preserves_exception_type_and_releases"); exit(saved);
        foreach (string invalid in new[] { "null_Single_receiver", "null_Draw_receiver", "out_of_range_Single" })
        {
            CostFixture.Reset(); ReuseFixture.Reset(); Exception? oldError = null, error = null;
            try { if (invalid == "null_Draw_receiver") drawBefore(null!, true, false, 0); else before(invalid == "null_Single_receiver" ? null! : owner, a, b, invalid == "out_of_range_Single" ? 99 : 0, 0); } catch (Exception e) { oldError = e; }
            saved = enter(owner); scope = Current()!;
            try { if (invalid == "null_Draw_receiver") drawAfter(null!, true, false, 0); else after(invalid == "null_Single_receiver" ? null! : owner, a, b, invalid == "out_of_range_Single" ? 99 : 0, 0); } catch (Exception e) { error = e; }
            Check(oldError != null && error?.GetType() == oldError.GetType() && ReferenceEquals(Current(), scope) && !Busy(scope), invalid + ":exception_type_and_outer_scope_preserved"); exit(saved);
        }

        void DrawScenario(string name, int width, bool solid, Action? configure = null)
        {
            CostFixture.Reset(width, 1, solid: solid); ReuseFixture.Reset(); configure?.Invoke(); var oldOwner = new CostDrawing(); drawBefore(oldOwner, solid, false, 3);
            string oldState = State(oldOwner); var oldTrace = CostFixture.Trace.ToArray(); var oldSnapshots = ReuseFixture.Snapshots.ToArray(); int oldObjects = ReuseFixture.Scratch.Distinct(ReferenceEqualityComparer.Instance).Count();
            CostFixture.Reset(width, 1, solid: solid); ReuseFixture.Reset(); configure?.Invoke(); var newOwner = new CostDrawing(); drawAfter(newOwner, solid, false, 3);
            int objects = ReuseFixture.Scratch.Distinct(ReferenceEqualityComparer.Instance).Count(), arrays = ReuseFixture.Slices.Distinct(ReferenceEqualityComparer.Instance).Count();
            Check(State(newOwner) == oldState && CostFixture.Trace.SequenceEqual(oldTrace) && ReuseFixture.Snapshots.SequenceEqual(oldSnapshots) && Current() == null, name + ":actual_Draw_full_Single_equivalence");
            Check(objects == (oldObjects == 0 ? 0 : 1) && arrays == objects, name + ":descriptor_and_array_bounded_per_Draw");
            scenarios.Add(new { name, width, solid, baselineDescriptors = oldObjects, candidateDescriptors = objects, candidateArrays = arrays, trace = oldTrace, snapshots = oldSnapshots, state = oldState });
        }
        foreach (bool solid in new[] { true, false }) DrawScenario("many_tiles_" + solid, 65, solid);
        DrawScenario("empty", 0, true); DrawScenario("filtered", 5, true, () => CostFixture.Solid = false);
        DrawScenario("inactive_cells", 5, true, () => { foreach (var cell in CostWorld.tile) cell!.Active = false; });
        DrawScenario("null_cells_constructed", 3, true, () => CostWorld.tile[1, 0] = null);
        DrawScenario("consecutive_different_tiles", 12, true, () => {
            ushort[] types = { 0, 80, 83, 314, 171, 72, 518, 751, 752, 323, 129, 429 };
            for (int x = 0; x < types.Length; x++) { CostWorld.tile[x, 0]!.type = types[x]; CostWorld.tile[x, 0]!.frameX = (short)(x * 18); CostWorld.tile[x, 0]!.Half = (x & 1) == 0; }
            CostWorld.tile[6, 0]!.liquid = 1; CostSets.HasOutlines[80] = true;
        });

        ReentrantProof(before, after, drawBefore, drawAfter, enter, exit, Current, Get, Check, scenarios);
        CostFixture.Reset(); ReuseFixture.Reset(); saved = enter(owner); scope = Current()!;
        after(owner, a, b, 0, 0); var correctSlices = Get(scope, "Slices"); Set(scope, "Slices", new CostVec3[1]);
        Exception? resetError = null; try { after(owner, a, b, 0, 0); } catch (Exception e) { resetError = e; }
        Check(resetError != null && !Busy(scope) && ReferenceEquals(Current(), scope), "reset_failure_inside_Acquire_releases_captured_lease");
        Set(scope, "Slices", correctSlices); after(owner, a, b, 0, 0); Check(!Busy(scope), "scope_reusable_after_reset_failure"); exit(saved);
        ThreadProof(owner, enter, exit, acquire, release, Current, Get, Check);
        var allocations = Measurements(before, after, drawBefore, drawAfter, enter, exit, acquire, release, Check, output);
        Check(CostFixture.ClockCalls == 0 && TileFixture.ClockCalls == 0, "clean42_and46_execute_no43_44_45_observer_clocks");
        var result = new {
            passed = true, probeSha256 = Sha(bytes), baselineMvid = baseline42.MainModule.Mvid, candidateMvid = candidate46.MainModule.Mvid,
            completeSerializedDrawAndSingleIlExecuted = true, originalConstructorIlExecuted = true, realResetAndScopeHelpersExecuted = true,
            receipts, checkCount = checks.Count, checks, scenarios, allocations,
            limitations = new[] {
                "Host-only executable differential fixture. Full serialized Draw, Single, original constructor, reset and helper bodies run; external game/graphics callees use frozen deterministic boundaries, not the actual renderer/GPU.",
                "Descriptor consumer wrappers capture every field and all vector elements. LastScratch/list retention is diagnostic fixture retention, not evidence of a game escape. Shared replacement arrays and poison are adversarial fixture inputs.",
                "Original exception objects are compared by identity for deterministic throwing boundaries; runtime null/reset failures compare type or cleanup, not separately created exception identity. OOM/allocation failure is not forced.",
                "Recorded branch scenarios are not exhaustive coverage of every tile/frame/world state. Boundary RNG arguments/order and particle state are checked, not the implementation of game RNG or particle/render callees.",
                "ThreadStatic helper isolation is exercised with separate threads and overlapping active leases; this does not claim game renderer thread safety. No multiplayer/mod-loader validation.",
                "Allocation sizes and repeated alternating warmed timings are host .NET fixture observations including Scope allocation, reset, fixture boundary overhead, GC and scheduler noise; not Switch sizes, FPS, frame time, GPU time, or a hardware speed gain."
            }
        };
        Json(Path.Combine(output, "reuse-proof.json"), result); return result;
    }

    static void Poison(object scratch, CostCell tile, CostTexture texture)
    {
        foreach (var field in scratch.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            Type t = field.FieldType;
            object value = t == typeof(CostCell) ? tile : t == typeof(CostTexture) ? texture :
                t == typeof(CostColor) ? new CostColor(211, 177, 93, 61) : t == typeof(CostRect) ? new CostRect(91, -32, 27, 41) :
                t == typeof(CostVec3[]) ? Enumerable.Range(0, 9).Select(i => new CostVec3 { X = i + 1, Y = i + 2, Z = i + 3 }).ToArray() :
                t == typeof(int) ? (object)917 : t == typeof(short) ? (object)(short)-127 : t == typeof(ushort) ? (object)(ushort)653 :
                throw new InvalidDataException("missing poison field " + field.Name + ":" + t);
            field.SetValue(scratch, value);
        }
    }

    static T Bind<T>(MethodInfo method) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!; var arguments = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
        var dynamic = new DynamicMethod("Bridge_" + method.Name, invoke.ReturnType, arguments, typeof(ReuseProof).Module, true); var il = dynamic.GetILGenerator();
        var required = (method.IsStatic ? Array.Empty<Type>() : new[] { method.DeclaringType! }).Concat(method.GetParameters().Select(p => p.ParameterType)).ToArray();
        Require(required.Length == arguments.Length, "delegate bridge arity");
        for (int i = 0; i < arguments.Length; i++) { il.Emit(System.Reflection.Emit.OpCodes.Ldarg, i); if (arguments[i] != required[i]) il.Emit(System.Reflection.Emit.OpCodes.Castclass, required[i]); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, method); il.Emit(System.Reflection.Emit.OpCodes.Ret); return dynamic.CreateDelegate<T>();
    }
    static AcquireCall BindAcquire(MethodInfo method, Type scope)
    {
        var dynamic = new DynamicMethod("Bridge_Acquire", typeof(object), new[] { typeof(CostDrawing), typeof(object).MakeByRefType() }, typeof(ReuseProof).Module, true); var il = dynamic.GetILGenerator();
        var lease = il.DeclareLocal(scope); var result = il.DeclareLocal(typeof(object));
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldloca, lease); il.Emit(System.Reflection.Emit.OpCodes.Call, method); il.Emit(System.Reflection.Emit.OpCodes.Stloc, result);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Ldloc, lease); il.Emit(System.Reflection.Emit.OpCodes.Stind_Ref); il.Emit(System.Reflection.Emit.OpCodes.Ldloc, result); il.Emit(System.Reflection.Emit.OpCodes.Ret);
        return dynamic.CreateDelegate<AcquireCall>();
    }
    static Func<object> NewScratch(ConstructorInfo constructor)
    {
        var dynamic = new DynamicMethod("Bridge_Ctor", typeof(object), Type.EmptyTypes, typeof(ReuseProof).Module, true); var il = dynamic.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Newobj, constructor); il.Emit(System.Reflection.Emit.OpCodes.Ret); return dynamic.CreateDelegate<Func<object>>();
    }

    static void ReentrantProof(Action<CostDrawing, CostVec, CostVec, int, int> before, Action<CostDrawing, CostVec, CostVec, int, int> after,
        Action<CostDrawing, bool, bool, int> drawBefore, Action<CostDrawing, bool, bool, int> drawAfter, Func<CostDrawing, object?> enter, Action<object?> exit,
        Func<object?> current, Func<object, string, object?> get, Action<bool, string> check, List<object> scenarios)
    {
        foreach (bool nestedDraw in new[] { false, true }) foreach (bool sameOwner in new[] { false, true }) foreach (bool throwChild in new[] { false, true })
        {
            string label = (nestedDraw ? "nested_Draw" : "recursive_Single") + (sameOwner ? "_same_owner" : "_other_owner") + (throwChild ? "_throw_caught" : "_success");
            (string state, string[] trace, string[] snapshots) Run(bool candidate)
            {
                CostFixture.Reset(3); ReuseFixture.Reset(); var owner = new CostDrawing(); var other = sameOwner ? owner : new CostDrawing();
                var single = candidate ? after : before; var draw = candidate ? drawAfter : drawBefore;
                CostFixture.Reenter = () => {
                    var parent = CostFixture.LastScratch!; string parentSnapshot = ReuseFixture.Snapshot(parent); var parentScope = current(); int start = ReuseFixture.Scratch.Count;
                    if (throwChild) CostFixture.ThrowAt = "DrawBasicTile";
                    Exception? error = null;
                    try { if (nestedDraw) draw(other, true, true, 8); else single(other, new(10, 20), new(3, 4), 1, 0); }
                    catch (Exception e) { error = e; }
                    finally { CostFixture.ThrowAt = null; }
                    check(throwChild ? ReferenceEquals(error, CostFixture.Failure) : error == null, label + ":child_exception_" + candidate);
                    check(ReuseFixture.Snapshot(parent) == parentSnapshot && ReuseFixture.Scratch.Skip(start).All(item => !ReferenceEquals(item, parent)), label + ":parent_storage_untouched_" + candidate);
                    if (candidate) check(ReferenceEquals(current(), parentScope) && (bool)get(parentScope!, "Busy")!, label + ":parent_lease_and_scope_restored");
                };
                draw(owner, true, false, 9); check(current() == null, label + ":outer_scope_restored_" + candidate);
                return (State(owner), CostFixture.Trace.ToArray(), ReuseFixture.Snapshots.ToArray());
            }
            var baseline = Run(false); var candidate = Run(true);
            check(baseline.state == candidate.state && baseline.trace.SequenceEqual(candidate.trace) && baseline.snapshots.SequenceEqual(candidate.snapshots), label + ":effects_fields_order_equivalent");
            scenarios.Add(new { name = label, trace = baseline.trace, snapshots = baseline.snapshots, state = baseline.state });
        }
        foreach (string boundary in new[] { "EnsureWindGridSize", "GetScreenDrawArea", "DrawBasicTile", "RestartSpriteBatch" })
        {
            CostFixture.Reset(3); ReuseFixture.Reset(); var owner = new CostDrawing(); var previous = enter(owner); var parent = current(); CostFixture.ThrowAt = boundary;
            Exception? error = null; try { drawAfter(owner, true, false, 0); } catch (Exception e) { error = e; }
            check(ReferenceEquals(error, CostFixture.Failure) && ReferenceEquals(current(), parent) && !(bool)get(parent!, "Busy")!, "Draw_throw_" + boundary + "_restores_outer_scope"); exit(previous);
        }
        CostFixture.Reset(3); ReuseFixture.Reset(); var mismatchOwner = new CostDrawing(); var saved = enter(mismatchOwner); var outer = current()!;
        after(new CostDrawing(), default, default, 0, 0); after(new CostDrawing(), default, default, 0, 0);
        check(get(outer, "Scratch") == null && !(bool)get(outer, "Busy")! && ReuseFixture.Scratch.Distinct(ReferenceEqualityComparer.Instance).Count() == 2, "actual_unmatched_Single_keeps_fresh_storage_and_outer_scope_empty");
        CostFixture.Solid = false; drawAfter(mismatchOwner, true, false, 0);
        check(ReferenceEquals(current(), outer) && get(outer, "Scratch") == null, "nested_filtered_Draw_restores_empty_parent"); exit(saved);
    }

    static void ThreadProof(CostDrawing owner, Func<CostDrawing, object?> enter, Action<object?> exit, AcquireCall acquire, Action<object?> release,
        Func<object?> current, Func<object, string, object?> get, Action<bool, string> check)
    {
        var saved = enter(owner); var parent = current()!; var parentScratch = acquire(owner, out var parentLease); var parentArray = get(parentScratch, "colorSlices");
        using var ready = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim(); Exception? failure = null;
        bool clean = false, independent = false, restored = false; object? childScope = null, childScratch = null;
        var thread = new Thread(() => {
            try {
                clean = current() == null; var prior = enter(owner); childScope = current(); childScratch = acquire(owner, out var lease);
                independent = !ReferenceEquals(childScope, parent) && !ReferenceEquals(childScratch, parentScratch) && !ReferenceEquals(get(childScratch, "colorSlices"), parentArray);
                ready.Set(); if (!finish.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("thread release"); release(lease); exit(prior); restored = current() == null;
            } catch (Exception e) { failure = e; ready.Set(); }
        });
        thread.Start(); bool signalled = ready.Wait(TimeSpan.FromSeconds(20)); bool parentUntouched = ReferenceEquals(current(), parent) && (bool)get(parent, "Busy")! && ReferenceEquals(get(parent, "Scratch"), parentScratch);
        finish.Set(); bool joined = thread.Join(TimeSpan.FromSeconds(20)); release(parentLease); exit(saved);
        check(signalled && joined && failure == null && clean && independent && restored && parentUntouched && current() == null, "overlapping_thread_scopes_and_leases_are_independent_not_renderer_thread_safety");
    }

    static object Measurements(Action<CostDrawing, CostVec, CostVec, int, int> before, Action<CostDrawing, CostVec, CostVec, int, int> after,
        Action<CostDrawing, bool, bool, int> drawBefore, Action<CostDrawing, bool, bool, int> drawAfter, Func<CostDrawing, object?> enter,
        Action<object?> exit, AcquireCall acquire, Action<object?> release, Action<bool, string> check, string output)
    {
        const int singleCalls = 20000, drawCalls = 1000, tiles = 65;
        (long bytes, long ticks) Single(Action<CostDrawing, CostVec, CostVec, int, int> run, int mode)
        {
            CostFixture.Reset(); ReuseFixture.Reset(); CostFixture.Record = false; var owner = new CostDrawing();
            long beginBytes = GC.GetAllocatedBytesForCurrentThread(), beginTicks = Stopwatch.GetTimestamp(); object? saved = null, lease = null;
            if (mode != 0) saved = enter(owner); if (mode == 2) acquire(owner, out lease);
            for (int i = 0; i < singleCalls; i++) run(owner, default, default, 0, 0);
            if (mode == 2) release(lease); if (mode != 0) exit(saved);
            return (GC.GetAllocatedBytesForCurrentThread() - beginBytes, Stopwatch.GetTimestamp() - beginTicks);
        }
        (long bytes, long ticks) Draw(Action<CostDrawing, bool, bool, int> run, int width)
        {
            CostFixture.Reset(width); ReuseFixture.Reset(); CostFixture.Record = false; var owner = new CostDrawing();
            long beginBytes = GC.GetAllocatedBytesForCurrentThread(), beginTicks = Stopwatch.GetTimestamp();
            for (int i = 0; i < drawCalls; i++) run(owner, true, false, 0);
            return (GC.GetAllocatedBytesForCurrentThread() - beginBytes, Stopwatch.GetTimestamp() - beginTicks);
        }
        // Warm every path before collecting either allocations or timings.
        Single(before, 0); Single(after, 0); Single(after, 1); Single(after, 2); Draw(drawBefore, tiles); Draw(drawAfter, tiles); Draw(drawBefore, 0); Draw(drawAfter, 0);
        var baseline = Single(before, 0); var unscoped = Single(after, 0); var scoped = Single(after, 1); var reentrant = Single(after, 2);
        var oldEmpty = Draw(drawBefore, 0); var newEmpty = Draw(drawAfter, 0); var oldDraw = Draw(drawBefore, tiles); var newDraw = Draw(drawAfter, tiles);
        long pairBytes = baseline.bytes / singleCalls, scopeBytes = (newEmpty.bytes - oldEmpty.bytes) / drawCalls;
        check(baseline.bytes > 0 && baseline.bytes % singleCalls == 0 && baseline.bytes == unscoped.bytes, "actual_unscoped_Single_retains_fresh_allocation_per_call");
        check(scopeBytes > 0 && scoped.bytes == pairBytes + scopeBytes, "scoped_full_Single_batch_allocates_one_descriptor_array_pair_plus_Scope");
        check(reentrant.bytes == baseline.bytes + pairBytes + scopeBytes, "busy_reentrant_full_Single_batch_retains_fresh_allocations_plus_parent_storage");
        check(newDraw.bytes - oldDraw.bytes == drawCalls * (scopeBytes - (tiles - 1) * pairBytes), "actual_Draw_allocation_delta_matches_one_pair_per_invocation_instead_of_per_tile");
        check(newEmpty.bytes > oldEmpty.bytes, "empty_Draw_pays_honest_scope_allocation_overhead");
        var rounds = new List<object>(); var baselineTicks = new List<long>(); var candidateTicks = new List<long>(); var deltas = new List<long>();
        for (int round = 0; round < 9; round++)
        {
            (long bytes, long ticks) old, candidate;
            if ((round & 1) == 0) { old = Draw(drawBefore, tiles); candidate = Draw(drawAfter, tiles); }
            else { candidate = Draw(drawAfter, tiles); old = Draw(drawBefore, tiles); }
            check(old.bytes == oldDraw.bytes && candidate.bytes == newDraw.bytes, "alternating_Draw_allocation_counts_" + round);
            baselineTicks.Add(old.ticks); candidateTicks.Add(candidate.ticks); deltas.Add(candidate.ticks - old.ticks);
            rounds.Add(new { round, baselineFirst = (round & 1) == 0, baselineTicks = old.ticks, candidateTicks = candidate.ticks, deltaTicks = candidate.ticks - old.ticks, baselineBytes = old.bytes, candidateBytes = candidate.bytes });
        }
        long Median(List<long> values) => values.OrderBy(v => v).ElementAt(values.Count / 2);
        var timing = new { hostFixtureOnly = true, drawCallsPerRound = drawCalls, singleCallsPerDraw = tiles, rounds, stopwatchFrequency = Stopwatch.Frequency,
            medianBaselineTicks = Median(baselineTicks), minimumBaselineTicks = baselineTicks.Min(), maximumBaselineTicks = baselineTicks.Max(), medianCandidateTicks = Median(candidateTicks), minimumCandidateTicks = candidateTicks.Min(), maximumCandidateTicks = candidateTicks.Max(),
            medianPairedDeltaTicks = Median(deltas), minimumPairedDeltaTicks = deltas.Min(), maximumPairedDeltaTicks = deltas.Max(), caveat = "Full serialized Draw->65 full Singles, warmed alternating host batches. Includes Scope, reset, fixture boundaries, GC and scheduling; not hardware gain or Switch object sizes." };
        Json(Path.Combine(output, "host-timing.json"), timing);
        var result = new { hostFixtureOnly = true, singleCalls, drawCalls, tilesPerDraw = tiles, baselineSingleBytes = baseline.bytes, candidateUnscopedBytes = unscoped.bytes, candidateScopedBytes = scoped.bytes, candidateReentrantBytes = reentrant.bytes,
            hostDescriptorAndArrayPairBytes = pairBytes, hostScopeBytes = scopeBytes, baselineDrawBytes = oldDraw.bytes, candidateDrawBytes = newDraw.bytes, baselineEmptyDrawBytes = oldEmpty.bytes, candidateEmptyDrawBytes = newEmpty.bytes,
            baselineDescriptorsAndArraysPerDraw = tiles, candidateDescriptorsAndArraysPerDraw = 1, candidateScopesPerDraw = 1, timing };
        Json(Path.Combine(output, "allocations.json"), result); CostFixture.Record = true; return result;
    }

    static byte[] Build(AssemblyDefinition baseline, AssemblyDefinition candidate, byte[] frameProbe, List<object> receipts, bool baselineOnly = false)
    {
        using var probe = AssemblyDefinition.ReadAssembly(new MemoryStream(frameProbe)); var module = probe.MainModule;
        probe.Name.Name = "TileReuseExactProbe"; module.Name = "TileReuseExactProbe";
        var hooks = new TypeDefinition("Probe", "ReuseHooks", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
        var hosts = new Dictionary<string, Type> {
            ["Terraria.GameContent.Drawing.TileDrawing"] = typeof(CostDrawing), ["Terraria.GameContent.Drawing.TileDrawingBase"] = typeof(CostDrawing),
            ["Terraria.Main"] = typeof(CostWorld), ["Terraria.Tile"] = typeof(CostCell), ["Terraria.Player"] = typeof(CostPlayer), ["Terraria.HitTile"] = typeof(object),
            ["Terraria.TimeLogger/TimeLogData"] = typeof(FixtureMetric), ["Terraria.TimeLogger/DataSeries"] = typeof(FixtureSeries), ["Terraria.TimeLogger"] = typeof(CostWorld),
            ["Microsoft.Xna.Framework.Vector2"] = typeof(CostVec), ["Microsoft.Xna.Framework.Vector3"] = typeof(CostVec3), ["Microsoft.Xna.Framework.Vector4"] = typeof(CostVec4), ["Microsoft.Xna.Framework.Point"] = typeof(CostPoint), ["Microsoft.Xna.Framework.Rectangle"] = typeof(CostRect), ["Microsoft.Xna.Framework.Color"] = typeof(CostColor), ["Microsoft.Xna.Framework.Graphics.SpriteEffects"] = typeof(int),
            ["Terraria.GameContent.TilePaintSystemV2/TileVariationkey"] = typeof(TileKey), ["Terraria.GameContent.Drawing.DrawBlackHelper"] = typeof(CostBlack),
            ["Terraria.Graphics.Camera"] = typeof(CostCamera), ["Terraria.SceneMetrics"] = typeof(CostScene), ["Terraria.Graphics.TileBatch"] = typeof(CostBatch), ["Terraria.Graphics.VertexColors"] = typeof(CostVertices),
            ["Terraria.Testing.DebugOptions"] = typeof(TileDebug), ["Terraria.FocusHelper"] = typeof(CostFocus), ["Terraria.GameContent.TextureAssets"] = typeof(CostTextures),
            ["ReLogic.Content.Asset`1<Microsoft.Xna.Framework.Graphics.Texture2D>"] = typeof(CostAsset), ["Microsoft.Xna.Framework.Graphics.Texture2D"] = typeof(CostTexture),
            ["Microsoft.Xna.Framework.Graphics.SpriteBatch"] = typeof(object), ["Terraria.DataStructures.TileObjectPreviewData"] = typeof(TilePreview), ["Terraria.TileObject"] = typeof(CostObject),
            ["Terraria.Graphics.Capture.CaptureManager"] = typeof(TileCapture), ["Terraria.GameContent.Drawing.TileDrawing/TileCounterType"] = typeof(int),
            ["Terraria.ID.TileID/Sets"] = typeof(CostSets), ["Terraria.Lighting"] = typeof(CostLighting), ["Terraria.Dust"] = typeof(CostDust), ["Terraria.Utilities.UnifiedRandom"] = typeof(CostRandom), ["Terraria.Utilities.FastRandom"] = typeof(CostFastRandom),
            ["Terraria.Utils"] = typeof(CostUtils), ["Terraria.WorldGen"] = typeof(CostUtils), ["Terraria.GameContent.Liquid.LiquidRenderer"] = typeof(CostUtils), ["Terraria.GameContent.PortalHelper"] = typeof(CostUtils),
            ["Terraria.Graphics.Effects.Filters"] = typeof(CostFilters), ["Terraria.Graphics.Effects.FilterManager"] = typeof(CostFilterManager), ["Terraria.Graphics.Effects.EffectManager`1<Terraria.Graphics.Effects.Filter>"] = typeof(CostFilterManager), ["Terraria.Graphics.Effects.Filter"] = typeof(CostFilter), ["Terraria.Graphics.Effects.GameEffect"] = typeof(CostFilter)
        };
        var mapped = new Dictionary<string, TypeDefinition>(); var originals = new List<TypeDefinition>();
        void DefineType(TypeDefinition source, string name)
        {
            var target = new TypeDefinition("Probe", name, TA.Public | (source.IsValueType ? TA.SequentialLayout | TA.Sealed : source.IsAbstract ? TA.Abstract | TA.Sealed : 0), source.IsValueType ? module.ImportReference(typeof(ValueType)) : module.TypeSystem.Object);
            module.Types.Add(target); mapped.Add(source.FullName, target); originals.Add(source);
        }
        var sourceScratch = Types(candidate.MainModule).Single(t => t.FullName == "Terraria.DataStructures.TileDrawInfo"); DefineType(sourceScratch, "ReuseScratch");
        var sourceRuntime = baselineOnly ? null : Types(candidate.MainModule).Single(t => t.Name == "NXTileReuse46");
        if (sourceRuntime != null) { DefineType(sourceRuntime, "ReuseRuntime"); foreach (var nested in sourceRuntime.NestedTypes) DefineType(nested, "Reuse" + nested.Name); }
        TypeReference TypeMap(TypeReference t)
        {
            if (mapped.TryGetValue(t.FullName, out var n)) return n;
            if (hosts.TryGetValue(t.FullName, out var h)) return module.ImportReference(h);
            if (t is ArrayType a) { var copy = new ArrayType(TypeMap(a.ElementType), a.Rank); for (int i = 0; i < a.Rank; i++) copy.Dimensions[i] = new ArrayDimension(a.Dimensions[i].LowerBound, a.Dimensions[i].UpperBound); return copy; }
            if (t is ByReferenceType r) return new ByReferenceType(TypeMap(r.ElementType));
            if (t is GenericInstanceType g) { var copy = new GenericInstanceType(TypeMap(g.ElementType)); foreach (var x in g.GenericArguments) copy.GenericArguments.Add(TypeMap(x)); return copy; }
            Require(t.Namespace.StartsWith("System"), "unmapped reuse fixture type " + t.FullName);
            if (t.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr) return Primitive(module, t.FullName);
            return module.ImportReference(t);
        }
        bool Included(TypeDefinition source, MethodDefinition method) => source != sourceScratch || method.IsConstructor && !method.IsStatic || method.Name == "NXReset46";
        foreach (var source in originals)
        {
            var target = mapped[source.FullName];
            foreach (var f in source.Fields)
            {
                var field = new FieldDefinition(f.Name, (f.Attributes & ~FA.FieldAccessMask) | FA.Public, TypeMap(f.FieldType)); if (f.HasConstant) field.Constant = f.Constant;
                foreach (var a in f.CustomAttributes) { Require(a.AttributeType.FullName == "System.ThreadStaticAttribute", "unsupported fixture field attribute " + a.AttributeType.FullName); field.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!))); }
                target.Fields.Add(field);
            }
            foreach (var m in source.Methods.Where(m => Included(source, m))) Invoke("Patcher", "Define", m, target, (Func<TypeReference, TypeReference>)TypeMap);
        }
        var singles = new Dictionary<string, MethodDefinition>(); var allCopies = new List<(MethodDefinition source, MethodDefinition target)>();
        foreach (var (game, prefix) in new[] { (baseline, "Baseline"), (candidate, "Candidate") })
        {
            var drawing = Types(game.MainModule).Single(t => t.FullName == "Terraria.GameContent.Drawing.TileDrawing");
            foreach (var (source, suffix) in new[] { (drawing.Methods.Single(m => m.Name == "DrawSingleTile" && m.Parameters.Count == 4), "Single"), (drawing.Methods.Single(m => m.Name == "Draw" && m.Parameters.Count == 3), "Draw") })
            {
                var target = new MethodDefinition(prefix + suffix, MA.Public | MA.Static, module.TypeSystem.Void) { ImplAttributes = source.ImplAttributes }; hooks.Methods.Add(target); target.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(CostDrawing)))); foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
                singles[prefix + suffix] = target; allCopies.Add((source, target));
            }
        }
        MethodReference HostMethod(Type type, string name) => module.ImportReference(type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!);
        string singlePrefix = "Candidate";
        object Member(object o)
        {
            if (o is TypeReference t) return TypeMap(t);
            if (o is FieldReference f)
            {
                if (f.DeclaringType.FullName == "Terraria.TimeLogger" && f.Name == "activeDataSeries") return module.ImportReference(typeof(FixtureLogger).GetField("activeDataSeries")!);
                if (mapped.TryGetValue(f.DeclaringType.FullName, out var d)) return d.Fields.Single(x => x.Name == f.Name);
                if (hosts.TryGetValue(f.DeclaringType.FullName, out var h)) { var field = h.GetField(f.Name, Flags); Require(field != null, "missing host field " + f.FullName); var reference = module.ImportReference(field!); reference.FieldType = TypeMap(f.FieldType); return reference; }
                return new FieldReference(f.Name, TypeMap(f.FieldType), TypeMap(f.DeclaringType));
            }
            if (o is MethodReference m)
            {
                if (m.DeclaringType.FullName == "Terraria.GameContent.Drawing.TileDrawing" && m.Name == "DrawSingleTile") return singles[singlePrefix + "Single"];
                if (mapped.TryGetValue(m.DeclaringType.FullName, out var d)) return d.Methods.Single(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count);
                if (m.DeclaringType.FullName == "Terraria.GameContent.Drawing.TileDrawing" && m.Name is "CacheSpecialDraws_Part2" or "DrawBasicTile" or "DrawTile_MinecartTrack" or "DrawXmasTree") return HostMethod(typeof(ReuseFixture), m.Name);
                if (m.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && m.Name == "GetTimestamp") return HostMethod(typeof(TileFixture), "Clock");
                if (m.DeclaringType.FullName == "Terraria.TimeLogger" && m.Name.StartsWith("New")) return HostMethod(typeof(TileFixture), m.Name);
                if (m.DeclaringType.FullName == "Terraria.TimeLogger/TimeLogData" && m.Name == "Add") return HostMethod(typeof(TileFixture), "Add");
                if (hosts.TryGetValue(m.DeclaringType.FullName, out var h))
                {
                    if (m.Name == ".ctor") return module.ImportReference(h.GetConstructors().Single(c => c.GetParameters().Length == m.Parameters.Count && c.GetParameters().Select(p => module.ImportReference(p.ParameterType).FullName).SequenceEqual(m.Parameters.Select(p => TypeMap(p.ParameterType).FullName))));
                    var candidates = h.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Where(x => x.Name == m.Name && x.GetParameters().Length == m.Parameters.Count).ToArray();
                    Require(candidates.Length == 1, "missing/ambiguous reuse boundary " + m.FullName);
                    Require(candidates[0].IsStatic != m.HasThis, "reuse boundary receiver mismatch " + m.FullName); return module.ImportReference(candidates[0]);
                }
                var n = new MethodReference(m.Name, TypeMap(m.ReturnType), TypeMap(m.DeclaringType)) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention }; foreach (var p in m.Parameters) n.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType))); return n;
            }
            return o;
        }
        foreach (var source in originals)
            foreach (var method in source.Methods.Where(m => Included(source, m)))
            {
                // The constructor is always copied from the pinned baseline, not a replacement.
                var original = source == sourceScratch && method.IsConstructor ? Types(baseline.MainModule).Single(t => t.FullName == sourceScratch.FullName).Methods.Single(m => m.IsConstructor && !m.IsStatic) : method;
                var target = mapped[source.FullName].Methods.Single(m => m.Name == method.Name && m.Parameters.Count == method.Parameters.Count); Copy(original, target, TypeMap, Member); allCopies.Add((original, target));
            }
        foreach (var (source, target) in allCopies.Where(p => p.target.DeclaringType == hooks))
        { singlePrefix = target.Name.StartsWith("Baseline") ? "Baseline" : "Candidate"; Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)TypeMap, (Func<object, object>)Member, 1); }
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Types(module).SelectMany(t => t.Methods).Select(Body)))).AsSpan(0, 16));
        using var stream = new MemoryStream(); probe.Write(stream, new WriterParameters { Timestamp = 0 }); byte[] bytes = stream.ToArray();
        using var roundtrip = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var (source, target) in allCopies)
        {
            var serialized = Types(roundtrip.MainModule).Single(t => t.FullName == target.DeclaringType.FullName).Methods.Single(m => m.Name == target.Name && m.Parameters.Count == target.Parameters.Count);
            Require(Fingerprint(target) == Fingerprint(serialized), "reuse fixture serialization body mismatch " + target.FullName);
            receipts.Add(new { source = source.FullName, sourceModuleMvid = source.Module.Mvid, sourceBodySha256 = Fingerprint(source), sourceInstructions = source.Body.Instructions.Count, sourceLocals = source.Body.Variables.Count, sourceExceptionHandlers = source.Body.ExceptionHandlers.Count, fixture = target.FullName, fixtureBodySha256 = Fingerprint(serialized), fixtureInstructions = serialized.Body.Instructions.Count, fixtureLocals = serialized.Body.Variables.Count, fixtureExceptionHandlers = serialized.Body.ExceptionHandlers.Count });
        }
        return bytes;
    }
}
