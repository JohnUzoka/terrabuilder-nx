using System.Diagnostics;
using System.Reflection;
using static TileHelperProof.Session;

internal static class TileHelperRuntimeProof
{
    internal static object Run(TileHelperProof.Session p, string output)
    {
        void Check(bool value, string name) => p.Check(value, name);
        void NormalPass(ref object pass) { p.Region(ref pass, 1); p.Region(ref pass, 2); p.Finish(pass); }
        object Selected()
        {
            p.Reset(); p.Set("FrameCollecting", true); object pass = p.Enter();
            Field(pass, "Visited", 1L); Field(pass, "Eligible", 1L); Field(pass, "Calls", 1L);
            p.Region(ref pass, 0); p.Call("BeginSelected"); p.Call("ConsumePending"); return pass;
        }
        void Invalid(string name, Action corrupt)
        {
            object pass = Selected(); corrupt(); object before = p.Get("current"); p.Call("EndSample"); object context = p.Get("current");
            Check(Number(context, "Completed") == 1 && Number(context, "Invalid") == 1 && Number(context, "Valid") == 0 && (bool)p.Get("MeasurementInvalid"), name + ":explicit_invalid_sample");
            Check(TileHelperProof.MetricFields.All(field => Field(context, field).Equals(Field(before, field))), name + ":atomic_no_metric_donation"); NormalPass(ref pass);
        }
        Invalid("backwards_sample", () => { Tile49ObserverFixture.Script = new[] { 1L }; Tile49ObserverFixture.ScriptIndex = 0; });
        Invalid("negative_sample", () => { Tile49ObserverFixture.Script = new[] { -1L }; Tile49ObserverFixture.ScriptIndex = 0; });
        Invalid("clock_throw_sample", () => Tile49ObserverFixture.ThrowClock = true); Tile49ObserverFixture.ThrowClock = false;
        Invalid("overlapping_operations", () => { p.Call("BeginOp", 0); p.Call("BeginOp", 1); p.Call("EndOp", 1); });
        Invalid("mismatched_operation", () => { p.Call("BeginOp", 0); p.Call("EndOp", 1); });
        Invalid("unclosed_operation", () => p.Call("BeginOp", 0));
        Invalid("invalid_operation_id", () => { p.Call("BeginOp", 5); p.Call("EndOp", 5); });
        Invalid("pending_not_consumed", () => p.Set("Pending", true));
        Invalid("helper_sum_exceeds_sample", () => { object c = p.Get("current"), m = Field(c, "SampleColor"); Field(m, "Calls", 1L); Field(m, "Ticks", 100000L); Field(m, "Max", 100000L); Field(c, "SampleColor", m); p.Set("current", c); });
        Invalid("helper_sum_overflow", () => { object c = p.Get("current"); foreach (string name in new[] { "SampleColor", "SampleData" }) { object m = Field(c, name); Field(m, "Calls", 1L); Field(m, "Ticks", long.MaxValue); Field(m, "Max", long.MaxValue); Field(c, name, m); } p.Set("current", c); });
        Invalid("malformed_zero_count_metric", () => { object c = p.Get("current"), m = Field(c, "SampleColor"); Field(m, "Ticks", 1L); Field(c, "SampleColor", m); p.Set("current", c); });
        Invalid("metric_count_overflow", () => { object c = p.Get("current"), m = Field(c, "Color"); Field(m, "Calls", long.MaxValue); Field(c, "Color", m); p.Set("current", c); p.Call("BeginOp", 0); p.Call("EndOp", 0); });
        object zeroPass = Selected(); p.Call("EndSample"); object zero = p.Get("current");
        Check(Number(zero, "Valid") == 1 && Enumerable.Range(0, 5).All(i => p.Metric(zero, i) == 0) && Number(Field(zero, "ClockPair"), "Calls") == 1, "zero_helper_execution_is_unused_metric_not_fake_operation"); NormalPass(ref zeroPass);
        object parent = Selected(); object beforeNested = p.Get("current"); p.Set("Pending", true); object child = p.Enter(false);
        Check(!(bool)Field(child, "Enabled") && !(bool)p.Get("Pending"), "nested_pass_masks_pending_and_collection"); p.Finish(child);
        Check(p.Get("current").Equals(beforeNested) && (bool)p.Get("Pending"), "nested_pass_restores_exact_parent_context_pending");
        p.Call("ConsumePending"); p.Call("EndSample"); NormalPass(ref parent);
        p.Reset(); p.Set("FrameCollecting", true); object abort = p.Enter(); Field(abort, "Visited", 1L); Field(abort, "Eligible", 1L); Field(abort, "Calls", 1L); p.Call("BeginSelected"); p.Finish(abort);
        Check(p.Layer(true, "Selected") == 1 && p.Layer(true, "AbortedSamples") == 1 && p.Layer(true, "CompletedSamples") == 0, "pending_selected_call_aborted_before_GetColor_zero_operations");
        p.Reset(); p.Set("FrameCollecting", true); object noLoop = p.Enter(); p.Region(ref noLoop, 2); p.Finish(noLoop);
        Check(p.Layer(true, "CompletedPasses") == 1 && p.Layer(true, "CompletedLoops") == 0 && p.Layer(true, "LoopTicks") == 0, "normal_pass_without_reached_loop_has_no_loop_time");
        object completedAbort = Selected(); p.Call("BeginOp", 0); p.Call("EndOp", 0); p.Call("EndSample"); p.Finish(completedAbort);
        Check(p.Layer(true, "DiscardedSamples") == 1 && p.Layer(true, "ValidSamples") == 0 && p.Metric(p.Get("FrameSolid"), 0) == 0, "aborted_pass_discards_previously_valid_completion");
        foreach (bool badCounts in new[] { true, false })
        {
            object malformed = Selected(); p.Call("BeginOp", 0); p.Call("EndOp", 0); p.Call("EndSample"); object context = p.Get("current");
            if (badCounts) Field(context, "Completed", 2L);
            else { object metric = Field(context, "Color"); Field(metric, "Max", 1L); Field(context, "Color", metric); }
            p.Set("current", context); NormalPass(ref malformed);
            Check(p.Layer(true, "InvalidPasses") == 1 && p.Layer(true, "ValidSamples") == 0 && p.Metric(p.Get("FrameSolid"), 0) == 0 && (bool)p.Get("MeasurementInvalid"), "malformed_aggregate_no_timing_donation_" + badCounts);
            if (badCounts) Check(p.Layer(true, "CompletedSamples") == 2, "malformed_accounting_preserved_as_invalid_not_rewritten");
        }
        p.Reset(); object destination = p.Get("WindowSolid"), source = p.Get("FrameSolid"); Field(destination, "Calls", long.MaxValue); Field(source, "Calls", 1L);
        object originalSource = source; var merge = new[] { destination, source }; p.Call("MergeLayer", merge);
        Check(Number(merge[0], "Calls") == long.MaxValue && (bool)p.Get("MeasurementInvalid") && merge[1].Equals(originalSource), "MergeLayer_saturates_without_wrap_and_preserves_source");
        var add = new object[] { long.MaxValue, 1L }; p.Call("Add", add); Check((long)add[0] == long.MaxValue, "Add_overflow_saturates");
        add = new object[] { 0L, -1L }; p.Call("Add", add); Check((bool)p.Get("MeasurementInvalid") && (long)add[0] >= 0, "negative_counter_input_invalidates_without_wrap");

