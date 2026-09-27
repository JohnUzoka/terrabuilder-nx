using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

internal static partial class Recipe55Template
{
    private static int ownerThread;
    private static StringBuilder? reportBuffer;
    private static char[]? outputBuffer;
    private static CultureInfo? reportCulture;
    private static int[]? cohortPhases;
    private static long frequency, windowStart, lastAttempt, interval, frameId, stateClockFloor;
    private static long ignoredThreads, reentrantFrames, reportFailures, reportAttempts, reportTicks, reportMax;
    private static long snapshotCalls, snapshotTicks, snapshotMax;
    private static bool reporting, finalized, reportFloorPending;
    [ThreadStatic] private static Recipe55State frameBeginState;
    [ThreadStatic] private static bool rootOwned, rootReady, frameSelected, savedActive, savedSelected, nestedActive, nestedSelected;
    [ThreadStatic] private static long frameSnapshotTicks, frameSnapshotStop;

    // Allocation is confined to the protected first owner root and report formatting.
    private static void InitializeState()
    {
        if (reportBuffer != null) return;
        StringBuilder builder = new StringBuilder(65536, 65536);
        char[] chars = new char[65536];
        int[] phases = new int[CohortCount];
        Recipe55Cohort[] cohorts = new Recipe55Cohort[CohortCount];
        CultureInfo culture = CultureInfo.InvariantCulture;
        long rate = Stopwatch.Frequency;
        long now = Stopwatch.GetTimestamp();
        if (rate <= 0 || rate > long.MaxValue / 5 || now < 0) throw new InvalidOperationException();
        InitializeTiming();
        if (FrameMetrics == null || WindowMetrics == null) throw new InvalidOperationException();
        outputBuffer = chars; reportCulture = culture; cohortPhases = phases; Cohorts = cohorts; frequency = rate;
        windowStart = lastAttempt = stateClockFloor = now;
        reportBuffer = builder;
    }

    private static void CountOtherThread()
    {
        long before;
        do
        {
            before = Interlocked.Read(ref ignoredThreads);
            if (before == long.MaxValue) return;
        } while (Interlocked.CompareExchange(ref ignoredThreads, before + 1, before) != before);
    }

    internal static int FrameBegin()
    {
        int previous = FrameDepth;
        try
        {
            if (previous == int.MaxValue) { MeasurementInvalid = true; return previous; }
            FrameDepth = previous + 1;
            if (previous != 0)
            {
                if (previous == 1)
                {
                    nestedActive = Active; nestedSelected = SampleSelected;
                    Active = false; SampleSelected = false;
                }
                if (Thread.CurrentThread.ManagedThreadId == ownerThread) Add(ref reentrantFrames, 1);
                return previous;
            }
            savedActive = Active; savedSelected = SampleSelected;
            rootOwned = rootReady = frameSelected = false;
            Active = SampleSelected = false;
            int thread = Thread.CurrentThread.ManagedThreadId;
            int owner = Interlocked.CompareExchange(ref ownerThread, thread, 0);
            if (owner != 0 && owner != thread) { CountOtherThread(); return previous; }
            if (reporting || finalized) { Add(ref reentrantFrames, 1); return previous; }
            if (!Fits(WindowState.Attempts, 1)) { MeasurementInvalid = true; return previous; }
            ++WindowState.Attempts;
            rootOwned = true;
            InitializeState();
            frameSnapshotTicks = frameSnapshotStop = 0;
            if (!TryCaptureState(out frameBeginState, out frameSnapshotTicks, out frameSnapshotStop, stateClockFloor)) return previous;
            rootReady = true;
            int frameCohort = CohortOf(ref frameBeginState);
            if (frameCohort < 0) return previous;
            int phase = cohortPhases![frameCohort];
            cohortPhases[frameCohort] = (phase + 1) & SampleMask;
            if (phase != 0) return previous;
            if (!Fits(WindowState.SelectedAttempts, 1)) { MeasurementInvalid = true; return previous; }
            ++WindowState.SelectedAttempts;
            frameSelected = SampleSelected = true;
            BeginSample();
        }
        catch { MeasurementInvalid = true; }
        return previous;
    }

