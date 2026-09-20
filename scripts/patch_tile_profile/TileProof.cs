using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class TileProof
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    internal static object Verify(AssemblyDefinition baseline, AssemblyDefinition patched, string output)
    {
        byte[] bytes = Build(baseline, patched, File.ReadAllBytes(Path.Combine(output, "FrameProfileProbe.dll")));
        File.WriteAllBytes(Path.Combine(output, "TileProfileProbe.dll"), bytes);
        var assembly = Assembly.Load(bytes); var hooks = assembly.GetType("Probe.Hooks", true)!;
        var helper = assembly.GetType("Probe.TileRuntime", true)!; var runtime43 = assembly.GetType("Probe.Runtime", true)!;
        var before = hooks.GetMethod("TileBaseline")!.CreateDelegate<Action<TileDrawingFixture, bool, bool, int>>();
        var after = hooks.GetMethod("TilePatched")!.CreateDelegate<Action<TileDrawingFixture, bool, bool, int>>();
        TileFixture.TimerRegister = hooks.GetMethod("NewEntry")!.CreateDelegate<Func<FixtureMetric, FixtureMetric>>();
        TileFixture.CounterRegister = hooks.GetMethod("NewCounterEntry")!.CreateDelegate<Func<FixtureMetric, FixtureMetric>>();
        FixtureLogger.entries.Clear(); FixtureLogger.activeDataSeries = 0;
        var checks = new List<string>(); void Check(bool ok, string name) { Require(ok, "tile proof: " + name); checks.Add(name); }
        var drawing = new TileDrawingFixture();
        long Metric(bool solid, string suffix) { var m = FixtureLogger.entries.Single(m => m.name == "tile44." + (solid ? "solid." : "nonsolid.") + suffix); return m.data[0].values[0]; }
        bool Used(bool solid, string suffix) { var m = FixtureLogger.entries.Single(m => m.name == "tile44." + (solid ? "solid." : "nonsolid.") + suffix); return m.data[0].used[0]; }
        int Phase(bool solid) => (int)helper.GetField(solid ? "solidPhase" : "nonSolidPhase", Flags)!.GetValue(null)!;
        void Phases(int solid = 0, int nonsolid = 0) { helper.GetField("solidPhase", Flags)!.SetValue(null, solid); helper.GetField("nonSolidPhase", Flags)!.SetValue(null, nonsolid); }
        string State() => $"{TileFixture.Effects}:{TileFixture.Created}:{TileFixture.Draws}:{TileFixture.Liquid}:{TileFixture.Special}:{TileWorld.mapTime}:{TileWorld.critterCage}:{drawing._isActiveAndNotPaused}:{drawing._highQualityLightingRequirement.R}:{drawing._mediumQualityLightingRequirement.B}:{drawing._lastPaintLookupKey.TileType}";
        void Scenario(string name, int width, int height, bool solid, Func<int, int, TileCell?> factory, int failure = 0, int throwDraw = 0, Action? bounds = null)
        {
            TileFixture.ThrowEffect = failure; TileFixture.ThrowDraw = throwDraw;
            TileFixture.Reset(width, height, factory); bounds?.Invoke(); Exception? e1 = null, e2 = null;
            try { before(drawing, solid, false, 7); } catch (Exception e) { e1 = e; }
            string state = State(); int draws = TileFixture.Draws;
            TileFixture.Reset(width, height, factory); bounds?.Invoke();
            int eligible = 0, visited = 0;
            for (int y = TileFixture.Y0; y < TileFixture.Y1 + 4; y++) for (int x = TileFixture.X0 - 2; x < TileFixture.X1 + 2; x++) { visited++; var t = TileWorld.tile[x, y]; if (t?.Active == true && ((t.type & 1) == 0) == solid) eligible++; }
            int phase = Phase(solid);
            try { after(drawing, solid, false, 7); } catch (Exception e) { e2 = e; }
            Check(e1 == e2 && state == State(), name + ":complete_IL_effect_order_and_exception_identity");
            Check((e2 != null) == (failure != 0 || throwDraw != 0), name + ":requested_exception_reached");
            Check(Metric(solid, "passes.started") == 1 && Metric(solid, "passes.completed") == (e2 == null ? 1 : 0), name + ":attempt_completion");
            Check(Metric(solid, "DrawSingleTile.calls_attempted") == draws, name + ":once_only_original_callsite");
            if (e2 == null) Check(Metric(solid, "tiles.visited_attempted") == visited && Metric(solid, "tiles.layer_eligible") == eligible, name + ":bounds_null_and_layer_counters");
            int completedCalls = draws - (throwDraw != 0 && draws == throwDraw ? 1 : 0);
            int samples = Enumerable.Range(0, completedCalls).Count(i => ((i + phase) & 31) == 0);
            Check(Metric(solid, "DrawSingleTile.samples_completed_1in32") == samples && Metric(solid, "DrawSingleTile.sampled_time_only_1in32") == samples * 100, name + ":rotating_samples_only_completed_targets");
            Check(Used(solid, "DrawSingleTile.sampled_time_only_1in32") == (samples != 0), name + ":no_fake_zero_sample");
            if (e2 == null) Check(TileFixture.ClockCalls == 6 + 2 * samples, name + ":exact_clock_bound");
            else Check(TileFixture.ClockCalls <= 6 + 2 * ((draws + 31) / 32), name + ":partial_clock_bound");
            Check(Used(solid, "setup.completed_region") == (failure != 1 && failure != 10 && failure != 20), name + ":setup_completed_only");
            Check(Used(solid, "loop.completed_region") == (e2 == null || failure >= 200), name + ":loop_completed_only");
            Check(Used(solid, "post.completed_region") == (e2 == null), name + ":post_completed_only");
            if (e2 == null) Check(Metric(solid, "setup.completed_region") == 100 && Metric(solid, "loop.completed_region") == 100 + 200 * samples && Metric(solid, "post.completed_region") == 100, name + ":exact_completed_region_clock_deltas");
            TileFixture.ThrowEffect = TileFixture.ThrowDraw = 0;
        }
        TileCell Cell(int type = 0) => new() { Active = true, type = (ushort)type };
        // First actual Draw triggers helper initialization, never an eager external initializer.
        TileFixture.Reset(0, 0, (_, _) => null); after(drawing, true, false, 0);
        Check(FixtureLogger.entries.Count == 20, "registration_exactly_twenty_once");
        var kinds = (int[])runtime43.GetField("kinds", Flags)!.GetValue(null)!;
        Check(FixtureLogger.entries.All(m => kinds[m._nxProfileId - 1] == (m.name.Contains("completed_region") || m.name.Contains("sampled_time_only") ? 0 : 1)), "actual43_factory_epilogues_time_vs_count");
        Phases();
        foreach (bool solid in new[] { true, false }) {
            Scenario("empty_" + solid, 0, 0, solid, (_, _) => null);
            Scenario("sparse_null_inactive_" + solid, 13, 9, solid, (x, y) => (x + y) % 4 == 0 ? null : new TileCell { Active = (x + y) % 4 != 1, type = (ushort)(x % 2) });
            Scenario("dense_" + solid, 128, 96, solid, (x, y) => Cell(solid ? 0 : 1));
            Scenario("variable_bounds_" + solid, 17, 14, solid, (x, y) => Cell(x % 2), bounds: () => { TileFixture.X0 = 5; TileFixture.X1 = 13; TileFixture.Y0 = 3; TileFixture.Y1 = 7; });
            foreach (short frame in new short[] { 0, 18, 36, 54, 72, 270, 486 }) {
                TileFixture.Sway = (frame & 2) != 0;
                Scenario("all_types_frames_" + solid + "_" + frame, 1000, 2, solid, (x, y) => new TileCell { Active = true, type = (ushort)x, frameX = frame, frameY = (short)(y == 0 ? 0 : frame) });
            }
            TileFixture.Sway = false;
            TileDebug.ShowUnbreakableWall = true;
            Scenario("debug_wall_" + solid, 20, 3, solid, (x, y) => new TileCell { Active = true, type = (ushort)(solid ? 0 : 1), wall = (ushort)(x % 2 == 0 ? 350 : 0) });
            TileDebug.ShowUnbreakableWall = false;
            foreach (int failure in new[] { 1, 10, 20, 70, 200, 201, 202, 203, 204 }) {
                if (!solid && failure is 70 or 202 or 203) continue;
                Scenario("exception_" + solid + "_" + failure, 65, 1, solid, (_, _) => Cell(solid ? 0 : 1), failure);
            }
            foreach (int n in new[] { 1, 2, 31, 32, 33, 65 }) Scenario("target_throw_" + solid + "_" + n, 65, 1, solid, (_, _) => Cell(solid ? 0 : 1), throwDraw: n);
            Phases(); Scenario("selected_target_throw_" + solid, 65, 1, solid, (_, _) => Cell(solid ? 0 : 1), throwDraw: 1);
            Phases(); Scenario("selected_target_throw_after_completed_sample_" + solid, 65, 1, solid, (_, _) => Cell(solid ? 0 : 1), throwDraw: 33);
            Scenario("normal_after_exceptions_" + solid, 65, 1, solid, (_, _) => Cell(solid ? 0 : 1));
        }
        Scenario("special_branch_exception", 4, 1, true, (_, _) => Cell(34), 110);
        TileObject.objectPreview.Active = true; TileWorld.player[0].cursorItemIconEnabled = TileWorld.placementPreview = true;
        Scenario("preview_normal", 4, 1, true, (_, _) => Cell()); Scenario("preview_throw", 4, 1, true, (_, _) => Cell(), 208);
        TileObject.objectPreview.Active = false;
        Phases(); int solidSamples = 0, nonSolidSamples = 0;
        for (int p = 0; p < 32; p++) {
            Scenario("low_count_rotation_solid_" + p, 1, 1, true, (_, _) => Cell()); solidSamples += (int)Metric(true, "DrawSingleTile.samples_completed_1in32");
            Scenario("low_count_rotation_nonsolid_" + p, 1, 1, false, (_, _) => Cell(1)); nonSolidSamples += (int)Metric(false, "DrawSingleTile.samples_completed_1in32");
        }
        Check(solidSamples == 1 && nonSolidSamples == 1 && Phase(true) == 0 && Phase(false) == 0, $"independent32_pass_phase_rotation_low_count:samples={solidSamples},{nonSolidSamples};phases={Phase(true)},{Phase(false)}");
        Phases(); TileFixture.Reset(65, 1, (_, _) => Cell()); after(drawing, true, false, 1); after(drawing, true, true, 2);
        Check(Metric(true, "passes.started") == 2 && Metric(true, "passes.completed") == 2 && Metric(true, "DrawSingleTile.calls_attempted") == 130 && Metric(true, "DrawSingleTile.samples_completed_1in32") == 5, "multiple_pass_same_frame_accumulates");
        Phases(); TileFixture.Reset(65, 1, (_, _) => Cell());
        TileFixture.Reenter = () => before(drawing, true, true, 8); before(drawing, true, false, 9); string nestedState = State();
        TileFixture.Reset(65, 1, (_, _) => Cell());
        TileFixture.Reenter = () => after(drawing, true, true, 8); after(drawing, true, false, 9);
        Check(State() == nestedState && Metric(true, "passes.completed") == 2 && Metric(true, "tiles.visited_attempted") == 130 && Metric(true, "DrawSingleTile.calls_attempted") == 130 && Metric(true, "DrawSingleTile.samples_completed_1in32") == 5 && TileFixture.ClockCalls == 22, "nested_pass_local_state_and_draw_order");
        // Measure only steady-state actual Draw delegates; allocations in fixtures occur equally.
        TileFixture.Reset(128, 96, (_, _) => Cell());
        for (int i = 0; i < 100; i++) { before(drawing, true, false, 0); after(drawing, true, false, 0); }
        long Measure(Action<TileDrawingFixture, bool, bool, int> run) { long start = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 100; i++) run(drawing, true, false, 0); return GC.GetAllocatedBytesForCurrentThread() - start; }
        long baselineAllocations = Measure(before), patchedAllocations = Measure(after);
        Check(baselineAllocations == patchedAllocations, $"actual_12288_tile_loop_zero_extra_steady_state_allocations:{baselineAllocations},{patchedAllocations}");
        Phases(); TileFixture.Reset(128, 96, (_, _) => Cell()); after(drawing, true, false, 0);
        Check(TileFixture.ClockCalls == 774 && Metric(true, "DrawSingleTile.samples_completed_1in32") == 384, "12288_calls_384_samples_774_clock_calls");
        // Feed these actual44 metric values through unchanged43 capture and report IL.
        runtime43.GetField("attempts", Flags)!.SetValue(null, 1); runtime43.GetField("complete", Flags)!.SetValue(null, true);
        runtime43.GetField("game", Flags)!.SetValue(null, new FixtureMain());
        var sink = new StringWriter(); var console = Console.Out;
        try { Console.SetOut(sink); runtime43.GetMethod("Boundary", Flags)!.Invoke(null, null); runtime43.GetMethod("Flush", Flags)!.Invoke(null, null); } finally { Console.SetOut(console); }
        string report = sink.ToString(); File.WriteAllText(Path.Combine(output, "tile-report-example.log"), report);
        Check(report.Contains("NX_PROFILE END") && report.Contains("label=\"tile44.solid.DrawSingleTile.calls_attempted\" unit=count") && report.Contains("label=\"tile44.solid.DrawSingleTile.sampled_time_only_1in32\" unit=ms"), "unchanged43_export_consumes_new_metric_units");
        var result = new { passed = true, probeSha256 = Sha(bytes), completeOriginalDrawIlExecuted = true, fixtureLimitations = "Graphics/asset/world and TimeLogData.Add external calls are deterministic fixture effects; registration epilogues, helpers, full Draw and43 capture/export IL are serialized production code. Allocation result is this host fixture, not a full game/GPU or enabled TimeLogger text logging claim. No Switch or performance claim. Liquid operation unchanged, not sampled.", checkCount = checks.Count, checks, allocations = new { iterations = 100, tilesPerPass = 12288, baselineBytes = baselineAllocations, instrumentedBytes = patchedAllocations, extraBytes = patchedAllocations - baselineAllocations }, clockBound = "normal pass:6+2*completed_samples; 12288calls=384samples=774clocks", metricNames = FixtureLogger.entries.Select(m => m.name).ToArray() };
        Json(Path.Combine(output, "tile-proof.json"), result); return result;
    }
    static byte[] Build(AssemblyDefinition baseline, AssemblyDefinition patched, byte[] frameProbe)
    {
        using var probe = AssemblyDefinition.ReadAssembly(new MemoryStream(frameProbe)); var module = probe.MainModule;
        probe.Name.Name = "TileProfileExactProbe"; module.Name = "TileProfileExactProbe";
        var hooks = module.Types.Single(t => t.Name == "Hooks");
        var sourceHelper = Types(patched.MainModule).Single(t => t.Name == TilePatcher.HelperName); var sourcePass = sourceHelper.NestedTypes.Single();
        var helper = new TypeDefinition("Probe", "TileRuntime", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(helper);
        var pass = new TypeDefinition("Probe", "TilePass", TA.Public | TA.SequentialLayout | TA.Sealed, module.ImportReference(typeof(ValueType))); module.Types.Add(pass);
        var hosts = new Dictionary<string, Type> {
            ["Terraria.GameContent.Drawing.TileDrawing"] = typeof(TileDrawingFixture), ["Terraria.GameContent.Drawing.TileDrawingBase"] = typeof(TileDrawingFixture),
            ["Terraria.Main"] = typeof(TileWorld), ["Terraria.Tile"] = typeof(TileCell), ["Terraria.Player"] = typeof(TilePlayer), ["Terraria.HitTile"] = typeof(object),
            ["Terraria.TimeLogger/TimeLogData"] = typeof(FixtureMetric), ["Terraria.TimeLogger"] = typeof(TileWorld),
            ["Microsoft.Xna.Framework.Vector2"] = typeof(TileVec), ["Microsoft.Xna.Framework.Color"] = typeof(TileColor),
            ["Terraria.GameContent.TilePaintSystemV2/TileVariationkey"] = typeof(TileKey), ["Terraria.GameContent.Drawing.DrawBlackHelper"] = typeof(TileBlack),
            ["Terraria.Graphics.Camera"] = typeof(TileCamera), ["Terraria.SceneMetrics"] = typeof(TileScene), ["Terraria.Graphics.TileBatch"] = typeof(TileBatch),
            ["Terraria.Testing.DebugOptions"] = typeof(TileDebug), ["Terraria.FocusHelper"] = typeof(TileFocus), ["Terraria.GameContent.TextureAssets"] = typeof(TileTextures),
            ["ReLogic.Content.Asset`1<Microsoft.Xna.Framework.Graphics.Texture2D>"] = typeof(TileAsset), ["Microsoft.Xna.Framework.Graphics.Texture2D"] = typeof(object),
            ["Microsoft.Xna.Framework.Graphics.SpriteBatch"] = typeof(object), ["Terraria.DataStructures.TileObjectPreviewData"] = typeof(TilePreview), ["Terraria.TileObject"] = typeof(TileObject),
            ["Terraria.Graphics.Capture.CaptureManager"] = typeof(TileCapture), ["Terraria.GameContent.Drawing.TileDrawing/TileCounterType"] = typeof(int)
        };
        var mapped = new Dictionary<string, TypeReference> { [sourceHelper.FullName] = helper, [sourcePass.FullName] = pass };
        TypeReference TypeMap(TypeReference t) {
            if (mapped.TryGetValue(t.FullName, out var n)) return n;
            if (hosts.TryGetValue(t.FullName, out var h)) return module.ImportReference(h);
            if (t is ArrayType a) {
                var array = new ArrayType(TypeMap(a.ElementType), a.Rank);
                for (int i = 0; i < a.Rank; i++) array.Dimensions[i] = new ArrayDimension(a.Dimensions[i].LowerBound, a.Dimensions[i].UpperBound);
                return array;
            }
            if (t is ByReferenceType r) return new ByReferenceType(TypeMap(r.ElementType));
            if (t is GenericInstanceType g) { var copy = new GenericInstanceType(TypeMap(g.ElementType)); foreach (var x in g.GenericArguments) copy.GenericArguments.Add(TypeMap(x)); return copy; }
            Require(t.Namespace.StartsWith("System"), "unmapped fixture type " + t.FullName);
            if (t.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr) return Primitive(module, t.FullName);
            return module.ImportReference(t);
        }
        foreach (var (source, target) in new[] { (sourceHelper, helper), (sourcePass, pass) }) {
            foreach (var f in source.Fields) { var n = new FieldDefinition(f.Name, f.Attributes, TypeMap(f.FieldType)); if (f.HasConstant) n.Constant = f.Constant; target.Fields.Add(n); }
            foreach (var m in source.Methods) Invoke("Patcher", "Define", m, target, (Func<TypeReference, TypeReference>)TypeMap);
        }
        MethodReference HostMethod(Type t, string name) => module.ImportReference(t.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!);
        object Member(object o) {
            if (o is TypeReference t) return TypeMap(t);
            if (o is FieldReference f) {
                if (mapped.TryGetValue(f.DeclaringType.FullName, out var d)) return ((TypeDefinition)d).Fields.Single(x => x.Name == f.Name);
                if (hosts.TryGetValue(f.DeclaringType.FullName, out var h)) {
                    var field = module.ImportReference(h.GetField(f.Name)!);
                    field.FieldType = TypeMap(f.FieldType);
                    return field;
                }
                return new FieldReference(f.Name, TypeMap(f.FieldType), TypeMap(f.DeclaringType));
            }
            if (o is MethodReference m) {
                if (mapped.TryGetValue(m.DeclaringType.FullName, out var d)) return ((TypeDefinition)d).Methods.Single(x => x.Name == m.Name);
                if (m.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && m.Name == "GetTimestamp") return HostMethod(typeof(TileFixture), "Clock");
                if (m.DeclaringType.FullName == "Terraria.TimeLogger" && m.Name.StartsWith("New")) return HostMethod(typeof(TileFixture), m.Name);
                if (m.DeclaringType.FullName == "Terraria.TimeLogger/TimeLogData" && m.Name == "Add") return HostMethod(typeof(TileFixture), "Add");
                if (hosts.TryGetValue(m.DeclaringType.FullName, out var h)) {
                    if (m.Name == ".ctor") return module.ImportReference(h.GetConstructors().Single(c => c.GetParameters().Length == m.Parameters.Count));
                    return HostMethod(h, m.Name);
                }
                var n = new MethodReference(m.Name, TypeMap(m.ReturnType), TypeMap(m.DeclaringType)) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention };
                foreach (var p in m.Parameters) n.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType))); return n;
            }
            return o;
        }
        foreach (var m in sourceHelper.Methods) Copy(m, helper.Methods.Single(x => x.Name == m.Name), TypeMap, Member);
        foreach (var (game, name) in new[] { (baseline, "TileBaseline"), (patched, "TilePatched") }) {
            var source = Program.Draw(game.MainModule); var target = new MethodDefinition(name, MA.Public | MA.Static, module.TypeSystem.Void); hooks.Methods.Add(target);
            target.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(TileDrawingFixture)))); foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
            Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)TypeMap, (Func<object, object>)Member, 1);
        }
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Types(module).SelectMany(t => t.Methods).Select(Body)))).AsSpan(0, 16));
        using var stream = new MemoryStream(); probe.Write(stream, new WriterParameters { Timestamp = 0 }); return stream.ToArray();
    }
}
