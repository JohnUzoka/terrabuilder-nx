using System.Reflection;
using System.Text.Json;

// Executes only the serialized candidate-derived Probe.UIRuntime. Fixture boundaries
// supply deterministic clocks/state/output; they are not a replacement runtime.
internal static class UIProfileRuntimeProof
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly string[] MetricFields = { "Calls", "InclusiveTicks", "ExclusiveTicks", "InclusiveMax", "ExclusiveMax" };

    internal static object Run(Type runtime, Type hooks, List<string> checks, string outputDirectory)
    {
        var p = new Session(runtime, checks, outputDirectory);
        p.Check(runtime.FullName == "Probe.UIRuntime" && runtime.Assembly == hooks.Assembly,
            "actual_candidate_derived_runtime_and_behavior_hooks_share_serialized_probe");
        Timing(p);
        State(p);
        BoundaryFaults(p);
        Allocations(p);
        Reports(p);
        var candidateField = typeof(UIProfileFixture).GetField("CandidateSha256", Static);
        string candidateSha256 = (string?)candidateField?.GetValue(null) ?? "";
        p.Check(candidateSha256.Length == 64, "report_manifest_has_actual_candidate_sha256");
        var manifest = new { candidateSha256, accepted = p.Accepted, rejected = p.Rejected };
        File.WriteAllText(Path.Combine(outputDirectory, "report-manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        var result = new
        {
            passed = true,
            candidateSha256,
            runtime = runtime.FullName,
            probeMvid = runtime.Module.ModuleVersionId,
            checks = checks.Skip(p.FirstCheck).ToArray(),
            receipts = p.Receipts,
            reports = manifest,
            limits = new[] {
                "Actual candidate helper IL is executed after the Behavior mapper redirects game state, clock and output boundaries to deterministic host fixtures.",
                "Raw deterministic ticks prove accounting and failure paths, not elapsed game time, observer overhead on Switch, GPU work or performance gains.",
                "State is sampled only at Draw boundaries; intermediate toggle-and-return, effective render transforms and unsupported capture modes are not inferred.",
                "Warm allocation measurement covers direct generated runtime frame/scope hooks with reporting disabled, not full game methods, report formatting or GPU work.",
                "Partial raw packets are retained as rejected evidence; successful retries are separate input files rather than silently repaired captures."
            }
        };
        File.WriteAllText(Path.Combine(outputDirectory, "runtime-proof.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    static void Timing(Session p)
    {
        p.Reset(); p.Call("InitializeTiming");
        int clocks = UIProfileFixture.ClockCalls;
        for (int i = 0; i < 128; i++) { p.Exit(p.Enter(10), true); p.Exit(p.EnterLayer(null!), true); }
        p.Check(UIProfileFixture.ClockCalls == clocks && !p.Bool("Active"), "unselected_inner_and_null_layer_hooks_use_zero_clocks");
        p.Select();
        clocks = UIProfileFixture.ClockCalls;
        p.Check(p.Enter(10) == 0 && p.EnterLayer(null!) == 0 && UIProfileFixture.ClockCalls == clocks,
            "selected_frame_outside_ui_inner_hooks_use_zero_clocks");
        p.Call("EndSample", true);
        p.Check(p.Valid() && p.Metric(false, 0, 0, "Calls") == 1 && p.Metric(false, 0, 1, "Calls") == 0,
            "frame_without_ui_is_valid_and_unused_ui_stays_zero");
        p.AssertPartitions("no_ui");

        p.Reset(); p.Select();
        int ui = p.Enter(1), outer = p.Enter(10), same = p.Enter(10), layout = p.Enter(11);
        p.Exit(layout, true); p.Exit(same, true); int format = p.Enter(9); p.Exit(format, true); p.Exit(outer, true); p.Exit(ui, true); p.Call("EndSample", true);
        p.Check(p.Valid(), "nested_same_and_different_category_sample_valid");
        p.Check(p.Metric(false, 0, 10, "Calls") == 2 && p.Metric(false, 0, 10, "InclusiveTicks") == 100 &&
            p.Metric(false, 0, 10, "ExclusiveTicks") == 50 && p.Metric(false, 0, 11, "InclusiveTicks") == 10 &&
            p.Metric(false, 0, 9, "InclusiveTicks") == 10 && p.Metric(false, 0, 0, "InclusiveTicks") == 110 &&
            UIProfileFixture.ClockCalls == 14, "nested_exact_ticks_and_outside_root_clock_pair");
        p.AssertPartitions("nested");
        p.Check((bool)p.Call("CommitSample", 0)! && p.Metric(true, 0, 10, "Calls") == 2, "completed_nested_sample_merges_once");
        object[] once = p.Metrics("WindowMetrics");
        p.Check(!(bool)p.Call("CommitSample", 0)! && p.Bool("MeasurementInvalid") && p.MetricsEqual(once, "WindowMetrics"),
            "duplicate_commit_is_observable_and_never_donates_twice");

        var layerNames = new[] { "Vanilla: Inventory", "Vanilla: Hotbar", "Vanilla: Mouse Over", "Vanilla: Mouse Text", "Vanilla: Any Other", null };
        for (int i = 0; i < layerNames.Length; i++)
        {
            p.Reset(); p.Select(); ui = p.Enter(1); int layer = p.EnterLayer(new UIProofLayer { Name = layerNames[i] });
            p.ExitLayer(layer, true, false); p.Exit(ui, true); p.Call("EndSample", true);
            p.Check(p.Valid() && p.Metric(false, 0, Math.Min(i + 2, 6), "Calls") == 1 && p.Number("FrameStoppedLayers") == 1 && !p.Bool("FrameScopeAborted"),
                "layer_name_mapping_and_legitimate_false_stop_" + i);
        }
        p.Reset(); p.Select(); ui = p.Enter(1);
        var depthCookies = new int[62]; for (int i = 0; i < depthCookies.Length; i++) depthCookies[i] = p.Enter(10);
        for (int i = depthCookies.Length - 1; i >= 0; i--) p.Exit(depthCookies[i], true);
        p.Exit(ui, true); p.Call("EndSample", true);
        p.Check(p.Valid() && p.Number("MaxObservedDepth") == 64 && p.Number("DepthOverflows") == 0, "depth64_boundary_is_valid");
        p.AssertPartitions("depth64");

        void Invalid(string name, Action body)
        {
            p.Reset(); p.Select(); int u = p.Enter(1); body(); p.Exit(u, true); p.Call("EndSample", true);
            p.Check(!p.Valid() && p.Bool("MeasurementInvalid") && p.AllZero("WindowMetrics"), "invalid_" + name + "_observable_without_donation");
        }
        Invalid("mismatched_cookie", () => { int c = p.Enter(10); p.Exit(c + 123, true); });
        Invalid("negative_cookie", () => p.Exit(-1, true));
        Invalid("unclosed_child", () => p.Enter(11));
        Invalid("repeated_ui", () => p.Enter(1));
        Invalid("invalid_metric_id", () => p.Enter(12));
        Invalid("frame_metric_reentry", () => p.Enter(0));
        Invalid("null_sampled_layer", () => p.EnterLayer(null!));
        Invalid("depth65", () => { for (int i = 0; i < 63; i++) p.Enter(10); });
        Invalid("cookie_overflow", () => { p.Set("nextCookie", int.MaxValue); p.Enter(10); });
        Invalid("negative_cookie_counter", () => { p.Set("nextCookie", -1); p.Enter(10); });
        Invalid("scope_counter_overflow", () => { int c = p.Enter(11); p.SetMetric("FrameMetrics", 11, "Calls", long.MaxValue); p.Exit(c, true); });
        Invalid("child_tick_overflow", () => { p.SetArrayStruct("timingStack", 1, "ChildTicks", long.MaxValue); int c = p.Enter(11); p.Exit(c, true); });
        Invalid("negative_scope_start", () => { int c = p.Enter(10); p.SetArrayStruct("timingStack", 2, "Start", -1L); p.Exit(c, true); });
        Invalid("malformed_arrays", () => p.Set("FrameMetrics", null));

        foreach (string fault in new[] { "throw", "negative", "backward" })
        for (int position = fault == "backward" ? 2 : 1; position <= 8; position++)
        {
            p.Reset(); p.Call("InitializeTiming");
            UIProfileFixture.ClockScript = Enumerable.Range(0, 64).Select(i => 100000L + i * 10).ToArray();
            if (fault == "throw") UIProfileFixture.ThrowClockAt = position;
            else UIProfileFixture.ClockScript[position - 1] = fault == "negative" ? -1 : UIProfileFixture.ClockScript[position - 2] - 1;
            p.Set("SampleSelected", true); p.Call("BeginSample"); ui = p.Enter(1); int child = p.Enter(11); p.Exit(child, true); p.Exit(ui, true); p.Call("EndSample", true);
            p.Check(!p.Valid() && p.Bool("MeasurementInvalid") && p.Number("timingDepth") == 0 && !p.Bool("Active") && p.AllZero("WindowMetrics"),
                "all_timing_clock_positions_" + fault + "_" + position);
        }
        foreach (string field in MetricFields.Skip(1))
        {
            p.Reset(); p.Select(); ui = p.Enter(1); p.Exit(ui, true); p.Call("EndSample", true);
            p.SetMetric("FrameMetrics", 7, field, 1);
            p.Check(!p.Valid() && p.Bool("MeasurementInvalid"), "unused_metric_corruption_rejected_" + field);
        }
        p.Reset(); p.Select(); ui = p.Enter(1); int last = p.Enter(11); p.Exit(last, true); p.Exit(ui, true); p.Call("EndSample", true);
        p.SetMetric("WindowMetrics", 11, "Calls", long.MaxValue); p.SetMetric("WindowMetrics", 11, "InclusiveTicks", 1); p.SetMetric("WindowMetrics", 11, "ExclusiveTicks", 1);
        p.SetMetric("WindowMetrics", 11, "InclusiveMax", 1); p.SetMetric("WindowMetrics", 11, "ExclusiveMax", 1);
        object[] before = p.Metrics("WindowMetrics");
        p.Check(!(bool)p.Call("CommitSample", 0)! && p.Bool("MeasurementInvalid") && p.MetricsEqual(before, "WindowMetrics"), "last_metric_overflow_preflight_prevents_partial_window_merge");
        foreach (var values in new[] { new object[] { long.MaxValue, 1L }, new object[] { 0L, -1L }, new object[] { -1L, 0L } })
        {
            p.Reset(); p.Call("Add", values);
            p.Check(p.Bool("MeasurementInvalid") && (long)values[0] >= 0, "counter_add_saturates_or_rejects_without_wrap_" + values[1]);
        }
        p.Reset(); p.Select(); ui = p.Enter(1); int aborted = p.Enter(10); p.Exit(aborted, false); p.Exit(ui, true); p.Call("EndSample", true);
        p.Check(!p.Valid() && p.Bool("FrameScopeAborted") && !p.Bool("MeasurementInvalid") && p.Number("ScopeFailures") == 0 && p.Metric(false, 0, 10, "Calls") == 0,
            "caught_original_scope_exception_is_abort_not_observer_fault");
    }

    static void State(Session p)
    {
        p.Reset();
        for (int frame = 0; frame < 33; frame++) for (int cohort = 0; cohort < 3; cohort++)
        {
            UIProofMain.playerInventory = cohort != 0; UIProofMain.gamePaused = cohort == 2;
            bool selected = false; p.Frame(() => { selected = p.Bool("SampleSelected"); p.UI(); });
            p.Check(selected == (frame % 16 == 0), "independent_phase_cohort_" + cohort + "_frame_" + frame);
        }
        for (int cohort = 0; cohort < 3; cohort++)
            p.Check(p.Cohort(cohort, "Frames") == 33 && p.Cohort(cohort, "Selected") == 3 && p.Cohort(cohort, "Valid") == 3 && p.Scene(cohort, "Samples") == 66,
                "cohort_exact_frames_samples_and_all_boundary_scene_counts_" + cohort);
        p.Accounting("interleaved_cohorts"); p.Flush(); p.Save("valid-cohorts.log");

        var excluded = new (string Name, Action Set, string Counter)[] {
            ("menu", () => UIProofMain.gameMenu = true, "Menu"), ("paused_without_inventory", () => UIProofMain.gamePaused = true, "Paused"),
            ("map", () => UIProofMain.mapFullscreen = true, "Map"), ("dead", () => UIProofMain.player![0].dead = true, "Dead"),
            ("ghost", () => UIProofMain.player![0].ghost = true, "Ghost"), ("spectator", () => UIProofMain.player![0].spectating = 0, "Spectator"),
            ("null_players", () => UIProofMain.player = null, "InvalidPlayer"), ("negative_player", () => UIProofMain.myPlayer = -1, "InvalidPlayer"),
            ("out_of_range_player", () => UIProofMain.myPlayer = 4, "InvalidPlayer"), ("null_player", () => UIProofMain.player![0] = null!, "InvalidPlayer"),
            ("nan_player", () => UIProofMain.player![0].position.X = float.NaN, "InvalidPlayer"),
            ("infinite_camera", () => UIProofMain.screenPosition.Y = float.PositiveInfinity, "InvalidScene"),
            ("zero_zoom", () => UIProofMain.GameZoomTarget = 0, "InvalidScene"), ("nan_ui_scale", () => UIProofMain.UIScaleValue = float.NaN, "InvalidScene"),
            ("zero_viewport", () => UIProofMain.screenWidth = 0, "InvalidScene"), ("nan_time", () => UIProofMain.time = double.NaN, "InvalidScene") };
        foreach (var item in excluded)
        {
            p.Reset(); item.Set(); bool selected = true; p.Frame(() => { selected = p.Bool("SampleSelected"); p.UI(); });
            p.Check(!selected && p.State("Eligible") == 0 && p.State(item.Counter) == 1 && p.State("CaptureFailures") == 0 && p.AllZero("WindowMetrics"), "raw_state_exclusion_" + item.Name);
            p.Accounting(item.Name);
        }
        p.Reset(); UIProofMain.autoPause = true; p.Frame(p.UI);
        p.Check(p.State("AutoPause") == 1 && p.State("Eligible") == 1 && p.Cohort(0, "Valid") == 1, "raw_autopause_does_not_imply_paused");
        p.Reset(); UIProofMain.HoverItem = UIProofMain.mouseItem = null; p.Frame(p.UI);
        p.Check(p.State("Eligible") == 1 && p.Scene(0, "FirstHoverType") == -1 && p.Scene(0, "LastMouseItemType") == -1, "null_items_are_raw_minus_one_not_invalid_player");
        p.Reset(); p.Frame(() => { p.UI(); UIProofMain.player![0].position.X += 10; UIProofMain.screenPosition.Y += 20; UIProofMain.mouseX += 4; UIProofMain.time += 10; UIProofMain.HoverItem!.type = 7; UIProofMain.HoverItem.prefix = 3; UIProofMain.mouseItem!.type = 8; });
        p.Check(p.State("Eligible") == 1 && p.State("Changed") == 0 && p.Cohort(0, "Valid") == 1 && p.Scene(0, "Samples") == 2 &&
            p.Scene(0, "PlayerMoved") == 1 && p.Scene(0, "CameraMoved") == 1 && p.Scene(0, "MouseMoved") == 1 && p.Scene(0, "HoverChanged") == 1 && p.Scene(0, "MouseItemChanged") == 1,
            "movement_hover_and_item_changes_recorded_without_rejecting_ui_work");
        p.Accounting("movement"); p.Flush(); p.Save("valid-normal.log");
        var changes = new (string Name, Action Set)[] {
            ("inventory", () => UIProofMain.playerInventory = true), ("menu", () => UIProofMain.gameMenu = true),
            ("paused", () => UIProofMain.gamePaused = true), ("autopause", () => UIProofMain.autoPause = true),
            ("zoom", () => UIProofMain.GameZoomTarget = 2), ("ui_scale", () => UIProofMain.UIScaleValue = 2),
            ("width", () => UIProofMain.screenWidth = 800), ("height", () => UIProofMain.screenHeight = 600),
            ("frame_skip", () => UIProofMain.FrameSkipMode = 1), ("day", () => UIProofMain.dayTime = false),
            ("player_id", () => { UIProofMain.player = new[] { new UIProofPlayer(), new UIProofPlayer() }; UIProofMain.myPlayer = 1; }) };
        foreach (var change in changes)
        {
            p.Reset(); p.Frame(() => { p.UI(); change.Set(); });
            p.Check(p.State("Changed") == 1 && p.State("Eligible") == 0 && p.State("SelectedAttempts") == 1 && p.State("DiscardedSelected") == 1 && p.AllZero("WindowMetrics"), "boundary_change_discard_" + change.Name);
            p.Accounting("changed_" + change.Name);
        }
        p.Reset(); p.Frame(() => { UIProofMain.playerInventory = true; UIProofMain.playerInventory = false; });
        p.Check(p.State("Eligible") == 1 && p.State("Changed") == 0, "boundary_only_toggle_return_attribution_limit_demonstrated");
        p.Reset(); p.Frame(); p.Check(p.Cohort(0, "NoUI") == 1 && p.Cohort(0, "Valid") == 1, "no_ui_sample_has_explicit_counter");
        p.Flush(); p.Save("valid-unused.log");

        p.Reset(); p.Frame(p.UI, false);
        p.Check(p.State("Aborted") == 1 && p.State("Eligible") == 0 && p.State("DiscardedSelected") == 1 && p.AllZero("WindowMetrics"), "original_frame_abort_discards_selected_timings");
        p.Accounting("frame_abort");
        p.Reset(); p.Frame(() => { int u = p.Enter(1), c = p.Enter(10); p.Exit(c, false); p.Exit(u, true); });
        p.Check(p.State("Completed") == 1 && p.Cohort(0, "Aborted") == 1 && p.Cohort(0, "Valid") == 0 && !p.Bool("MeasurementInvalid") && p.AllZero("WindowMetrics"), "caught_child_exception_aborts_cost_sample_not_game_frame");
        p.Accounting("caught_exception"); p.Flush(); p.Save("valid-aborted-scope.log");
        p.Reset(); p.Frame(() => { int u = p.Enter(1), c = p.EnterLayer(new UIProofLayer { Name = "Vanilla: Inventory" }); p.ExitLayer(c, true, false); p.Exit(u, true); });
        p.Check(p.Cohort(0, "StoppedUI") == 1 && p.Cohort(0, "Valid") == 1, "false_layer_result_counts_stopped_valid_sample");
        p.Accounting("stopped"); p.Flush(); p.Save("valid-stopped.log");

        p.Reset(); int root = p.Begin(), ui = p.Enter(1), text = p.Enter(10); int beforeClocks = UIProfileFixture.ClockCalls;
        int nested = p.Begin();
        p.Check(!p.Bool("Active") && !p.Bool("SampleSelected"), "reentrant_Draw_masks_collection");
        p.UI(); p.End(nested, false);
        p.Check(p.Bool("Active") && p.Bool("SampleSelected") && p.Number("FrameDepth") == 1 && UIProfileFixture.ClockCalls == beforeClocks,
            "reentrant_Draw_restores_parent_flags_and_spends_zero_clocks");
        p.Exit(text, true); p.Exit(ui, true); p.End(root, true);
        p.Check(p.State("Attempts") == 1 && p.Cohort(0, "Valid") == 1 && p.Number("reentrantFrames") == 1 && p.Number("FrameDepth") == 0, "reentrant_Draw_preserves_parent_stack_and_single_root_accounting");
        p.Reset(); p.Frame(); Exception? threadError = null; bool excludedThread = false; int threadClocks = -1;
        var thread = new Thread(() => { try { int d = p.Begin(); excludedThread = !p.Bool("SampleSelected") && !p.Bool("Active"); p.UI(); p.End(d, true); threadClocks = UIProfileFixture.ClockCalls; } catch (Exception e) { threadError = e; } });
        thread.Start(); thread.Join();
        p.Check(threadError == null && excludedThread && threadClocks == 0 && p.State("Attempts") == 1 && p.Number("ignoredThreads") == 1 && p.Number("FrameDepth") == 0,
            "nonowner_thread_ignored_without_clocks_or_parent_state_corruption");

        foreach (bool atBegin in new[] { true, false }) foreach (int offset in new[] { 1, 2 }) foreach (string fault in new[] { "throw", "negative", "backward" })
        {
            p.Reset(); p.Frame();
            int d = atBegin ? -1 : p.Begin();
            // This frame is unsampled (phase1), so end clocks begin at its snapshot.
            int target = UIProfileFixture.ClockCalls + offset;
            if (fault == "throw") UIProfileFixture.ThrowClockAt = target;
            else
            {
                long tick = UIProfileFixture.Tick;
                UIProfileFixture.ClockScript = new[] { tick + 10, tick + 20 };
                UIProfileFixture.ClockScript[offset - 1] = fault == "negative" ? -1 : tick - 1;
                UIProfileFixture.ClockIndex = 0;
            }
            if (atBegin) p.Frame(); else p.End(d, true);
            string name = "capture_" + (atBegin ? "begin" : "end") + "_" + fault + "_" + offset;
            p.Check(p.State("Attempts") == 2 && p.State("Frames") == 1 && p.State("CaptureFailures") == 1 && p.Number("snapshotCalls") == 2 && p.Bool("MeasurementInvalid"), name + "_pair_atomicity");
            p.Check(p.Number("FrameDepth") == 0 && !p.Bool("Active") && !p.Bool("SampleSelected"), name + "_restoration");
            UIProfileFixture.ClockScript = null; UIProfileFixture.ThrowClockAt = -1; p.Frame(); p.Accounting(name); p.Flush(); p.Save("invalid-" + name + ".log");
        }
        foreach (int snapshot in new[] { 1, 2 })
        {
            p.Reset(); UIProofMain.ThrowSnapshotAt = snapshot; p.Frame(p.UI);
            p.Check(p.State("Attempts") == 1 && p.State("Frames") == 0 && p.State("CaptureFailures") == 1 && p.Number("snapshotCalls") == 0 && p.AllZero("WindowMetrics") && p.Bool("MeasurementInvalid"), "getter_capture_failure_pair_atomicity_" + snapshot);
            p.Accounting("getter_failure_" + snapshot); p.Flush(); p.Save("invalid-getter-" + snapshot + ".log");
        }
    }

    static void Allocations(Session p)
    {
        p.Reset(); UIProfileFixture.CaptureOutput = false; UIProfileFixture.Step = 0;
        for (int i = 0; i < 4096; i++) { int d = p.Begin(), u = p.Enter(1), t = p.Enter(10); p.Exit(t, true); p.Exit(u, true); p.End(d, true); }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) { int d = p.Begin(), u = p.Enter(1), t = p.Enter(10); p.Exit(t, true); p.Exit(u, true); p.End(d, true); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        p.Check(bytes == 0 && UIProfileFixture.WriteCalls == 0 && p.State("Frames") == 8192 && p.Cohort(0, "Valid") == 512 && !p.Bool("MeasurementInvalid"), "zero_warmed_frame_and_scope_hook_allocations_reporting_disabled");
        p.Receipts.Add(new { kind = "allocation", warmFrames = 4096, measuredFrames = 4096, allocatedBytes = bytes, sampledFramesAcrossBothLoops = 512, clockStep = 0, writes = UIProfileFixture.WriteCalls });
    }

    static void BoundaryFaults(Session p)
    {
        p.Reset(); UIProofMain.gameMenu = true;
        for (int i = 0; i < 17; i++) p.Frame(p.UI);
        UIProofMain.gameMenu = false; bool selected = false;
        p.Frame(() => { selected = p.Bool("SampleSelected"); p.UI(); });
        p.Check(selected && p.State("SelectedAttempts") == 1 && p.State("Menu") == 17, "excluded_frames_do_not_advance_eligible_cohort_phase");
        p.Frame(() => { int clocks = UIProfileFixture.ClockCalls; p.UI(); p.Check(UIProfileFixture.ClockCalls == clocks, "unsampled_eligible_inner_scopes_spend_zero_clocks"); });
        p.Accounting("excluded_phase"); p.Flush(); p.Save("valid-excluded-phase.log");
        p.Reset(); UIProofMain.gameMenu = UIProofMain.gamePaused = UIProofMain.playerInventory = UIProofMain.mapFullscreen = UIProofMain.autoPause = true;
        UIProofMain.player![0].dead = UIProofMain.player[0].ghost = true; UIProofMain.player[0].spectating = 0;
        p.Frame();
        p.Check(p.State("FirstFlags") == 383 && p.State("LastFlags") == 383 && p.State("FlagsOr") == 383 &&
            new[] { "Menu", "Paused", "Inventory", "Map", "AutoPause", "Dead", "Ghost", "Spectator" }.All(f => p.State(f) == 1),
            "overlapping_raw_flags_are_not_a_disjoint_partition");
        p.Accounting("overlapping_flags"); p.Flush(); p.Save("valid-overlapping-exclusions.log");

        foreach (string fault in new[] { "throw", "negative" })
        {
            p.Reset();
            if (fault == "throw") UIProfileFixture.ThrowClockAt = 1;
            else UIProfileFixture.ClockScript = new[] { -1L };
            p.Frame();
            p.Check(p.Bool("MeasurementInvalid") && p.State("Attempts") == 1 && p.State("CaptureFailures") == 1 && p.State("Frames") == 0 && p.Number("FrameDepth") == 0,
                "initialization_clock_failure_restores_root_" + fault);
            UIProfileFixture.ThrowClockAt = -1; UIProfileFixture.ClockScript = null; p.Frame(p.UI); p.Accounting("initialization_" + fault);
            p.Flush(); p.Save("invalid-initialization-" + fault + ".log");
        }
        for (int offset = 1; offset <= 2; offset++)
        {
            p.Reset(); int d = p.Begin();
            // EndSample reads root-end plus a bare clock pair before end capture.
            UIProfileFixture.ThrowClockAt = UIProfileFixture.ClockCalls + 3 + offset; p.End(d, true);
            p.Check(p.State("SelectedAttempts") == 1 && p.State("DiscardedSelected") == 1 && p.State("CaptureFailures") == 1 && p.Number("snapshotCalls") == 0 && p.AllZero("WindowMetrics"),
                "selected_end_capture_failure_discards_complete_timing_" + offset);
            p.Accounting("selected_capture_" + offset); UIProfileFixture.ThrowClockAt = -1; p.Flush(); p.Save("invalid-selected-capture-" + offset + ".log");
        }
        foreach (bool rootStart in new[] { true, false })
        {
            p.Reset(); int target = rootStart ? 4 : 8;
            UIProfileFixture.OnClock = () =>
            {
                if (UIProfileFixture.ClockCalls != target) return;
                UIProfileFixture.ClockScript = new[] { UIProfileFixture.Tick - 1 };
                UIProfileFixture.ClockIndex = 0;
            };
            p.Frame(); UIProfileFixture.OnClock = null; UIProfileFixture.ClockScript = null;
            p.Check(p.Bool("MeasurementInvalid") && p.AllZero("WindowMetrics") &&
                (rootStart ? p.Cohort(0, "Invalid") == 1 : p.State("CaptureFailures") == 1),
                "cross_boundary_positive_clock_regression_" + (rootStart ? "snapshot_to_root" : "clock_pair_to_snapshot"));
            p.Accounting("cross_boundary_" + rootStart); p.Flush(); p.Save("invalid-cross-boundary-" + rootStart + ".log");
        }
        foreach (string field in new[] { "snapshotCalls", "snapshotTicks", "frameId" })
        {
            p.Reset(); int d = p.Begin(); p.Set(field, long.MaxValue); p.End(d, true);
            p.Check(p.Bool("MeasurementInvalid") && p.State("Frames") == 0 && p.State("CaptureFailures") == 1 && p.Number(field) == long.MaxValue && p.AllZero("WindowMetrics"),
                "capture_counter_overflow_preserved_not_repaired_" + field);
        }
        p.Reset(); int root = p.Begin(); p.SetArrayStruct("Cohorts", 0, "ClockTicks", long.MaxValue); p.End(root, true);
        p.Check(p.Bool("MeasurementInvalid") && p.Cohort(0, "Selected") == 1 && p.Cohort(0, "Invalid") == 1 && p.Cohort(0, "ClockTicks") == long.MaxValue && p.AllZero("WindowMetrics"),
            "cohort_clock_counter_overflow_prevents_metric_donation_without_repair");

        foreach (string fault in new[] { "throw", "negative", "backward" })
        {
            p.Reset(); p.Frame(); int d = p.Begin();
            int target = UIProfileFixture.ClockCalls + 3; long floor = UIProfileFixture.Tick;
            UIProfileFixture.OnClock = () =>
            {
                if (UIProfileFixture.ClockCalls != target) return;
                if (fault == "throw") UIProfileFixture.ThrowClockAt = target;
                else { UIProfileFixture.ClockScript = new[] { fault == "negative" ? -1L : floor - 1 }; UIProfileFixture.ClockIndex = 0; }
            };
            p.End(d, true); UIProfileFixture.OnClock = null; UIProfileFixture.ThrowClockAt = -1; UIProfileFixture.ClockScript = null;
            p.Check(p.Bool("MeasurementInvalid") && p.State("Frames") == 2 && p.Number("snapshotCalls") == 4 && p.Number("FrameDepth") == 0, "periodic_check_clock_failure_keeps_complete_frame_" + fault);
            p.Accounting("periodic_check_" + fault); p.Flush(); p.Save("invalid-periodic-check-" + fault + ".log");
        }
        foreach (string counter in new[] { "reportTicks", "reportAttempts", "interval" })
        {
            p.Reset(); p.Frame(); p.Set(counter, long.MaxValue); p.Flush();
            p.Check(p.Bool("MeasurementInvalid") && !p.HasEnd && p.State("Frames") == 1 && !p.Bool("finalized") && p.Number(counter) == long.MaxValue,
                "publish_counter_overflow_retains_data_and_corrupted_raw_counter_" + counter);
            p.Save("partial-counter-" + counter + ".log", reject: true);
        }
        foreach (string fault in new[] { "throw", "negative", "backward" })
        {
            p.Reset(); p.Frame(); UIProfileFixture.ThrowWriteAt = 1; int baseClocks = UIProfileFixture.ClockCalls;
            UIProfileFixture.OnClock = () =>
            {
                if (UIProfileFixture.ClockCalls != baseClocks + 3) return;
                if (fault == "throw") UIProfileFixture.ThrowClockAt = UIProfileFixture.ClockCalls;
                else { UIProfileFixture.ClockScript = new[] { fault == "negative" ? -1L : 0L }; UIProfileFixture.ClockIndex = 0; }
            };
            p.Flush();
            p.Check(p.Bool("MeasurementInvalid") && !p.HasEnd && p.State("Frames") == 1 && p.Number("reportFailures") == 1 && !p.Bool("reporting"),
                "failed_output_recovery_cost_clock_" + fault);
            p.Save("partial-recovery-clock-" + fault + ".log", reject: true);
            UIProfileFixture.OnClock = null; UIProfileFixture.ClockScript = null; UIProfileFixture.ThrowClockAt = UIProfileFixture.ThrowWriteAt = -1;
            p.ClearOutput(); p.Flush(); p.Check(p.HasEnd, "failed_output_recovery_clock_final_retry_" + fault); p.Save("invalid-recovery-clock-" + fault + ".log");
        }
        p.Reset(); p.Frame(); UIProfileFixture.Tick += UIProfileFixture.Frequency() * 5; p.Frame();
        int writes = UIProfileFixture.WriteCalls;
        UIProfileFixture.ThrowClockAt = UIProfileFixture.ClockCalls + 5; p.Frame();
        p.Check(p.Bool("MeasurementInvalid") && p.Bool("reportFloorPending") && UIProfileFixture.WriteCalls == writes,
            "deferred_cooldown_anchor_clock_failure_cannot_trigger_report");
        UIProfileFixture.ThrowClockAt = -1; p.Flush(); p.Check(p.Rows.Count(r => r.StartsWith("NX_PROFILE END ")) == 2, "final_flush_bypasses_pending_failed_cooldown_anchor"); p.Save("invalid-cooldown-anchor-final.log");
    }

    static void Reports(Session p)
    {
        p.Reset(); p.Frame(p.UI); p.Flush(); string normal = p.Output;
        p.Check(p.Rows.Length == 46 && p.Rows[0].StartsWith("NX_PROFILE BEGIN ") && p.Rows[^1].StartsWith("NX_PROFILE END "), "actual_runtime_compact_46_row_report");
        p.Check(p.State("Frames") == 0 && p.Bool("finalized"), "successful_END_clears_window_and_finalizes");
        int calls = UIProfileFixture.ClockCalls, writes = UIProfileFixture.WriteCalls; p.Flush();
        p.Check(p.Output == normal && UIProfileFixture.ClockCalls == calls && UIProfileFixture.WriteCalls == writes, "successful_final_flush_is_idempotent_without_extra_clocks");
        p.Reset(); p.Frame(p.UI); p.Flush(); p.Check(p.Output == normal, "actual_runtime_serialized_report_deterministic");
        p.Save("valid-deterministic.log");

        p.Reset(); p.Frame(); int reportStart = UIProfileFixture.ClockCalls; p.Flush(); int reportClocks = UIProfileFixture.ClockCalls - reportStart;
        int reportWrites = UIProfileFixture.WriteCalls;
        p.Check(reportClocks == 4, "four_publish_relevant_clocks_and_none_after_END");
        p.Receipts.Add(new { kind = "report_boundaries", reportClocks, reportWrites, rows = p.Rows.Length });
        for (int offset = 1; offset <= reportClocks; offset++)
        {
            p.Reset(); p.Frame(); p.Set("Active", true); p.Set("SampleSelected", true);
            UIProfileFixture.ThrowClockAt = UIProfileFixture.ClockCalls + offset; p.Flush();
            p.Check(!p.HasEnd && p.State("Frames") == 1 && !p.Bool("finalized") && p.Bool("MeasurementInvalid") && !p.Bool("reporting") && p.Bool("Active") && p.Bool("SampleSelected"), "report_clock_throw_retains_data_and_saved_flags_" + offset);
            p.Save("partial-clock-" + offset + ".log", reject: true);
            p.ClearOutput(); UIProfileFixture.ThrowClockAt = -1; p.Flush();
            p.Check(p.HasEnd && p.Bool("finalized") && p.State("Frames") == 0 && p.Number("reportFailures") >= 1, "report_clock_throw_retry_reports_failure_" + offset);
            p.Save("invalid-clock-recovered-" + offset + ".log");
        }
        foreach (string fault in new[] { "negative", "backward" }) for (int offset = 1; offset <= reportClocks; offset++)
        {
            p.Reset(); p.Frame(); long tick = UIProfileFixture.Tick;
            UIProfileFixture.ClockScript = Enumerable.Range(1, reportClocks).Select(i => tick + 10 * i).ToArray();
            UIProfileFixture.ClockScript[offset - 1] = fault == "negative" ? -1 : tick + 10 * (offset - 1) - 1;
            UIProfileFixture.ClockIndex = 0; int start = UIProfileFixture.ClockCalls; p.Flush();
            p.Check(p.Bool("MeasurementInvalid") && !p.Bool("reporting") && UIProfileFixture.ClockCalls - start <= reportClocks,
                "report_" + fault + "_clock_" + offset + "_visible_without_post_END_clock");
            if (p.HasEnd)
            {
                p.Check(p.Rows.Single(r => r.StartsWith("NX_PROFILE REPORT_COST ")).Contains("measurement_invalid=1"), "report_footer_exposes_clock_fault_" + fault + offset);
                p.Save("invalid-report-" + fault + "-" + offset + ".log");
            }
            else
            {
                p.Check(p.State("Frames") == 1 && !p.Bool("finalized"), "report_clock_invalidity_retains_unpublished_window_" + fault + offset);
                p.Save("partial-report-" + fault + "-" + offset + ".log", reject: true);
                p.ClearOutput(); UIProfileFixture.Tick = Math.Max(UIProfileFixture.Tick, UIProfileFixture.ClockScript!.Max()); UIProfileFixture.ClockScript = null;
                p.Flush(); p.Check(p.HasEnd, "report_clock_invalidity_retry_" + fault + offset); p.Save("invalid-report-retry-" + fault + "-" + offset + ".log");
            }
        }
        for (int offset = 1; offset <= reportWrites; offset++)
        {
            p.Reset(); p.Frame(); UIProfileFixture.ThrowWriteAt = offset; p.Flush();
            p.Check(!p.HasEnd && p.State("Frames") == 1 && !p.Bool("finalized") && p.Number("reportFailures") == 1 && p.Bool("MeasurementInvalid"), "output_failure_retains_unreported_counts_" + offset);
            p.Save("partial-output-" + offset + ".log", reject: true);
            p.ClearOutput(); UIProfileFixture.ThrowWriteAt = -1; p.Flush();
            p.Check(p.HasEnd && p.State("Frames") == 0 && p.Number("reportFailures") == 1, "output_failure_recovery_preserves_failure_receipt_" + offset); p.Save("invalid-output-recovered-" + offset + ".log");
        }
        p.Reset(); p.Frame(); UIProfileFixture.Tick += UIProfileFixture.Frequency() * 5; p.Frame();
        p.Check(p.HasEnd && p.Output.Contains(" final=0 ") && p.State("Frames") == 0, "periodic_report_before_empty_final");
        int periodicWrites = UIProfileFixture.WriteCalls; p.Flush();
        p.Check(UIProfileFixture.WriteCalls == periodicWrites + reportWrites && p.Rows.Count(r => r.StartsWith("NX_PROFILE END ")) == 2 && p.State("Frames") == 0 && p.Number("snapshotCalls") == 0, "empty_final_emits_no_fake_frames_or_snapshots");
        string periodicFinal = p.Output; p.Flush(); p.Check(p.Output == periodicFinal, "empty_final_flush_idempotent"); p.Save("valid-periodic-empty-final.log");
        p.Reset(); p.Frame(); UIProfileFixture.Tick += UIProfileFixture.Frequency() * 5;
        UIProfileFixture.OnWrite = n => { if (n == reportWrites) UIProfileFixture.Tick += UIProfileFixture.Frequency() * 10; };
        p.Frame(); p.Check(UIProfileFixture.WriteCalls == reportWrites, "slow_footer_first_report_complete");
        long startTick = p.Number("windowStart"); p.Frame();
        p.Check(UIProfileFixture.WriteCalls == reportWrites && p.Number("windowStart") == startTick, "slow_footer_next_root_anchors_cooldown_without_moving_window_start");
        UIProfileFixture.Tick += UIProfileFixture.Frequency() * 5 - 10000; p.Frame(); p.Check(UIProfileFixture.WriteCalls == reportWrites, "slow_footer_less_than_five_seconds_after_anchor_no_retry");
        UIProfileFixture.Tick += 10000; p.Frame(); p.Check(UIProfileFixture.WriteCalls == reportWrites * 2, "slow_footer_five_seconds_after_anchor_allows_report");
        p.Flush(); p.Save("valid-slow-footer.log");
        p.Reset(); p.Frame(); UIProfileFixture.ThrowWriteAt = 1; UIProfileFixture.Tick += UIProfileFixture.Frequency() * 5; p.Frame();
        long attempts = p.Number("reportAttempts"); for (int i = 0; i < 3; i++) p.Frame();
        p.Check(attempts == 1 && p.Number("reportAttempts") == 1 && p.Number("reportFailures") == 1 && p.State("Frames") == 5, "failed_periodic_report_not_retried_each_frame");
        p.ClearOutput(); UIProfileFixture.ThrowWriteAt = -1; p.Flush(); p.Check(p.HasEnd, "final_flush_bypasses_failed_periodic_cooldown"); p.Save("invalid-periodic-failure-final.log");
        p.Reset(); p.Frame(); bool outputSuspended = false;
        UIProfileFixture.OnWrite = n => { if (n == 1) { outputSuspended = !p.Bool("Active") && !p.Bool("SampleSelected"); p.Frame(p.UI); } };
        p.Flush(); p.Check(outputSuspended && p.HasEnd && p.Number("reentrantFrames") == 1 && p.Number("FrameDepth") == 0, "output_reentry_suspends_observer_without_fake_frame"); p.Save("valid-output-reentry.log");

        // Parser-negative records derive from actual generated output, not an invented baseline.
        p.SaveRaw("malformed-missing-END.log", string.Join('\n', normal.Split('\n').Where(r => !r.StartsWith("NX_PROFILE END "))) + "\n", true);
        string stateRow = normal.Split('\n').Single(r => r.StartsWith("NX_PROFILE STATE "));
        p.SaveRaw("malformed-duplicate-state.log", normal.Replace(stateRow, stateRow + "\n" + stateRow), true);
        p.SaveRaw("malformed-wrong-scope-label.log", normal.Replace("label=frame_draw", "label=not_frame_draw"), true);
        p.SaveRaw("malformed-missing-scope.log", string.Join('\n', normal.Split('\n').Where(r => !(r.StartsWith("NX_PROFILE SCOPE ") && r.Contains(" id=11 ")))) + "\n", true);
    }

    sealed class Session
    {
        readonly Type runtime;
        readonly List<string> checks;
        readonly string output;
        public readonly int FirstCheck;
        public readonly Func<int> Begin;
        public readonly Action<int, bool> End, Exit;
        public readonly Func<int, int> Enter;
        public readonly Func<UIProofLayer, int> EnterLayer;
        public readonly Action<int, bool, bool> ExitLayer;
        public readonly Action Flush;
        public readonly List<object> Accepted = new(), Rejected = new(), Receipts = new();
        public Session(Type type, List<string> list, string directory)
        {
            runtime = type; checks = list; output = directory; FirstCheck = list.Count;
            Begin = Method("FrameBegin").CreateDelegate<Func<int>>(); End = Method("FrameEnd").CreateDelegate<Action<int, bool>>();
            Enter = Method("Enter").CreateDelegate<Func<int, int>>(); Exit = Method("Exit").CreateDelegate<Action<int, bool>>();
            EnterLayer = Method("EnterLayer").CreateDelegate<Func<UIProofLayer, int>>(); ExitLayer = Method("ExitLayer").CreateDelegate<Action<int, bool, bool>>();
            Flush = Method("Flush").CreateDelegate<Action>(); Directory.CreateDirectory(Path.Combine(directory, "runtime-logs"));
        }
        MethodInfo Method(string name) => runtime.GetMethod(name, Static) ?? throw new InvalidOperationException("Missing runtime method " + name);
        FieldInfo Field(string name) => runtime.GetField(name, Static) ?? throw new InvalidOperationException("Missing runtime field " + name);
        public object? Call(string name, params object[] args) => Method(name).Invoke(null, args);
        public object Get(string name) => Field(name).GetValue(null)!;
        public void Set(string name, object? value) => Field(name).SetValue(null, value);
        public bool Bool(string name) => (bool)Get(name);
        public long Number(string name) => Convert.ToInt64(Get(name));
        static object Member(object value, string field) => value.GetType().GetField(field, Instance)!.GetValue(value)!;
        static long NumberMember(object value, string field) => Convert.ToInt64(Member(value, field));
        public long State(string name) => NumberMember(Get("WindowState"), name);
        public long Cohort(int cohort, string name) => NumberMember(((Array)Get("Cohorts")).GetValue(cohort)!, name);
        public long Scene(int cohort, string name) => NumberMember(Member(((Array)Get("Cohorts")).GetValue(cohort)!, "Scene"), name);
        public long Metric(bool window, int cohort, int id, string field) => NumberMember(((Array)Get(window ? "WindowMetrics" : "FrameMetrics")).GetValue((window ? cohort * 12 : 0) + id)!, field);
        public void SetArrayStruct(string arrayName, int index, string field, object value)
        {
            Array array = (Array)Get(arrayName); object item = array.GetValue(index)!; item.GetType().GetField(field, Instance)!.SetValue(item, value); array.SetValue(item, index);
        }
        public void SetMetric(string array, int index, string field, long value) => SetArrayStruct(array, index, field, value);
        public object[] Metrics(string name) => ((Array)Get(name)).Cast<object>().ToArray();
        public bool MetricsEqual(object[] before, string name) => before.SequenceEqual(Metrics(name));
        public bool AllZero(string name) => Metrics(name).All(m => MetricFields.All(f => NumberMember(m, f) == 0));
        public bool Valid() => (bool)Call("ValidateSample")!;
        public void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("UI runtime proof failed: " + name);
            checks.Add(name);
        }
        public void Reset()
        {
            foreach (FieldInfo field in runtime.GetFields(Static))
                if (!field.IsLiteral && !field.IsInitOnly) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
            UIProfileFixture.Reset();
        }
        public void Select() { Call("InitializeTiming"); Set("SampleSelected", true); Call("BeginSample"); }
        public void UI() { int u = Enter(1), t = Enter(10); Exit(t, true); Exit(u, true); }
        public void Frame(Action? body = null, bool complete = true) { int d = Begin(); try { body?.Invoke(); } finally { End(d, complete); } }
        public void AssertPartitions(string name)
        {
            long frame = Metric(false, 0, 0, "InclusiveTicks"), ui = Metric(false, 0, 1, "InclusiveTicks");
            Check(Enumerable.Range(0, 12).Sum(i => Metric(false, 0, i, "ExclusiveTicks")) == frame &&
                Metric(false, 0, 0, "ExclusiveTicks") + ui == frame && Metric(false, 0, 1, "ExclusiveTicks") + Enumerable.Range(2, 10).Sum(i => Metric(false, 0, i, "ExclusiveTicks")) == ui,
                name + "_all_three_exclusive_partitions");
            Check(Enumerable.Range(0, 12).All(i => Metric(false, 0, i, "Calls") != 0 || MetricFields.All(f => Metric(false, 0, i, f) == 0)), name + "_unused_metrics_are_exact_zero");
            Receipts.Add(new { kind = "partition", name, frameTicks = frame, uiTicks = ui, metrics = Enumerable.Range(0, 12).Select(i => new { id = i, calls = Metric(false, 0, i, "Calls"), inclusive = Metric(false, 0, i, "InclusiveTicks"), exclusive = Metric(false, 0, i, "ExclusiveTicks") }).ToArray() });
        }
        public void Accounting(string name)
        {
            Check(State("Attempts") == State("Frames") + State("CaptureFailures") && State("Frames") == State("Completed") + State("Aborted") &&
                State("Eligible") <= State("Completed") && State("Eligible") == Enumerable.Range(0, 3).Sum(i => Cohort(i, "Frames")) &&
                State("SelectedAttempts") == State("DiscardedSelected") + Enumerable.Range(0, 3).Sum(i => Cohort(i, "Selected")) && Number("snapshotCalls") == 2 * State("Frames"), name + "_state_accounting");
            for (int i = 0; i < 3; i++) Check(Cohort(i, "Selected") == Cohort(i, "Valid") + Cohort(i, "Invalid") + Cohort(i, "Aborted") &&
                Cohort(i, "Selected") <= Cohort(i, "Frames") && Scene(i, "Samples") == 2 * Cohort(i, "Frames") && Metric(true, i, 0, "Calls") == Cohort(i, "Valid") &&
                Metric(true, i, 1, "Calls") == Cohort(i, "Valid") - Cohort(i, "NoUI"), name + "_cohort_accounting_" + i);
        }
        public string Output => string.Join("\n", UIProfileFixture.Lines) + (UIProfileFixture.Lines.Count == 0 ? "" : "\n");
        public string[] Rows => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        public bool HasEnd => Rows.Any(r => r.StartsWith("NX_PROFILE END "));
        public void ClearOutput() => UIProfileFixture.Lines.Clear();
        public void Save(string file, bool reject = false) => SaveRaw(file, Output, reject);
        public void SaveRaw(string file, string text, bool reject)
        {
            string relative = "runtime-logs/" + file; File.WriteAllText(Path.Combine(output, relative), text);
            if (reject) Rejected.Add(new { file = relative });
            else
            {
                Check(text.Contains("NX_PROFILE END "), "accepted_log_has_END_" + file);
                Accepted.Add(new { file = relative, measurement_invalid = text.Contains("measurement_invalid=1"), final_seen = text.Contains(" final=1 ") });
            }
        }
    }
}