        void Frame(Action? body = null, bool complete = true)
        {
            int depth = (int)p.Call("FrameBegin")!; body?.Invoke(); p.Call("FrameEnd", depth, complete);
        }
        long State(string field) => Number(p.Get("WindowState"), field);
        p.Reset(); Frame(() => { CostFixture.Reset(129); p.AfterDraw(p.Drawing, true, false, 0); });
        Check(State("Frames") == 1 && State("Eligible") == 1 && p.Layer(true, "ValidSamples", true) == 2 && p.Number("FrameDepth") == 0 && !(bool)p.Get("FrameCollecting"), "completed_eligible_frame_merges_passes_and_restores");
        p.Reset(); Frame(() => { CostFixture.Reset(129); p.AfterDraw(p.Drawing, true, false, 0); }, false);
        Check(State("Aborted") == 1 && State("Eligible") == 0 && p.Layer(true, "ValidSamples", true) == 0, "aborted_frame_donates_no_tile_timings");
        p.Reset(); Frame(() => { CostFixture.Reset(); p.AfterDraw(p.Drawing, true, false, 0); Tile49WorldFixture.playerInventory = true; });
        Check(State("Changed") == 1 && State("Inventory") == 1 && State("Eligible") == 0 && p.Layer(true, "ValidSamples", true) == 0, "mixed_inventory_frame_excluded");
        var flags = new (string name, Action set, string count)[] {
            ("menu", () => Tile49WorldFixture.gameMenu = true, "Menu"), ("paused", () => Tile49WorldFixture.gamePaused = true, "Paused"),
            ("inventory", () => Tile49WorldFixture.playerInventory = true, "Inventory"), ("map", () => Tile49WorldFixture.mapFullscreen = true, "Map"),
            ("dead", () => Tile49WorldFixture.player![0].dead = true, "Dead"), ("ghost", () => Tile49WorldFixture.player![0].ghost = true, "Ghost"),
            ("spectator", () => Tile49WorldFixture.player![0].spectating = 0, "Spectator"), ("null_players", () => Tile49WorldFixture.player = null, "InvalidPlayer"),
            ("out_of_range_player", () => Tile49WorldFixture.myPlayer = 4, "InvalidPlayer"), ("negative_player", () => Tile49WorldFixture.myPlayer = -1, "InvalidPlayer"),
            ("null_player", () => Tile49WorldFixture.player![0] = null!, "InvalidPlayer"), ("nan_player", () => Tile49WorldFixture.player![0].position.X = float.NaN, "InvalidPlayer"),
            ("infinite_camera", () => Tile49WorldFixture.screenPosition.Y = float.PositiveInfinity, "InvalidScene"), ("invalid_zoom", () => Tile49WorldFixture.GameZoomTarget = 0, "InvalidScene"),
            ("nan_time", () => Tile49WorldFixture.time = double.NaN, "InvalidScene"), ("empty_viewport", () => Tile49WorldFixture.screenHeight = 0, "InvalidScene") };
        foreach (var item in flags)
        {
            p.Reset(); item.set(); bool collecting = true; Frame(() => collecting = (bool)p.Get("FrameCollecting"));
            Check(!collecting && State("Eligible") == 0 && State(item.count) == 1, "state_exclusion_" + item.name);
        }
        p.Reset(); Tile49WorldFixture.autoPause = true; Frame(); Check(State("AutoPause") == 1 && State("Eligible") == 1, "autopause_alone_is_not_paused");
        p.Reset(); Frame(() => { Tile49WorldFixture.playerInventory = true; Tile49WorldFixture.playerInventory = false; });
        Check(State("Eligible") == 1 && State("Changed") == 0, "boundary_snapshot_toggle_return_limit_disclosed");
        p.Reset(); Frame(() => { Tile49WorldFixture.player![0].position.X += 10; Tile49WorldFixture.screenPosition.Y += 20; Tile49WorldFixture.time += 1; });
        Check(State("Eligible") == 1 && State("SceneSamples") == 2 && State("PlayerMoved") == 1 && State("CameraMoved") == 1, "scene_boundary_motion_records_ranges_without_invented_identity");
        foreach (var change in new Action[] { () => Tile49WorldFixture.GameZoomTarget = 2, () => Tile49WorldFixture.screenWidth = 800, () => Tile49WorldFixture.FrameSkipMode = 1, () => Tile49WorldFixture.dayTime = false, () => { Tile49WorldFixture.player = new[] { new Tile49PlayerFixture(), new Tile49PlayerFixture() }; Tile49WorldFixture.myPlayer = 1; } })
        { p.Reset(); Frame(change); Check(State("Changed") == 1 && State("Eligible") == 0, "changed_view_or_player_or_day_excludes_frame_" + p.Checks.Count); }
        p.Reset(); int root = (int)p.Call("FrameBegin")!; p.Set("Pending", true); int nested = (int)p.Call("FrameBegin")!;
        Check(!(bool)p.Get("FrameCollecting") && !(bool)p.Get("Pending"), "nested_frame_disables_collection"); p.Call("FrameEnd", nested, false);
        Check((bool)p.Get("FrameCollecting") && (bool)p.Get("Pending") && p.Number("FrameDepth") == 1, "nested_frame_restores_parent_flags"); p.Call("FrameEnd", root, true);
        Check(State("Frames") == 1 && p.Number("reentrantFrames") == 1 && !(bool)p.Get("Pending"), "nested_frame_not_double_counted");
        p.Reset(); Frame(); Exception? threadError = null; bool childExcluded = false;
        var thread = new Thread(() => { try { Tile49ObserverFixture.ResetClock(); int d = (int)p.Call("FrameBegin")!; childExcluded = !(bool)p.Get("FrameCollecting"); p.Call("FrameEnd", d, true); } catch (Exception e) { threadError = e; } }); thread.Start(); thread.Join();
        Check(threadError == null && childExcluded && State("Frames") == 1 && p.Number("ignoredThreads") == 1 && p.Number("FrameDepth") == 0, "other_thread_excluded_without_owner_or_threadstatic_corruption");