    internal static void FrameEnd(int previousDepth, bool completed)
    {
        if (previousDepth != 0)
        {
            FrameDepth = previousDepth;
            if (previousDepth == 1) { Active = nestedActive; SampleSelected = nestedSelected; }
            return;
        }
        try
        {
            if (!rootOwned) return;
            if (frameSelected) EndSample(completed);
            Active = SampleSelected = false;
            Recipe55State end = default;
            long endTicks = 0, endStop = 0;
            long captureFloor = frameSelected && timingClockSeen && timingLast > frameSnapshotStop ? timingLast : frameSnapshotStop;
            bool captured = rootReady && TryCaptureState(out end, out endTicks, out endStop, captureFloor);
            if (!captured)
            {
                Add(ref WindowState.CaptureFailures, 1);
                if (frameSelected) Add(ref WindowState.DiscardedSelected, 1);
            }
            else
            {
                // The two snapshots and their costs are one transaction. Failed pairs
                // never donate half a count, half a cost, flags, scene data or scopes.
                if (!Fits(frameSnapshotTicks, endTicks) || !Fits(snapshotTicks, frameSnapshotTicks + endTicks) ||
                    !Fits(snapshotCalls, 2) || !Fits(WindowState.Frames, 1) || !Fits(frameId, 1))
                {
                    MeasurementInvalid = true;
                    Add(ref WindowState.CaptureFailures, 1);
                    if (frameSelected) Add(ref WindowState.DiscardedSelected, 1);
                }
                else
                {
                    snapshotCalls += 2; snapshotTicks += frameSnapshotTicks + endTicks;
                    if (frameSnapshotTicks > snapshotMax) snapshotMax = frameSnapshotTicks;
                    if (endTicks > snapshotMax) snapshotMax = endTicks;
                    stateClockFloor = endStop;
                    ++frameId; ++WindowState.Frames;
                    if (completed) Add(ref WindowState.Completed, 1); else Add(ref WindowState.Aborted, 1);
                    int flags = frameBeginState.Flags | end.Flags;
                    RecordFlags(flags, frameBeginState.Flags, end.Flags);
                    bool stable = Stable(ref frameBeginState, ref end);
                    if (!stable) Add(ref WindowState.Changed, 1);
                    int cohort = completed && stable ? CohortOf(ref end) : -1;
                    if (cohort >= 0)
                    {
                        Add(ref WindowState.Eligible, 1);
                        ref Recipe55Cohort value = ref Cohorts![cohort];
                        Add(ref value.Frames, 1);
                        RecordScene(ref value.Scene, ref frameBeginState); RecordScene(ref value.Scene, ref end);
                        if (frameSelected) DonateSample(cohort);
                    }
                    else if (frameSelected) Add(ref WindowState.DiscardedSelected, 1);
                }
            }
            if (reportBuffer != null) CheckReport();
        }
        catch { MeasurementInvalid = true; }
        finally
        {
            rootOwned = rootReady = frameSelected = false; FrameDepth = previousDepth;
            Active = savedActive; SampleSelected = savedSelected;
        }
    }

    private static void DonateSample(int cohort)
    {
        ref Recipe55Cohort value = ref Cohorts![cohort];
        Add(ref value.Selected, 1);
        if (FrameScopeAborted) { Add(ref value.Aborted, 1); return; }
        if (!ValidateSample()) { Add(ref value.Invalid, 1); return; }
        bool noUI = FrameMetrics![UIMetric].Calls == 0;
        if (!Fits(value.Valid, 1) || !Fits(value.ClockCalls, 1) || !Fits(value.ClockTicks, FrameClockPairTicks) ||
            (noUI && !Fits(value.NoUI, 1)))
        {
            MeasurementInvalid = true; Add(ref value.Invalid, 1); return;
        }
        if (!CommitSample(cohort)) { Add(ref value.Invalid, 1); return; }
        ++value.Valid; ++value.ClockCalls; value.ClockTicks += FrameClockPairTicks;
        if (FrameClockPairTicks > value.ClockMax) value.ClockMax = FrameClockPairTicks;
        if (noUI) ++value.NoUI;
    }

