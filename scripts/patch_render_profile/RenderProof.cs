using System.Diagnostics;
using System.Reflection;
using Mono.Cecil;
using static Common;

internal static class RenderProof
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static readonly string[] MetricFields = { "Color", "Data", "Outline", "Override", "Final" };
    internal static readonly string[] MetricLabels = { "GetColor", "GetTileDrawData", "GetTileOutlineInfo", "DrawTiles_GetLightOverride", "GetFinalLight" };
    internal static object Run(string originalPath, string candidatePath, string fna, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Require(Sha(File.ReadAllBytes(originalPath)) == "90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22", "proof original is not pinned accepted52");
        Require(Sha(File.ReadAllBytes(fna)) == "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f", "proof FNA is not pinned accepted52");
        using var original = AssemblyDefinition.ReadAssembly(originalPath); using var candidate = AssemblyDefinition.ReadAssembly(candidatePath);
        var receipts = new List<object>(); byte[] bytes = RenderProbe.Build(original, candidate, receipts);
        File.WriteAllBytes(Path.Combine(outputDirectory, "RenderExactProbe.dll"), bytes);
        var assembly = Assembly.Load(bytes); var hooks = assembly.GetType("Probe.Hooks", true)!; var runtime = assembly.GetType("Probe.Runtime", true)!;
        var proof = new Session(runtime, hooks);
        var methods = proof.ExerciseMethods(); var batches = RenderBatchProof.Run(proof, outputDirectory); var invariants = RenderRuntimeProof.Run(proof, outputDirectory);
        var manifest = System.Text.Json.JsonSerializer.SerializeToElement(invariants);
        Json(Path.Combine(outputDirectory, "report-manifest.json"), new { candidateSha256 = Sha(File.ReadAllBytes(candidatePath)), accepted = manifest.GetProperty("acceptedReports"), rejected = manifest.GetProperty("rejectedReports") });
        var negatives = RenderNegativeProof.Run(originalPath, candidatePath, outputDirectory);
        var coverage = RenderCoverageProof.Run(bytes, outputDirectory);
        var result = new { passed = true, proofVersion = 53, originalSha256 = Sha(File.ReadAllBytes(originalPath)), candidateSha256 = Sha(File.ReadAllBytes(candidatePath)), fnaSha256 = Sha(File.ReadAllBytes(fna)), probeSha256 = Sha(bytes), checkCount = proof.Checks.Count, checks = proof.Checks, receipts, methods, batches, invariants, negatives, coverage,
            executionContract = "Whole serialized original/candidate TileDrawing.Draw, DrawSingleTile, Main.Draw, Program.RunGame, original TileDrawInfo constructor, TileBatch.End, RenderBatch, FlushLayered, and every candidate helper method are cloned; runtime/scenario coverage is reported explicitly. Only external game/graphics/state/time boundaries are deterministic host fixtures. No earlier injected profiler required.",
            limits = "Not exhaustive game branch coverage. External lighting/graphics/RNG callees are fixture boundaries, not game/GPU implementations. Runtime timers are actual candidate IL; clock values are deterministic substitutions except labeled host overhead runs. Begin/end state snapshots cannot detect toggle-and-return between boundaries. Host allocation/wall time is not Switch overhead, FPS, GPU cost or optimization adoption." };
        Json(Path.Combine(outputDirectory, "proof.json"), result); return result;
    }
    internal sealed class Session
    {
        internal readonly Type Runtime, Hooks;
        internal readonly List<string> Checks = new();
        internal readonly Action<CostDrawing, CostVec, CostVec, int, int> BeforeSingle, AfterSingle;
        internal readonly Action<CostDrawing, bool, bool, int> BeforeDraw, AfterDraw;
        internal readonly Action<FixtureMain, FixtureTime> BeforeMain, AfterMain;
        internal readonly Action BeforeRun, AfterRun;
        internal readonly CostDrawing Drawing = new();
        internal Session(Type runtime, Type hooks)
        {
            Runtime = runtime; Hooks = hooks;
            BeforeSingle = hooks.GetMethod("BaselineSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>();
            AfterSingle = hooks.GetMethod("PatchedSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>();
            BeforeDraw = hooks.GetMethod("BaselineDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>(); AfterDraw = hooks.GetMethod("PatchedDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>();
            BeforeMain = hooks.GetMethod("BaselineMain")!.CreateDelegate<Action<FixtureMain, FixtureTime>>(); AfterMain = hooks.GetMethod("PatchedMain")!.CreateDelegate<Action<FixtureMain, FixtureTime>>();
            BeforeRun = hooks.GetMethod("BaselineRun")!.CreateDelegate<Action>(); AfterRun = hooks.GetMethod("PatchedRun")!.CreateDelegate<Action>();
        }
        internal void Check(bool condition, string name) { Require(condition, "render53 proof: " + name); Checks.Add(name); }
        internal object Get(string field) => Runtime.GetField(field, Flags)!.GetValue(null)!;
        internal void Set(string field, object? value) => Runtime.GetField(field, Flags)!.SetValue(null, value);
        internal static object Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value)!;
        internal static void Field(object value, string name, object? data) => value.GetType().GetField(name, Flags)!.SetValue(value, data);
        internal static long Number(object value, string field) => Convert.ToInt64(Field(value, field));
        internal long Number(string field) => Convert.ToInt64(Get(field));
        internal object? Call(string method, params object[] arguments)
        {
            try { return Runtime.GetMethod(method, Flags)!.Invoke(null, arguments); }
            catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        internal void Reset()
        {
            foreach (var field in Runtime.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral && !f.IsInitOnly)) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
            RenderWorldFixture.Reset(); RenderFixture.ResetClock(); CostFixture.Reset(); FixtureLogger.entries.Clear();
            FixtureMain.ThrowStage = 0; FixtureMain.Record = true; FixtureMain.Effects = 0; FixtureMain.GraphicsAvailable = true; FixtureMain.OnPostDraw = null; FixtureMain.ToggleInventory = false;
            RenderGameFixture.Reset();
        }
        internal object Enter(bool solid = true)
        {
            var passType = Runtime.GetMethod("Enter", Flags)!.GetParameters()[1].ParameterType.GetElementType()!;
            var args = new object[] { solid, Activator.CreateInstance(passType)! }; Call("Enter", args); return args[1];
        }
        internal void Region(ref object pass, int region) { var args = new[] { pass, (object)region }; Call("Region", args); pass = args[0]; }
        internal void Finish(object pass) { Call("Finish", pass); }
        internal void ResetPhases() { Set("phaseSolid", 0); Set("phaseNonSolid", 0); }
        internal long Layer(bool solid, string field, bool window = false) => Number(Get((window ? "Window" : "Frame") + (solid ? "Solid" : "NonSolid")), field);
        internal long Metric(object totals, int id, string field = "Calls") => Number(Field(totals, MetricFields[id]), field);
        internal void Single(bool after = true) => (after ? AfterSingle : BeforeSingle)(Drawing, new(10, 20), new(3, 4), 0, 0);
        internal string Effects() => $"{CostFixture.Effects}:{CostWorld.mapTime}:{CostWorld.critterCage}:{CostFixture.Dust[0].fadeIn}:{CostFixture.Dust[0].velocity.X}:{CostFixture.Dust[0].noGravity}:{CostFixture.Dust[0].noLight}:{CostFixture.Dust[0].noLightEmittance}";
        internal object ExerciseMethods()
        {
            var scenarios = new List<object>(); var allocationRounds = new List<object>();
            void Scenario(string name, ushort type = 0, Action? configure = null, string? failure = null, bool selected = true)
            {
                Reset(); CostFixture.Reset(type: type); configure?.Invoke(); CostFixture.ThrowAt = failure;
                Exception? beforeError = null; try { Single(false); } catch (Exception e) { beforeError = e; }
                string expected = Effects(); string[] trace = CostFixture.Trace.ToArray();
                Reset(); CostFixture.Reset(type: type); configure?.Invoke(); CostFixture.ThrowAt = failure;
                Exception? controlError = null; try { Single(false); } catch (Exception e) { controlError = e; }
                Check(Effects() == expected && CostFixture.Trace.SequenceEqual(trace) && ReferenceEquals(controlError, beforeError), name + ":same_baseline_replay_control");
                Reset(); CostFixture.Reset(type: type); configure?.Invoke(); CostFixture.ThrowAt = failure; Set("FrameCollecting", true);
                object pass = Enter(); Field(pass, "Visited", 1L); Field(pass, "Eligible", 1L); Field(pass, "Calls", 1L);
                Region(ref pass, 0); if (selected) Call("BeginSelected"); long initialClocks = RenderFixture.ClockCalls;
                Exception? error = null; try { Single(); } catch (Exception e) { error = e; }
                long clocks = RenderFixture.ClockCalls - initialClocks; object totals = Get("current");
                Check(Effects() == expected && CostFixture.Trace.SequenceEqual(trace), name + ":effects_order_arguments_byrefs_values");
                Check(ReferenceEquals(error, beforeError) && (failure == null ? error == null : ReferenceEquals(error, CostFixture.Failure)), name + ":exception_identity");
                Check(Number(totals, "Selected") == (selected ? 1 : 0) && Number(totals, "Completed") == (selected && error == null ? 1 : 0), name + ":selection_completion");
                if (!selected) Check(clocks == 0, name + ":unsampled_no_clock");
                if (selected && error == null)
                {
                    Check(Number(totals, "Valid") == 1 && Number(totals, "Invalid") == 0, name + ":valid_sample");
                    for (int i = 0; i < 5; i++) Check(Metric(totals, i) == trace.Count(t => t == MetricLabels[i]), name + ":executed_" + MetricLabels[i]);
                    long operations = Enumerable.Range(0, 5).Sum(i => Metric(totals, i));
                    Check(clocks == 1 + 2 * operations, name + ":one_end_plus_two_per_operation_clocks");
                    Check(Enumerable.Range(0, 5).Sum(i => Metric(totals, i, "Ticks")) <= Number(totals, "SampleTicks"), name + ":helper_sum_within_sample");
                    Region(ref pass, 1); Region(ref pass, 2);
                }
                Finish(pass); Check(!(bool)Field(Get("current"), "Active") && !(bool)Get("Pending"), name + ":scope_restored");
                if (error != null) Check(Layer(true, "ValidSamples") == 0 && Enumerable.Range(0, 5).All(i => Metric(Get("FrameSolid"), i) == 0), name + ":no_partial_sample_donation");
                scenarios.Add(new { name, type, selected, clocks, trace, expected });
            }
            Scenario("basic"); Scenario("unsampled_basic", selected: false);
            Scenario("return_007a_liquid", 518, () => CostWorld.tile[0, 0]!.liquid = 1);
            Scenario("outline", configure: () => CostSets.HasOutlines[0] = true);
            Scenario("minecart", 314); Scenario("tree", 171); Scenario("cactus", 80); Scenario("plant_reload", 83);
            Scenario("return_0916", 751, () => CostWorld.tile[0, 0]!.frameX = 90);
            Scenario("return_094c", 752, () => CostWorld.tile[0, 0]!.frameX = 90);
            Scenario("return_0ce5", 323, () => CostWorld.tile[0, 0]!.frameX = 90);
            Scenario("two_GetColor", 72, () => CostWorld.tile[0, 0]!.frameX = 36);
            Scenario("crystal_reload", 129, () => { CostWorld.tileGlowMask[129] = 0; CostWorld.tile[0, 0]!.frameX = 324; });
            Scenario("glow_reload", 429, () => CostWorld.tileGlowMask[429] = 0); Scenario("generic_filter_return", 725, () => CostWorld.tileGlowMask[725] = 0);
            Scenario("hidden_dark", configure: () => { CostFixture.Dark = true; CostWorld.tile[0, 0]!.Hidden = true; });
            Scenario("glow", configure: () => CostFixture.Glow = true); Scenario("over_layer", configure: () => CostFixture.TileTop = -2); Scenario("behind_layer", configure: () => CostFixture.TileHeight = 24);
            Scenario("particles_rng", configure: () => { Drawing._isActiveAndNotPaused = true; CostWorld.player[0].dangerSense = CostWorld.player[0].findTreasure = CostWorld.player[0].biomeSight = true; }); Drawing._isActiveAndNotPaused = false;
            Scenario("value_rng", configure: () => CostFixture.UpdateEveryFrame = true);
            foreach (string boundary in MetricLabels.Concat(new[] { "GetTileDrawTexture", "CacheSpecialDraws_Part2", "DrawBasicTile", "Graphics.Draw" })) Scenario("throw_" + boundary, configure: () => CostSets.HasOutlines[0] = true, failure: boundary);
            Scenario("throw_minecart", 314, failure: "DrawTile_MinecartTrack"); Scenario("throw_tree", 171, failure: "DrawXmasTree");
            foreach (bool solid in new[] { true, false })
            {
                Reset(); CostFixture.Reset(257, solid: solid); BeforeDraw(Drawing, solid, false, 3); string state = Effects(); string[] trace = CostFixture.Trace.ToArray();
                Reset(); CostFixture.Reset(257, solid: solid); Set("FrameCollecting", true); AfterDraw(Drawing, solid, false, 3);
                Check(state == Effects() && trace.SequenceEqual(CostFixture.Trace), "whole_Draw_to_Single_257_effects_" + solid);
                Check(Layer(solid, "Calls") == 257 && Layer(solid, "Selected") == 3 && Layer(solid, "ValidSamples") == 3, "whole_Draw_inline_counts_sampling_" + solid);
                Check(Layer(solid, "Visited") >= 257 && Layer(solid, "Eligible") >= 257, "whole_Draw_observed_count_bound_" + solid);
            }
            Reset(); Set("FrameCollecting", true);
            for (int phase = 0; phase < 128; phase++)
            {
                CostFixture.Reset(); AfterDraw(Drawing, true, false, 0); CostFixture.Reset(solid: false); AfterDraw(Drawing, false, false, 0);
            }
            Check(Layer(true, "Selected") == 1 && Layer(false, "Selected") == 1 && Layer(true, "Passes") == 128 && Layer(false, "Passes") == 128, "independent_interleaved_128_phase_rotation");
            Reset(); Set("FrameCollecting", true); CostFixture.Reset(0, 0); AfterDraw(Drawing, true, false, 0); CostFixture.Reset(); AfterDraw(Drawing, true, false, 0);
            Check(Layer(true, "Selected") == 0 && Layer(true, "Passes") == 2, "empty_pass_advances_rotation_without_fake_sample");
            Reset(); CostFixture.Reset(129); CostFixture.Reenter = () => BeforeDraw(Drawing, true, true, 0); BeforeDraw(Drawing, true, false, 0); string normal = Effects(); string[] nestedTrace = CostFixture.Trace.ToArray();
            Reset(); Set("FrameCollecting", true); CostFixture.Reset(129); CostFixture.Reenter = () => AfterDraw(Drawing, true, true, 0); AfterDraw(Drawing, true, false, 0);
            Check(Layer(true, "NestedPasses") == 1 && Layer(true, "Selected") == 2 && !(bool)Field(Get("current"), "Active"), "nested_actual_Draw_excluded_parent_restored");
            Check(Effects() == normal && nestedTrace.SequenceEqual(CostFixture.Trace), "nested_whole_Draw_original_effects_and_arguments");
            foreach (bool childSolid in new[] { true, false })
            {
                Reset(); Set("FrameCollecting", true); CostFixture.Reset(129); bool caught = false, restored = false;
                CostFixture.Reenter = () => {
                    CostFixture.Solid = childSolid; CostFixture.ThrowAt = "DrawBasicTile";
                    try { AfterDraw(Drawing, childSolid, true, 0); } catch (Exception e) { caught = ReferenceEquals(e, CostFixture.Failure); }
                    finally { CostFixture.ThrowAt = null; CostFixture.Solid = true; }
                    restored = (bool)Field(Get("current"), "SampleActive") && !(bool)Get("Pending");
                };
                AfterDraw(Drawing, true, false, 0);
                Check(caught && restored && Layer(true, "ValidSamples") == 2 && Layer(childSolid, "NestedPasses") == 1, "nested_throw_restores_live_parent_sample_" + childSolid);
            }
            Reset(); Set("FrameCollecting", true); CostFixture.Reset(); long nestedClocks = -1;
            CostFixture.Reenter = () => { long clocks = RenderFixture.ClockCalls; Single(); nestedClocks = RenderFixture.ClockCalls - clocks; };
            AfterDraw(Drawing, true, false, 0);
            Check(nestedClocks == 0 && Layer(true, "Selected") == 1 && Layer(true, "ValidSamples") == 1 && Metric(Get("FrameSolid"), 0) == 1, "unscoped_nested_Single_preserves_parent_no_clocks_or_double_helper_count");
            Reset(); Set("FrameCollecting", true); CostFixture.Reset(3); CostFixture.ThrowAt = "DrawBasicTile";
            Exception? drawError = null; try { AfterDraw(Drawing, true, false, 0); } catch (Exception e) { drawError = e; }
            Check(ReferenceEquals(drawError, CostFixture.Failure) && Layer(true, "AbortedPasses") == 1 && Layer(true, "AbortedSamples") == 1 && Layer(true, "ValidSamples") == 0 && !(bool)Field(Get("current"), "Active"), "actual_Draw_exception_finally_restores_and_discards");
            Reset(); Single(); Check(RenderFixture.ClockCalls == 0 && !(bool)Get("Pending"), "unscoped_actual_Single_no_clocks");
            const int iterations = 10000;
            Reset(); CostFixture.Record = false; for (int i = 0; i < 1000; i++) { Single(false); Single(); }
            (long bytes, long ticks, long clocks) Measure(Action<CostDrawing, CostVec, CostVec, int, int> action)
            {
                long clock = RenderFixture.ClockCalls, startBytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) action(Drawing, default, default, 0, 0);
                return (GC.GetAllocatedBytesForCurrentThread() - startBytes, Stopwatch.GetTimestamp() - start, RenderFixture.ClockCalls - clock);
            }
            for (int round = 0; round < 5; round++)
            {
                var before = Measure(BeforeSingle); var after = Measure(AfterSingle);
                Check(before.bytes > 0 && before.bytes == after.bytes && after.clocks == 0, "warmed_unsampled_retains_original_allocations_zero_added_bytes_clocks_" + round);
                allocationRounds.Add(new { round, iterations, baselineBytes = before.bytes, candidateBytes = after.bytes, baselineTicks = before.ticks, candidateTicks = after.ticks, candidateClockCalls = after.clocks });
            }
            CostFixture.Record = true;
            return new { scenarios, scenarioCount = scenarios.Count, allocationRounds, hostFrequency = Stopwatch.Frequency,
                unsampledContract = "Only Pending field load/local bool plus branches in Single; observed host additional allocation bytes=0 and timestamp calls=0. Original TileDrawInfo class+Vector3[9] allocations remain. Caller has inline counters/bitmask guards; selected sample includes calibration/gross/helper timestamp overhead.",
                getColorZero = "GetColor first site is unconditional after tile dereference, so completed actual Single has 1 or 2 calls. Zero is exercised by empty passes and aborted-before-helper samples, not invented as a normal Single branch." };
        }
    }
}