        var wrapperCases = new List<object>();
        foreach (int stage in new[] { 0, 22, 1, 2, 4, 25, 99 })
        {
            p.Reset(); FixtureMain.ThrowStage = stage; FixtureMain.OnPostDraw = _ => FixtureMain.Effect(99); var baseline = new FixtureMain(); Exception? before = null;
            try { p.BeforeMain(baseline, new()); } catch (Exception e) { before = e; }
            long effects = FixtureMain.Effects; bool stuck = baseline._isDrawingOrUpdating;
            p.Reset(); FixtureMain.ThrowStage = stage; FixtureMain.OnPostDraw = _ => FixtureMain.Effect(99); var candidate = new FixtureMain(); Exception? after = null;
            try { p.AfterMain(candidate, new()); } catch (Exception e) { after = e; }
            Check(ReferenceEquals(before, after) && effects == FixtureMain.Effects && stuck == candidate._isDrawingOrUpdating && p.Number("FrameDepth") == 0, "Main_Draw_original_order_exception_identity_stuck_flag_" + stage);
            Check(State("Frames") == 1 && State("Aborted") == (stage == 0 ? 0 : 1), "Main_Draw_completed_aborted_frame_hook_" + stage);
            wrapperCases.Add(new { stage, effects, originalStuckFlag = stuck });
        }
        foreach (bool busy in new[] { true, false })
        { p.Reset(); FixtureMain.GraphicsAvailable = busy; var game = new FixtureMain { _isDrawingOrUpdating = busy }; p.AfterMain(game, new()); Check(State("Frames") == 0 && Tile49ObserverFixture.ClockCalls == 0, "Main_Draw_early_guard_no_observer_" + busy); }
        p.Reset(); Tile49ObserverFixture.ThrowClock = true; var clockGame = new FixtureMain(); p.AfterMain(clockGame, new());
        Check(!clockGame._isDrawingOrUpdating && p.Number("FrameDepth") == 0 && (bool)p.Get("MeasurementInvalid"), "observer_entry_clock_failure_cannot_replace_successful_Main_Draw");
        p.Reset(); Tile49ObserverFixture.ThrowClock = true; FixtureMain.ThrowStage = 2; Exception? preserved = null;
        try { p.AfterMain(new(), new()); } catch (Exception e) { preserved = e; }
        Check(ReferenceEquals(preserved, FixtureMain.Failure) && p.Number("FrameDepth") == 0, "observer_failure_cannot_replace_game_exception");
        foreach (int stage in new[] { 0, 12, 34, 35, 38, 39, 40 })
        {
            p.Reset(); FixtureMain.ThrowStage = stage; Tile49GameFixture.dedServ = true; Exception? before = null; try { p.BeforeRun(); } catch (Exception e) { before = e; }
            long effects = FixtureMain.Effects; Exception? displayed = Tile49GameFixture.Displayed;
            p.Reset(); FixtureMain.ThrowStage = stage; Tile49GameFixture.dedServ = true; Exception? after = null; try { p.AfterRun(); } catch (Exception e) { after = e; }
            Check(ReferenceEquals(before, after) && ReferenceEquals(displayed, Tile49GameFixture.Displayed) && effects == FixtureMain.Effects, "whole_RunGame_catch_finally_disposal_identity_" + stage);
        }
        string Capture(Action action, Tile49OutputFixture sink)
        { var saved = Console.Out; try { Console.SetOut(sink); action(); } finally { Console.SetOut(saved); } return sink.ToString(); }
        string Generate()
        {
            p.Reset(); Frame(() => { CostFixture.Reset(257); p.AfterDraw(p.Drawing, true, false, 0); CostFixture.Reset(129, solid: false); p.AfterDraw(p.Drawing, false, false, 0); });
            var sink = new Tile49OutputFixture(); string report = Capture(() => p.Call("Flush"), sink);
            Check(sink.Writes == 2 && report.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 19 && report.Contains("NX_PROFILE END"), "actual_runtime_compact_19_row_report");
            string after = Capture(() => p.Call("Flush"), sink); Check(after == report && State("Frames") == 0 && (bool)p.Get("finalized"), "successful_final_report_idempotent_clear_after_END"); return report;
        }
        string packet = Generate(); string repeated = Generate(); Check(packet == repeated, "actual_report_generation_deterministic"); File.WriteAllText(Path.Combine(output, "tile-helper-report.log"), packet);
        var captureFailureReports = new List<string>();
        foreach (bool atBegin in new[] { true, false }) foreach (int clockOffset in new[] { 1, 2 })
        {
            p.Reset(); Frame();
            if (atBegin)
            {
                Tile49ObserverFixture.ThrowClockCall = Tile49ObserverFixture.ClockCalls + clockOffset;
                Frame();
            }
            else
            {
                int depth = (int)p.Call("FrameBegin")!;
                Tile49ObserverFixture.ThrowClockCall = Tile49ObserverFixture.ClockCalls + clockOffset;
                p.Call("FrameEnd", depth, true);
            }
            string name = "capture_" + (atBegin ? "begin" : "end") + "_clock_" + clockOffset;
            Check(State("Frames") == 1 && p.Number("snapshotCalls") == 2 && p.Number("captureFailureFrames") == 1 && (bool)p.Get("MeasurementInvalid"), name + ":partial_snapshot_not_committed");
            Check(p.Number("FrameDepth") == 0 && !(bool)p.Get("FrameCollecting") && !(bool)p.Get("Pending"), name + ":scope_restored");
            Frame();
            Check(State("Frames") == 2 && State("Eligible") == 2 && p.Number("snapshotCalls") == 4, name + ":subsequent_complete_capture_recovers");
            string file = "tile-helper-" + name + ".log";
            File.WriteAllText(Path.Combine(output, file), Capture(() => p.Call("Flush"), new Tile49OutputFixture())); captureFailureReports.Add(file);
        }
        var reportClockCases = new List<object>();
        foreach (int clockOffset in new[] { 1, 2, 3, 4 })
        {
            p.Reset(); Frame(); p.Set("FrameCollecting", true); p.Set("Pending", true);
            Tile49ObserverFixture.ThrowClockCall = Tile49ObserverFixture.ClockCalls + clockOffset;
            string first = Capture(() => p.Call("Flush"), new Tile49OutputFixture());
            Check((bool)p.Get("MeasurementInvalid") && !(bool)p.Get("reporting") && (bool)p.Get("FrameCollecting") && (bool)p.Get("Pending"), "report_clock_failure_restores_saved_flags_" + clockOffset);
            Check(!first.Contains("NX_PROFILE END") && State("Frames") == 1 && !(bool)p.Get("finalized"), "report_clock_failure_retains_unreported_data_" + clockOffset);
            string finalPacket = Capture(() => p.Call("Flush"), new Tile49OutputFixture());
            Check(finalPacket.Contains("NX_PROFILE END") && State("Frames") == 0 && (bool)p.Get("finalized"), "report_clock_failure_retry_recovers_" + clockOffset);
            string file = "tile-helper-report-clock-" + clockOffset + ".log"; File.WriteAllText(Path.Combine(output, file), finalPacket);
            if (clockOffset == 3) File.WriteAllText(Path.Combine(output, "tile-helper-report-clock-incomplete.log"), first);
            reportClockCases.Add(new { clockOffset, file });
        }
        var reportRegressions = new List<string>();
        foreach (var regression in new[] {
            ("start", new long[] { 10000, 9999, 10020, 10030 }),
            ("body", new long[] { 10000, 10010, 10009, 10030 }),
            ("footer", new long[] { 10000, 10010, 10020, 10019 }) })
        {
            p.Reset(); Frame(); Tile49ObserverFixture.Script = regression.Item2; Tile49ObserverFixture.ScriptIndex = 0;
            long clockStart = Tile49ObserverFixture.ClockCalls; string report = Capture(() => p.Call("Flush"), new Tile49OutputFixture());
            Check(report.Contains("NX_PROFILE END") && report.Split('\n').Single(row => row.StartsWith("NX_PROFILE REPORT_COST ")).Contains("measurement_invalid=1"), "report_" + regression.Item1 + "_clock_regression_visible_before_END");
            Check((bool)p.Get("MeasurementInvalid") && (bool)p.Get("finalized") && !(bool)p.Get("reporting") && Tile49ObserverFixture.ClockCalls - clockStart == 4, "report_" + regression.Item1 + "_clock_regression_no_unreportable_clock");
            string file = "tile-helper-report-regression-" + regression.Item1 + ".log"; File.WriteAllText(Path.Combine(output, file), report); reportRegressions.Add(file);
        }
        p.Reset(); Frame(); Tile49ObserverFixture.Tick += Tile49ObserverFixture.Frequency * 5;
        var finalSink = new Tile49OutputFixture(); string periodic = Capture(() => Frame(), finalSink);
        Check(periodic.Contains(" final=0 ") && State("Frames") == 0, "periodic_report_finishes_before_empty_final");
        string periodicAndFinal = Capture(() => p.Call("Flush"), finalSink);
        Check(finalSink.Writes == 4 && periodicAndFinal.Length > periodic.Length && State("Frames") == 0 && p.Number("snapshotCalls") == 0, "empty_final_emitted_without_fake_frame_or_snapshot");
        Check(Capture(() => p.Call("Flush"), finalSink) == periodicAndFinal && finalSink.Writes == 4, "empty_final_idempotent");
        File.WriteAllText(Path.Combine(output, "tile-helper-periodic-empty-final.log"), periodicAndFinal);
        p.Reset(); Frame(); Tile49ObserverFixture.Tick += Tile49ObserverFixture.Frequency * 5;
        var slowFooter = new Tile49OutputFixture { OnWrite = write => { if (write == 2) Tile49ObserverFixture.Tick += Tile49ObserverFixture.Frequency * 10; } };
        Capture(() => Frame(), slowFooter); Check(slowFooter.Writes == 2, "slow_footer_first_periodic_report_complete");
        Capture(() => Frame(), slowFooter); Check(slowFooter.Writes == 2, "slow_footer_next_root_only_anchors_retry_floor");
        Tile49ObserverFixture.Tick += Tile49ObserverFixture.Frequency * 5 - 10000;
        Capture(() => Frame(), slowFooter); Check(slowFooter.Writes == 2, "slow_footer_less_than_five_seconds_after_anchor_no_retry");
        Tile49ObserverFixture.Tick += 10000;
        string slowFooterReports = Capture(() => Frame(), slowFooter); Check(slowFooter.Writes == 4, "slow_footer_five_seconds_after_anchor_allows_next_report");
        File.WriteAllText(Path.Combine(output, "tile-helper-slow-footer-periodic.log"), slowFooterReports);
        p.Reset(); Frame(); var partial = new Tile49OutputFixture { FailWrite = 2 }; string incomplete = Capture(() => p.Call("Flush"), partial);
        Check(incomplete.Contains("NX_PROFILE BEGIN") && !incomplete.Contains("NX_PROFILE END") && State("Frames") == 1 && p.Number("reportFailures") == 1 && !(bool)p.Get("finalized"), "footer_output_failure_retains_data_and_incomplete_packet");
        File.WriteAllText(Path.Combine(output, "tile-helper-incomplete.log"), incomplete);
        var recovered = new Tile49OutputFixture(); string retry = Capture(() => p.Call("Flush"), recovered); Check(retry.Contains("NX_PROFILE END") && State("Frames") == 0 && p.Number("reportFailures") == 1, "successful_retry_contains_retained_data_and_failure_counter");
        File.WriteAllText(Path.Combine(output, "tile-helper-recovered.log"), retry);
        p.Reset(); Frame(); var reentrant = new Tile49OutputFixture { Reenter = () => { bool collecting = (bool)p.Get("FrameCollecting"); Frame(); Check(!collecting, "output_reentry_collection_disabled"); } };
        string reentryReport = Capture(() => p.Call("Flush"), reentrant); Check(reentryReport.Contains("NX_PROFILE END") && p.Number("reentrantFrames") == 1 && p.Number("FrameDepth") == 0, "output_reentry_does_not_create_fake_frame");
        p.Reset(); Frame(); var failing = new Tile49OutputFixture { FailWrite = 1 }; Tile49ObserverFixture.Tick += Tile49ObserverFixture.Frequency * 5;
        Capture(() => Frame(), failing); long attempts = p.Number("reportAttempts"); for (int i = 0; i < 3; i++) Frame();
        Check(attempts == 1 && p.Number("reportAttempts") == 1 && p.Number("reportFailures") == 1 && State("Frames") == 5, "failed_periodic_report_not_retried_each_frame");
        foreach (bool fail in new[] { false, true })
        {
            p.Reset(); Frame(); FixtureMain.ThrowStage = fail ? 12 : 0; var sink = new Tile49OutputFixture(); string text = Capture(p.AfterRun, sink);
            Check(fail ? text.Length == 0 && State("Frames") == 1 : text.Contains("NX_PROFILE END") && State("Frames") == 0, "RunGame_flush_only_after_successful_Game_Run_" + fail);
        }
        var parser = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        parser.ArgumentList.Add(Environment.GetEnvironmentVariable("TILE49_REPORT_PROOF") ?? "/work/scripts/patch_tile_helpers/prove_tile_helper_reports.py");
        parser.ArgumentList.Add(Environment.GetEnvironmentVariable("TILE49_ANALYZER") ?? "/work/scripts/analyze_tile_helpers.py"); parser.ArgumentList.Add(output);
        using var process = Process.Start(parser)!; string parserOut = process.StandardOutput.ReadToEnd(), parserError = process.StandardError.ReadToEnd(); process.WaitForExit();
        Check(process.ExitCode == 0, "strict_analyzer_accepts_actual_packets_rejects_adversaries: " + parserOut + parserError);
        return new { wrapperCases, stateExclusions = flags.Select(f => f.name).ToArray(), parserOutput = parserOut.Trim(), report = "tile-helper-report.log", incompleteReport = "tile-helper-incomplete.log", recoveredReport = "tile-helper-recovered.log", captureFailureReports, reportClockCases, reportRegressions, emptyFinalReport = "tile-helper-periodic-empty-final.log", slowFooterReport = "tile-helper-slow-footer-periodic.log" };
    }
}