    private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

    private static bool TryCaptureState(out Recipe55State state, out long ticks, out long stop, long floor)
    {
        state = default; ticks = stop = 0;
        try
        {
            long start = Stopwatch.GetTimestamp();
            if (start < 0 || start < floor) throw new InvalidOperationException();
            if (Recipe55MainShape.gameMenu) state.Flags |= MenuFlag;
            if (Recipe55MainShape.gamePaused) state.Flags |= PausedFlag;
            if (Recipe55MainShape.playerInventory) state.Flags |= InventoryFlag;
            if (Recipe55MainShape.mapFullscreen) state.Flags |= MapFlag;
            if (Recipe55MainShape.autoPause) state.Flags |= AutoPauseFlag;
            state.PlayerId = Recipe55MainShape.myPlayer;
            Recipe55PlayerShape[]? players = Recipe55MainShape.player;
            Recipe55PlayerShape? player = players != null && state.PlayerId >= 0 && state.PlayerId < players.Length ? players[state.PlayerId] : null;
            if (player == null) state.Flags |= InvalidPlayerFlag;
            else
            {
                if (player.dead) state.Flags |= DeadFlag;
                if (player.ghost) state.Flags |= GhostFlag;
                if (player.spectating >= 0) state.Flags |= SpectatorFlag;
                state.PlayerX = player.position.X; state.PlayerY = player.position.Y;
                if (!Finite(state.PlayerX) || !Finite(state.PlayerY)) state.Flags |= InvalidPlayerFlag;
            }
            state.CameraX = Recipe55MainShape.screenPosition.X; state.CameraY = Recipe55MainShape.screenPosition.Y;
            state.Width = Recipe55MainShape.screenWidth; state.Height = Recipe55MainShape.screenHeight;
            state.ZoomTarget = Recipe55MainShape.GameZoomTarget; state.FrameSkip = Recipe55MainShape.FrameSkipMode;
            state.UIScale = Recipe55MainShape.UIScale;
            state.WorldTime = Recipe55MainShape.time; state.DayTime = Recipe55MainShape.dayTime;
            state.MouseX = Recipe55MainShape.mouseX; state.MouseY = Recipe55MainShape.mouseY;
            Recipe55ItemShape? hover = Recipe55MainShape.HoverItem, mouse = Recipe55MainShape.mouseItem;
            state.HoverType = hover == null ? -1 : hover.type; state.HoverPrefix = hover == null ? -1 : hover.prefix;
            state.MouseItemType = mouse == null ? -1 : mouse.type; state.MouseItemPrefix = mouse == null ? -1 : mouse.prefix;
            Recipe55ItemShape? guide = Recipe55MainShape.guideItem;
            state.GuideType = guide == null ? -1 : guide.type;
            state.AvailableRecipes = Recipe55MainShape.numAvailableRecipes;
            if (!Finite(state.CameraX) || !Finite(state.CameraY) || !Finite(state.ZoomTarget) || state.ZoomTarget <= 0 ||
                !Finite(state.UIScale) || state.UIScale <= 0 || !Finite(state.WorldTime) || state.Width <= 0 || state.Height <= 0)
                state.Flags |= InvalidSceneFlag;
            if (Recipe55MainShape.myPlayer != state.PlayerId) state.Flags |= InvalidPlayerFlag;
            stop = Stopwatch.GetTimestamp();
            if (stop < start) throw new InvalidOperationException();
            ticks = stop - start;
            return true;
        }
        catch
        {
            state = default; ticks = stop = 0; MeasurementInvalid = true;
            return false;
        }
    }

