using System.Reflection;
using static RenderProof.Session;

internal static class RenderRuntimeProof
{
    internal static object Run(RenderProof.Session p, string output)
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
            Check(RenderProof.MetricFields.All(field => Field(context, field).Equals(Field(before, field))), name + ":atomic_no_metric_donation"); NormalPass(ref pass);
        }
        Invalid("backwards_sample", () => { RenderFixture.Script = new[] { 1L }; RenderFixture.ScriptIndex = 0; });
        Invalid("negative_sample", () => { RenderFixture.Script = new[] { -1L }; RenderFixture.ScriptIndex = 0; });
        Invalid("clock_throw_sample", () => RenderFixture.ThrowClock = true); RenderFixture.ThrowClock = false;
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
        p.Reset(); p.Set("FrameCollecting", true);
        for (int phase = 0; phase < 128; phase++) { CostFixture.Reset(); p.AfterDraw(p.Drawing, true, false, 0); }
        Check(p.Layer(true, "Passes") == 128 && p.Layer(true, "Selected") == 1 && p.Number("phaseSolid") == 0 && p.Number("phaseNonSolid") == 0, "runtime_actual_Draw_rotates_full_solid_cycle_without_advancing_nonsolid");
        for (int phase = 0; phase < 2; phase++) { CostFixture.Reset(solid: false); p.AfterDraw(p.Drawing, false, false, 0); }
        Check(p.Layer(false, "Passes") == 2 && p.Layer(false, "Selected") == 1 && p.Number("phaseSolid") == 0 && p.Number("phaseNonSolid") == 2, "runtime_actual_Draw_independent_nonsolid_phase_not_shared_with_solid");
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
        p.Reset(); Frame(() => { CostFixture.Reset(); p.AfterDraw(p.Drawing, true, false, 0); RenderWorldFixture.playerInventory = true; });
        Check(State("Changed") == 1 && State("Inventory") == 1 && State("Eligible") == 0 && p.Layer(true, "ValidSamples", true) == 0, "mixed_inventory_frame_excluded");
        var flags = new (string name, Action set, string count)[] {
            ("menu", () => RenderWorldFixture.gameMenu = true, "Menu"), ("paused", () => RenderWorldFixture.gamePaused = true, "Paused"),
            ("inventory", () => RenderWorldFixture.playerInventory = true, "Inventory"), ("map", () => RenderWorldFixture.mapFullscreen = true, "Map"),
            ("dead", () => RenderWorldFixture.player![0].dead = true, "Dead"), ("ghost", () => RenderWorldFixture.player![0].ghost = true, "Ghost"),
            ("spectator", () => RenderWorldFixture.player![0].spectating = 0, "Spectator"), ("null_players", () => RenderWorldFixture.player = null, "InvalidPlayer"),
            ("out_of_range_player", () => RenderWorldFixture.myPlayer = 4, "InvalidPlayer"), ("negative_player", () => RenderWorldFixture.myPlayer = -1, "InvalidPlayer"),
            ("null_player", () => RenderWorldFixture.player![0] = null!, "InvalidPlayer"), ("nan_player", () => RenderWorldFixture.player![0].position.X = float.NaN, "InvalidPlayer"),
            ("infinite_camera", () => RenderWorldFixture.screenPosition.Y = float.PositiveInfinity, "InvalidScene"), ("invalid_zoom", () => RenderWorldFixture.GameZoomTarget = 0, "InvalidScene"),
            ("nan_time", () => RenderWorldFixture.time = double.NaN, "InvalidScene"), ("empty_viewport", () => RenderWorldFixture.screenHeight = 0, "InvalidScene") };
        foreach (var item in flags)
        {
            p.Reset(); item.set(); bool collecting = true; Frame(() => collecting = (bool)p.Get("FrameCollecting"));
            Check(!collecting && State("Eligible") == 0 && State(item.count) == 1, "state_exclusion_" + item.name);
        }
        p.Reset(); RenderWorldFixture.autoPause = true; Frame(); Check(State("AutoPause") == 1 && State("Eligible") == 1, "autopause_alone_is_not_paused");
        p.Reset(); Frame(() => { RenderWorldFixture.playerInventory = true; RenderWorldFixture.playerInventory = false; });
        Check(State("Eligible") == 1 && State("Changed") == 0, "boundary_snapshot_toggle_return_limit_disclosed");
        p.Reset(); Frame(() => { RenderWorldFixture.player![0].position.X += 10; RenderWorldFixture.screenPosition.Y += 20; RenderWorldFixture.time += 1; });
        Check(State("Eligible") == 1 && State("SceneSamples") == 2 && State("PlayerMoved") == 1 && State("CameraMoved") == 1, "scene_boundary_motion_records_ranges_without_invented_identity");
        foreach (var change in new Action[] { () => RenderWorldFixture.GameZoomTarget = 2, () => RenderWorldFixture.screenWidth = 800, () => RenderWorldFixture.FrameSkipMode = 1, () => RenderWorldFixture.dayTime = false, () => { RenderWorldFixture.player = new[] { new RenderPlayerFixture(), new RenderPlayerFixture() }; RenderWorldFixture.myPlayer = 1; } })
        { p.Reset(); Frame(change); Check(State("Changed") == 1 && State("Eligible") == 0, "changed_view_or_player_or_day_excludes_frame_" + p.Checks.Count); }
        p.Reset(); int root = (int)p.Call("FrameBegin")!; p.Set("Pending", true); int nested = (int)p.Call("FrameBegin")!;
        Check(!(bool)p.Get("FrameCollecting") && !(bool)p.Get("Pending"), "nested_frame_disables_collection"); p.Call("FrameEnd", nested, false);
        Check((bool)p.Get("FrameCollecting") && (bool)p.Get("Pending") && p.Number("FrameDepth") == 1, "nested_frame_restores_parent_flags"); p.Call("FrameEnd", root, true);
        Check(State("Frames") == 1 && p.Number("reentrantFrames") == 1 && !(bool)p.Get("Pending"), "nested_frame_not_double_counted");
        p.Reset(); Frame(); Exception? threadError = null; bool childExcluded = false;
        var thread = new Thread(() => { try { RenderFixture.ResetClock(); int d = (int)p.Call("FrameBegin")!; childExcluded = !(bool)p.Get("FrameCollecting"); p.Call("FrameEnd", d, true); } catch (Exception e) { threadError = e; } }); thread.Start(); thread.Join();
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
        { p.Reset(); FixtureMain.GraphicsAvailable = busy; var game = new FixtureMain { _isDrawingOrUpdating = busy }; p.AfterMain(game, new()); Check(State("Frames") == 0 && RenderFixture.ClockCalls == 0, "Main_Draw_early_guard_no_observer_" + busy); }
        p.Reset(); RenderFixture.ThrowClock = true; var clockGame = new FixtureMain(); p.AfterMain(clockGame, new());
        Check(!clockGame._isDrawingOrUpdating && p.Number("FrameDepth") == 0 && (bool)p.Get("MeasurementInvalid"), "observer_entry_clock_failure_cannot_replace_successful_Main_Draw");
        p.Reset(); RenderFixture.ThrowClock = true; FixtureMain.ThrowStage = 2; Exception? preserved = null;
        try { p.AfterMain(new(), new()); } catch (Exception e) { preserved = e; }
        Check(ReferenceEquals(preserved, FixtureMain.Failure) && p.Number("FrameDepth") == 0, "observer_failure_cannot_replace_game_exception");
        foreach (int stage in new[] { 0, 12, 34, 35, 38, 39, 40 })
        {
            p.Reset(); FixtureMain.ThrowStage = stage; RenderGameFixture.dedServ = true; Exception? before = null; try { p.BeforeRun(); } catch (Exception e) { before = e; }
            long effects = FixtureMain.Effects; Exception? displayed = RenderGameFixture.Displayed;
            p.Reset(); FixtureMain.ThrowStage = stage; RenderGameFixture.dedServ = true; Exception? after = null; try { p.AfterRun(); } catch (Exception e) { after = e; }
            Check(ReferenceEquals(before, after) && ReferenceEquals(displayed, RenderGameFixture.Displayed) && effects == FixtureMain.Effects, "whole_RunGame_catch_finally_disposal_identity_" + stage);
        }
        string Capture(Action action, RenderOutputFixture sink)
        { var saved = Console.Out; try { Console.SetOut(sink); action(); } finally { Console.SetOut(saved); } return sink.ToString(); }
        string Generate()
        {
            p.Reset(); Frame(() => {
                int end = (int)p.Call("BatchBegin", 0)!;
                CostFixture.Reset(257); p.AfterDraw(p.Drawing, true, false, 0);
                CostFixture.Reset(129, solid: false); p.AfterDraw(p.Drawing, false, false, 0);
                int upload = (int)p.Call("BatchBegin", 1)!; RenderFixture.Tick += 70; p.Call("BatchEnd", upload, true);
                int submit = (int)p.Call("BatchBegin", 2)!; RenderFixture.Tick += 90; p.Call("BatchEnd", submit, true);
                p.Call("BatchEnd", end, true);
            });
            Check(p.Layer(true, "ValidSamples", true) > 0 && p.Layer(false, "ValidSamples", true) > 0 && Number(Field(p.Get("WindowBatch"), "End"), "Calls") == 1 && Number(Field(p.Get("WindowBatch"), "Upload"), "Calls") == 1 && Number(Field(p.Get("WindowBatch"), "Submit"), "Calls") == 1 && !(bool)p.Get("MeasurementInvalid"), "healthy_actual_report_contains_tile_samples_and_all_batch_metrics");
            var sink = new RenderOutputFixture(); string report = Capture(() => p.Call("Flush"), sink);
            Check(sink.Writes == 2 && report.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 23 && report.Contains("NX_PROFILE END"), "actual_runtime_compact_23_row_report");
            string after = Capture(() => p.Call("Flush"), sink); Check(after == report && State("Frames") == 0 && (bool)p.Get("finalized"), "successful_final_report_idempotent_clear_after_END"); return report;
        }
        string packet = Generate(); string repeated = Generate(); Check(packet == repeated, "actual_report_generation_deterministic"); File.WriteAllText(Path.Combine(output, "render-report.log"), packet);
        var captureFailureReports = new List<string>();
        foreach (bool atBegin in new[] { true, false }) foreach (int clockOffset in new[] { 1, 2 })
        {
            p.Reset(); Frame();
            if (atBegin)
            {
                RenderFixture.ThrowClockCall = RenderFixture.ClockCalls + clockOffset;
                Frame();
            }
            else
            {
                int depth = (int)p.Call("FrameBegin")!;
                RenderFixture.ThrowClockCall = RenderFixture.ClockCalls + clockOffset;
                p.Call("FrameEnd", depth, true);
            }
            string name = "capture_" + (atBegin ? "begin" : "end") + "_clock_" + clockOffset;
            Check(State("Frames") == 1 && p.Number("snapshotCalls") == 2 && p.Number("captureFailureFrames") == 1 && (bool)p.Get("MeasurementInvalid"), name + ":partial_snapshot_not_committed");
            Check(p.Number("FrameDepth") == 0 && !(bool)p.Get("FrameCollecting") && !(bool)p.Get("Pending"), name + ":scope_restored");
            Frame();
            Check(State("Frames") == 2 && State("Eligible") == 2 && p.Number("snapshotCalls") == 4, name + ":subsequent_complete_capture_recovers");
            string file = "render-" + name + ".log";
            File.WriteAllText(Path.Combine(output, file), Capture(() => p.Call("Flush"), new RenderOutputFixture())); captureFailureReports.Add(file);
        }
        var reportClockCases = new List<object>();
        foreach (int clockOffset in new[] { 1, 2, 3, 4 })
        {
            p.Reset(); Frame(); p.Set("FrameCollecting", true); p.Set("Pending", true);
            RenderFixture.ThrowClockCall = RenderFixture.ClockCalls + clockOffset;
            string first = Capture(() => p.Call("Flush"), new RenderOutputFixture());
            Check((bool)p.Get("MeasurementInvalid") && !(bool)p.Get("reporting") && (bool)p.Get("FrameCollecting") && (bool)p.Get("Pending"), "report_clock_failure_restores_saved_flags_" + clockOffset);
            Check(!first.Contains("NX_PROFILE END") && State("Frames") == 1 && !(bool)p.Get("finalized"), "report_clock_failure_retains_unreported_data_" + clockOffset);
            string finalPacket = Capture(() => p.Call("Flush"), new RenderOutputFixture());
            Check(finalPacket.Contains("NX_PROFILE END") && State("Frames") == 0 && (bool)p.Get("finalized"), "report_clock_failure_retry_recovers_" + clockOffset);
            string file = "render-report-clock-" + clockOffset + ".log"; File.WriteAllText(Path.Combine(output, file), finalPacket);
            if (clockOffset == 3) File.WriteAllText(Path.Combine(output, "render-report-clock-incomplete.log"), first);
            reportClockCases.Add(new { clockOffset, file });
        }
        var reportRegressions = new List<string>();
        foreach (var regression in new[] {
            ("start", new long[] { 10000, 9999, 10020, 10030 }),
            ("body", new long[] { 10000, 10010, 10009, 10030 }),
            ("footer", new long[] { 10000, 10010, 10020, 10019 }) })
        {
            p.Reset(); Frame(); RenderFixture.Script = regression.Item2; RenderFixture.ScriptIndex = 0;
            long clockStart = RenderFixture.ClockCalls; string report = Capture(() => p.Call("Flush"), new RenderOutputFixture());
            Check(report.Contains("NX_PROFILE END") && report.Split('\n').Single(row => row.StartsWith("NX_PROFILE REPORT_COST ")).Contains("measurement_invalid=1"), "report_" + regression.Item1 + "_clock_regression_visible_before_END");
            Check((bool)p.Get("MeasurementInvalid") && (bool)p.Get("finalized") && !(bool)p.Get("reporting") && RenderFixture.ClockCalls - clockStart == 4, "report_" + regression.Item1 + "_clock_regression_no_unreportable_clock");
            string file = "render-report-regression-" + regression.Item1 + ".log"; File.WriteAllText(Path.Combine(output, file), report); reportRegressions.Add(file);
        }
        p.Reset(); Frame(); RenderFixture.Tick += RenderFixture.Frequency * 5;
        var finalSink = new RenderOutputFixture(); string periodic = Capture(() => Frame(), finalSink);
        Check(periodic.Contains(" final=0 ") && State("Frames") == 0, "periodic_report_finishes_before_empty_final");
        string periodicAndFinal = Capture(() => p.Call("Flush"), finalSink);
        Check(finalSink.Writes == 4 && periodicAndFinal.Length > periodic.Length && State("Frames") == 0 && p.Number("snapshotCalls") == 0, "empty_final_emitted_without_fake_frame_or_snapshot");
        Check(Capture(() => p.Call("Flush"), finalSink) == periodicAndFinal && finalSink.Writes == 4, "empty_final_idempotent");
        File.WriteAllText(Path.Combine(output, "render-periodic-empty-final.log"), periodicAndFinal);
        File.WriteAllText(Path.Combine(output, "render-empty-final.log"), periodicAndFinal[periodic.Length..]);
        p.Reset(); Frame(); RenderFixture.Tick += RenderFixture.Frequency * 5;
        var slowFooter = new RenderOutputFixture { OnWrite = write => { if (write == 2) RenderFixture.Tick += RenderFixture.Frequency * 10; } };
        Capture(() => Frame(), slowFooter); Check(slowFooter.Writes == 2, "slow_footer_first_periodic_report_complete");
        Capture(() => Frame(), slowFooter); Check(slowFooter.Writes == 2, "slow_footer_next_root_only_anchors_retry_floor");
        RenderFixture.Tick += RenderFixture.Frequency * 5 - 10000;
        Capture(() => Frame(), slowFooter); Check(slowFooter.Writes == 2, "slow_footer_less_than_five_seconds_after_anchor_no_retry");
        RenderFixture.Tick += 10000;
        string slowFooterReports = Capture(() => Frame(), slowFooter); Check(slowFooter.Writes == 4, "slow_footer_five_seconds_after_anchor_allows_next_report");
        File.WriteAllText(Path.Combine(output, "render-slow-footer-periodic.log"), slowFooterReports);
        p.Reset(); Frame(); var partial = new RenderOutputFixture { FailWrite = 2 }; string incomplete = Capture(() => p.Call("Flush"), partial);
        Check(incomplete.Contains("NX_PROFILE BEGIN") && !incomplete.Contains("NX_PROFILE END") && State("Frames") == 1 && p.Number("reportFailures") == 1 && !(bool)p.Get("finalized"), "footer_output_failure_retains_data_and_incomplete_packet");
        File.WriteAllText(Path.Combine(output, "render-incomplete.log"), incomplete);
        var recovered = new RenderOutputFixture(); string retry = Capture(() => p.Call("Flush"), recovered); Check(retry.Contains("NX_PROFILE END") && State("Frames") == 0 && p.Number("reportFailures") == 1, "successful_retry_contains_retained_data_and_failure_counter");
        File.WriteAllText(Path.Combine(output, "render-recovered.log"), retry);
        p.Reset(); Frame(); var reentrant = new RenderOutputFixture { Reenter = () => { bool collecting = (bool)p.Get("FrameCollecting"); Frame(); Check(!collecting, "output_reentry_collection_disabled"); } };
        string reentryReport = Capture(() => p.Call("Flush"), reentrant); Check(reentryReport.Contains("NX_PROFILE END") && p.Number("reentrantFrames") == 1 && p.Number("FrameDepth") == 0, "output_reentry_does_not_create_fake_frame");
        p.Reset(); Frame(); var failing = new RenderOutputFixture { FailWrite = 1 }; RenderFixture.Tick += RenderFixture.Frequency * 5;
        Capture(() => Frame(), failing); long attempts = p.Number("reportAttempts"); for (int i = 0; i < 3; i++) Frame();
        Check(attempts == 1 && p.Number("reportAttempts") == 1 && p.Number("reportFailures") == 1 && State("Frames") == 5, "failed_periodic_report_not_retried_each_frame");
        foreach (bool fail in new[] { false, true })
        {
            p.Reset(); Frame(); FixtureMain.ThrowStage = fail ? 12 : 0; var sink = new RenderOutputFixture(); string text = Capture(p.AfterRun, sink);
            Check(fail ? text.Length == 0 && State("Frames") == 1 : text.Contains("NX_PROFILE END") && State("Frames") == 0, "RunGame_flush_only_after_successful_Game_Run_" + fail);
        }
        var acceptedReports = new List<object> {
            new { file = "render-report.log", measurement_invalid = false, final_seen = true },
            new { file = "render-periodic-empty-final.log", measurement_invalid = false, final_seen = true },
            new { file = "render-empty-final.log", measurement_invalid = false, final_seen = true },
            new { file = "render-slow-footer-periodic.log", measurement_invalid = false, final_seen = false },
            new { file = "render-recovered.log", measurement_invalid = false, final_seen = true }
        };
        foreach (string file in captureFailureReports.Concat(reportRegressions))
            acceptedReports.Add(new { file, measurement_invalid = true, final_seen = true });
        foreach (int clockOffset in new[] { 1, 2, 3, 4 })
            acceptedReports.Add(new { file = "render-report-clock-" + clockOffset + ".log", measurement_invalid = true, final_seen = true });
        var rejectedReports = new[] { new { file = "render-incomplete.log" }, new { file = "render-report-clock-incomplete.log" } };
        object batches = BatchScenarios(p, output, acceptedReports);
        object allocations = AllocationScenarios(p);
        return new { wrapperCases, stateExclusions = flags.Select(f => f.name).ToArray(), report = "render-report.log", incompleteReport = "render-incomplete.log", recoveredReport = "render-recovered.log", captureFailureReports, reportClockCases, reportRegressions, emptyFinalReport = "render-periodic-empty-final.log", slowFooterReport = "render-slow-footer-periodic.log", batches, allocations, acceptedReports, rejectedReports };
    }

    private static object BatchScenarios(RenderProof.Session p, string output, List<object> accepted)
    {
        var scenarios = new List<string>();
        string[] metricNames = { "End", "Upload", "Submit" };
        long Batch(string field) => Number(p.Get("WindowBatch"), field);
        long Metric(string name, string field) => Number(Field(p.Get("WindowBatch"), name), field);
        bool EmptyMetrics(object totals) => metricNames.All(name => new[] { "Calls", "InclusiveTicks", "ExclusiveTicks", "InclusiveMax", "ExclusiveMax" }.All(field => Number(Field(totals, name), field) == 0));
        void Check(bool value, string name) { p.Check(value, name); scenarios.Add(name); }
        int Begin(int id) => (int)p.Call("BatchBegin", id)!;
        void End(int cookie, bool completed = true) => p.Call("BatchEnd", cookie, completed);
        void Scope(int id, long ticks = 10) { int cookie = Begin(id); RenderFixture.Tick += ticks; End(cookie); }
        void Frame(Action? body = null, bool completed = true)
        {
            int root = (int)p.Call("FrameBegin")!;
            body?.Invoke();
            p.Call("FrameEnd", root, completed);
        }
        void Save(string file, bool invalid)
        {
            var sink = new RenderOutputFixture(); var saved = Console.Out;
            try { Console.SetOut(sink); p.Call("Flush"); } finally { Console.SetOut(saved); }
            string packet = sink.ToString();
            Check(packet.Contains("NX_PROFILE END") && packet.Contains("NX_PROFILE BATCH_STATE") && packet.Contains("label=indexed_submit"), file + ":actual_runtime_packet_complete");
            File.WriteAllText(Path.Combine(output, file), packet);
            accepted.Add(new { file, measurement_invalid = invalid, final_seen = true });
        }

        p.Reset(); long inactiveClocks = RenderFixture.ClockCalls;
        Check(Begin(0) == 0, "batch_inactive_begin_zero_cookie"); End(0); End(0, false);
        Check(RenderFixture.ClockCalls == inactiveClocks && !(bool)p.Get("MeasurementInvalid"), "batch_inactive_hooks_no_clock_or_invalidity");
        Frame(); Check(Batch("Frames") == 1 && Batch("ValidFrames") == 1 && Batch("DiscardedFrames") == 0 && Batch("ClockCalls") == 0 && EmptyMetrics(p.Get("WindowBatch")), "batch_eligible_zero_scope_frame_valid_without_fake_metrics");

        p.Reset(); Frame(() => {
            RenderFixture.Step = 0; RenderFixture.Tick = 10000;
            int outer = Begin(0); RenderFixture.Tick += 10;
            int upload = Begin(1); RenderFixture.Tick += 20; End(upload);
            RenderFixture.Tick += 5; int submit = Begin(2); RenderFixture.Tick += 30; End(submit);
            RenderFixture.Tick += 7; End(outer);
            Scope(1, 11);
            Check(Batch("Frames") == 0 && EmptyMetrics(p.Get("WindowBatch")), "batch_completed_scopes_remain_provisional_until_root_commit");
        });
        Check(Batch("Frames") == 1 && Batch("ValidFrames") == 1 && Batch("DiscardedFrames") == 0 && Batch("ClockCalls") == 8, "batch_all_eligible_scopes_not_rotating_tile_sampled");
        Check(Metric("End", "Calls") == 1 && Metric("End", "InclusiveTicks") == 72 && Metric("End", "ExclusiveTicks") == 22 && Metric("End", "InclusiveMax") == 72 && Metric("End", "ExclusiveMax") == 22, "batch_parent_inclusive_minus_immediate_children_exact_ticks");
        Check(Metric("Upload", "Calls") == 2 && Metric("Upload", "InclusiveTicks") == 31 && Metric("Upload", "ExclusiveTicks") == 31 && Metric("Upload", "InclusiveMax") == 20 && Metric("Submit", "InclusiveTicks") == 30, "batch_outside_End_upload_preserved_without_false_parent_bound");
        Save("render-batch-inclusive.log", false);
        Check(Batch("Frames") == 0 && EmptyMetrics(p.Get("WindowBatch")), "batch_successful_report_resets_window_only_after_END");

        p.Reset(); Frame(() => {
            RenderFixture.Step = 0;
            int outer = Begin(0); RenderFixture.Tick += 2;
            int inner = Begin(0); RenderFixture.Tick += 3; End(inner);
            RenderFixture.Tick += 5; End(outer);
        });
        Check(Metric("End", "Calls") == 2 && Metric("End", "InclusiveTicks") == 13 && Metric("End", "ExclusiveTicks") == 10 && Metric("End", "InclusiveMax") == 10 && Metric("End", "ExclusiveMax") == 7, "batch_same_id_recursive_scopes_preserve_exclusive_accounting");

        p.Reset(); Frame(() => {
            RenderFixture.Step = 0; int outer = Begin(0); object stack = p.Get("batchStack");
            int nested = (int)p.Call("FrameBegin")!; long clocks = RenderFixture.ClockCalls;
            Check(Begin(1) == 0, "batch_nested_frame_suspends_new_scope"); End(0, false);
            Check(RenderFixture.ClockCalls == clocks && p.Number("batchDepth") == 1 && ReferenceEquals(stack, p.Get("batchStack")), "batch_nested_frame_no_clocks_no_live_stack_reset");
            p.Call("FrameEnd", nested, false);
            RenderFixture.Tick += 17; End(outer);
        });
        Check(Batch("Frames") == 1 && Batch("ValidFrames") == 1 && Metric("End", "InclusiveTicks") == 17 && Batch("AbortedScopes") == 0 && p.Number("reentrantFrames") == 1, "batch_nested_aborted_root_does_not_abort_outer_scope");

        p.Reset(); Exception? threadError = null; bool foreignInactive = false;
        Frame(() => {
            RenderFixture.Step = 0; int outer = Begin(0); object stack = p.Get("batchStack");
            var thread = new Thread(() => {
                try {
                    RenderFixture.ResetClock(); int child = (int)p.Call("FrameBegin")!;
                    long clocks = RenderFixture.ClockCalls; int cookie = Begin(1); End(cookie, false);
                    foreignInactive = cookie == 0 && RenderFixture.ClockCalls == clocks && !(bool)p.Get("FrameCollecting");
                    p.Call("FrameEnd", child, true);
                } catch (Exception error) { threadError = error; }
            });
            thread.Start(); thread.Join();
            Check(threadError == null && foreignInactive && p.Number("batchDepth") == 1 && ReferenceEquals(stack, p.Get("batchStack")), "batch_foreign_thread_leaves_owner_stack_and_clock_untouched");
            RenderFixture.Tick += 19; End(outer);
        });
        Check(Batch("Frames") == 1 && Batch("ValidFrames") == 1 && Metric("End", "InclusiveTicks") == 19 && Batch("ClockCalls") == 2 && p.Number("ignoredThreads") == 1, "batch_foreign_thread_never_donates_or_poison_owner");

        p.Reset(); Frame(() => {
            RenderFixture.Step = 0; int outer = Begin(0); Scope(1, 13); End(outer, false);
        });
        Check(Batch("Frames") == 1 && Batch("ValidFrames") == 0 && Batch("DiscardedFrames") == 1 && Batch("AbortedScopes") == 1 && Batch("ObserverFailures") == 0 && Batch("ClockCalls") == 4 && EmptyMetrics(p.Get("WindowBatch")) && !(bool)p.Get("MeasurementInvalid"), "batch_caught_game_abort_discards_completed_siblings_without_global_invalidity");
        Save("render-batch-aborted.log", false);
        p.Reset(); Frame(() => { int outer = Begin(0); Scope(1); End(outer, false); }); Frame(() => Scope(2));
        Check(Batch("Frames") == 2 && Batch("ValidFrames") == 1 && Batch("DiscardedFrames") == 1 && Metric("Submit", "Calls") == 1 && Metric("Upload", "Calls") == 0, "batch_frame_local_abort_recovers_on_next_eligible_root");
        Save("render-batch-abort-recovered.log", false);

        foreach (string excluded in new[] { "menu", "changed", "aborted_root", "capture_end" })
        {
            p.Reset(); if (excluded == "menu") RenderWorldFixture.gameMenu = true;
            Frame(() => {
                Scope(0);
                if (excluded == "changed") RenderWorldFixture.playerInventory = true;
                if (excluded == "capture_end") RenderFixture.ThrowClockCall = RenderFixture.ClockCalls + 1;
            }, excluded != "aborted_root");
            Check(Batch("Frames") == 0 && Batch("ValidFrames") == 0 && Batch("DiscardedFrames") == 0 && Batch("ClockCalls") == 0 && Batch("AbortedScopes") == 0 && Batch("ObserverFailures") == 0 && EmptyMetrics(p.Get("WindowBatch")), "batch_excluded_frame_no_counts_or_metric_donation_" + excluded);
        }

        void Invalid(string name, Action corrupt, bool recover = true)
        {
            p.Reset(); Frame(() => { RenderFixture.Step = 0; corrupt(); RenderFixture.ThrowClock = false; RenderFixture.Script = null; });
            Check((bool)p.Get("MeasurementInvalid") && Batch("Frames") == 1 && Batch("ValidFrames") == 0 && Batch("DiscardedFrames") == 1 && Batch("ObserverFailures") > 0 && EmptyMetrics(p.Get("WindowBatch")), "batch_" + name + ":invalid_frame_atomic_no_metric_donation");
            Check(p.Number("batchDepth") == 0 && p.Number("FrameDepth") == 0 && !(bool)p.Get("FrameCollecting"), "batch_" + name + ":root_unwinds");
            if (recover)
            {
                Frame(() => Scope(2));
                Check(Batch("Frames") == 2 && Batch("ValidFrames") == 1 && Batch("DiscardedFrames") == 1 && Metric("Submit", "Calls") == 1, "batch_" + name + ":next_frame_healthy_metrics_global_invalid_sticky");
            }
            Save("render-batch-" + name + ".log", true);
        }
        Invalid("negative_begin", () => { RenderFixture.Script = new[] { -1L }; int cookie = Begin(0); End(cookie); });
        Invalid("backwards_end", () => { int cookie = Begin(0); RenderFixture.Script = new[] { RenderFixture.Tick - 1 }; End(cookie); });
        Invalid("backwards_between_scopes", () => { Scope(0); RenderFixture.Script = new[] { RenderFixture.Tick - 1 }; int cookie = Begin(1); End(cookie); });
        Invalid("clock_throw_begin", () => { RenderFixture.ThrowClock = true; int cookie = Begin(0); End(cookie); });
        Invalid("clock_throw_end", () => { int cookie = Begin(0); RenderFixture.ThrowClock = true; End(cookie); });
        Invalid("mismatched_cookie", () => { int outer = Begin(0); int inner = Begin(1); End(outer); End(inner); });
        Invalid("duplicate_cookie", () => { int cookie = Begin(0); End(cookie); End(cookie); });
        Invalid("unclosed_scope", () => Begin(0));
        Invalid("invalid_scope_id", () => End(Begin(3)));
        Invalid("cookie_overflow", () => { p.Set("batchNextCookie", int.MaxValue); End(Begin(0)); }, false);
        Invalid("depth_overflow", () => {
            var cookies = new int[65];
            for (int i = 0; i < cookies.Length; i++) cookies[i] = Begin(i % 3);
            for (int i = cookies.Length - 1; i >= 0; i--) End(cookies[i]);
        });
        Invalid("tile_observer_failure", () => {
            Scope(1); RenderFixture.ThrowClockCall = RenderFixture.ClockCalls + 1;
            object pass = p.Enter(true); p.Region(ref pass, 2); p.Finish(pass);
        });
        p.Reset(); Frame(() => { Scope(1); RenderFixture.ThrowClockCall = RenderFixture.ClockCalls + 3; });
        Check((bool)p.Get("MeasurementInvalid") && Batch("Frames") == 1 && Batch("ValidFrames") == 0 && Batch("DiscardedFrames") == 1 && Batch("ObserverFailures") == 1 && EmptyMetrics(p.Get("WindowBatch")), "batch_root_scheduling_clock_failure_discards_provisional_metrics");
        Save("render-batch-root-clock.log", true);

        p.Reset(); Frame(() => {
            RenderFixture.Step = 0; var cookies = new int[64];
            for (int i = 0; i < cookies.Length; i++) cookies[i] = Begin(i % 3);
            Check(cookies.All(cookie => cookie > 0) && cookies.Distinct().Count() == 64 && p.Number("batchDepth") == 64, "batch_exact_bound_64_has_unique_cookies_and_live_stack");
            for (int i = cookies.Length - 1; i >= 0; i--) End(cookies[i]);
        });
        Check(Batch("ValidFrames") == 1 && Batch("ClockCalls") == 128 && metricNames.Sum(name => Metric(name, "Calls")) == 64 && !(bool)p.Get("MeasurementInvalid"), "batch_exact_bound_64_completes_without_overflow");

        foreach (string field in new[] { "Calls", "InclusiveTicks", "ExclusiveTicks" })
        {
            p.Reset(); Frame(() => {
                RenderFixture.Step = 0; Scope(0, 2); Scope(1, 3);
                object frame = p.Get("frameBatch"), metric = Field(frame, "Submit");
                Field(metric, "Calls", field == "Calls" ? long.MaxValue : 1L);
                Field(metric, "InclusiveTicks", field == "Calls" ? 0L : long.MaxValue);
                Field(metric, "ExclusiveTicks", field == "Calls" ? 0L : long.MaxValue);
                Field(metric, "InclusiveMax", field == "Calls" ? 0L : long.MaxValue);
                Field(metric, "ExclusiveMax", field == "Calls" ? 0L : long.MaxValue);
                Field(frame, "Submit", metric); p.Set("frameBatch", frame); Scope(2, 1);
            });
            Check((bool)p.Get("MeasurementInvalid") && Batch("DiscardedFrames") == 1 && Batch("ValidFrames") == 0 && EmptyMetrics(p.Get("WindowBatch")), "batch_provisional_" + field + "_overflow_discards_prior_sibling_metrics");
        }
        foreach (string metricName in metricNames)
        {
            p.Reset(); Frame(() => Scope(Array.IndexOf(metricNames, metricName)));
            object window = p.Get("WindowBatch"), metric = Field(window, metricName);
            foreach (string field in new[] { "InclusiveTicks", "ExclusiveTicks", "InclusiveMax", "ExclusiveMax" }) Field(metric, field, long.MaxValue);
            Field(window, metricName, metric); p.Set("WindowBatch", window);
            Frame(() => { RenderFixture.Step = 0; Scope(0); Scope(1); Scope(2); });
            Check((bool)p.Get("MeasurementInvalid") && Batch("Frames") == 2 && Batch("DiscardedFrames") == 1 && Batch("ValidFrames") == 1 && metricNames.All(name => Field(p.Get("WindowBatch"), name).Equals(Field(window, name))), "batch_window_" + metricName + "_overflow_preflights_all_metrics_before_any_donation");
        }

        foreach (string counter in new[] { "eligible_frames", "ClockCalls", "AbortedScopes", "ObserverFailures" })
        {
            void CountedFrame()
            {
                Frame(() => {
                    CostFixture.Reset(); p.AfterDraw(p.Drawing, true, false, 0);
                    if (counter == "ObserverFailures") RenderFixture.ThrowClockCall = RenderFixture.ClockCalls + 1;
                    int cookie = Begin(1); End(cookie, counter != "AbortedScopes");
                });
            }
            p.Reset(); CountedFrame();
            object window = p.Get("WindowBatch"), state = p.Get("WindowState"), solid = p.Get("WindowSolid");
            if (counter == "eligible_frames")
            {
                Field(window, "Frames", long.MaxValue); Field(window, "ValidFrames", long.MaxValue);
                Field(state, "Eligible", long.MaxValue); p.Set("WindowState", state);
            }
            else Field(window, counter, long.MaxValue);
            p.Set("WindowBatch", window); CountedFrame();
            Check((bool)p.Get("MeasurementInvalid") && window.Equals(p.Get("WindowBatch")) && solid.Equals(p.Get("WindowSolid")) && Number(p.Get("WindowState"), "Eligible") == Number(state, "Eligible") && Number(p.Get("WindowState"), "SceneSamples") == Number(state, "SceneSamples"), "batch_" + counter + "_counter_overflow_rejects_entire_eligible_scene_pass_batch_donation");
        }
        foreach (string counter in new[] { "ClockCalls", "AbortedScopes" })
        {
            p.Reset(); Frame(() => {
                int cookie = counter == "AbortedScopes" ? Begin(0) : 0;
                object frame = p.Get("frameBatch"); Field(frame, counter, long.MaxValue); p.Set("frameBatch", frame);
                if (counter == "AbortedScopes") End(cookie, false); else Scope(0);
            });
            Check((bool)p.Get("MeasurementInvalid") && Number(p.Get("WindowState"), "Eligible") == 0 && Number(p.Get("WindowState"), "SceneSamples") == 0 && Batch("Frames") == 0 && Batch("ClockCalls") == 0 && Batch("AbortedScopes") == 0 && EmptyMetrics(p.Get("WindowBatch")), "batch_provisional_" + counter + "_unrepresentable_diagnostic_rejects_entire_eligible_donation");
        }

        foreach (bool solidLayer in new[] { true, false })
        {
            void VisitedPass(long visited)
            {
                object pass = p.Enter(solidLayer); Field(pass, "Visited", visited);
                p.Region(ref pass, 0); p.Region(ref pass, 1); p.Region(ref pass, 2); p.Finish(pass);
            }
            p.Reset(); Frame(() => VisitedPass(long.MaxValue));
            Check(p.Layer(solidLayer, "Visited", true) == long.MaxValue && Batch("ValidFrames") == 1 && !(bool)p.Get("MeasurementInvalid"), "batch_late_tile_donation_coherent_saturated_visited_precondition_" + solidLayer);
            Frame(() => {
                VisitedPass(1); Scope(0);
                Check(!(bool)p.Get("MeasurementInvalid") && Number(Field(p.Get("frameBatch"), "End"), "Calls") == 1, "batch_late_tile_donation_valid_until_root_merge_" + solidLayer);
            });
            Check((bool)p.Get("MeasurementInvalid") && Batch("Frames") == 2 && Batch("ValidFrames") == 1 && Batch("DiscardedFrames") == 1 && Batch("ObserverFailures") == 1 && EmptyMetrics(p.Get("WindowBatch")) && p.Layer(solidLayer, "Visited", true) == long.MaxValue, "batch_late_tile_merge_overflow_discards_provisional_scope_without_changing49_saturation_" + solidLayer);
        }
        p.Reset(); Frame();
        long priorEligible = long.MaxValue / 2;
        object sceneState = p.Get("WindowState"), sceneBatch = p.Get("WindowBatch");
        foreach (string field in new[] { "Frames", "Completed", "Eligible", "LastFrameId" }) Field(sceneState, field, priorEligible);
        Field(sceneState, "SceneSamples", priorEligible * 2); Field(sceneState, "Day", priorEligible * 2);
        Field(sceneBatch, "Frames", priorEligible); Field(sceneBatch, "ValidFrames", priorEligible);
        p.Set("WindowState", sceneState); p.Set("WindowBatch", sceneBatch); p.Set("frameId", priorEligible);
        Frame(() => {
            Scope(0);
            Check(!(bool)p.Get("MeasurementInvalid"), "batch_late_scene_donation_healthy_before_second_scene_sample_overflows");
        });
        Check((bool)p.Get("MeasurementInvalid") && Number(p.Get("WindowState"), "SceneSamples") == long.MaxValue && Number(p.Get("WindowState"), "Eligible") == priorEligible + 1 && Batch("Frames") == priorEligible + 1 && Batch("ValidFrames") == priorEligible && Batch("DiscardedFrames") == 1 && Batch("ObserverFailures") == 1 && EmptyMetrics(p.Get("WindowBatch")), "batch_late_scene_merge_overflow_discards_provisional_scope_after_counter_preflight");

        p.Reset(); Frame(() => Scope(1)); object retained = p.Get("WindowBatch");
        var partial = new RenderOutputFixture { FailWrite = 2 }; var originalOut = Console.Out;
        try { Console.SetOut(partial); p.Call("Flush"); } finally { Console.SetOut(originalOut); }
        Check(retained.Equals(p.Get("WindowBatch")) && !(bool)p.Get("finalized") && !partial.ToString().Contains("NX_PROFILE END"), "batch_failed_footer_preserves_exact_unreported_window");
        Save("render-batch-report-recovered.log", false);
        Check(Batch("Frames") == 0 && EmptyMetrics(p.Get("WindowBatch")), "batch_report_retry_clears_retained_window_after_END");
        return new { scenarios, limits = "Actual copied helper IL with deterministic qualified clock/state/output boundaries. Scope ticks are inclusive/exclusive host-fixture values, not GPU execution or performance measurements." };
    }

    private static object AllocationScenarios(RenderProof.Session p)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var frameBegin = p.Runtime.GetMethod("FrameBegin", flags)!.CreateDelegate<Func<int>>();
        var frameEnd = p.Runtime.GetMethod("FrameEnd", flags)!.CreateDelegate<Action<int, bool>>();
        var batchBegin = p.Runtime.GetMethod("BatchBegin", flags)!.CreateDelegate<Func<int, int>>();
        var batchEnd = p.Runtime.GetMethod("BatchEnd", flags)!.CreateDelegate<Action<int, bool>>();
        const int iterations = 10000;
        void Cycle(bool scopes)
        {
            int frame = frameBegin();
            if (scopes)
            {
                int outer = batchBegin(0); int upload = batchBegin(1); batchEnd(upload, true);
                int submit = batchBegin(2); batchEnd(submit, true); batchEnd(outer, true);
            }
            frameEnd(frame, true);
        }
        var rounds = new List<object>();
        foreach (bool scopes in new[] { false, true })
        {
            p.Reset(); RenderFixture.Step = 0;
            for (int i = 0; i < 2000; i++) Cycle(scopes);
            object stack = p.Get("batchStack");
            for (int round = 0; round < 3; round++)
            {
                long clocks = RenderFixture.ClockCalls, bytes = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < iterations; i++) Cycle(scopes);
                bytes = GC.GetAllocatedBytesForCurrentThread() - bytes; clocks = RenderFixture.ClockCalls - clocks;
                p.Check(bytes == 0 && clocks == iterations * (scopes ? 11L : 5L), "warmed_copied_runtime_zero_allocation_exact_clocks_" + scopes + "_" + round);
                p.Check(ReferenceEquals(stack, p.Get("batchStack")) && !(bool)p.Get("MeasurementInvalid"), "warmed_runtime_reuses_protected_root_stack_" + scopes + "_" + round);
                rounds.Add(new { scopes, round, iterations, allocationBytes = bytes, clockCalls = clocks });
            }
        }
        p.Reset(); for (int i = 0; i < 2000; i++) { int cookie = batchBegin(0); batchEnd(cookie, true); }
        long inactiveClocks = RenderFixture.ClockCalls, inactiveBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) { int cookie = batchBegin(0); batchEnd(cookie, true); }
        inactiveBytes = GC.GetAllocatedBytesForCurrentThread() - inactiveBytes; inactiveClocks = RenderFixture.ClockCalls - inactiveClocks;
        p.Check(inactiveBytes == 0 && inactiveClocks == 0, "warmed_inactive_batch_hooks_zero_allocations_zero_clocks");
        return new { rounds, inactiveBytes, inactiveClocks, limits = "Warmed direct delegates to actual emitted candidate runtime. Excludes protected initialization/report allocation and original game allocations. No Switch overhead or speedup claim." };
    }
}
