using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Program;
using MA = Mono.Cecil.MethodAttributes;
using FA = Mono.Cecil.FieldAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class Probe
{
    internal static object Verify(AssemblyDefinition baseline, AssemblyDefinition patched, AssemblyDefinition framework, string output)
    {
        byte[] bytes = Build(baseline, patched, framework);
        File.WriteAllBytes(Path.Combine(output, "FrameProfileProbe.dll"), bytes);
        var loaded = Assembly.Load(bytes);
        var hooks = loaded.GetType("Probe.Hooks", true)!;
        var runtime = loaded.GetType("Probe.Runtime", true)!;
        var proof = Exercise(hooks, runtime, output);
        Json(Path.Combine(output, "proof.json"), proof);
        return new { probeSha256 = Sha(bytes), result = proof };
    }
    static byte[] Build(AssemblyDefinition baseline, AssemblyDefinition patched, AssemblyDefinition framework)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("FrameProfileExactProbe", new Version(1, 0)), "FrameProfileExactProbe", ModuleKind.Dll);
        var module = assembly.MainModule;
        var hooks = new TypeDefinition("Probe", "Hooks", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        var runtime = new TypeDefinition("Probe", "Runtime", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        module.Types.Add(hooks); module.Types.Add(runtime);
        var sourceRuntime = Types(patched.MainModule).Single(t => t.Name == "NXFrameProfile43");
        var typeMap = new Dictionary<string, TypeReference> {
            [sourceRuntime.FullName] = runtime,
            ["Terraria.Main"] = module.ImportReference(typeof(FixtureMain)), ["Terraria.TimeLogger"] = module.ImportReference(typeof(FixtureLogger)),
            ["Terraria.TimeLogger/TimeLogData"] = module.ImportReference(typeof(FixtureMetric)), ["Terraria.TimeLogger/DataSeries"] = module.ImportReference(typeof(FixtureSeries)),
            ["Microsoft.Xna.Framework.GameTime"] = module.ImportReference(typeof(FixtureTime)),
            ["ReLogic.Content.IAssetRepository"] = module.ImportReference(typeof(FixtureAssets)),
            ["Terraria.Cinematics.CinematicManager"] = module.ImportReference(typeof(FixtureCinematic)),
            ["Terraria.Enums.FrameSkipMode"] = module.TypeSystem.Int32,
            ["Terraria.Testing.DetailedFPS/OperationCategory"] = module.TypeSystem.Int32
        };
        TypeReference MapType(TypeReference t)
        {
            if (typeMap.TryGetValue(t.FullName, out var mapped)) return mapped;
            if (t is GenericParameter) return t;
            if (t is ArrayType a) return new ArrayType(MapType(a.ElementType), a.Rank);
            if (t is ByReferenceType b) return new ByReferenceType(MapType(b.ElementType));
            if (t is GenericInstanceType g) { var n = new GenericInstanceType(MapType(g.ElementType)); foreach (var x in g.GenericArguments) n.GenericArguments.Add(MapType(x)); return n; }
            if (RawSignatures.PrimitiveNames.Contains(t.FullName))
            {
                var primitive = Patcher.Mapper.Primitive(module, t.FullName);
                Require(t.MetadataType == primitive.MetadataType && t.IsValueType == primitive.IsValueType, "probe refuses to normalize malformed primitive signature: " + t.FullName);
                return primitive;
            }
            Require(!t.FullName.StartsWith("Terraria") && !t.FullName.StartsWith("Microsoft.Xna") && !t.FullName.StartsWith("ReLogic"), "unmapped probe type " + t.FullName);
            return module.ImportReference(t);
        }
        foreach (var f in sourceRuntime.Fields) runtime.Fields.Add(new FieldDefinition(f.Name, f.Attributes, MapType(f.FieldType)) { Constant = f.Constant });
        foreach (var m in sourceRuntime.Methods) Patcher.Define(m, runtime, MapType);
        MethodReference FixtureMethod(Type t, string name) => module.ImportReference(t.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!);
        var read = new MethodDefinition("Read", MA.Public | MA.Static, module.TypeSystem.Void);
        read.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(FixtureSeries)))); read.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32)); hooks.Methods.Add(read);
        object Map(object o)
        {
            if (o is TypeReference t) return MapType(t);
            if (o is FieldReference f)
            {
                if (f.DeclaringType.FullName == sourceRuntime.FullName) return runtime.Fields.Single(x => x.Name == f.Name);
                if (typeMap.TryGetValue(f.DeclaringType.FullName, out var mapped)) {
                    var host = mapped.FullName switch { nameof(FixtureMain) => typeof(FixtureMain), nameof(FixtureLogger) => typeof(FixtureLogger), nameof(FixtureMetric) => typeof(FixtureMetric), nameof(FixtureSeries) => typeof(FixtureSeries), nameof(FixtureCinematic) => typeof(FixtureCinematic), _ => throw new InvalidDataException("field owner " + mapped.FullName) };
                    return module.ImportReference(host.GetField(f.Name)!);
                }
                return new FieldReference(f.Name, MapType(f.FieldType), MapType(f.DeclaringType));
            }
            if (o is MethodReference m)
            {
                if (m.DeclaringType.FullName == sourceRuntime.FullName) return runtime.Methods.Single(x => x.Name == m.Name);
                if (m.Name == "NXProfileRead") return read;
                if (m.DeclaringType.FullName == "Microsoft.Xna.Framework.Game") return FixtureMethod(typeof(FixtureMain), m.Name == "Update" ? "BaseUpdate" : m.Name);
                if (m.DeclaringType.FullName == "Terraria.Testing.DetailedFPS") return FixtureMethod(typeof(FixtureMain), "Detailed" + m.Name);
                if (m.DeclaringType.FullName == "Terraria.Main") return FixtureMethod(typeof(FixtureMain), m.Name == "get_IsGraphicsDeviceAvailable" ? "IsGraphicsDeviceAvailable" : m.Name);
                if (m.DeclaringType.FullName == "Terraria.TimeLogger") return FixtureMethod(typeof(FixtureLogger), m.Name);
                if (m.DeclaringType.FullName == "Terraria.TimeLogger/TimeLogData") return FixtureMethod(typeof(FixtureMetric), m.Name);
                if (m.DeclaringType.FullName == "ReLogic.Content.IAssetRepository") return FixtureMethod(typeof(FixtureAssets), m.Name);
                if (m.DeclaringType.FullName == "Terraria.Cinematics.CinematicManager") return FixtureMethod(typeof(FixtureCinematic), m.Name);
                var n = new MethodReference(m.Name, MapType(m.ReturnType), MapType(m.DeclaringType)) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention };
                foreach (var p in m.Parameters) n.Parameters.Add(new ParameterDefinition(MapType(p.ParameterType)));
                return n;
            }
            return o;
        }
        foreach (var m in sourceRuntime.Methods) Patcher.CopyBody(m, runtime.Methods.Single(x => x.Name == m.Name), MapType, Map);
        var originalRead = Types(patched.MainModule).Single(t => t.FullName == "Terraria.TimeLogger/DataSeries").Methods.Single(m => m.Name == "NXProfileRead");
        Patcher.CopyBody(originalRead, read, MapType, Map, 1);
        foreach (var (game, prefix) in new[] { (baseline, "Baseline"), (patched, "Patched") })
        {
            var main = Types(game.MainModule).Single(t => t.FullName == "Terraria.Main");
            foreach (string name in new[] { "Draw", "Update" })
            {
                var source = main.Methods.Single(m => m.Name == name && m.Parameters.Count == 1);
                var target = new MethodDefinition(prefix + name, MA.Public | MA.Static, module.TypeSystem.Void);
                target.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(FixtureMain))));
                target.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(FixtureTime)))); hooks.Methods.Add(target);
                Patcher.CopyBody(source, target, MapType, Map, 1);
            }
        }
        // Execute the actual StartNextFrame prefix through callback draining, ABTest,
        // enumeration and finally disposal; omit unrelated optional file logging tail.
        var advance = Types(patched.MainModule).Single(t => t.FullName == "Terraria.TimeLogger").Methods.Single(m => m.Name == "StartNextFrame");
        var advanceCopy = new MethodDefinition("Advance", MA.Public | MA.Static, module.TypeSystem.Void); hooks.Methods.Add(advanceCopy);
        var cut = advance.Body.Instructions.First(i => i.Operand is FieldReference f && f.Name == "startLoggingNextFrame");
        var savedBody = advance.Body;
        var segment = new Mono.Cecil.Cil.MethodBody(advance) { InitLocals = savedBody.InitLocals };
        foreach (var v in savedBody.Variables.Take(1)) segment.Variables.Add(v);
        foreach (var i in savedBody.Instructions.TakeWhile(i => i != cut)) segment.Instructions.Add(i);
        // The original leave targets cut. Preserve that target as a ret sentinel.
        var oldCode = cut.OpCode; var oldOperand = cut.Operand; cut.OpCode = OpCodes.Ret; cut.Operand = null; segment.Instructions.Add(cut);
        segment.ExceptionHandlers.Add(savedBody.ExceptionHandlers[0]); advance.Body = segment;
        Patcher.CopyBody(advance, advanceCopy, MapType, Map);
        advance.Body = savedBody; cut.OpCode = oldCode; cut.Operand = oldOperand;
        // Factory epilogues are copied from serialized production output, not modeled.
        foreach (string name in new[] { "NewEntry", "NewCounterEntry" })
        {
            var factory = Types(patched.MainModule).Single(t => t.FullName == "Terraria.TimeLogger").Methods.Single(m => m.Name == name);
            var target = new MethodDefinition(name, MA.Public | MA.Static, module.ImportReference(typeof(FixtureMetric)));
            target.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(FixtureMetric)))); hooks.Methods.Add(target);
            target.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            var register = factory.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "Register");
            int start = factory.Body.Instructions.IndexOf(register) - 4;
            foreach (var i in factory.Body.Instructions.Skip(start)) { var c = Instruction.Create(OpCodes.Nop); c.OpCode = i.OpCode; c.Operand = i.Operand == null ? null : Map(i.Operand); target.Body.Instructions.Add(c); }
        }
        var runSource = Types(patched.MainModule).Single(t => t.FullName == "Terraria.Program").Methods.Single(m => m.Name == "RunGame");
        var run = new MethodDefinition("Run", MA.Public | MA.Static, module.TypeSystem.Void); run.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(FixtureMain)))); hooks.Methods.Add(run);
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        var gameRun = runSource.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Microsoft.Xna.Framework.Game" && m.Name == "Run");
        foreach (var i in new[] { gameRun, gameRun.Next }) { var c = Instruction.Create(OpCodes.Nop); c.OpCode = i.OpCode; c.Operand = Map(i.Operand); run.Body.Instructions.Add(c); }
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var lerpSource = Types(framework.MainModule).Single(t => t.FullName == "Microsoft.Xna.Framework.MathHelper").Methods.Single(m => m.Name == "Lerp");
        var lerp = Patcher.Define(lerpSource, hooks, MapType);
        Patcher.CopyBody(lerpSource, lerp, MapType, Map);
        foreach (var (image, name) in new[] { (baseline, "BaselineSeries"), (patched, "PatchedSeries") })
        {
            var original = Types(image.MainModule).Single(t => t.FullName == "Terraria.TimeLogger/DataSeries");
            var copy = new TypeDefinition("Probe", name, TA.Public | TA.BeforeFieldInit, module.TypeSystem.Object); module.Types.Add(copy);
            foreach (var f in original.Fields) copy.Fields.Add(new FieldDefinition(f.Name, f.Attributes, MapType(f.FieldType)));
            foreach (var m in original.Methods) Patcher.Define(m, copy, MapType);
            object MapSeries(object o)
            {
                if (o is FieldReference f && f.DeclaringType.FullName == original.FullName) return copy.Fields.Single(x => x.Name == f.Name);
                if (o is MethodReference m && m.DeclaringType.FullName == original.FullName) return copy.Methods.Single(x => x.Name == m.Name);
                if (o is MethodReference l && l.FullName == lerpSource.FullName) return lerp;
                return Map(o);
            }
            foreach (var m in original.Methods) Patcher.CopyBody(m, copy.Methods.Single(x => x.Name == m.Name), MapType, MapSeries);
        }
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Types(module).SelectMany(t => t.Methods).Select(Body)))).AsSpan(0,16));
        using var buffer = new MemoryStream(); assembly.Write(buffer, new WriterParameters { Timestamp = 0 }); return buffer.ToArray();
    }

    static object Exercise(Type hooks, Type runtime, string output)
    {
        var draw = hooks.GetMethod("PatchedDraw")!.CreateDelegate<Action<FixtureMain, FixtureTime>>();
        var originalDraw = hooks.GetMethod("BaselineDraw")!.CreateDelegate<Action<FixtureMain, FixtureTime>>();
        var update = hooks.GetMethod("PatchedUpdate")!.CreateDelegate<Action<FixtureMain, FixtureTime>>();
        var originalUpdate = hooks.GetMethod("BaselineUpdate")!.CreateDelegate<Action<FixtureMain, FixtureTime>>();
        var advance = hooks.GetMethod("Advance")!.CreateDelegate<Action>();
        var run = hooks.GetMethod("Run")!.CreateDelegate<Action<FixtureMain>>();
        var timer = hooks.GetMethod("NewEntry")!.CreateDelegate<Func<FixtureMetric, FixtureMetric>>();
        var counter = hooks.GetMethod("NewCounterEntry")!.CreateDelegate<Func<FixtureMetric, FixtureMetric>>();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        object Field(string name) => runtime.GetField(name, flags)!.GetValue(null)!;
        void Set(string name, object value) => runtime.GetField(name, flags)!.SetValue(null, value);
        long[] A(string name) => (long[])Field(name);
        long L(string name) => (long)Field(name);
        var main = new FixtureMain(); var time = new FixtureTime();
        var sink = new StringWriter(System.Globalization.CultureInfo.InvariantCulture); var console = Console.Out; Console.SetOut(sink);
        var checks = new List<string>();
        void Check(bool ok, string name) { Require(ok, "probe: " + name); checks.Add(name); }
        try
        {
            var t = new FixtureMetric { name = "timer\"\\\n☃" }; var c = new FixtureMetric { name = "counter" };
            Check(ReferenceEquals(timer(t), t) && ReferenceEquals(counter(c), c), "actual_factory_epilogues_preserve_return_identity");
            FixtureLogger.entries.Add(t); FixtureLogger.entries.Add(c);
            Check(t._nxProfileId == 1 && c._nxProfileId == 2 && ((int[])Field("kinds"))[0] == 0 && ((int[])Field("kinds"))[1] == 1, "factory_units");
            void Value(FixtureMetric m, int value, bool present = true, int series = 0) { var s = m.data[series]; s.values[s.next] = value; s.used[s.next] = present; }
            void ResetEffects() { main._isDrawingOrUpdating = false; FixtureMain.Effects = 0; FixtureMain.IsEnginePreloaded = false; }
            foreach (int failure in new[] { 0, 1, 2, 3, 4 })
            {
                FixtureMain.ThrowStage = failure; FixtureMain.OnPostDraw = _ => FixtureMain.Effect(3);
                ResetEffects(); Exception? e1 = null, e2 = null;
                try { originalDraw(main, time); } catch (Exception e) { e1 = e; }
                long effects = FixtureMain.Effects; bool guard = main._isDrawingOrUpdating;
                ResetEffects(); try { draw(main, time); } catch (Exception e) { e2 = e; }
                Check(e1 == e2 && effects == FixtureMain.Effects && guard == main._isDrawingOrUpdating, "draw_effect_order_guard_exception_" + failure);
                advance();
            }
            FixtureMain.ThrowStage = 0; FixtureMain.OnPostDraw = null;
            ResetEffects(); originalUpdate(main, time); long originalEffects = FixtureMain.Effects;
            ResetEffects(); update(main, time); Check(originalEffects == FixtureMain.Effects, "actual_update_effects");
            foreach (int failure in new[] { 6, 7, 8, 10, 11 })
            {
                FixtureMain.ThrowStage = failure; FixtureMain.GameAskedToQuit = true;
                ResetEffects(); Exception? e1 = null, e2 = null;
                try { originalUpdate(main, time); } catch (Exception e) { e1 = e; }
                long effects = FixtureMain.Effects; bool guard = main._isDrawingOrUpdating;
                ResetEffects(); long calls = L("intervalUpdates");
                try { update(main, time); } catch (Exception e) { e2 = e; }
                Check(e1 == e2 && effects == FixtureMain.Effects && guard == main._isDrawingOrUpdating && L("intervalUpdates") == calls + 1, "update_call_and_exception_" + failure);
            }
            FixtureMain.ThrowStage = 0; FixtureMain.GameAskedToQuit = false;
            ResetEffects(); main._isDrawingOrUpdating = true;
            long guardedCalls = L("intervalUpdates"); update(main, time);
            Check(L("intervalUpdates") == guardedCalls + 1, "guarded_update_call_counted");
            // Finish the effect-comparison interval and start clean using the real report path.
            Set("lastReport", Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency); advance(); sink.GetStringBuilder().Clear();
            for (int bits = 0; bits < 16; bits++)
            {
                FixtureMain.gameMenu = (bits & 1) != 0; FixtureMain.gamePaused = (bits & 2) != 0; FixtureMain.playerInventory = (bits & 4) != 0; FixtureMain.mapFullscreen = (bits & 8) != 0;
                FixtureMain.autoPause = true; FixtureMain.FrameSkipMode = 2; FixtureMain.renderCount = bits; FixtureMain.renderNow = bits == 3;
                main.IsActive = bits % 2 == 0; ResetEffects(); update(main, time); update(main, time);
                Value(t, bits); Value(c, bits + 10); draw(main, time); advance();
                Check(A("frames")[bits] == 1 && A("updates")[bits] == 2 && A("raw")[bits * 518] == bits && A("used")[bits * 518] == 1, "state_bucket_sparse_zero_" + bits);
                Check(A("active")[bits] == (bits % 2 == 0 ? 1 : 0) && A("pause")[bits] == 1 && A("render")[bits] == (bits == 3 ? 1 : 0) && ((int[])Field("skipMin"))[bits] == 2 && ((int[])Field("skipMax"))[bits] == 2 && ((int[])Field("renderMin"))[bits] == bits && ((int[])Field("renderMax"))[bits] == bits, "auxiliary_state_bucket_" + bits);
            }
            FixtureMain.gameMenu = FixtureMain.gamePaused = FixtureMain.playerInventory = FixtureMain.mapFullscreen = false;
            ResetEffects(); Value(t, -9); Value(c, -3); draw(main, time); advance();
            Check(A("invalid")[0] == 1 && A("raw")[0] == -9 && A("sum")[0] == 0, "negative_ticks_not_positive_times");
            ResetEffects(); Value(t, 777, false); Value(c, 0); draw(main, time); advance();
            Check(A("used")[0] == 2 && A("used")[1] == 3, "unused_not_sticky_and_used_zero_retained");
            ResetEffects(); Value(t, 30); draw(main, time);
            FixtureLogger._onNextFrame.Enqueue(() => { t.data[0].Reset(); c.data[0].Reset(); }); FixtureLogger.nextSeries = 1; advance();
            Check(A("raw")[0] == 21 && FixtureLogger.activeDataSeries == 1, "sample_before_queued_reset_and_ab_switch");
            ResetEffects(); Value(t, 50, true, 1); draw(main, time); advance();
            Check(A("raw")[16 * 518] == 50 && A("frames")[16] == 1, "ab_selected_slot_not_baseline");
            FixtureLogger.nextSeries = 0; advance();
            long noDraw = L("noDraw"); advance(); Check(L("noDraw") == noDraw + 1, "repeated_boundary_no_duplicate_samples");
            long frame0 = A("frames")[0]; ResetEffects(); draw(main, time); draw(main, time); advance(); Check(A("frames")[0] == frame0 && L("repeated") == 1, "repeated_draw_epoch_excluded");
            FixtureMain.GraphicsAvailable = false; ResetEffects(); draw(main, time); advance(); FixtureMain.GraphicsAvailable = true;
            Check(A("frames")[0] == frame0, "guard_return_is_not_draw");
            FixtureMain.ThrowStage = 2; ResetEffects(); try { draw(main, time); } catch (Exception e) { Check(ReferenceEquals(e, FixtureMain.Failure), "game_exception_not_swallowed"); } advance(); FixtureMain.ThrowStage = 0;
            ResetEffects(); FixtureMain.ToggleInventory = true; draw(main, time); advance(); FixtureMain.ToggleInventory = false;
            Check(A("changed")[0] == 1 && A("frames")[0] == frame0 + 1 && A("used")[514] == 0, "begin_attribution_change_and_skipped_post_stage");
            // Registered late metrics are included; capacity overflow is explicit.
            var late = new FixtureMetric { name = "late" }; timer(late); FixtureLogger.entries.Add(late);
            FixtureMain.playerInventory = false; ResetEffects(); Value(late, -99); draw(main, time); advance(); Check(A("raw")[2] == -99, "late_registration");
            const BindingFlags instanceFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var seriesType = hooks.Assembly.GetType("Probe.PatchedSeries", true)!;
            var exact = Activator.CreateInstance(seriesType)!;
            var exactValues = (int[])seriesType.GetField("values", instanceFlags)!.GetValue(exact)!;
            var exactUsed = (bool[])seriesType.GetField("used", instanceFlags)!.GetValue(exact)!;
            exactValues[0] = 1234; exactUsed[0] = true;
            seriesType.GetMethod("Reset")!.Invoke(exact, null);
            long rawBeforeReset = A("raw")[0];
            seriesType.GetMethod("NXProfileRead", instanceFlags)!.Invoke(exact, new object[] { 1 });
            Check(exactValues[0] == 1234 && exactUsed[0] && A("raw")[0] == rawBeforeReset && L("resetDropped") == 1, "actual_reset_retains_arrays_but_read_excludes_dirty_slot");
            seriesType.GetMethod("StartNextFrame")!.Invoke(exact, null);
            Check(!(bool)seriesType.GetField("_nxProfileReset", instanceFlags)!.GetValue(exact)! && !exactUsed[1] && exactValues[1] == 0, "actual_incremental_advance_clears_dirty_observer_after_slot_clear");
            // Forced I/O failure is contained in profiler reporting, not the game.
            long retainedFrames = A("frames")[0]; long retainedInterval = L("interval");
            Console.SetOut(new FailingWriter());
            Set("lastReport", Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency); advance();
            Console.SetOut(sink);
            Check(A("frames")[0] == retainedFrames && L("reportFailures") == 1 && L("interval") == retainedInterval, "report_io_failure_retains_interval");
            Set("lastReport", Stopwatch.GetTimestamp()); long reportCount = L("reports"); advance(); Check(L("reports") == reportCount, "five_second_floor");
            var savedCulture = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            Set("lastReport", Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency); advance();
            System.Globalization.CultureInfo.CurrentCulture = savedCulture;
            Check(L("reports") == reportCount + 1 && sink.ToString().Contains("timer\\\"\\\\\\u000a\\u2603"), "cadence_and_label_escaping");
            string report = sink.ToString(); File.WriteAllText(Path.Combine(output, "report-example.log"), report);
            Check(report.Contains("unit=count") && report.Contains("mean_per_frame=null") && report.Contains("invalid_negative=1") && report.Contains("frequency=" + Stopwatch.Frequency), "units_null_and_frequency_report");
            var parsedReport = ReportProof.Verify(report);
            Check(true, "parseable_blocks_invariant_numeric_and_exact_mean_contracts");
            for (int i = 3; i < 514; i++)
            {
                var item = timer(new FixtureMetric { name = "bounded" });
                if (item._nxProfileId > 0) FixtureLogger.entries.Add(item);
            }
            Check((int)Field("count") == 512 && L("droppedRegistrations") == 2, "bounded_registry_overflow_explicit");
            FixtureMain.Record = false; Set("lastReport", long.MaxValue / 2);
            for (int i = 0; i < 2000; i++) { FixtureLogger.Populate(); update(main, time); draw(main, time); advance(); }
            long allocated = GC.GetAllocatedBytesForCurrentThread(); long started = Stopwatch.GetTimestamp();
            const int loops = 20000;
            for (int i = 0; i < loops; i++) { FixtureLogger.Populate(); update(main, time); update(main, time); draw(main, time); advance(); }
            long elapsed = Stopwatch.GetTimestamp() - started; allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(allocated == 0, "steady_extracted_frame_loop_zero_allocations");
            var measurement = new { loops, registryEntries = FixtureLogger.entries.Count, scenario = "512_entries_two_updates_sparse_used_fixture_writes_included", allocatedBytes = allocated, elapsedTicks = elapsed, frequency = Stopwatch.Frequency, nsPerFrame = elapsed * 1e9 / Stopwatch.Frequency / loops, captureTicks = L("captureTicks"), observerTicks = L("observerTicks"), reportTicksBeforeFooters = L("reportTicks"), reportMaxTicksBeforeFooter = L("reportMax"), hostOnly = true };
            FixtureMain.Record = true; FixtureMain.ThrowStage = 12;
            try { run(main); } catch (Exception e) { Check(ReferenceEquals(e, FixtureMain.Failure) && !(bool)Field("finalized"), "abnormal_run_does_not_flush"); }
            FixtureMain.ThrowStage = 0; run(main); long finalCount = L("reports"); run(main);
            Check((bool)Field("finalized") && L("reports") == finalCount && sink.ToString().Contains("final=1"), "normal_exit_partial_flush_once");
            return new { passed = true, checks, measurement, parsedReport, extracted = new[] { "baseline/patched Main.Draw", "baseline/patched Main.Update", "patched TimeLogger.StartNextFrame prefix through original queue/AB/enumerator/finally", "patched factory return epilogues", "patched Program.RunGame Run+Flush pair", "all serialized profiler methods", "serialized DataSeries.NXProfileRead", "all baseline/patched DataSeries methods and exact FNA Lerp" }, fixtureOnly = "Render/update callees and TimeLogData advancement are controlled host effects. No full Terraria rendering or Switch AOT exercised." };
        }
        finally { Console.SetOut(console); }
    }
}