    private static int CohortOf(ref Recipe55State state)
    {
        if ((state.Flags & IneligibleMask) != 0) return -1;
        if ((state.Flags & InventoryFlag) != 0) return (state.Flags & PausedFlag) != 0 ? 2 : 1;
        return (state.Flags & PausedFlag) == 0 ? 0 : -1;
    }

    private static bool Stable(ref Recipe55State begin, ref Recipe55State end)
    {
        return begin.Flags == end.Flags && begin.PlayerId == end.PlayerId && begin.Width == end.Width &&
            begin.Height == end.Height && begin.FrameSkip == end.FrameSkip && begin.ZoomTarget == end.ZoomTarget &&
            begin.UIScale == end.UIScale && begin.DayTime == end.DayTime;
    }

    private static void RecordFlags(int flags, int begin, int end)
    {
        if (!WindowState.HasFlags)
        {
            WindowState.HasFlags = true; WindowState.FirstFlags = begin; WindowState.FirstFrameId = frameId;
        }
        WindowState.LastFlags = end; WindowState.FlagsOr |= flags; WindowState.LastFrameId = frameId;
        if ((flags & MenuFlag) != 0) Add(ref WindowState.Menu, 1);
        if ((flags & PausedFlag) != 0) Add(ref WindowState.Paused, 1);
        if ((flags & InventoryFlag) != 0) Add(ref WindowState.Inventory, 1);
        if ((flags & MapFlag) != 0) Add(ref WindowState.Map, 1);
        if ((flags & DeadFlag) != 0) Add(ref WindowState.Dead, 1);
        if ((flags & GhostFlag) != 0) Add(ref WindowState.Ghost, 1);
        if ((flags & SpectatorFlag) != 0) Add(ref WindowState.Spectator, 1);
        if ((flags & AutoPauseFlag) != 0) Add(ref WindowState.AutoPause, 1);
        if ((flags & InvalidPlayerFlag) != 0) Add(ref WindowState.InvalidPlayer, 1);
        if ((flags & InvalidSceneFlag) != 0) Add(ref WindowState.InvalidScene, 1);
    }

    private static void Range(ref Recipe55Range range, double value)
    {
        if (!range.Used) { range.Used = true; range.First = range.Min = range.Max = value; }
        else { if (value < range.Min) range.Min = value; if (value > range.Max) range.Max = value; }
        range.Last = value;
    }

    private static void RecordScene(ref Recipe55Scene scene, ref Recipe55State state)
    {
        if (scene.Samples != 0)
        {
            if (scene.LastPlayerId != state.PlayerId) Add(ref scene.PlayerChanged, 1);
            if (scene.PlayerX.Last != state.PlayerX || scene.PlayerY.Last != state.PlayerY) Add(ref scene.PlayerMoved, 1);
            if (scene.CameraX.Last != state.CameraX || scene.CameraY.Last != state.CameraY) Add(ref scene.CameraMoved, 1);
            if (scene.MouseX.Last != state.MouseX || scene.MouseY.Last != state.MouseY) Add(ref scene.MouseMoved, 1);
            if (scene.LastDay != state.DayTime) Add(ref scene.DayChanged, 1);
            if (scene.LastHoverType != state.HoverType || scene.LastHoverPrefix != state.HoverPrefix) Add(ref scene.HoverChanged, 1);
            if (scene.LastMouseItemType != state.MouseItemType || scene.LastMouseItemPrefix != state.MouseItemPrefix) Add(ref scene.MouseItemChanged, 1);
        }
        else
        {
            scene.FirstPlayerId = state.PlayerId;
            scene.FirstHoverType = state.HoverType; scene.FirstHoverPrefix = state.HoverPrefix;
            scene.FirstMouseItemType = state.MouseItemType; scene.FirstMouseItemPrefix = state.MouseItemPrefix;
        }
        scene.LastPlayerId = state.PlayerId; scene.LastDay = state.DayTime;
        scene.LastHoverType = state.HoverType; scene.LastHoverPrefix = state.HoverPrefix;
        scene.LastMouseItemType = state.MouseItemType; scene.LastMouseItemPrefix = state.MouseItemPrefix;
        Add(ref scene.Samples, 1);
        if (state.DayTime) Add(ref scene.Day, 1); else Add(ref scene.Night, 1);
        Range(ref scene.PlayerX, state.PlayerX); Range(ref scene.PlayerY, state.PlayerY);
        Range(ref scene.CameraX, state.CameraX); Range(ref scene.CameraY, state.CameraY);
        Range(ref scene.ZoomTarget, state.ZoomTarget); Range(ref scene.UIScale, state.UIScale);
        Range(ref scene.Width, state.Width); Range(ref scene.Height, state.Height); Range(ref scene.FrameSkip, state.FrameSkip);
        Range(ref scene.WorldTime, state.WorldTime); Range(ref scene.MouseX, state.MouseX); Range(ref scene.MouseY, state.MouseY);
        Range(ref scene.GuideType, state.GuideType); Range(ref scene.AvailableRecipes, state.AvailableRecipes);
    }

