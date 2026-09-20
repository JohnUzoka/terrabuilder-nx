using System.Diagnostics;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class CostProof
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
#if COST_BASELINE_ONLY
    public static int Main(string[] args)
    {
        try
        {
            Require(args.Length is 3 or 4 && !Directory.Exists(args[2]), "standalone proof requires input, frame probe, fresh output, and optional patched45 image"); Directory.CreateDirectory(args[2]);
            using var source = AssemblyDefinition.ReadAssembly(args[0]); var receipts = new List<object>();
            if (args.Length == 4)
            {
                using var patched = AssemblyDefinition.ReadAssembly(args[3]); File.WriteAllBytes(Path.Combine(args[2], "FrameProfileProbe.dll"), File.ReadAllBytes(args[1])); Verify(source, patched, args[2]); Console.WriteLine("FULL45 COST PROOF PASS"); return 0;
            }
            byte[] bytes = Build(source, source, File.ReadAllBytes(args[1]), receipts, true); File.WriteAllBytes(Path.Combine(args[2], "BaselineTileCostProbe.dll"), bytes);
            var loaded = Assembly.Load(bytes); var hooks = loaded.GetType("Probe.CostHooks", true)!; var frame = loaded.GetType("Probe.Hooks", true)!;
            TileFixture.TimerRegister = frame.GetMethod("NewEntry")!.CreateDelegate<Func<FixtureMetric, FixtureMetric>>(); TileFixture.CounterRegister = frame.GetMethod("NewCounterEntry")!.CreateDelegate<Func<FixtureMetric, FixtureMetric>>(); FixtureLogger.entries.Clear();
            var single = hooks.GetMethod("BaselineSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>(); var draw = hooks.GetMethod("BaselineDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>(); var drawing = new CostDrawing(); var cases = new List<object>();
            foreach (ushort type in new ushort[] { 0, 518, 751, 752, 323, 72, 80, 83, 129, 429, 314, 171, 725 })
            {
                CostFixture.Reset(type: type); CostSets.HasOutlines[type] = true;
                if (type == 518) CostWorld.tile[0, 0]!.liquid = 1;
                if (type is 751 or 752 or 323) CostWorld.tile[0, 0]!.frameX = 90;
                if (type == 72) CostWorld.tile[0, 0]!.frameX = 36;
                if (type is 129 or 429 or 725) CostWorld.tileGlowMask[type] = 0;
                if (type == 129) CostWorld.tile[0, 0]!.frameX = 324;
                single(drawing, new(10, 20), new(3, 4), 0, 0); cases.Add(new { type, trace = CostFixture.Trace.ToArray(), effectHash = CostFixture.Effects });
            }
            CostFixture.Reset(65); draw(drawing, true, false, 0); Require(CostFixture.Trace.Count(n => n == "DrawBasicTile") == 65, "baseline actual Draw/Single linkage");
            CostFixture.Reset(); CostFixture.Record = false; for (int i = 0; i < 1000; i++) single(drawing, default, default, 0, 0);
            long start = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 10000; i++) single(drawing, default, default, 0, 0); long allocation = GC.GetAllocatedBytesForCurrentThread() - start; Require(allocation > 0, "original Single must allocate");
            Json(Path.Combine(args[2], "baseline-smoke.json"), new { passed = true, baselineOnly = true, hostFixtureOnly = true, probeSha256 = Sha(bytes), receipts, cases, calls = 10000, allocatedBytes = allocation, bytesPerCall = allocation / 10000, caveat = "Host fixture allocation bytes only, not Switch object/array size, time, FPS, or later GC cost" }); Console.WriteLine("BASELINE FULL-IL PASS allocatedBytes=" + allocation); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
#endif
    internal static object Verify(AssemblyDefinition baseline44, AssemblyDefinition patched45, string output)
    {
        var receipts = new List<object>();
        byte[] bytes = Build(baseline44, patched45, File.ReadAllBytes(Path.Combine(output, "FrameProfileProbe.dll")), receipts);
        File.WriteAllBytes(Path.Combine(output, "TileCostProbe.dll"), bytes);
        var loaded = Assembly.Load(bytes); var hooks = loaded.GetType("Probe.CostHooks", true)!;
        var helper = loaded.GetType("Probe.CostRuntime", true)!; var context = loaded.GetType("Probe.CostContext", true)!; var sample = loaded.GetType("Probe.CostSample", true)!;
        var tile44 = loaded.GetType("Probe.CostTile44", true)!; var runtime43 = loaded.GetType("Probe.Runtime", true)!; var frameHooks = loaded.GetType("Probe.Hooks", true)!;
        var before = hooks.GetMethod("BaselineSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>();
        var after = hooks.GetMethod("PatchedSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>();
        var drawBefore = hooks.GetMethod("BaselineDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>();
        var drawAfter = hooks.GetMethod("PatchedDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>();
        TileFixture.TimerRegister = frameHooks.GetMethod("NewEntry")!.CreateDelegate<Func<FixtureMetric, FixtureMetric>>();
        TileFixture.CounterRegister = frameHooks.GetMethod("NewCounterEntry")!.CreateDelegate<Func<FixtureMetric, FixtureMetric>>();
        FixtureLogger.entries.Clear(); FixtureLogger.activeDataSeries = 0;
        var checks = new List<string>(); var scenarios = new List<object>();
        void Check(bool ok, string text) { Require(ok, "cost proof: " + text); checks.Add(text); }
        object Current() => helper.GetField("current", Flags)!.GetValue(null)!;
        long Number(object value, string field) => Convert.ToInt64(value.GetType().GetField(field, Flags)!.GetValue(value));
        bool Active() => (bool)context.GetField("Active", Flags)!.GetValue(Current())!;
        void Set(object value, string name, object fieldValue) => value.GetType().GetField(name, Flags)!.SetValue(value, fieldValue);
        object Saved(int phase = 0, int offset = 0) { var args = new object[] { Activator.CreateInstance(context)!, phase, offset }; helper.GetMethod("Enter", Flags)!.Invoke(null, args); return args[0]; }
        void Finish(object saved) => helper.GetMethod("Finish", Flags)!.Invoke(null, new[] { saved });
        void Phases(int solid = 0, int nonsolid = 0) { tile44.GetField("solidPhase", Flags)!.SetValue(null, solid); tile44.GetField("nonSolidPhase", Flags)!.SetValue(null, nonsolid); }
        long Metric(bool solid, string name) => FixtureLogger.entries.Single(m => m.name == "tile45." + (solid ? "solid." : "nonsolid.") + name).data[0].values[0];
        bool Used(bool solid, string name) => FixtureLogger.entries.Single(m => m.name == "tile45." + (solid ? "solid." : "nonsolid.") + name).data[0].used[0];
        var drawing = new CostDrawing();
        CostFixture.Reset(); after(drawing, new CostVec(10, 20), new CostVec(3, 4), 0, 0);
        Check(CostFixture.ClockCalls == 0 && !Active(), "cold_unscoped_full_Single_has_no_observation");
        CostFixture.Reset(0, 0); drawAfter(drawing, true, false, 0);
        Check(FixtureLogger.entries.Count == 48, "actual44_and45_registration_20_plus28_once");
        var kinds = (int[])runtime43.GetField("kinds", Flags)!.GetValue(null)!;
        Check(FixtureLogger.entries.Where(m => m.name.StartsWith("tile45.")).All(m => kinds[m._nxProfileId - 1] == (m.name.EndsWith(".completed_samples") ? 0 : 1)), "actual43_factory_epilogues_distinguish_time_and_count");
        var scratchCtor = loaded.GetType("Probe.CostScratch", true)!.GetConstructor(Type.EmptyTypes)!;
        object scratch = scratchCtor.Invoke(null);
        Check(scratch.GetType().GetField("colorSlices")!.GetValue(scratch) is CostVec3[] { Length: 9 }, "original_constructor_allocates_Vector3_array_length9");
        var metricCurrent = hooks.GetMethod("MetricCurrent")!.CreateDelegate<Func<FixtureMetric, int>>(); var seriesCurrent = hooks.GetMethod("SeriesCurrent")!.CreateDelegate<Func<FixtureSeries, int>>();
        var slotMetric = new FixtureMetric(); slotMetric.data[0].next = 17; slotMetric.data[0].values[0] = 101; slotMetric.data[0].values[17] = 203; slotMetric.data[1].next = 23; slotMetric.data[1].values[23] = 407;
        FixtureLogger.activeDataSeries = 1; Check(metricCurrent(slotMetric) == 407 && seriesCurrent(slotMetric.data[0]) == 203, "serialized_accessors_select_active_series_and_current_slot");
        FixtureLogger.activeDataSeries = 0; Check(metricCurrent(slotMetric) == 203 && slotMetric.data[0].values[0] == 101 && slotMetric.data[0].next == 17 && slotMetric.data[1].next == 23 && !slotMetric.data[0].used.Any(v => v) && !slotMetric.data[1].used.Any(v => v), "serialized_accessors_are_read_only");
        var vecA = new CostVec(10, 20); var vecB = new CostVec(3, 4);
        string State() => $"{CostFixture.Effects}:{CostWorld.mapTime}:{CostWorld.critterCage}:{CostFixture.Dust[0].fadeIn}:{CostFixture.Dust[0].velocity.X}:{CostFixture.Dust[0].noGravity}:{CostFixture.Dust[0].noLight}:{CostFixture.Dust[0].noLightEmittance}";
        void Scenario(string name, ushort type = 0, Action? configure = null, string? failure = null, int phase = 0)
        {
            CostFixture.Reset(type: type); configure?.Invoke(); CostFixture.ThrowAt = failure;
            Exception? oldError = null; try { before(drawing, vecA, vecB, 0, 0); } catch (Exception e) { oldError = e; }
            string oldState = State(); string[] oldTrace = CostFixture.Trace.ToArray();
            CostFixture.Reset(type: type); configure?.Invoke(); CostFixture.ThrowAt = failure;
            object saved = Saved(phase); Exception? error = null;
            try { after(drawing, vecA, vecB, 0, 0); } catch (Exception e) { error = e; }
            object state = Current(); long clocks = CostFixture.ClockCalls; Finish(saved);
            Check(State() == oldState && CostFixture.Trace.SequenceEqual(oldTrace), name + ":effect_order_arguments_byrefs_and_values");
            Check(ReferenceEquals(error, oldError) && (failure == null ? error == null : ReferenceEquals(error, CostFixture.Failure)), name + ":exception_identity");
            Check(!Active(), name + ":scope_restored");
            long selected = phase == 0 ? 1 : 0, completed = error == null ? selected : 0;
            Check(Number(state, "Calls") == 1 && Number(state, "Selected") == selected && Number(state, "Completed") == completed, name + ":matched_selected_completed");
            long operations = new[] { "AllocOps", "LightOps", "TextureOps", "BaseOps" }.Sum(f => Number(state, f));
            if (phase != 0) Check(clocks == 0, name + ":zero_unsampled_clocks");
            if (error == null && phase == 0) Check(clocks == 2 + operations * 2 && Number(state, "Invalid") == 0 && Number(state, "Valid") == 1, name + ":exact_two_body_plus_two_per_operation_clocks");
            if (error != null) Check(operations == 0 && Number(state, "Body") == 0 && !Used(true, "call_body.completed_samples"), name + ":exception_publishes_no_partial_time_or_operations");
            if (error == null && phase == 0) foreach (var pair in new[] { ("AllocOps", "alloc_init"), ("LightOps", "light_and_frame"), ("TextureOps", "texture_lookup"), ("BaseOps", "base_draw") })
                Check(Used(true, pair.Item2 + ".completed_samples") == (Number(state, pair.Item1) != 0), name + ":zero_operation_time_is_unused_" + pair.Item2);
            if (error == null && phase == 0)
            {
                Check(Number(state, "AllocOps") == 1 && Number(state, "LightOps") == oldTrace.Count(n => n is "GetColor" or "GetTileDrawData" or "GetTileOutlineInfo" or "DrawTiles_GetLightOverride" or "GetFinalLight") && Number(state, "TextureOps") == oldTrace.Count(n => n == "GetTileDrawTexture") && Number(state, "BaseOps") == oldTrace.Count(n => n is "DrawBasicTile" or "DrawTile_MinecartTrack" or "DrawXmasTree"), name + ":operation_counts_match_only_executed_direct_calls");
                Check(Number(state, "Body") == Number(state, "Alloc") + Number(state, "Light") + Number(state, "Texture") + Number(state, "Base") + Number(state, "Other") && Number(state, "Other") >= 0, name + ":body_is_disjoint_groups_plus_observer_remainder");
            }
            scenarios.Add(new { name, type, phase, trace = oldTrace, effectHash = oldState, clocks45 = clocks, operations, selected, completed });
        }
        Scenario("normal_basic");
        Check(CostFixture.Trace.Contains("DrawBasicTile") && CostFixture.LastScratch != null && (int)CostFixture.LastScratch.GetType().GetField("tileWidth")!.GetValue(CostFixture.LastScratch)! == 16, "byref_frame_width_reaches_base_draw");
        Scenario("unselected_basic", phase: 1);
        Scenario("liquid_type518_early_return", 518, () => CostWorld.tile[0, 0]!.liquid = 1);
        Check(!CostFixture.Trace.Contains("GetTileDrawData"), "type518_liquid_returns_before_frame_boundary");
        Scenario("type518_without_liquid", 518);
        Scenario("outline", configure: () => CostSets.HasOutlines[0] = true);
        Check(CostFixture.Trace.Contains("GetTileOutlineInfo") && CostFixture.Trace.Contains("Graphics.Draw"), "outline_byref_texture_and_color_reach_graphics");
        Scenario("minecart", 314); Check(CostFixture.Trace.Contains("DrawTile_MinecartTrack") && !CostFixture.Trace.Contains("DrawBasicTile"), "mutually_exclusive_minecart_boundary");
        Scenario("tree", 171); Check(CostFixture.Trace.Contains("DrawXmasTree") && !CostFixture.Trace.Contains("DrawBasicTile"), "mutually_exclusive_tree_boundary");
        Scenario("cactus_byref_flags", 80); Check(CostFixture.Trace.Contains("GetCactusType"), "cactus_byref_flags_boundary_covered");
        Scenario("plant_texture_reload", 83); Check(CostFixture.Trace.Count(n => n == "GetTileDrawTexture") >= 2, "texture_reload_callsite_covered");
        foreach (ushort earlyType in new ushort[] { 751, 752, 323 }) { Scenario("frame_early_return_" + earlyType, earlyType, () => CostWorld.tile[0, 0]!.frameX = 90); Check(!CostFixture.Trace.Contains("DrawBasicTile"), "frame_early_return_before_base_" + earlyType); }
        Scenario("shroom_additional_lighting", 72, () => CostWorld.tile[0, 0]!.frameX = 36); Check(CostFixture.Trace.Count(n => n == "GetColor") == 2, "secondary_GetColor_callsite_covered");
        Scenario("glow_texture_reload", 429, () => CostWorld.tileGlowMask[429] = 0); Check(CostFixture.Trace.Count(n => n == "GetTileDrawTexture") == 2, "third_texture_callsite_covered");
        Scenario("crystal_texture_reload", 129, () => { CostWorld.tileGlowMask[129] = 0; CostWorld.tile[0, 0]!.frameX = 324; }); Check(CostFixture.Trace.Count(n => n == "GetTileDrawTexture") == 2, "fourth_texture_callsite_covered");
        Scenario("filter_generic_return", 725, () => CostWorld.tileGlowMask[725] = 0); Check(CostFixture.Trace.Contains("Filter.get_Item"), "closed_generic_boundary_return_preserved");
        Scenario("glow_texture", configure: () => CostFixture.Glow = true);
        Scenario("hidden_dark", configure: () => { CostFixture.Dark = true; CostWorld.tile[0, 0]!.Hidden = true; });
        Scenario("layer_over", configure: () => CostFixture.TileTop = -2);
        Scenario("layer_behind", configure: () => CostFixture.TileHeight = 24);
        drawing._isActiveAndNotPaused = true;
        Scenario("senses_rng_particles", configure: () => { CostWorld.player[0].dangerSense = CostWorld.player[0].findTreasure = CostWorld.player[0].biomeSight = true; });
        Check(CostFixture.Trace.Contains("NewDust") && CostFixture.Trace.Contains("DrawTiles_EmitParticles"), "original_rng_and_particle_boundaries_executed");
        Scenario("fast_random_value_return", configure: () => CostFixture.UpdateEveryFrame = true); Check(CostFixture.Trace.Contains("FastRandom.WithModifier"), "FastRandom_value_return_preserved");
        drawing._isActiveAndNotPaused = false;
        foreach (string boundary in new[] { "GetColor", "GetTileDrawData", "GetTileOutlineInfo", "GetTileDrawTexture", "DrawTiles_GetLightOverride", "GetFinalLight", "CacheSpecialDraws_Part2", "DrawBasicTile", "Graphics.Draw" })
            Scenario("throw_" + boundary, configure: () => CostSets.HasOutlines[0] = true, failure: boundary);
        Scenario("throw_minecart", 314, failure: "DrawTile_MinecartTrack"); Scenario("throw_tree", 171, failure: "DrawXmasTree");
        Scenario("invalid_clock_then_original_throw", configure: () => CostFixture.ClockScript = new long[] { -1, 100, 200, 300 }, failure: "DrawBasicTile");
        CostFixture.Reset(); after(drawing, vecA, vecB, 0, 0);
        Check(CostFixture.ClockCalls == 0 && !Active() && Metric(true, "calls.selected") == 0, "unscoped_Single_no_timestamps_or_fabricated_observation");
        foreach (bool solid in new[] { true, false })
        {
            CostFixture.Reset(65, 1, solid: solid); Phases(); drawBefore(drawing, solid, false, 3); string state = State(); string[] trace = CostFixture.Trace.ToArray();
            CostFixture.Reset(65, 1, solid: solid); Phases(); drawAfter(drawing, solid, false, 3);
            Check(State() == state && CostFixture.Trace.SequenceEqual(trace), $"actual44_Draw_to_full_Single_linkage_{solid}");
            Check(Metric(solid, "calls.selected") == 3 && Metric(solid, "calls.completed") == 3 && Metric(solid, "alloc_init.operations") == 3 && !Active(), $"actual_Draw_65_calls_three_completed_samples_{solid}");
            Check(CostFixture.ClockCalls == 48 && TileFixture.ClockCalls == 12, $"actual_Draw_clock_accounting_48_inside45_plus12_retained44_{solid}");
            int selected = 0; Phases();
            for (int p = 0; p < 32; p++) { CostFixture.Reset(solid: solid); drawAfter(drawing, solid, false, 0); selected += (int)Metric(solid, "calls.selected"); }
            Check(selected == 1, $"actual44_32_pass_phase_rotation_{solid}");
        }
        Phases(); int interleavedSolid = 0, interleavedNonSolid = 0;
        for (int phase = 0; phase < 32; phase++)
        {
            CostFixture.Reset(); drawAfter(drawing, true, false, 0); interleavedSolid += (int)Metric(true, "calls.selected");
            CostFixture.Reset(solid: false); drawAfter(drawing, false, false, 0); interleavedNonSolid += (int)Metric(false, "calls.selected");
        }
        Check(interleavedSolid == 1 && interleavedNonSolid == 1, "interleaved_solid_and_nonsolid_keep_independent32_pass_phases");
        CostFixture.Reset(0, 0); drawAfter(drawing, true, false, 0);
        Check(Metric(true, "calls.selected") == 0 && CostFixture.ClockCalls == 0 && !Used(true, "call_body.completed_samples") && !Active(), "empty_pass_unused_time_not_fake_zero");
        CostFixture.Reset(65); Phases(); CostFixture.Reenter = () => drawBefore(drawing, true, true, 8); drawBefore(drawing, true, false, 9); string nestedState = State(); string[] nestedTrace = CostFixture.Trace.ToArray();
        CostFixture.Reset(65); Phases(); CostFixture.Reenter = () => drawAfter(drawing, true, true, 8); drawAfter(drawing, true, false, 9);
        Check(State() == nestedState && CostFixture.Trace.SequenceEqual(nestedTrace) && Metric(true, "calls.selected") == 5 && Metric(true, "calls.completed") == 5 && !Active(), "nested_actual_Draw_restores_parent_phase_counts_accumulators_and_effect_order");
        CostFixture.Reset(3); Phases(); CostFixture.ThrowAt = "DrawBasicTile"; Exception? drawError = null;
        try { drawAfter(drawing, true, false, 0); } catch (Exception e) { drawError = e; }
        Check(ReferenceEquals(drawError, CostFixture.Failure) && !Active() && Metric(true, "calls.selected") == 1 && Metric(true, "calls.completed") == 0, "actual_Draw_outer_finally_restores_context_on_Single_exception");
        CostFixture.ThrowAt = null;
        foreach (bool childSolid in new[] { true, false })
        {
            CostFixture.Reset(65); Phases(); bool caughtChild = false, restoredParent = false;
            CostFixture.Reenter = () => {
                CostFixture.Solid = childSolid; Phases(); CostFixture.ThrowAt = "DrawBasicTile";
                try { drawAfter(drawing, childSolid, true, 8); } catch (Exception e) { caughtChild = ReferenceEquals(e, CostFixture.Failure); }
                finally { CostFixture.ThrowAt = null; CostFixture.Solid = true; }
                restoredParent = Active() && Number(Current(), "Calls") == 1 && Number(Current(), "Selected") == 1 && Number(Current(), "Phase") == 0;
            };
            drawAfter(drawing, true, false, 9);
            Check(caughtChild && restoredParent && Metric(true, "calls.completed") == 3 && !Active(), "nested_throw_parent_continues_" + childSolid);
        }
        CostFixture.Reset(); object parentSaved = Saved(7); var parent = Current(); Set(parent, "Calls", 17L); helper.GetField("current", Flags)!.SetValue(null, parent);
        using (var ready = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
        {
            Exception? threadFailure = null; bool childClean = false, childRestored = false; long childCalls = 0;
            var thread = new Thread(() => { try { childClean = !Active(); CostFixture.ClockStep = 100; object saved = Saved(0, 14); helper.GetMethod("BeginSample", Flags)!.Invoke(null, new[] { Activator.CreateInstance(sample)! }); childCalls = Number(Current(), "Calls"); ready.Set(); release.Wait(); Finish(saved); childRestored = !Active(); } catch (Exception e) { threadFailure = e; ready.Set(); } });
            thread.Start(); ready.Wait(); bool parentUntouched = Number(Current(), "Calls") == 17 && Number(Current(), "Phase") == 7; release.Set(); thread.Join();
            Check(threadFailure == null && childClean && childRestored && childCalls == 1 && parentUntouched, "deterministic_independent_ThreadStatic_context_not_renderer_thread_safety");
        }
        Finish(parentSaved);
        void Invalid(string name, long[]? timestamps, Action<object>? corrupt = null)
        {
            CostFixture.Reset(); CostFixture.ClockScript = timestamps; object saved = Saved();
            if (corrupt == null) after(drawing, vecA, vecB, 0, 0);
            else { object value = Activator.CreateInstance(sample)!; Set(value, "Selected", true); Set(value, "Valid", true); Set(value, "Start", 0L); Set(value, "Last", 0L); corrupt(value); var c = Current(); Set(c, "Selected", 1L); helper.GetField("current", Flags)!.SetValue(null, c); helper.GetMethod("EndSample", Flags)!.Invoke(null, new[] { value }); }
            var state = Current(); Finish(saved);
            Check(Number(state, "Completed") == 1 && Number(state, "Invalid") == 1 && Number(state, "Valid") == 0 && Metric(true, "calls.invalid_timing") == 1 && !Used(true, "call_body.completed_samples") && Metric(true, "alloc_init.operations") == 0, name);
        }
        Invalid("negative_timestamp_discarded", new long[] { -1, 0, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400 });
        Invalid("backward_timestamp_discarded", new long[] { 100, 200, 199, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500 });
        Invalid("group_sum_exceeds_body_discarded", null, value => Set(value, "Alloc", 1000L));
        Invalid("overflowing_group_sum_discarded", null, value => { Set(value, "Alloc", long.MaxValue); Set(value, "Base", 1L); });
        CostFixture.Reset(); object outSaved = Saved(); object outContext = Current();
        Set(outContext, "Selected", 1L); Set(outContext, "Completed", 1L); Set(outContext, "Valid", 1L); Set(outContext, "Alloc", long.MaxValue / 4); Set(outContext, "Body", long.MaxValue / 4);
        Set(outContext, "AllocOps", 1L);
        helper.GetField("current", Flags)!.SetValue(null, outContext); Finish(outSaved);
        Check(Metric(true, "time_metrics.omitted_out_of_range") >= 2 && !Used(true, "alloc_init.completed_samples") && !Used(true, "call_body.completed_samples"), "out_of_Int32_time_omitted_not_wrapped");
        CostFixture.Reset(); object maxSaved = Saved(), maxContext = Current();
        Set(maxContext, "Selected", 1L); Set(maxContext, "Completed", 1L); Set(maxContext, "Valid", 1L); Set(maxContext, "AllocOps", 1L); Set(maxContext, "Alloc", (long)int.MaxValue); Set(maxContext, "Body", (long)int.MaxValue);
        helper.GetField("current", Flags)!.SetValue(null, maxContext); Finish(maxSaved);
        Check(Metric(true, "alloc_init.completed_samples") == int.MaxValue && Metric(true, "call_body.completed_samples") == int.MaxValue && Metric(true, "time_metrics.omitted_out_of_range") == 0, "Int32_max_time_is_in_range_and_preserved");
        CostFixture.Reset();
        for (int passIndex = 0; passIndex < 2; passIndex++)
        {
            object saved = Saved(), value = Current(); Set(value, "Selected", 1L); Set(value, "Completed", 1L); Set(value, "Valid", 1L); Set(value, "AllocOps", 1L); Set(value, "Alloc", 1500000000L); Set(value, "Body", 1500000000L);
            helper.GetField("current", Flags)!.SetValue(null, value); Finish(saved);
        }
        Check(Metric(true, "alloc_init.completed_samples") == 1500000000L && Metric(true, "call_body.completed_samples") == 1500000000L && Metric(true, "time_metrics.omitted_out_of_range") == 2, "two_in_range_pass_totals_do_not_overflow_existing_Int32_time_cell");
        CostFixture.Reset(); Phases(); CostFixture.Record = false;
        CostFixture.RealClock = true;
        for (int i = 0; i < 1000; i++) { before(drawing, vecA, vecB, 0, 0); after(drawing, vecA, vecB, 0, 0); }
        const int iterations = 20000;
        (long bytes, long ticks, long clocks) Measure(Action<CostDrawing, CostVec, CostVec, int, int> run, bool scoped, int phase)
        {
            object? saved = scoped ? Saved(phase) : null; CostFixture.ClockCalls = 0;
            long startBytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) run(drawing, vecA, vecB, 0, 0);
            long ticks = Stopwatch.GetTimestamp() - start, allocation = GC.GetAllocatedBytesForCurrentThread() - startBytes, clocks = CostFixture.ClockCalls;
            if (saved != null) Finish(saved); return (allocation, ticks, clocks);
        }
        var baseline = Measure(before, false, 0); var unscoped = Measure(after, false, 0); var scoped = Measure(after, true, 0);
        Check(baseline.bytes > 0 && baseline.bytes == unscoped.bytes && baseline.bytes == scoped.bytes, "warm_actual_Single_original_class_and_array_allocations_retained_no45_extra_bytes");
        Check(unscoped.clocks == 0 && scoped.clocks == ((iterations + 31) / 32) * 16, "warm_actual_Single_sampling_clock_bound");
        var allocations = new { iterations, baselineBytes = baseline.bytes, patchedUnscopedBytes = unscoped.bytes, patchedScopedBytes = scoped.bytes, baselineBytesPerCall = baseline.bytes / iterations, extraScopedBytes = scoped.bytes - baseline.bytes, baselineWallTicks = baseline.ticks, patchedUnscopedWallTicks = unscoped.ticks, patchedScopedWallTicks = scoped.ticks, extraScopedWallTicks = scoped.ticks - baseline.ticks, stopwatchFrequency = Stopwatch.Frequency, patchedScopedClocks = scoped.clocks, caveat = "Host deterministic game/graphics boundaries with real host timestamps plus clock-counting wrapper overhead. Noisy wall-time comparison only, not Switch cost, FPS, GPU time, or downstream game-callee allocations." };
        var timingRounds = new List<object>(); var baselineTicks = new List<long>(); var scopedTicks = new List<long>(); var deltaTicks = new List<long>();
        for (int round = 0; round < 9; round++)
        {
            (long bytes, long ticks, long clocks) first, second;
            if ((round & 1) == 0) { first = Measure(before, false, 0); second = Measure(after, true, 0); }
            else { second = Measure(after, true, 0); first = Measure(before, false, 0); }
            Check(first.bytes == baseline.bytes && second.bytes == baseline.bytes, "alternating_warm_allocation_identity_" + round);
            Check(first.clocks == 0 && second.clocks == ((iterations + 31) / 32) * 16, "alternating_warm_clock_bound_" + round);
            baselineTicks.Add(first.ticks); scopedTicks.Add(second.ticks); deltaTicks.Add(second.ticks - first.ticks);
            timingRounds.Add(new { round, baselineFirst = (round & 1) == 0, baselineTicks = first.ticks, instrumentedTicks = second.ticks, deltaTicks = second.ticks - first.ticks, baselineBytes = first.bytes, instrumentedBytes = second.bytes, instrumentedClocks = second.clocks });
        }
        long Median(List<long> values) => values.OrderBy(value => value).ElementAt(values.Count / 2);
        Json(Path.Combine(output, "host-overhead.json"), new { hostFixtureOnly = true, iterationsPerRound = iterations, rounds = timingRounds, frequency = Stopwatch.Frequency, medianBaselineTicks = Median(baselineTicks), medianInstrumentedTicks = Median(scopedTicks), medianPairedDeltaTicks = Median(deltaTicks), medianPairedExtraNanosecondsPerCall = Median(deltaTicks) * 1e9 / Stopwatch.Frequency / iterations, minimumPairedDeltaTicks = deltaTicks.Min(), maximumPairedDeltaTicks = deltaTicks.Max(), caveat = "Alternating warmed host batches include clock-counting wrappers, allocation/GC and host scheduling noise; these are not Switch overhead or a game optimization benchmark" });
        CostFixture.Record = true; CostFixture.Reset(65); Phases(); drawAfter(drawing, true, false, 0);
        runtime43.GetField("attempts", Flags)!.SetValue(null, 1); runtime43.GetField("complete", Flags)!.SetValue(null, true); runtime43.GetField("game", Flags)!.SetValue(null, new FixtureMain());
        var sink = new StringWriter(); var console = Console.Out;
        try { Console.SetOut(sink); runtime43.GetMethod("Boundary", Flags)!.Invoke(null, null); runtime43.GetMethod("Flush", Flags)!.Invoke(null, null); } finally { Console.SetOut(console); }
        string report = sink.ToString(); File.WriteAllText(Path.Combine(output, "cost-report-example.log"), report);
        Check(report.Contains("NX_PROFILE END") && report.Contains("label=\"tile45.solid.alloc_init.completed_samples\" unit=ms") && report.Contains("label=\"tile45.solid.alloc_init.operations\" unit=count"), "retained43_serialized_capture_export_honest_time_count_units");
        var result = new { passed = true, probeSha256 = Sha(bytes), completeOriginalAndPatchedSingleIlExecuted = true, originalScratchConstructorIlExecuted = true, actual44And45DrawIlExecuted = true, helperIlExecuted = true, receipts, checkCount = checks.Count, checks, scenarios, allocations, clocks = "Single:0 unselected/unscoped; selected normal:2+2*executed grouped operations. Normal basic:16. Retained44 Draw adds6+2*completed selected calls;45 Enter/Finish add no clocks.", metricNames = FixtureLogger.entries.Select(m => m.name).ToArray(), limitations = "Full serialized methods under test with deterministic game/graphics boundaries, including byrefs and value-type returns. External callees are not game or GPU implementations. No claim of complete branch coverage, renderer thread safety, unbiased timing, later GC attribution, Switch overhead, speed gain or FPS. Constructor class plus Vector3[9] remain allocated; remainder includes observers. Wall timing is host-only. Exact target metadata, reverse-splice and source-image preservation checks are supplied separately by acceptance audit." };
        Json(Path.Combine(output, "cost-proof.json"), result); return result;
    }

    static byte[] Build(AssemblyDefinition baseline, AssemblyDefinition patched, byte[] frameProbe, List<object> receipts, bool baselineOnly = false)
    {
        using var probe = AssemblyDefinition.ReadAssembly(new MemoryStream(frameProbe)); var module = probe.MainModule;
        probe.Name.Name = "TileCostExactProbe"; module.Name = "TileCostExactProbe";
        var hooks = new TypeDefinition("Probe", "CostHooks", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
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
        var source44 = Types(patched.MainModule).Single(t => t.Name == "NXTileProfile44"); DefineType(source44, "CostTile44"); DefineType(source44.NestedTypes.Single(), "CostPass44");
        var source45 = baselineOnly ? null : Types(patched.MainModule).Single(t => t.Name == "NXTileCost45");
        if (source45 != null) { DefineType(source45, "CostRuntime"); foreach (var nested in source45.NestedTypes) DefineType(nested, "Cost" + nested.Name); }
        var sourceScratch = Types(baseline.MainModule).Single(t => t.FullName == "Terraria.DataStructures.TileDrawInfo"); DefineType(sourceScratch, "CostScratch");
        TypeReference TypeMap(TypeReference t)
        {
            if (mapped.TryGetValue(t.FullName, out var n)) return n;
            if (hosts.TryGetValue(t.FullName, out var h)) return module.ImportReference(h);
            if (t is ArrayType a) { var copy = new ArrayType(TypeMap(a.ElementType), a.Rank); for (int i = 0; i < a.Rank; i++) copy.Dimensions[i] = new ArrayDimension(a.Dimensions[i].LowerBound, a.Dimensions[i].UpperBound); return copy; }
            if (t is ByReferenceType r) return new ByReferenceType(TypeMap(r.ElementType));
            if (t is GenericInstanceType g) { var copy = new GenericInstanceType(TypeMap(g.ElementType)); foreach (var x in g.GenericArguments) copy.GenericArguments.Add(TypeMap(x)); return copy; }
            Require(t.Namespace.StartsWith("System"), "unmapped cost fixture type " + t.FullName);
            if (t.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr) return Primitive(module, t.FullName);
            return module.ImportReference(t);
        }
        foreach (var source in originals)
        {
            var target = mapped[source.FullName];
            foreach (var f in source.Fields) { var field = new FieldDefinition(f.Name, (f.Attributes & ~FA.FieldAccessMask) | FA.Public, TypeMap(f.FieldType)); if (f.HasConstant) field.Constant = f.Constant; foreach (var a in f.CustomAttributes) { Require(a.AttributeType.FullName == "System.ThreadStaticAttribute", "unsupported fixture field attribute " + a.AttributeType.FullName); field.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!))); } target.Fields.Add(field); }
            foreach (var m in source.Methods.Where(m => source != sourceScratch || m.IsConstructor && !m.IsStatic)) Invoke("Patcher", "Define", m, target, (Func<TypeReference, TypeReference>)TypeMap);
        }
        var singles = new Dictionary<string, MethodDefinition>(); var allCopies = new List<(MethodDefinition source, MethodDefinition target)>();
        foreach (var (game, prefix) in new[] { (baseline, "Baseline"), (patched, "Patched") })
        {
#if COST_BASELINE_ONLY
            var sourceDrawing = Types(game.MainModule).Single(t => t.FullName == "Terraria.GameContent.Drawing.TileDrawing");
            foreach (var (source, suffix) in new[] { (sourceDrawing.Methods.Single(m => m.Name == "DrawSingleTile" && m.Parameters.Count == 4), "Single"), (sourceDrawing.Methods.Single(m => m.Name == "Draw" && m.Parameters.Count == 3), "Draw") })
#else
            foreach (var (source, suffix) in new[] { (Program.Single(game.MainModule), "Single"), (Program.Draw(game.MainModule), "Draw") })
#endif
            {
                var target = new MethodDefinition(prefix + suffix, MA.Public | MA.Static, module.TypeSystem.Void) { ImplAttributes = source.ImplAttributes }; hooks.Methods.Add(target); target.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(CostDrawing)))); foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
                singles[prefix + suffix] = target; allCopies.Add((source, target));
            }
        }
        var accessors = new Dictionary<string, MethodDefinition>();
        if (!baselineOnly) foreach (var (owner, name) in new[] { ("Terraria.TimeLogger/TimeLogData", "MetricCurrent"), ("Terraria.TimeLogger/DataSeries", "SeriesCurrent") })
        {
            var source = Types(patched.MainModule).Single(t => t.FullName == owner).Methods.Single(m => m.Name == "NXCost45Current");
            var target = new MethodDefinition(name, MA.Public | MA.Static, TypeMap(source.ReturnType)) { ImplAttributes = source.ImplAttributes };
            target.Parameters.Add(new ParameterDefinition(TypeMap(source.DeclaringType))); foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
            hooks.Methods.Add(target); accessors.Add(owner, target); allCopies.Add((source, target));
        }
        MethodReference HostMethod(Type type, string name) => module.ImportReference(type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!);
        string singlePrefix = "Patched"; bool clock45 = false;
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
                if (m.Name == "NXCost45Current" && accessors.TryGetValue(m.DeclaringType.FullName, out var accessor)) return accessor;
                if (m.DeclaringType.FullName == "Terraria.GameContent.Drawing.TileDrawing" && m.Name == "DrawSingleTile") return singles[singlePrefix + "Single"];
                if (mapped.TryGetValue(m.DeclaringType.FullName, out var d)) return d.Methods.Single(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count);
                if (m.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && m.Name == "GetTimestamp") return HostMethod(clock45 ? typeof(CostFixture) : typeof(TileFixture), "Clock");
                if (m.DeclaringType.FullName == "Terraria.TimeLogger" && m.Name.StartsWith("New")) return HostMethod(typeof(TileFixture), m.Name);
                if (m.DeclaringType.FullName == "Terraria.TimeLogger/TimeLogData" && m.Name == "Add") return HostMethod(typeof(TileFixture), "Add");
                if (hosts.TryGetValue(m.DeclaringType.FullName, out var h))
                {
                    if (m.Name == ".ctor") return module.ImportReference(h.GetConstructors().Single(c => c.GetParameters().Length == m.Parameters.Count && c.GetParameters().Select(p => module.ImportReference(p.ParameterType).FullName).SequenceEqual(m.Parameters.Select(p => TypeMap(p.ParameterType).FullName))));
                    var candidates = h.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Where(x => x.Name == m.Name && x.GetParameters().Length == m.Parameters.Count).ToArray();
                    Require(candidates.Length == 1, "missing/ambiguous cost boundary " + m.FullName);
                    Require(candidates[0].IsStatic != m.HasThis, "cost boundary receiver mismatch " + m.FullName); return module.ImportReference(candidates[0]);
                }
                var n = new MethodReference(m.Name, TypeMap(m.ReturnType), TypeMap(m.DeclaringType)) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention }; foreach (var p in m.Parameters) n.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType))); return n;
            }
            return o;
        }
        foreach (var source in originals)
        {
            clock45 = source45 != null && (source == source45 || source.DeclaringType == source45);
            foreach (var method in source.Methods.Where(m => source != sourceScratch || m.IsConstructor && !m.IsStatic))
            { var target = mapped[source.FullName].Methods.Single(m => m.Name == method.Name && m.Parameters.Count == method.Parameters.Count); Copy(method, target, TypeMap, Member); allCopies.Add((method, target)); }
        }
        clock45 = false;
        foreach (var (source, target) in allCopies.Where(p => p.target.DeclaringType == hooks))
        { singlePrefix = target.Name.StartsWith("Baseline") ? "Baseline" : "Patched"; Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)TypeMap, (Func<object, object>)Member, 1); }
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Types(module).SelectMany(t => t.Methods).Select(Body)))).AsSpan(0, 16));
        using var stream = new MemoryStream(); probe.Write(stream, new WriterParameters { Timestamp = 0 }); byte[] bytes = stream.ToArray();
        using var roundtrip = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var (source, target) in allCopies)
        {
            var serialized = Types(roundtrip.MainModule).Single(t => t.FullName == target.DeclaringType.FullName).Methods.Single(m => m.Name == target.Name && m.Parameters.Count == target.Parameters.Count);
            Require(Fingerprint(target) == Fingerprint(serialized), "cost fixture serialization body mismatch " + target.FullName);
            receipts.Add(new { source = source.FullName, sourceModuleMvid = source.Module.Mvid, sourceBodySha256 = Fingerprint(source), sourceInstructions = source.Body.Instructions.Count, sourceLocals = source.Body.Variables.Count, sourceExceptionHandlers = source.Body.ExceptionHandlers.Count, fixture = target.FullName, fixtureBodySha256 = Fingerprint(serialized), fixtureInstructions = serialized.Body.Instructions.Count, fixtureLocals = serialized.Body.Variables.Count, fixtureExceptionHandlers = serialized.Body.ExceptionHandlers.Count });
        }
        return bytes;
    }
}
