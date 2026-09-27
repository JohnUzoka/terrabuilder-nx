using System.Reflection;
using System.Text.Json;

// Executes only the serialized candidate-derived Probe.UpdateRuntime. Fixture boundaries
// supply deterministic clocks/state/output; they are not a replacement runtime.
internal static class UpdateRuntimeProof
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly string[] MetricFields = { "Calls", "InclusiveTicks", "ExclusiveTicks", "InclusiveMax", "ExclusiveMax" };

    internal static object Run(string baselinePath, string candidatePath, string fnaPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        RuntimeFixture.InjectedFaults.Clear();
        string baselineSha256 = RuntimeProbe.Hash(baselinePath), candidateSha256 = RuntimeProbe.Hash(candidatePath), fnaSha256 = RuntimeProbe.Hash(fnaPath);
        RuntimeProbe.Require(baselineSha256 == "90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22", "baseline is not frozen52");
        RuntimeProbe.Require(fnaSha256 == "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f", "FNA is not frozen52");
        var receipts = new List<object>();
        byte[] bytes = RuntimeProbe.Build(candidatePath, receipts);
        string probePath = Path.Combine(outputDirectory, "candidate-runtime-probe.dll");
        File.WriteAllBytes(probePath, bytes);
        var assembly = Assembly.Load(bytes);
        Type runtime = assembly.GetType("Probe.UpdateRuntime", throwOnError: true)!;
        var checks = new List<string>();
        var p = new Session(runtime, checks, outputDirectory);
        p.Check(runtime.Assembly == assembly && runtime.FullName == "Probe.UpdateRuntime", "actual_emitted_candidate_runtime_serialized_and_loaded");
        Timing(p);
        State(p);
        UpdateBoundaries(p);
        BoundaryFaults(p);
        Allocations(p);
        Reports(p);
        var manifest = new { schema = "update56-report-fixtures-v1", candidate_sha256 = candidateSha256, entries = p.Entries };
        File.WriteAllText(Path.Combine(outputDirectory, "report-fixtures", "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        var result = new
        {
            passed = true, baselineSha256, candidateSha256, fnaSha256,
            runtime = runtime.FullName, probeMvid = runtime.Module.ModuleVersionId,
            probeSha256 = RuntimeProbe.Hash(probePath), toolSha256 = RuntimeProbe.Hash(typeof(UpdateRuntimeProof).Assembly.Location),
            checks, methodReceipts = receipts, executionReceipts = p.Receipts, reports = manifest,
            injectedFaultCount = RuntimeFixture.InjectedFaults.Count, injectedFaults = RuntimeFixture.InjectedFaults,
            limits = new[] {
                "Executes actual emitted candidate runtime IL after SourcePatch game/FNA binding, serialized with named typed host clock/state/output boundaries; never substitutes template runtime bodies.",
                "Does not run Terraria simulation, scheduler, rendering, GPU or Switch clocks; separate behavior proof executes original/candidate Update target bodies.",
                "Deterministic ticks and warmed allocation checks verify arithmetic and hot-path behavior, not device overhead or a speedup; initialization, reflection, fault injection and reporting are outside the allocation interval.",
                "Raw net mode, world dimensions, guide type and available recipe count are boundary ranges, not stability exclusions or proof of intermediate state."
            }
        };
        File.WriteAllText(Path.Combine(outputDirectory, "runtime.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    static void Timing(Session p)
    {
        p.Reset(); p.Call("InitializeTiming");
        int clocks = RuntimeFixture.ClockCalls;
        for (int i = 0; i < 128; i++) for (int id = 1; id < 19; id++) p.Exit(p.Enter(id), true);
        p.Check(RuntimeFixture.ClockCalls == clocks && !p.Bool("Active"), "unselected_inner_hooks_use_zero_clocks");
        p.Select();
        clocks = RuntimeFixture.ClockCalls;
        int rootAction = p.Enter(18); p.Exit(rootAction, true);
        p.Check(rootAction > 0 && RuntimeFixture.ClockCalls == clocks + 2,
            "selected_update_without_body_admits_root_main_thread_actions");
        p.Call("EndSample", true);
        p.Check(p.Valid() && p.Metric(false, 0, 0, "Calls") == 1 && p.Metric(false, 0, 1, "Calls") == 0 &&
            p.Metric(false, 0, 18, "Calls") == 1 && p.Metric(false, 0, 18, "InclusiveTicks") == 10 && p.Metric(false, 0, 0, "InclusiveTicks") == 30,
            "update_without_body_is_valid_with_other_timed_work");
        p.AssertPartitions("no_body_other_work");
        clocks = RuntimeFixture.ClockCalls; p.Exit(rootAction, true); RuntimeFixture.RecordFault("scope.stale_cookie_after_end");
        p.Check(RuntimeFixture.ClockCalls == clocks && p.Bool("MeasurementInvalid") && !p.Bool("Active"),
            "inactive_stale_positive_cookie_spends_zero_clocks_and_exposes_misuse");

        p.Reset(); p.Select();
        int ui = p.Enter(1), outer = p.Enter(10), same = p.Enter(10), layout = p.Enter(8);
        p.Exit(layout, true); p.Exit(same, true); int format = p.Enter(9); p.Exit(format, true); p.Exit(outer, true); p.Exit(ui, true); p.Call("EndSample", true);
        p.Check(p.Valid(), "nested_same_and_different_category_sample_valid");
        p.Check(p.Metric(false, 0, 10, "Calls") == 2 && p.Metric(false, 0, 10, "InclusiveTicks") == 100 &&
            p.Metric(false, 0, 10, "ExclusiveTicks") == 50 && p.Metric(false, 0, 8, "InclusiveTicks") == 10 &&
            p.Metric(false, 0, 9, "InclusiveTicks") == 10 && p.Metric(false, 0, 0, "InclusiveTicks") == 110 &&
            RuntimeFixture.ClockCalls == 14, "nested_exact_ticks_and_outside_root_clock_pair");
        p.AssertPartitions("nested");
        p.Check((bool)p.Call("CommitSample", 0)! && p.Metric(true, 0, 10, "Calls") == 2, "completed_nested_sample_merges_once");
        object[] once = p.Metrics("WindowMetrics");
        p.Check(!(bool)p.Call("CommitSample", 0)! && p.Bool("MeasurementInvalid") && p.MetricsEqual(once, "WindowMetrics"),
            "duplicate_commit_is_observable_and_never_donates_twice");
        RuntimeFixture.RecordFault("scope.duplicate_commit");

        p.Reset(); p.Select(); ui = p.Enter(1);
        var depthCookies = new int[62]; for (int i = 0; i < depthCookies.Length; i++) depthCookies[i] = p.Enter(10);
        for (int i = depthCookies.Length - 1; i >= 0; i--) p.Exit(depthCookies[i], true);
        p.Exit(ui, true); p.Call("EndSample", true);
        p.Check(p.Valid() && p.Number("MaxObservedDepth") == 64 && p.Number("DepthOverflows") == 0, "depth64_boundary_is_valid");
        p.AssertPartitions("depth64");

        void Invalid(string name, Action body)
        {
            p.Reset(); p.Select(); int u = p.Enter(1), before = RuntimeFixture.InjectedFaults.Count;
            body();
            if (RuntimeFixture.InjectedFaults.Count == before) RuntimeFixture.RecordFault("scope." + name);
            p.Exit(u, true); p.Call("EndSample", true);
            p.Check(!p.Valid() && p.Bool("MeasurementInvalid") && p.AllZero("WindowMetrics"), "invalid_" + name + "_observable_without_donation");
        }
        Invalid("mismatched_cookie", () => { int c = p.Enter(10); p.Exit(c + 123, true); });
        Invalid("negative_cookie", () => p.Exit(-1, true));
        Invalid("unclosed_child", () => p.Enter(8));
        Invalid("invalid_metric_id", () => p.Enter(19));
        Invalid("negative_metric_id", () => p.Enter(-1));
        Invalid("update_metric_reentry", () => p.Enter(0));
        Invalid("depth65", () => { for (int i = 0; i < 63; i++) p.Enter(10); });
        Invalid("cookie_overflow", () => { p.Inject("nextCookie", int.MaxValue); p.Enter(10); });
        Invalid("negative_cookie_counter", () => { p.Inject("nextCookie", -1); p.Enter(10); });
        Invalid("scope_counter_overflow", () => { int c = p.Enter(8); p.SetMetric("FrameMetrics", 8, "Calls", long.MaxValue); p.Exit(c, true); });
        Invalid("child_tick_overflow", () => { p.SetArrayStruct("timingStack", 1, "ChildTicks", long.MaxValue); int c = p.Enter(8); p.Exit(c, true); });
        Invalid("negative_scope_start", () => { int c = p.Enter(10); p.SetArrayStruct("timingStack", 2, "Start", -1L); p.Exit(c, true); });
        Invalid("malformed_arrays", () => p.Inject("FrameMetrics", null));

        foreach (string fault in new[] { "throw", "negative", "backward" })
        for (int position = fault == "backward" ? 2 : 1; position <= 8; position++)
        {
            p.Reset(); p.Call("InitializeTiming");
            RuntimeFixture.ClockScript = Enumerable.Range(0, 64).Select(i => 100000L + i * 10).ToArray();
            if (fault == "throw") RuntimeFixture.ThrowClockAt = position;
            else RuntimeFixture.ClockScript[position - 1] = fault == "negative" ? -1 : RuntimeFixture.ClockScript[position - 2] - 1;
            int injectedBefore = RuntimeFixture.InjectedFaults.Count;
            p.Set("SampleSelected", true); p.Call("BeginSample"); ui = p.Enter(1); int child = p.Enter(8); p.Exit(child, true); p.Exit(ui, true); p.Call("EndSample", true);
            p.Check(!p.Valid() && p.Bool("MeasurementInvalid") && p.Number("timingDepth") == 0 && !p.Bool("Active") && p.AllZero("WindowMetrics"),
                "all_timing_clock_positions_" + fault + "_" + position);
            p.Check(RuntimeFixture.InjectedFaults.Count > injectedBefore && RuntimeFixture.ClockCalls >= position,
                "timing_fault_was_actually_consumed_" + fault + "_" + position);
        }
        foreach (string field in MetricFields.Skip(1))
        {
            p.Reset(); p.Select(); ui = p.Enter(1); p.Exit(ui, true); p.Call("EndSample", true);
            p.SetMetric("FrameMetrics", 17, field, 1);
            p.Check(!p.Valid() && p.Bool("MeasurementInvalid"), "unused_metric_corruption_rejected_" + field);
        }
        p.Reset(); p.Select(); ui = p.Enter(1); int last = p.Enter(18); p.Exit(last, true); p.Exit(ui, true); p.Call("EndSample", true);
        p.SetMetric("WindowMetrics", 18, "Calls", long.MaxValue); p.SetMetric("WindowMetrics", 18, "InclusiveTicks", 1); p.SetMetric("WindowMetrics", 18, "ExclusiveTicks", 1);
        p.SetMetric("WindowMetrics", 18, "InclusiveMax", 1); p.SetMetric("WindowMetrics", 18, "ExclusiveMax", 1);
        object[] before = p.Metrics("WindowMetrics");
        p.Check(!(bool)p.Call("CommitSample", 0)! && p.Bool("MeasurementInvalid") && p.MetricsEqual(before, "WindowMetrics"), "last_metric_overflow_preflight_prevents_partial_window_merge");
        foreach (var values in new[] { new object[] { long.MaxValue, 1L }, new object[] { 0L, -1L }, new object[] { -1L, 0L } })
        {
            p.Reset(); p.Call("Add", values); RuntimeFixture.RecordFault("counter.add_invalid_input");
            p.Check(p.Bool("MeasurementInvalid") && (long)values[0] == long.MaxValue, "counter_add_saturates_or_rejects_without_wrap_" + values[1]);
        }
        foreach (long corrupted in new[] { long.MaxValue, -1L })
        {
            p.Reset(); p.Inject("ScopeFailures", corrupted);
            p.Check(!p.Valid() && p.Bool("MeasurementInvalid") && p.Number("ScopeFailures") == long.MaxValue,
                "failure_counter_saturates_without_wrap_" + corrupted);
        }
        p.Reset(); p.Select(); ui = p.Enter(1); int aborted = p.Enter(10); p.Exit(aborted, false); p.Exit(ui, true); p.Call("EndSample", true);
        p.Check(!p.Valid() && p.Bool("FrameScopeAborted") && !p.Bool("MeasurementInvalid") && p.Number("ScopeFailures") == 0 && p.Metric(false, 0, 10, "Calls") == 0,
            "caught_original_scope_exception_is_abort_not_observer_fault");
    }

    static void UpdateBoundaries(Session p)
    {
        p.Reset();
        for (int cohort = 0; cohort < 3; cohort++)
        {
            RuntimeMain.playerInventory = cohort != 0; RuntimeMain.gamePaused = cohort == 2;
            RuntimeMain.netMode = 0; RuntimeMain.maxTilesX = RuntimeMain.maxTilesY = 0;
            RuntimeMain.guideItem = null; RuntimeMain.numAvailableRecipes = 0;
            p.Frame(() => {
                p.AllMetrics(); RuntimeMain.netMode = cohort + 1; RuntimeMain.maxTilesX = 4200 + cohort * 1000; RuntimeMain.maxTilesY = 1200 + cohort * 200;
                RuntimeMain.guideItem = new RuntimeItem { type = 7 + cohort }; RuntimeMain.numAvailableRecipes = 17 + cohort;
            });
            p.Check(p.Cohort(cohort, "Valid") == 1 && p.State("Changed") == 0 &&
                Enumerable.Range(0, 19).All(id => p.Metric(true, cohort, id, "Calls") > 0), "all_update_metrics_donate_in_cohort_" + cohort);
            p.Check(p.SceneRange(cohort, "GuideType", "First") == -1 && p.SceneRange(cohort, "GuideType", "Last") == 7 + cohort &&
                p.SceneRange(cohort, "AvailableRecipes", "First") == 0 && p.SceneRange(cohort, "AvailableRecipes", "Last") == 17 + cohort,
                "raw_guide_and_available_boundary_changes_do_not_exclude_cohort_" + cohort);
            foreach (var range in new[] { ("NetMode", cohort + 1), ("WorldWidth", 4200 + cohort * 1000), ("WorldHeight", 1200 + cohort * 200) })
                p.Check(p.SceneRange(cohort, range.Item1, "First") == 0 && p.SceneRange(cohort, range.Item1, "Last") == range.Item2 &&
                    p.SceneRange(cohort, range.Item1, "Min") == 0 && p.SceneRange(cohort, range.Item1, "Max") == range.Item2,
                    "raw_zero_and_changed_world_context_not_admission_" + cohort + "_" + range.Item1);
        }
        p.Check(p.Number("snapshotCalls") == 6 && p.Number("snapshotTicks") == 60 && p.Number("snapshotMax") == 10 &&
            Enumerable.Range(0, 3).All(cohort => p.Cohort(cohort, "ClockCalls") == 1 && p.Cohort(cohort, "ClockTicks") == 10 && p.Cohort(cohort, "ClockMax") == 10),
            "separate_snapshot_and_bare_clock_pair_costs_are_observations");
        p.Check(((Array)p.Get("FrameMetrics")).Length == 19 && ((Array)p.Get("WindowMetrics")).Length == 57 &&
            ((Array)p.Get("timingStack")).Length == 64 && ((Array)p.Get("Cohorts")).Length == 3 &&
            ((char[])p.Get("outputBuffer")).Length == 65536 && ((System.Text.StringBuilder)p.Get("reportBuffer")).Capacity == 65536,
            "bounded_fixed_preallocation_matches_frozen_contract");
        p.Accounting("all_update_cohorts"); p.Flush(); p.Save("valid-all-update-cohorts.log");

        p.Reset(); p.Frame(() => { int action = p.Enter(18); p.Exit(action, true); });
        p.Check(p.Cohort(0, "NoBody") == 1 && p.Cohort(0, "Valid") == 1 && p.Metric(true, 0, 1, "Calls") == 0 && p.Metric(true, 0, 18, "Calls") == 1,
            "all_no_body_cohort_can_contain_other_root_work");
        p.AssertPartitions("no_body_root_actions"); p.Accounting("no_body_root_actions"); p.Flush(); p.Save("valid-no-body-other-work.log");

        p.Reset(); p.Frame(() => {
            int before = p.Enter(18); p.Exit(before, true);
            int first = p.Enter(1), second = p.Enter(1), third = p.Enter(1), fourth = p.Enter(1);
            int housing = p.Enter(9), nestedHousing = p.Enter(9); p.Exit(nestedHousing, true); p.Exit(housing, true);
            p.Exit(fourth, true); p.Exit(third, true); p.Exit(second, true); p.Exit(first, true);
            int repeated = p.Enter(1); p.Exit(repeated, true);
            int after = p.Enter(18); p.Exit(after, true);
        });
        p.Check(p.Cohort(0, "Valid") == 1 && p.Cohort(0, "NoBody") == 0 && p.Metric(true, 0, 1, "Calls") == 5 &&
            p.Metric(true, 0, 1, "InclusiveTicks") > p.Metric(true, 0, 0, "InclusiveTicks") && p.Metric(true, 0, 9, "Calls") == 2 && p.Metric(true, 0, 18, "Calls") == 2,
            "repeated_nested_body_and_same_id_inclusive_overlap_is_valid");
        p.AssertPartitions("repeated_nested_body"); p.Accounting("repeated_nested_body"); p.Flush(); p.Save("valid-repeated-nested-body.log");

        p.Reset();
        p.Frame(() => { int u = p.Enter(1), r = p.Enter(3); p.Exit(r + 1, true); p.Exit(r, true); p.Exit(u, true); });
        RuntimeFixture.RecordFault("scope.mismatched_cookie_cohort");
        p.Check(p.Cohort(0, "Invalid") == 1 && p.Cohort(0, "Valid") == 0 && p.AllZero("WindowMetrics") && p.Bool("MeasurementInvalid"),
            "bad_update_cookie_sample_cannot_donate_partial_costs");
        p.Accounting("bad_update_cookie");
        for (int update = 0; update < 64; update++) p.Frame(p.AllMetrics);
        p.Check(p.Cohort(0, "Invalid") == 1 && p.Cohort(0, "Valid") == 1 && p.Bool("MeasurementInvalid"), "later_healthy_sample_cannot_erase_sticky_failure");
        p.Flush(); p.Save("invalid-cookie-then-healthy.log");

        foreach (string array in new[] { "FrameMetrics", "WindowMetrics", "timingStack", "Cohorts" })
        {
            p.Reset(); int d = p.Begin(), u = p.Enter(1);
            Array original = (Array)p.Get(array); p.Inject(array, Array.CreateInstance(original.GetType().GetElementType()!, 0));
            p.Exit(u, true); p.End(d, true);
            p.Check(p.Bool("MeasurementInvalid") && !p.Bool("Active") && !p.Bool("SampleSelected") && p.Number("FrameDepth") == 0,
                "short_array_failure_cleans_active_root_" + array);
        }
        p.Reset(); p.Frame(p.AllMetrics); p.Inject("outputBuffer", new char[1]); p.Flush();
        p.Check(!p.HasEnd && p.State("Frames") == 1 && p.Bool("MeasurementInvalid") && !p.Bool("reporting"), "bounded_output_buffer_failure_retains_window");
        p.Save("partial-bounded-buffer.log", reject: true);
        p.Set("outputBuffer", new char[65536]); p.Flush(); p.Check(p.HasEnd, "bounded_output_buffer_recovery"); p.Save("invalid-buffer-recovered.log");

        p.Reset(); var originalFailure = new InvalidOperationException("original game failure"); Exception? observed = null; int finalizers = 0;
        try { p.Frame(() => { RuntimeMain.ThrowSnapshotAt = 2; try { throw originalFailure; } finally { finalizers++; } }); }
        catch (Exception error) { observed = error; }
        p.Check(ReferenceEquals(originalFailure, observed) && finalizers == 1 && p.State("CaptureFailures") == 1 && p.Bool("MeasurementInvalid") &&
            p.Number("FrameDepth") == 0 && !p.Bool("Active") && !p.Bool("SampleSelected"), "observer_end_failure_preserves_original_exception_and_finally");
        p.Accounting("original_exception_with_snapshot_fault"); p.Flush(); p.Save("invalid-original-exception-snapshot.log");
    }

    static void State(Session p)
    {
        p.Reset();
        for (int frame = 0; frame < 129; frame++) for (int cohort = 0; cohort < 3; cohort++)
        {
            RuntimeMain.playerInventory = cohort != 0; RuntimeMain.gamePaused = cohort == 2;
            bool selected = false; p.Frame(() => { selected = p.Bool("SampleSelected"); p.Body(); });
            p.Check(selected == (frame % 64 == 0), "independent_phase_cohort_" + cohort + "_update_" + frame);
        }
        for (int cohort = 0; cohort < 3; cohort++)
            p.Check(p.Cohort(cohort, "Frames") == 129 && p.Cohort(cohort, "Selected") == 3 && p.Cohort(cohort, "Valid") == 3 && p.Scene(cohort, "Samples") == 258,
                "cohort_exact_frames_samples_and_all_boundary_scene_counts_" + cohort);
        p.Accounting("interleaved_cohorts"); p.Flush(); p.Save("valid-cohorts.log");

        var excluded = new (string Name, Action Set, string Counter)[] {
            ("menu", () => RuntimeMain.gameMenu = true, "Menu"), ("paused_without_inventory", () => RuntimeMain.gamePaused = true, "Paused"),
            ("map", () => RuntimeMain.mapFullscreen = true, "Map"), ("dead", () => RuntimeMain.player![0].dead = true, "Dead"),
            ("ghost", () => RuntimeMain.player![0].ghost = true, "Ghost"), ("spectator", () => RuntimeMain.player![0].spectating = 0, "Spectator"),
            ("null_players", () => RuntimeMain.player = null, "InvalidPlayer"), ("negative_player", () => RuntimeMain.myPlayer = -1, "InvalidPlayer"),
            ("out_of_range_player", () => RuntimeMain.myPlayer = 4, "InvalidPlayer"), ("null_player", () => RuntimeMain.player![0] = null!, "InvalidPlayer"),
            ("nan_player", () => RuntimeMain.player![0].position.X = float.NaN, "InvalidPlayer"),
            ("infinite_camera", () => RuntimeMain.screenPosition.Y = float.PositiveInfinity, "InvalidScene"),
            ("zero_zoom", () => RuntimeMain.GameZoomTarget = 0, "InvalidScene"), ("nan_ui_scale", () => RuntimeMain.UIScaleValue = float.NaN, "InvalidScene"),
            ("zero_viewport", () => RuntimeMain.screenWidth = 0, "InvalidScene"), ("nan_time", () => RuntimeMain.time = double.NaN, "InvalidScene") };
        foreach (var item in excluded)
        {
            p.Reset(); item.Set(); bool selected = true; p.Frame(() => { selected = p.Bool("SampleSelected"); p.Body(); });
            p.Check(!selected && p.State("Eligible") == 0 && p.State(item.Counter) == 1 && p.State("CaptureFailures") == 0 && p.AllZero("WindowMetrics"), "raw_state_exclusion_" + item.Name);
            p.Accounting(item.Name);
        }
        p.Reset(); RuntimeMain.autoPause = true; p.Frame(p.Body);
        p.Check(p.State("AutoPause") == 1 && p.State("Eligible") == 1 && p.Cohort(0, "Valid") == 1, "raw_autopause_does_not_imply_paused");
        p.Reset(); RuntimeMain.HoverItem = RuntimeMain.mouseItem = null; p.Frame(p.Body);
        p.Check(p.State("Eligible") == 1 && p.Scene(0, "FirstHoverType") == -1 && p.Scene(0, "LastMouseItemType") == -1, "null_items_are_raw_minus_one_not_invalid_player");
        p.Reset(); p.Frame(() => { p.Body(); RuntimeMain.player![0].position.X += 10; RuntimeMain.screenPosition.Y += 20; RuntimeMain.mouseX += 4; RuntimeMain.time += 10; RuntimeMain.HoverItem!.type = 7; RuntimeMain.HoverItem.prefix = 3; RuntimeMain.mouseItem!.type = 8; });
        p.Check(p.State("Eligible") == 1 && p.State("Changed") == 0 && p.Cohort(0, "Valid") == 1 && p.Scene(0, "Samples") == 2 &&
            p.Scene(0, "PlayerMoved") == 1 && p.Scene(0, "CameraMoved") == 1 && p.Scene(0, "MouseMoved") == 1 && p.Scene(0, "HoverChanged") == 1 && p.Scene(0, "MouseItemChanged") == 1,
            "movement_hover_and_item_changes_recorded_without_rejecting_update_work");
        p.Accounting("movement"); p.Flush(); p.Save("valid-normal.log");
        var changes = new (string Name, Action Set)[] {
            ("inventory", () => RuntimeMain.playerInventory = true), ("menu", () => RuntimeMain.gameMenu = true),
            ("paused", () => RuntimeMain.gamePaused = true), ("autopause", () => RuntimeMain.autoPause = true),
            ("zoom", () => RuntimeMain.GameZoomTarget = 2), ("ui_scale", () => RuntimeMain.UIScaleValue = 2),
            ("width", () => RuntimeMain.screenWidth = 800), ("height", () => RuntimeMain.screenHeight = 600),
            ("frame_skip", () => RuntimeMain.FrameSkipMode = 1), ("day", () => RuntimeMain.dayTime = false),
            ("player_id", () => { RuntimeMain.player = new[] { new RuntimePlayer(), new RuntimePlayer() }; RuntimeMain.myPlayer = 1; }) };
        foreach (var change in changes)
        {
            p.Reset(); p.Frame(() => { p.Body(); change.Set(); });
            p.Check(p.State("Changed") == 1 && p.State("Eligible") == 0 && p.State("SelectedAttempts") == 1 && p.State("DiscardedSelected") == 1 && p.AllZero("WindowMetrics"), "boundary_change_discard_" + change.Name);
            p.Accounting("changed_" + change.Name);
        }
        p.Reset(); p.Frame(() => { RuntimeMain.playerInventory = true; RuntimeMain.playerInventory = false; });
        p.Check(p.State("Eligible") == 1 && p.State("Changed") == 0, "boundary_only_toggle_return_attribution_limit_demonstrated");
        p.Reset(); p.Frame(); p.Check(p.Cohort(0, "NoBody") == 1 && p.Cohort(0, "Valid") == 1, "no_body_sample_has_explicit_counter");
        p.Flush(); p.Save("valid-unused.log");

        p.Reset(); p.Frame(p.Body, false);
        p.Check(p.State("Aborted") == 1 && p.State("Eligible") == 0 && p.State("DiscardedSelected") == 1 && p.AllZero("WindowMetrics"), "original_update_abort_discards_selected_timings");
        p.Accounting("frame_abort");
        p.Reset(); p.Frame(() => { int u = p.Enter(1), c = p.Enter(10); p.Exit(c, false); p.Exit(u, true); });
        p.Check(p.State("Completed") == 1 && p.Cohort(0, "Aborted") == 1 && p.Cohort(0, "Valid") == 0 && !p.Bool("MeasurementInvalid") && p.AllZero("WindowMetrics"), "caught_child_exception_aborts_cost_sample_not_game_update");
        p.Accounting("caught_exception"); p.Flush(); p.Save("valid-aborted-scope.log");
        p.Reset(); int root = p.Begin(), ui = p.Enter(1), text = p.Enter(10); int beforeClocks = RuntimeFixture.ClockCalls;
        int nested = p.Begin();
        p.Check(!p.Bool("Active") && !p.Bool("SampleSelected"), "reentrant_Update_masks_collection");
        p.Body(); p.End(nested, false);
        p.Check(p.Bool("Active") && p.Bool("SampleSelected") && p.Number("FrameDepth") == 1 && RuntimeFixture.ClockCalls == beforeClocks,
            "reentrant_Update_restores_parent_flags_and_spends_zero_clocks");
        p.Exit(text, true); p.Exit(ui, true); p.End(root, true);
        p.Check(p.State("Attempts") == 1 && p.Cohort(0, "Valid") == 1 && p.Number("reentrantFrames") == 1 && p.Number("FrameDepth") == 0, "reentrant_Update_preserves_parent_stack_and_single_root_accounting");
        p.Reset(); p.Frame(); Exception? threadError = null; bool excludedThread = false; int threadClocks = -1;
        var thread = new Thread(() => { try { int d = p.Begin(); excludedThread = !p.Bool("SampleSelected") && !p.Bool("Active"); p.Body(); p.End(d, true); threadClocks = RuntimeFixture.ClockCalls; } catch (Exception e) { threadError = e; } });
        thread.Start(); thread.Join();
        p.Check(threadError == null && excludedThread && threadClocks == 0 && p.State("Attempts") == 1 && p.Number("ignoredThreads") == 1 && p.Number("FrameDepth") == 0,
            "nonowner_thread_ignored_without_clocks_or_parent_state_corruption");

        foreach (bool atBegin in new[] { true, false }) foreach (int offset in new[] { 1, 2 }) foreach (string fault in new[] { "throw", "negative", "backward" })
        {
            p.Reset(); p.Frame();
            int d = atBegin ? -1 : p.Begin();
            // This frame is unsampled (phase1), so end clocks begin at its snapshot.
            int target = RuntimeFixture.ClockCalls + offset;
            if (fault == "throw") RuntimeFixture.ThrowClockAt = target;
            else
            {
                long tick = RuntimeFixture.Tick;
                RuntimeFixture.ClockScript = new[] { tick + 10, tick + 20 };
                RuntimeFixture.ClockScript[offset - 1] = fault == "negative" ? -1 : tick - 1;
                RuntimeFixture.ClockIndex = 0;
            }
            if (atBegin) p.Frame(); else p.End(d, true);
            string name = "capture_" + (atBegin ? "begin" : "end") + "_" + fault + "_" + offset;
            p.Check(p.State("Attempts") == 2 && p.State("Frames") == 1 && p.State("CaptureFailures") == 1 && p.Number("snapshotCalls") == 2 && p.Bool("MeasurementInvalid"), name + "_pair_atomicity");
            p.Check(p.Number("FrameDepth") == 0 && !p.Bool("Active") && !p.Bool("SampleSelected"), name + "_restoration");
            RuntimeFixture.ClockScript = null; RuntimeFixture.ThrowClockAt = -1; p.Frame(); p.Accounting(name); p.Flush(); p.Save("invalid-" + name + ".log");
        }
        foreach (int snapshot in new[] { 1, 2 })
        {
            p.Reset(); RuntimeMain.ThrowSnapshotAt = snapshot; p.Frame(p.Body);
            p.Check(p.State("Attempts") == 1 && p.State("Frames") == 0 && p.State("CaptureFailures") == 1 && p.Number("snapshotCalls") == 0 && p.AllZero("WindowMetrics") && p.Bool("MeasurementInvalid"), "getter_capture_failure_pair_atomicity_" + snapshot);
            p.Check(RuntimeMain.SnapshotCalls == snapshot, "snapshot_throw_was_actually_reached_" + snapshot);
            p.Accounting("getter_failure_" + snapshot); p.Flush(); p.Save("invalid-getter-" + snapshot + ".log");
        }
    }

    static void Allocations(Session p)
    {
        p.Reset(); RuntimeFixture.CaptureOutput = false; RuntimeFixture.Step = 0;
        for (int update = 0; update < 4096; update++) for (int cohort = 0; cohort < 3; cohort++)
        {
            RuntimeMain.playerInventory = cohort != 0; RuntimeMain.gamePaused = cohort == 2;
            int root = p.Begin(), body = p.Enter(1);
            for (int id = 2; id < 19; id++) { int child = p.Enter(id); p.Exit(child, true); }
            p.Exit(body, true); p.End(root, true);
        }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int update = 0; update < 4096; update++) for (int cohort = 0; cohort < 3; cohort++)
        {
            RuntimeMain.playerInventory = cohort != 0; RuntimeMain.gamePaused = cohort == 2;
            int root = p.Begin(), body = p.Enter(1);
            for (int id = 2; id < 19; id++) { int child = p.Enter(id); p.Exit(child, true); }
            p.Exit(body, true); p.End(root, true);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        p.Check(bytes == 0 && RuntimeFixture.WriteCalls == 0 && p.State("Frames") == 24576 && !p.Bool("MeasurementInvalid") &&
            Enumerable.Range(0, 3).All(c => p.Cohort(c, "Frames") == 8192 && p.Cohort(c, "Valid") == 128 &&
                Enumerable.Range(0, 19).All(id => p.Metric(true, c, id, "Calls") == 128)), "zero_warmed_update_and_all_scope_hook_allocations_all_cohorts");
        p.Receipts.Add(new { kind = "allocation", warmUpdates = 12288, measuredUpdates = 12288, measuredUpdatesPerCohort = 4096,
            allocatedBytes = bytes, validSelectedUpdatesAcrossBothLoops = 384, validSelectedUpdatesPerCohort = 128,
            measuredSelectedUpdatesPerCohort = 64, sampleMask = 63, metricCount = 19, clockStep = 0, writes = RuntimeFixture.WriteCalls,
            exclusions = new[] { "Initialization and JIT occurred during warmup", "Reflection/assertions and fault injection outside measured interval", "Periodic reporting prevented by frozen clock; report construction/output excluded" } });
    }

    static void BoundaryFaults(Session p)
    {
        p.Reset(); RuntimeMain.gameMenu = true;
        for (int i = 0; i < 17; i++) p.Frame(p.Body);
        RuntimeMain.gameMenu = false; bool selected = false;
        p.Frame(() => { selected = p.Bool("SampleSelected"); p.Body(); });
        p.Check(selected && p.State("SelectedAttempts") == 1 && p.State("Menu") == 17, "excluded_updates_do_not_advance_eligible_cohort_phase");
        p.Frame(() => { int clocks = RuntimeFixture.ClockCalls; p.Body(); p.Check(RuntimeFixture.ClockCalls == clocks, "unsampled_eligible_inner_scopes_spend_zero_clocks"); });
        p.Accounting("excluded_phase"); p.Flush(); p.Save("valid-excluded-phase.log");
        p.Reset(); RuntimeMain.gameMenu = RuntimeMain.gamePaused = RuntimeMain.playerInventory = RuntimeMain.mapFullscreen = RuntimeMain.autoPause = true;
        RuntimeMain.player![0].dead = RuntimeMain.player[0].ghost = true; RuntimeMain.player[0].spectating = 0;
        p.Frame();
        p.Check(p.State("FirstFlags") == 383 && p.State("LastFlags") == 383 && p.State("FlagsOr") == 383 &&
            new[] { "Menu", "Paused", "Inventory", "Map", "AutoPause", "Dead", "Ghost", "Spectator" }.All(f => p.State(f) == 1),
            "overlapping_raw_flags_are_not_a_disjoint_partition");
        p.Accounting("overlapping_flags"); p.Flush(); p.Save("valid-overlapping-exclusions.log");

        foreach (string fault in new[] { "throw", "negative" })
        {
            p.Reset();
            if (fault == "throw") RuntimeFixture.ThrowClockAt = 1;
            else RuntimeFixture.ClockScript = new[] { -1L };
            p.Frame();
            p.Check(p.Bool("MeasurementInvalid") && p.State("Attempts") == 1 && p.State("CaptureFailures") == 1 && p.State("Frames") == 0 && p.Number("FrameDepth") == 0,
                "initialization_clock_failure_restores_root_" + fault);
            RuntimeFixture.ThrowClockAt = -1; RuntimeFixture.ClockScript = null; p.Frame(p.Body); p.Accounting("initialization_" + fault);
            p.Flush(); p.Save("invalid-initialization-" + fault + ".log");
        }
        for (int offset = 1; offset <= 2; offset++)
        {
            p.Reset(); int d = p.Begin();
            // EndSample reads root-end plus a bare clock pair before end capture.
            RuntimeFixture.ThrowClockAt = RuntimeFixture.ClockCalls + 3 + offset; p.End(d, true);
            p.Check(p.State("SelectedAttempts") == 1 && p.State("DiscardedSelected") == 1 && p.State("CaptureFailures") == 1 && p.Number("snapshotCalls") == 0 && p.AllZero("WindowMetrics"),
                "selected_end_capture_failure_discards_complete_timing_" + offset);
            p.Accounting("selected_capture_" + offset); RuntimeFixture.ThrowClockAt = -1; p.Flush(); p.Save("invalid-selected-capture-" + offset + ".log");
        }
        foreach (bool rootStart in new[] { true, false })
        {
            p.Reset(); int target = rootStart ? 4 : 8;
            RuntimeFixture.OnClock = () =>
            {
                if (RuntimeFixture.ClockCalls != target) return;
                RuntimeFixture.ClockScript = new[] { RuntimeFixture.Tick - 1 };
                RuntimeFixture.ClockIndex = 0;
            };
            p.Frame(); RuntimeFixture.OnClock = null; RuntimeFixture.ClockScript = null;
            p.Check(p.Bool("MeasurementInvalid") && p.AllZero("WindowMetrics") &&
                (rootStart ? p.Cohort(0, "Invalid") == 1 : p.State("CaptureFailures") == 1),
                "cross_boundary_positive_clock_regression_" + (rootStart ? "snapshot_to_root" : "clock_pair_to_snapshot"));
            p.Accounting("cross_boundary_" + rootStart); p.Flush(); p.Save("invalid-cross-boundary-" + rootStart + ".log");
        }
        foreach (string field in new[] { "snapshotCalls", "snapshotTicks", "frameId" })
        {
            p.Reset(); int d = p.Begin(); p.Inject(field, long.MaxValue); p.End(d, true);
            p.Check(p.Bool("MeasurementInvalid") && p.State("Frames") == 0 && p.State("CaptureFailures") == 1 && p.Number(field) == long.MaxValue && p.AllZero("WindowMetrics"),
                "capture_counter_overflow_preserved_not_repaired_" + field);
        }
        p.Reset(); int root = p.Begin(); p.SetArrayStruct("Cohorts", 0, "ClockTicks", long.MaxValue); p.End(root, true);
        p.Check(p.Bool("MeasurementInvalid") && p.Cohort(0, "Selected") == 1 && p.Cohort(0, "Invalid") == 1 && p.Cohort(0, "ClockTicks") == long.MaxValue && p.AllZero("WindowMetrics"),
            "cohort_clock_counter_overflow_prevents_metric_donation_without_repair");

        foreach (string fault in new[] { "throw", "negative", "backward" })
        {
            p.Reset(); p.Frame(); int d = p.Begin();
            int target = RuntimeFixture.ClockCalls + 3; long floor = RuntimeFixture.Tick;
            RuntimeFixture.OnClock = () =>
            {
                if (RuntimeFixture.ClockCalls != target) return;
                if (fault == "throw") RuntimeFixture.ThrowClockAt = target;
                else { RuntimeFixture.ClockScript = new[] { fault == "negative" ? -1L : floor - 1 }; RuntimeFixture.ClockIndex = 0; }
            };
            p.End(d, true); RuntimeFixture.OnClock = null; RuntimeFixture.ThrowClockAt = -1; RuntimeFixture.ClockScript = null;
            p.Check(p.Bool("MeasurementInvalid") && p.State("Frames") == 2 && p.Number("snapshotCalls") == 4 && p.Number("FrameDepth") == 0, "periodic_check_clock_failure_keeps_complete_frame_" + fault);
            p.Accounting("periodic_check_" + fault); p.Flush(); p.Save("invalid-periodic-check-" + fault + ".log");
        }
        foreach (string counter in new[] { "reportTicks", "reportAttempts", "interval" })
        {
            p.Reset(); p.Frame(); p.Inject(counter, long.MaxValue); p.Flush();
            p.Check(p.Bool("MeasurementInvalid") && !p.HasEnd && p.State("Frames") == 1 && !p.Bool("finalized") && p.Number(counter) == long.MaxValue,
                "publish_counter_overflow_retains_data_and_corrupted_raw_counter_" + counter);
            p.Save("partial-counter-" + counter + ".log", reject: true);
        }
        foreach (string fault in new[] { "throw", "negative", "backward" })
        {
            p.Reset(); p.Frame(); RuntimeFixture.ThrowWriteAt = 1; int baseClocks = RuntimeFixture.ClockCalls;
            RuntimeFixture.OnClock = () =>
            {
                if (RuntimeFixture.ClockCalls != baseClocks + 3) return;
                if (fault == "throw") RuntimeFixture.ThrowClockAt = RuntimeFixture.ClockCalls;
                else { RuntimeFixture.ClockScript = new[] { fault == "negative" ? -1L : 0L }; RuntimeFixture.ClockIndex = 0; }
            };
            p.Flush();
            p.Check(p.Bool("MeasurementInvalid") && !p.HasEnd && p.State("Frames") == 1 && p.Number("reportFailures") == 1 && !p.Bool("reporting"),
                "failed_output_recovery_cost_clock_" + fault);
            p.Save("partial-recovery-clock-" + fault + ".log", reject: true);
            RuntimeFixture.OnClock = null; RuntimeFixture.ClockScript = null; RuntimeFixture.ThrowClockAt = RuntimeFixture.ThrowWriteAt = -1;
            p.ClearOutput(); p.Flush(); p.Check(p.HasEnd, "failed_output_recovery_clock_final_retry_" + fault); p.Save("invalid-recovery-clock-" + fault + ".log");
        }
        p.Reset(); p.Frame(); RuntimeFixture.Tick += RuntimeFixture.Frequency() * 5; p.Frame();
        int writes = RuntimeFixture.WriteCalls;
        RuntimeFixture.ThrowClockAt = RuntimeFixture.ClockCalls + 5; p.Frame();
        p.Check(p.Bool("MeasurementInvalid") && p.Bool("reportFloorPending") && RuntimeFixture.WriteCalls == writes,
            "deferred_cooldown_anchor_clock_failure_cannot_trigger_report");
        RuntimeFixture.ThrowClockAt = -1; p.Flush(); p.Check(p.Rows.Count(r => r.StartsWith("NX_PROFILE END ")) == 2, "final_flush_bypasses_pending_failed_cooldown_anchor"); p.Save("invalid-cooldown-anchor-final.log");
    }

    static void Reports(Session p)
    {
        p.Reset(); p.Frame(p.Body); p.Flush(); string normal = p.Output;
        p.Check(p.Rows.Length == 67 && p.Rows[0].StartsWith("NX_PROFILE BEGIN ") && p.Rows[^1].StartsWith("NX_PROFILE END "), "actual_runtime_compact_67_row_report");
        p.Check(p.State("Frames") == 0 && p.Bool("finalized"), "successful_END_clears_window_and_finalizes");
        int calls = RuntimeFixture.ClockCalls, writes = RuntimeFixture.WriteCalls; p.Flush();
        p.Check(p.Output == normal && RuntimeFixture.ClockCalls == calls && RuntimeFixture.WriteCalls == writes, "successful_final_flush_is_idempotent_without_extra_clocks");
        p.Reset(); p.Frame(p.Body); p.Flush(); p.Check(p.Output == normal, "actual_runtime_serialized_report_deterministic");
        p.Save("valid-deterministic.log");

        p.Reset(); p.Frame(); int reportStart = RuntimeFixture.ClockCalls; p.Flush(); int reportClocks = RuntimeFixture.ClockCalls - reportStart;
        int reportWrites = RuntimeFixture.WriteCalls;
        p.Check(reportClocks == 4, "four_publish_relevant_clocks_and_none_after_END");
        p.Check(p.Number("reportTicks") == 10 && p.Number("reportMax") == 10 &&
            p.Rows.Single(r => r.StartsWith("NX_PROFILE REPORT_COST ")).Contains("ticks_before_footer=10"),
            "report_body_cost_excludes_footer_and_is_not_subtracted_from_scopes");
        p.Receipts.Add(new { kind = "report_boundaries", reportClocks, reportWrites, rows = p.Rows.Length });
        for (int offset = 1; offset <= reportClocks; offset++)
        {
            p.Reset(); p.Frame(); p.Set("Active", true); p.Set("SampleSelected", true);
            RuntimeFixture.ThrowClockAt = RuntimeFixture.ClockCalls + offset; p.Flush();
            p.Check(!p.HasEnd && p.State("Frames") == 1 && !p.Bool("finalized") && p.Bool("MeasurementInvalid") && !p.Bool("reporting") && p.Bool("Active") && p.Bool("SampleSelected"), "report_clock_throw_retains_data_and_saved_flags_" + offset);
            p.Save("partial-clock-" + offset + ".log", reject: true);
            p.ClearOutput(); RuntimeFixture.ThrowClockAt = -1; p.Flush();
            p.Check(p.HasEnd && p.Bool("finalized") && p.State("Frames") == 0 && p.Number("reportFailures") >= 1, "report_clock_throw_retry_reports_failure_" + offset);
            p.Save("invalid-clock-recovered-" + offset + ".log");
        }
        foreach (string fault in new[] { "negative", "backward" }) for (int offset = 1; offset <= reportClocks; offset++)
        {
            p.Reset(); p.Frame(); long tick = RuntimeFixture.Tick;
            RuntimeFixture.ClockScript = Enumerable.Range(1, reportClocks).Select(i => tick + 10 * i).ToArray();
            RuntimeFixture.ClockScript[offset - 1] = fault == "negative" ? -1 : tick + 10 * (offset - 1) - 1;
            RuntimeFixture.ClockIndex = 0; int start = RuntimeFixture.ClockCalls; p.Flush();
            p.Check(p.Bool("MeasurementInvalid") && !p.Bool("reporting") && RuntimeFixture.ClockCalls - start <= reportClocks,
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
                p.ClearOutput(); RuntimeFixture.Tick = Math.Max(RuntimeFixture.Tick, RuntimeFixture.ClockScript!.Max()); RuntimeFixture.ClockScript = null;
                p.Flush(); p.Check(p.HasEnd, "report_clock_invalidity_retry_" + fault + offset); p.Save("invalid-report-retry-" + fault + "-" + offset + ".log");
            }
        }
        for (int offset = 1; offset <= reportWrites; offset++)
        {
            p.Reset(); p.Frame(); RuntimeFixture.ThrowWriteAt = offset; p.Flush();
            p.Check(!p.HasEnd && p.State("Frames") == 1 && !p.Bool("finalized") && p.Number("reportFailures") == 1 && p.Bool("MeasurementInvalid"), "output_failure_retains_unreported_counts_" + offset);
            p.Save("partial-output-" + offset + ".log", reject: true);
            p.ClearOutput(); RuntimeFixture.ThrowWriteAt = -1; p.Flush();
            p.Check(p.HasEnd && p.State("Frames") == 0 && p.Number("reportFailures") == 1, "output_failure_recovery_preserves_failure_receipt_" + offset); p.Save("invalid-output-recovered-" + offset + ".log");
        }
        p.Reset(); p.Frame(); RuntimeFixture.Tick += RuntimeFixture.Frequency() * 5; p.Frame();
        p.Check(p.HasEnd && p.Output.Contains(" final=0 ") && p.State("Frames") == 0, "periodic_report_before_empty_final");
        int periodicWrites = RuntimeFixture.WriteCalls; p.Flush();
        p.Check(RuntimeFixture.WriteCalls == periodicWrites + reportWrites && p.Rows.Count(r => r.StartsWith("NX_PROFILE END ")) == 2 && p.State("Frames") == 0 && p.Number("snapshotCalls") == 0, "empty_final_emits_no_fake_updates_or_snapshots");
        string periodicFinal = p.Output; p.Flush(); p.Check(p.Output == periodicFinal, "empty_final_flush_idempotent"); p.Save("valid-periodic-empty-final.log");
        p.Reset(); p.Frame(); RuntimeFixture.Tick += RuntimeFixture.Frequency() * 5;
        RuntimeFixture.OnWrite = n => { if (n == reportWrites) RuntimeFixture.Tick += RuntimeFixture.Frequency() * 10; };
        p.Frame(); p.Check(RuntimeFixture.WriteCalls == reportWrites, "slow_footer_first_report_complete");
        long startTick = p.Number("windowStart"); p.Frame();
        p.Check(RuntimeFixture.WriteCalls == reportWrites && p.Number("windowStart") == startTick, "slow_footer_next_root_anchors_cooldown_without_moving_window_start");
        RuntimeFixture.Tick += RuntimeFixture.Frequency() * 5 - 10000; p.Frame(); p.Check(RuntimeFixture.WriteCalls == reportWrites, "slow_footer_less_than_five_seconds_after_anchor_no_retry");
        RuntimeFixture.Tick += 10000; p.Frame(); p.Check(RuntimeFixture.WriteCalls == reportWrites * 2, "slow_footer_five_seconds_after_anchor_allows_report");
        p.Flush(); p.Save("valid-slow-footer.log");
        p.Reset(); p.Frame(); RuntimeFixture.ThrowWriteAt = 1; RuntimeFixture.Tick += RuntimeFixture.Frequency() * 5; p.Frame();
        long attempts = p.Number("reportAttempts"); for (int i = 0; i < 3; i++) p.Frame();
        p.Check(attempts == 1 && p.Number("reportAttempts") == 1 && p.Number("reportFailures") == 1 && p.State("Frames") == 5, "failed_periodic_report_not_retried_each_update");
        p.ClearOutput(); RuntimeFixture.ThrowWriteAt = -1; p.Flush(); p.Check(p.HasEnd, "final_flush_bypasses_failed_periodic_cooldown"); p.Save("invalid-periodic-failure-final.log");
        p.Reset(); p.Frame(); bool outputSuspended = false;
        RuntimeFixture.OnWrite = n => { if (n == 1) { outputSuspended = !p.Bool("Active") && !p.Bool("SampleSelected"); p.Frame(p.Body); } };
        p.Flush(); p.Check(outputSuspended && p.HasEnd && p.Number("reentrantFrames") == 1 && p.Number("FrameDepth") == 0, "output_reentry_suspends_observer_without_fake_update"); p.Save("valid-output-reentry.log");

        // Parser-negative records derive from actual generated output, not an invented baseline.
        p.SaveRaw("malformed-missing-END.log", string.Join('\n', normal.Split('\n').Where(r => !r.StartsWith("NX_PROFILE END "))) + "\n", true);
        string stateRow = normal.Split('\n').Single(r => r.StartsWith("NX_PROFILE STATE "));
        p.SaveRaw("malformed-duplicate-state.log", normal.Replace(stateRow, stateRow + "\n" + stateRow), true);
        p.SaveRaw("malformed-wrong-scope-label.log", normal.Replace("label=update", "label=not_update"), true);
        p.SaveRaw("malformed-missing-scope.log", string.Join('\n', normal.Split('\n').Where(r => !(r.StartsWith("NX_PROFILE SCOPE ") && r.Contains(" id=10 ")))) + "\n", true);
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
        public readonly Action Flush;
        public readonly List<object> Entries = new(), Receipts = new();
        public Session(Type type, List<string> list, string directory)
        {
            runtime = type; checks = list; output = directory; FirstCheck = list.Count;
            Begin = Method("FrameBegin").CreateDelegate<Func<int>>(); End = Method("FrameEnd").CreateDelegate<Action<int, bool>>();
            Enter = Method("Enter").CreateDelegate<Func<int, int>>(); Exit = Method("Exit").CreateDelegate<Action<int, bool>>();
            Flush = Method("Flush").CreateDelegate<Action>(); Directory.CreateDirectory(Path.Combine(directory, "report-fixtures"));
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
        public double SceneRange(int cohort, string range, string member) => Convert.ToDouble(Member(Member(Member(((Array)Get("Cohorts")).GetValue(cohort)!, "Scene"), range), member));
        public long Metric(bool window, int cohort, int id, string field) => NumberMember(((Array)Get(window ? "WindowMetrics" : "FrameMetrics")).GetValue((window ? cohort * 19 : 0) + id)!, field);
        public void SetArrayStruct(string arrayName, int index, string field, object value)
        {
            Array array = (Array)Get(arrayName); object item = array.GetValue(index)!; item.GetType().GetField(field, Instance)!.SetValue(item, value); array.SetValue(item, index); RuntimeFixture.RecordFault("state." + arrayName + "[" + index + "]." + field);
        }
        public void SetMetric(string array, int index, string field, long value) => SetArrayStruct(array, index, field, value);
        public void Inject(string name, object? value) { Set(name, value); RuntimeFixture.RecordFault("state." + name); }
        public object[] Metrics(string name) => ((Array)Get(name)).Cast<object>().ToArray();
        public bool MetricsEqual(object[] before, string name) => before.SequenceEqual(Metrics(name));
        public bool AllZero(string name) => Metrics(name).All(m => MetricFields.All(f => NumberMember(m, f) == 0));
        public bool Valid() => (bool)Call("ValidateSample")!;
        public void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Update runtime proof failed: " + name);
            checks.Add(name);
        }
        public void Reset([System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            RuntimeFixture.Scenario = "runtime_proof_line_" + line;
            foreach (FieldInfo field in runtime.GetFields(Static))
                if (!field.IsLiteral && !field.IsInitOnly) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
            RuntimeFixture.Reset();
        }
        public void Select() { Call("InitializeTiming"); Set("SampleSelected", true); Call("BeginSample"); }
        public void Body() { int u = Enter(1), t = Enter(10); Exit(t, true); Exit(u, true); }
        public void AllMetrics()
        {
            int before = Enter(18); Exit(before, true);
            int body = Enter(1), inWorld = Enter(2), tiles = Enter(3), world = Enter(4);
            for (int id = 5; id <= 9; id++) { int cookie = Enter(id); Exit(cookie, true); }
            Exit(world, true); Exit(tiles, true);
            for (int id = 10; id <= 13; id++) { int cookie = Enter(id); Exit(cookie, true); }
            Exit(inWorld, true);
            for (int id = 14; id <= 17; id++) { int cookie = Enter(id); Exit(cookie, true); }
            Exit(body, true);
        }
        public void Frame(Action? body = null, bool complete = true) { int d = Begin(); bool returned = false; try { body?.Invoke(); returned = complete; } finally { End(d, returned); } }
        public void AssertPartitions(string name)
        {
            long update = Metric(false, 0, 0, "InclusiveTicks"), body = Metric(false, 0, 1, "InclusiveTicks");
            Check(Enumerable.Range(0, 19).Sum(i => Metric(false, 0, i, "ExclusiveTicks")) == update,
                name + "_whole_root_exclusive_partition");
            Check(Enumerable.Range(0, 19).All(i => Metric(false, 0, i, "Calls") != 0 || MetricFields.All(f => Metric(false, 0, i, f) == 0)), name + "_unused_metrics_are_exact_zero");
            Receipts.Add(new { kind = "partition", name, updateTicks = update, bodyInclusiveTicks = body,
                metrics = Enumerable.Range(0, 19).Select(i => new { id = i, calls = Metric(false, 0, i, "Calls"), inclusive = Metric(false, 0, i, "InclusiveTicks"), exclusive = Metric(false, 0, i, "ExclusiveTicks") }).ToArray() });
        }
        public void Accounting(string name)
        {
            Check(State("Attempts") == State("Frames") + State("CaptureFailures") && State("Frames") == State("Completed") + State("Aborted") &&
                State("Eligible") <= State("Completed") && State("Eligible") == Enumerable.Range(0, 3).Sum(i => Cohort(i, "Frames")) &&
                State("SelectedAttempts") == State("DiscardedSelected") + Enumerable.Range(0, 3).Sum(i => Cohort(i, "Selected")) && Number("snapshotCalls") == 2 * State("Frames"), name + "_state_accounting");
            for (int i = 0; i < 3; i++) Check(Cohort(i, "Selected") == Cohort(i, "Valid") + Cohort(i, "Invalid") + Cohort(i, "Aborted") &&
                Cohort(i, "Selected") <= Cohort(i, "Frames") && Scene(i, "Samples") == 2 * Cohort(i, "Frames") && Metric(true, i, 0, "Calls") == Cohort(i, "Valid") &&
                Metric(true, i, 1, "Calls") >= Cohort(i, "Valid") - Cohort(i, "NoBody") &&
                (Cohort(i, "NoBody") != Cohort(i, "Valid") || Metric(true, i, 1, "Calls") == 0) &&
                Enumerable.Range(0, 19).Sum(id => Metric(true, i, id, "ExclusiveTicks")) == Metric(true, i, 0, "InclusiveTicks"), name + "_cohort_accounting_" + i);
        }
        public string Output => string.Join("\n", RuntimeFixture.Lines) + (RuntimeFixture.Lines.Count == 0 ? "" : "\n");
        public string[] Rows => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        public bool HasEnd => Rows.Any(r => r.StartsWith("NX_PROFILE END "));
        public void ClearOutput() => RuntimeFixture.Lines.Clear();
        public void Save(string file, bool reject = false) => SaveRaw(file, Output, reject);
        public void SaveRaw(string file, string text, bool reject)
        {
            File.WriteAllText(Path.Combine(output, "report-fixtures", file), text);
            if (!reject) Check(text.Contains("NX_PROFILE END "), "structurally_valid_log_has_END_" + file);
            Entries.Add(new { path = file, structurally_valid = !reject, measurement_valid = !reject && !text.Contains("measurement_invalid=1"), scenario = Path.GetFileNameWithoutExtension(file) });
        }
    }
}