    private static void CheckReport()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < stateClockFloor || now < lastAttempt) { MeasurementInvalid = true; return; }
        stateClockFloor = now;
        if (reportFloorPending) { lastAttempt = now; reportFloorPending = false; }
        else if (now - lastAttempt >= frequency * 5) Report(now, false);
    }

    internal static void Flush()
    {
        bool active = Active, selected = SampleSelected;
        try
        {
            Active = SampleSelected = false;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread || reportBuffer == null || reporting || finalized || FrameDepth != 0) return;
            long now;
            try { now = Stopwatch.GetTimestamp(); }
            catch { MeasurementInvalid = true; Add(ref reportAttempts, 1); Add(ref reportFailures, 1); return; }
            if (Report(now, true)) finalized = true;
        }
        catch { MeasurementInvalid = true; }
        finally { Active = active; SampleSelected = selected; }
    }

    private static void Row(string kind, long id) { reportBuffer!.Append("NX_PROFILE ").Append(kind); Number("interval", id); }
    private static void Text(string name, string value) { reportBuffer!.Append(' ').Append(name).Append('=').Append(value); }
    private static void Number(string name, long value) { Text(name, value.ToString(reportCulture)); }
    private static void EndRow() { reportBuffer!.Append('\n'); }
    private static void EmitRange(string name, ref Recipe55Range range)
    {
        Text(name + "_first", range.Used ? range.First.ToString("R", reportCulture) : "null");
        Text(name + "_last", range.Used ? range.Last.ToString("R", reportCulture) : "null");
        Text(name + "_min", range.Used ? range.Min.ToString("R", reportCulture) : "null");
        Text(name + "_max", range.Used ? range.Max.ToString("R", reportCulture) : "null");
    }
    private static void WriteBuffer()
    {
        int length = reportBuffer!.Length;
        if (length > outputBuffer!.Length) throw new InvalidOperationException();
        reportBuffer.CopyTo(0, outputBuffer, 0, length);
        Console.Write(outputBuffer, 0, length);
        reportBuffer.Length = 0;
    }

    private static string CohortName(int id)
    {
        switch (id) { case 0: return "world"; case 1: return "inventory"; default: return "inventory_paused"; }
    }
    private static string MetricName(int id)
    {
        switch (id)
        {
            case 0: return "frame_draw"; case 1: return "ui"; case 2: return "inventory"; case 3: return "recipe_refresh";
            case 4: return "clear_available"; case 5: return "collect_items"; case 6: return "collect_guide"; case 7: return "refocus";
            case 8: return "reposition"; case 9: return "crafting_draw"; default: return "item_slot";
        }
    }
    private static void EmitState(long id)
    {
        Row("STATE", id);
        Number("attempted_frames", WindowState.Attempts); Number("frames", WindowState.Frames); Number("completed", WindowState.Completed); Number("aborted", WindowState.Aborted);
        Number("eligible", WindowState.Eligible); Number("excluded", WindowState.Frames - WindowState.Eligible); Number("state_changed", WindowState.Changed);
        Number("capture_failure_frames", WindowState.CaptureFailures); Number("selected_attempts", WindowState.SelectedAttempts); Number("discarded_selected", WindowState.DiscardedSelected);
        Number("invalid_player", WindowState.InvalidPlayer); Number("invalid_scene", WindowState.InvalidScene);
        Number("menu", WindowState.Menu); Number("paused", WindowState.Paused); Number("inventory", WindowState.Inventory); Number("map", WindowState.Map);
        Number("dead", WindowState.Dead); Number("ghost", WindowState.Ghost); Number("spectator", WindowState.Spectator); Number("autopause", WindowState.AutoPause);
        Number("first_flags", WindowState.FirstFlags); Number("last_flags", WindowState.LastFlags); Number("union_flags", WindowState.FlagsOr);
        Number("first_frame_id", WindowState.FirstFrameId); Number("last_frame_id", WindowState.LastFrameId);
        Number("ignored_threads_lifetime", Interlocked.Read(ref ignoredThreads)); Number("reentrant_frames_lifetime", reentrantFrames); Number("report_failures_lifetime", reportFailures);
        Number("depth_overflows_lifetime", DepthOverflows); Number("unbalanced_scopes_lifetime", UnbalancedScopes); Number("scope_failures_lifetime", ScopeFailures); Number("max_depth_lifetime", MaxObservedDepth);
        Number("snapshot_calls", snapshotCalls); Number("snapshot_ticks", snapshotTicks); Number("snapshot_max_ticks", snapshotMax); Number("measurement_invalid", MeasurementInvalid ? 1 : 0); EndRow();
    }
    private static void EmitScene(long id, string name, ref Recipe55Scene scene)
    {
        Row("SCENE", id); Text("cohort", name); Number("samples", scene.Samples);
        Number("player_id_first", scene.FirstPlayerId); Number("player_id_last", scene.LastPlayerId); Number("player_id_changes", scene.PlayerChanged);
        Number("player_position_changes", scene.PlayerMoved); Number("camera_position_changes", scene.CameraMoved); Number("mouse_position_changes", scene.MouseMoved);
        Number("day_samples", scene.Day); Number("night_samples", scene.Night); Number("day_changes", scene.DayChanged); Number("hover_changes", scene.HoverChanged); Number("mouse_item_changes", scene.MouseItemChanged);
        Number("hover_type_first", scene.FirstHoverType); Number("hover_type_last", scene.LastHoverType); Number("hover_prefix_first", scene.FirstHoverPrefix); Number("hover_prefix_last", scene.LastHoverPrefix);
        Number("mouse_item_type_first", scene.FirstMouseItemType); Number("mouse_item_type_last", scene.LastMouseItemType); Number("mouse_item_prefix_first", scene.FirstMouseItemPrefix); Number("mouse_item_prefix_last", scene.LastMouseItemPrefix);
        EmitRange("player_x", ref scene.PlayerX); EmitRange("player_y", ref scene.PlayerY); EmitRange("camera_x", ref scene.CameraX); EmitRange("camera_y", ref scene.CameraY);
        EmitRange("zoom_target", ref scene.ZoomTarget); EmitRange("ui_scale", ref scene.UIScale); EmitRange("width", ref scene.Width); EmitRange("height", ref scene.Height);
        EmitRange("skip", ref scene.FrameSkip); EmitRange("world_time", ref scene.WorldTime); EmitRange("mouse_x", ref scene.MouseX); EmitRange("mouse_y", ref scene.MouseY);
        EmitRange("guide_type", ref scene.GuideType); EmitRange("available_recipes", ref scene.AvailableRecipes); EndRow();
    }
    private static void EmitCohort(long id, int cohort)
    {
        string name = CohortName(cohort);
        ref Recipe55Cohort value = ref Cohorts![cohort];
        Row("COHORT", id); Text("name", name); Number("frames", value.Frames); Number("selected_frames", value.Selected);
        Number("valid_samples", value.Valid); Number("invalid_samples", value.Invalid); Number("aborted_samples", value.Aborted);
        Number("no_ui_samples", value.NoUI);
        Number("clock_pairs", value.ClockCalls); Number("clock_ticks", value.ClockTicks); Number("clock_max_ticks", value.ClockMax); EndRow();
        EmitScene(id, name, ref value.Scene);
        for (int metric = 0; metric < MetricCount; ++metric)
        {
            ref Recipe55Metric item = ref WindowMetrics![cohort * MetricCount + metric];
            Row("SCOPE", id); Text("cohort", name); Number("id", metric); Text("label", MetricName(metric)); Number("calls", item.Calls);
            Number("inclusive_ticks", item.InclusiveTicks); Number("exclusive_ticks", item.ExclusiveTicks);
            Number("max_inclusive_ticks", item.InclusiveMax); Number("max_exclusive_ticks", item.ExclusiveMax); EndRow();
        }
    }

    private static long Sum(long left, long right)
    {
        if (!Fits(left, right)) throw new InvalidOperationException();
        return left + right;
    }
    private static void ValidateWindow()
    {
        if (WindowState.Attempts != Sum(WindowState.Frames, WindowState.CaptureFailures) ||
            WindowState.Frames != Sum(WindowState.Completed, WindowState.Aborted) || WindowState.Eligible < 0 || WindowState.Eligible > WindowState.Completed ||
            WindowState.Changed < 0 || WindowState.Changed > WindowState.Frames || snapshotCalls != Sum(WindowState.Frames, WindowState.Frames) ||
            snapshotTicks < 0 || snapshotMax < 0 || snapshotMax > snapshotTicks || (snapshotCalls == 0 && snapshotTicks != 0)) throw new InvalidOperationException();
        long frames = 0, selected = WindowState.DiscardedSelected;
        for (int cohort = 0; cohort < CohortCount; ++cohort)
        {
            ref Recipe55Cohort value = ref Cohorts![cohort];
            frames = Sum(frames, value.Frames); selected = Sum(selected, value.Selected);
            if (value.Selected != Sum(Sum(value.Valid, value.Invalid), value.Aborted) || value.Selected > value.Frames || value.NoUI < 0 || value.NoUI > value.Valid ||
                value.ClockCalls != value.Valid || value.ClockTicks < 0 || value.ClockMax < 0 || value.ClockMax > value.ClockTicks ||
                (value.ClockCalls == 0 && value.ClockTicks != 0) || value.Scene.Samples != Sum(value.Frames, value.Frames) || value.Scene.Samples != Sum(value.Scene.Day, value.Scene.Night))
                throw new InvalidOperationException();
            int offset = cohort * MetricCount;
            ref Recipe55Metric root = ref WindowMetrics![offset];
            ref Recipe55Metric ui = ref WindowMetrics[offset + UIMetric];
            if (root.Calls != value.Valid || ui.Calls != value.Valid - value.NoUI || root.InclusiveTicks != Sum(root.ExclusiveTicks, ui.InclusiveTicks)) throw new InvalidOperationException();
            long exclusive = 0, children = 0;
            for (int metric = 0; metric < MetricCount; ++metric)
            {
                ref Recipe55Metric item = ref WindowMetrics[offset + metric];
                if (item.Calls < 0 || item.InclusiveTicks < 0 || item.ExclusiveTicks < 0 || item.InclusiveMax < 0 || item.ExclusiveMax < 0 ||
                    item.ExclusiveTicks > item.InclusiveTicks || item.InclusiveMax > item.InclusiveTicks || item.ExclusiveMax > item.ExclusiveTicks || item.ExclusiveMax > item.InclusiveMax ||
                    (item.Calls == 0 && (item.InclusiveTicks != 0 || item.ExclusiveTicks != 0 || item.InclusiveMax != 0 || item.ExclusiveMax != 0))) throw new InvalidOperationException();
                exclusive = Sum(exclusive, item.ExclusiveTicks);
                if (metric > UIMetric) children = Sum(children, item.ExclusiveTicks);
            }
            if (exclusive != root.InclusiveTicks || ui.InclusiveTicks != Sum(ui.ExclusiveTicks, children)) throw new InvalidOperationException();
        }
        if (frames != WindowState.Eligible || selected != WindowState.SelectedAttempts) throw new InvalidOperationException();
    }

    private static bool Report(long now, bool final)
    {
        if (reporting) return false;
        bool active = Active, selected = SampleSelected;
        reporting = true; Active = SampleSelected = false;
        long start = -1, reportBoundary = stateClockFloor > lastAttempt ? stateClockFloor : lastAttempt;
        bool costRecorded = false, emitted = false;
        Add(ref reportAttempts, 1);
        try
        {
            if (now < 0 || now < windowStart || now < lastAttempt || now < stateClockFloor || interval == long.MaxValue) throw new InvalidOperationException();
            reportBoundary = now;
            start = Stopwatch.GetTimestamp();
            if (start < now) throw new InvalidOperationException();
            reportBoundary = start;
            ValidateWindow();
            long id = interval + 1;
            if (reportAttempts != Sum(id, reportFailures)) throw new InvalidOperationException();
            reportBuffer!.Length = 0;
            Row("BEGIN", id); Number("version", Version); Text("schema", "recipe_costs"); Number("final", final ? 1 : 0);
            Number("frequency", frequency); Number("wall_ticks", now - windowStart); Number("start_tick", windowStart); Number("end_tick", now);
            Number("sample_denominator", SampleMask + 1); Number("metric_count", MetricCount); Number("cohort_count", CohortCount); EndRow();
            EmitState(id);
            for (int cohort = 0; cohort < CohortCount; ++cohort) EmitCohort(id, cohort);
            WriteBuffer();
            long stop = Stopwatch.GetTimestamp();
            if (stop < start) throw new InvalidOperationException();
            reportBoundary = stop;
            long cost = stop - start;
            if (!Fits(reportTicks, cost)) throw new InvalidOperationException();
            reportTicks += cost; if (cost > reportMax) reportMax = cost;
            costRecorded = true;
            long finalStop = Stopwatch.GetTimestamp();
            if (finalStop < stop) throw new InvalidOperationException();
            reportBoundary = finalStop;
            Row("REPORT_COST", id); Number("ticks_before_footer", cost); Number("lifetime_ticks_before_footers", reportTicks); Number("lifetime_max_ticks", reportMax);
            Number("reports", id); Number("attempts", reportAttempts); Number("failures_lifetime", reportFailures); Number("measurement_invalid", MeasurementInvalid ? 1 : 0); EndRow();
            Row("END", id); EndRow(); WriteBuffer();
            interval = id;
            WindowState = default;
            for (int cohort = 0; cohort < CohortCount; ++cohort) Cohorts![cohort] = default;
            for (int metric = 0; metric < CohortCount * MetricCount; ++metric) WindowMetrics![metric] = default;
            snapshotCalls = snapshotTicks = snapshotMax = 0;
            emitted = true;
            return true;
        }
        catch
        {
            MeasurementInvalid = true; Add(ref reportFailures, 1);
            if (!costRecorded && start >= 0)
            {
                try
                {
                    long stop = Stopwatch.GetTimestamp();
                    if (stop < start || stop < reportBoundary) MeasurementInvalid = true;
                    else
                    {
                        long cost = stop - start;
                        if (Fits(reportTicks, cost)) { reportTicks += cost; if (cost > reportMax) reportMax = cost; }
                        else MeasurementInvalid = true;
                        reportBoundary = stop;
                    }
                }
                catch { MeasurementInvalid = true; }
            }
            return false;
        }
        finally
        {
            // The next root timestamp anchors cooldown after output returned. Do
            // not move the new window start, or read an unreportable clock after END.
            lastAttempt = stateClockFloor = reportBoundary; reportFloorPending = true;
            if (emitted) windowStart = reportBoundary;
            reporting = false; Active = active; SampleSelected = selected;
        }
    }
}
