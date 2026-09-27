using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

internal static partial class Render53Template
{
    private static int ownerThread;
    private static StringBuilder? reportBuffer;
    private static char[]? outputBuffer;
    private static CultureInfo? reportCulture;
    private static long frequency, windowStart, lastAttempt, interval, frameId;
    private static long ignoredThreads, reentrantFrames, reportFailures, reportAttempts, reportTicks, reportMax;
    private static long snapshotCalls, snapshotTicks, snapshotMax, captureFailureFrames;
    private static bool reporting, finalized, reportFloorPending;
    [ThreadStatic] private static Render53State frameBeginState;
    [ThreadStatic] private static bool rootReady, rootBatchOwned, frameObserverInvalid, savedCollecting, savedPending, nestedCollecting, nestedPending;
    [ThreadStatic] private static Render53Context savedContext;
    [ThreadStatic] private static long frameSnapshotCalls, frameSnapshotTicks, frameSnapshotMax, frameSnapshotStop;

    // No allocating static initializer. Only protected root entry initializes buffers.
    private static void InitializeState()
    {
        if (reportBuffer != null) return;
        StringBuilder builder = new StringBuilder(16384);
        char[] chars = new char[16384];
        CultureInfo culture = CultureInfo.InvariantCulture;
        long rate = Stopwatch.Frequency;
        long now = Stopwatch.GetTimestamp();
        if (rate <= 0 || rate > long.MaxValue / 5 || now < 0) throw new InvalidOperationException();
        InitializeBatch();
        outputBuffer = chars; reportCulture = culture; frequency = rate;
        windowStart = lastAttempt = now;
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
        if (previous == int.MaxValue) return previous;
        FrameDepth = previous + 1;
        if (previous != 0)
        {
            if (previous == 1)
            {
                nestedCollecting = FrameCollecting; nestedPending = Pending;
                FrameCollecting = false; Pending = false;
            }
            if (Thread.CurrentThread.ManagedThreadId == ownerThread) Add(ref reentrantFrames, 1);
            return previous;
        }
        savedCollecting = FrameCollecting; savedPending = Pending; savedContext = current;
        rootReady = rootBatchOwned = false; FrameCollecting = false; Pending = false; current = default;
        try
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            int owner = Interlocked.CompareExchange(ref ownerThread, thread, 0);
            if (owner != 0 && owner != thread) { CountOtherThread(); return previous; }
            if (reporting || finalized) { Add(ref reentrantFrames, 1); return previous; }
            frameObserverInvalid = false;
            InitializeState();
            ResetBatchFrame(); rootBatchOwned = true;
            FrameSolid = default; FrameNonSolid = default;
            frameSnapshotCalls = frameSnapshotTicks = frameSnapshotMax = frameSnapshotStop = 0;
            if (!TryCaptureState(out frameBeginState)) return previous;
            rootReady = true;
            FrameCollecting = (frameBeginState.Flags & IneligibleMask) == 0;
            if (FrameCollecting) BeginBatchFrame();
        }
        catch
        {
            RuntimeFault();
            Add(ref captureFailureFrames, 1);
            if (reportBuffer == null) Interlocked.CompareExchange(ref ownerThread, 0, Thread.CurrentThread.ManagedThreadId);
        }
        return previous;
    }

    internal static void FrameEnd(int previousDepth, bool completed)
    {
        if (previousDepth != 0)
        {
            FrameDepth = previousDepth;
            if (previousDepth == 1) { FrameCollecting = nestedCollecting; Pending = nestedPending; }
            return;
        }
        try
        {
            FrameCollecting = false; Pending = false;
            if (!rootReady) return;
            Render53State end;
            if (!TryCaptureState(out end)) return;
            Add(ref snapshotCalls, frameSnapshotCalls); Add(ref snapshotTicks, frameSnapshotTicks);
            if (frameSnapshotMax > snapshotMax) snapshotMax = frameSnapshotMax;
            Add(ref frameId, 1);
            Add(ref WindowState.Frames, 1);
            if (completed) Add(ref WindowState.Completed, 1); else Add(ref WindowState.Aborted, 1);
            int flags = frameBeginState.Flags | end.Flags;
            RecordFlags(flags, frameBeginState.Flags, end.Flags);
            bool stable = Stable(ref frameBeginState, ref end);
            if (!stable) Add(ref WindowState.Changed, 1);
            // Observe the final root clock before provisional coarse data is donated.
            // A failed observer must not lose the already captured tile/state frame.
            long now = -1;
            try
            {
                now = Stopwatch.GetTimestamp();
                if (now < lastAttempt || now < frameSnapshotStop) RuntimeFault();
            }
            catch { RuntimeFault(); }
            if (completed && stable && (flags & IneligibleMask) == 0 && PrepareBatchFrame())
            {
                Add(ref WindowState.Eligible, 1);
                try
                {
                    RecordScene(ref frameBeginState); RecordScene(ref end);
                    MergeLayer(ref WindowSolid, ref FrameSolid);
                    MergeLayer(ref WindowNonSolid, ref FrameNonSolid);
                }
                catch { RuntimeFault(); }
                finally { CommitBatchFrame(); }
            }
            if (now >= lastAttempt && now >= frameSnapshotStop)
            {
                if (reportFloorPending) { lastAttempt = now; reportFloorPending = false; }
                else if (now - lastAttempt >= frequency * 5) Report(now, false);
            }
        }
        catch { RuntimeFault(); }
        finally
        {
            if (rootBatchOwned) ResetBatchFrame();
            rootBatchOwned = false;
            rootReady = false; FrameDepth = previousDepth;
            FrameCollecting = savedCollecting; Pending = savedPending; current = savedContext;
        }
    }

    private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

    private static bool TryCaptureState(out Render53State state)
    {
        try { state = CaptureState(); return true; }
        catch
        {
            state = default;
            frameSnapshotCalls = frameSnapshotTicks = frameSnapshotMax = 0;
            Add(ref captureFailureFrames, 1); RuntimeFault();
            return false;
        }
    }

    private static Render53State CaptureState()
    {
        long start = Stopwatch.GetTimestamp();
        Render53State state = default;
        try
        {
            if (Render53MainShape.gameMenu) state.Flags |= MenuFlag;
            if (Render53MainShape.gamePaused) state.Flags |= PausedFlag;
            if (Render53MainShape.playerInventory) state.Flags |= InventoryFlag;
            if (Render53MainShape.mapFullscreen) state.Flags |= MapFlag;
            if (Render53MainShape.autoPause) state.Flags |= AutoPauseFlag;
            state.PlayerId = Render53MainShape.myPlayer;
            Render53PlayerShape[]? players = Render53MainShape.player;
            Render53PlayerShape? player = players != null && state.PlayerId >= 0 && state.PlayerId < players.Length ? players[state.PlayerId] : null;
            if (player == null) state.Flags |= InvalidPlayerFlag;
            else
            {
                if (player.dead) state.Flags |= DeadFlag;
                if (player.ghost) state.Flags |= GhostFlag;
                if (player.spectating >= 0) state.Flags |= SpectatorFlag;
                state.PlayerX = player.position.X; state.PlayerY = player.position.Y;
                if (!Finite(state.PlayerX) || !Finite(state.PlayerY)) state.Flags |= InvalidPlayerFlag;
            }
            state.CameraX = Render53MainShape.screenPosition.X; state.CameraY = Render53MainShape.screenPosition.Y;
            state.Width = Render53MainShape.screenWidth; state.Height = Render53MainShape.screenHeight;
            state.Zoom = Render53MainShape.GameZoomTarget; state.FrameSkip = Render53MainShape.FrameSkipMode;
            state.WorldTime = Render53MainShape.time; state.DayTime = Render53MainShape.dayTime;
            if (!Finite(state.CameraX) || !Finite(state.CameraY) || !Finite(state.Zoom) || state.Zoom <= 0 ||
                !Finite(state.WorldTime) || state.Width <= 0 || state.Height <= 0) state.Flags |= InvalidSceneFlag;
            if (Render53MainShape.myPlayer != state.PlayerId) state.Flags |= InvalidPlayerFlag;
        }
        catch { state.Flags |= InvalidPlayerFlag | InvalidSceneFlag; }
        long stop = Stopwatch.GetTimestamp();
        if (batchFrameOpen && batchClockSeen && start < batchLast) RuntimeFault();
        frameSnapshotStop = stop;
        Add(ref frameSnapshotCalls, 1);
        if (start < 0 || stop < start) RuntimeFault();
        else
        {
            long elapsed = stop - start;
            Add(ref frameSnapshotTicks, elapsed);
            if (elapsed > frameSnapshotMax) frameSnapshotMax = elapsed;
        }
        return state;
    }

    private static bool Stable(ref Render53State begin, ref Render53State end)
    {
        return begin.Flags == end.Flags && begin.PlayerId == end.PlayerId && begin.Width == end.Width &&
            begin.Height == end.Height && begin.Zoom == end.Zoom && begin.FrameSkip == end.FrameSkip && begin.DayTime == end.DayTime;
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

    private static void Range(ref Render53Range range, double value)
    {
        if (!range.Used) { range.Used = true; range.First = range.Min = range.Max = value; }
        else { if (value < range.Min) range.Min = value; if (value > range.Max) range.Max = value; }
        range.Last = value;
    }

    private static bool lastSceneDay;
    private static void RecordScene(ref Render53State state)
    {
        if (WindowState.SceneSamples != 0)
        {
            if (WindowState.LastPlayerId != state.PlayerId) Add(ref WindowState.PlayerChanged, 1);
            if (WindowState.PlayerX.Last != state.PlayerX || WindowState.PlayerY.Last != state.PlayerY) Add(ref WindowState.PlayerMoved, 1);
            if (WindowState.CameraX.Last != state.CameraX || WindowState.CameraY.Last != state.CameraY) Add(ref WindowState.CameraMoved, 1);
            if (lastSceneDay != state.DayTime) Add(ref WindowState.DayChanged, 1);
        }
        else WindowState.FirstPlayerId = state.PlayerId;
        WindowState.LastPlayerId = state.PlayerId; lastSceneDay = state.DayTime;
        Add(ref WindowState.SceneSamples, 1);
        if (state.DayTime) Add(ref WindowState.Day, 1); else Add(ref WindowState.Night, 1);
        Range(ref WindowState.PlayerX, state.PlayerX); Range(ref WindowState.PlayerY, state.PlayerY);
        Range(ref WindowState.CameraX, state.CameraX); Range(ref WindowState.CameraY, state.CameraY);
        Range(ref WindowState.Zoom, state.Zoom); Range(ref WindowState.WorldTime, state.WorldTime);
        Range(ref WindowState.Width, state.Width); Range(ref WindowState.Height, state.Height); Range(ref WindowState.FrameSkip, state.FrameSkip);
    }

    internal static void Flush()
    {
        bool collecting = FrameCollecting, pending = Pending;
        try
        {
            FrameCollecting = false; Pending = false;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread || reportBuffer == null || reporting || finalized || FrameDepth != 0) return;
            if (Report(Stopwatch.GetTimestamp(), true)) finalized = true;
        }
        catch { RuntimeFault(); Add(ref reportAttempts, 1); Add(ref reportFailures, 1); }
        finally { FrameCollecting = collecting; Pending = pending; }
    }

    private static void Row(string kind, long id)
    {
        reportBuffer!.Append("NX_PROFILE ").Append(kind); Number("interval", id);
    }
    private static void Text(string name, string value) { reportBuffer!.Append(' ').Append(name).Append('=').Append(value); }
    private static void Number(string name, long value) { Text(name, value.ToString(reportCulture)); }
    private static void EndRow() { reportBuffer!.Append('\n'); }
    private static void EmitRange(string name, ref Render53Range range)
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

    private static void EmitMetric(long id, string layer, string label, ref Render53Metric metric)
    {
        Row("METRIC", id); Text("layer", layer); Text("label", label);
        Number("operations", metric.Calls); Number("total_ticks", metric.Ticks); Number("max_ticks", metric.Max); EndRow();
    }
    private static void EmitLayer(long id, string layer, ref Render53LayerTotals value)
    {
        Row("PASS", id); Text("layer", layer);
        Number("passes", value.Passes); Number("completed_passes", value.CompletedPasses); Number("aborted_passes", value.AbortedPasses); Number("nested_passes", value.NestedPasses);
        Number("invalid_passes", value.InvalidPasses);
        Number("visited", value.Visited); Number("eligible", value.Eligible); Number("calls", value.Calls);
        Number("selected", value.Selected); Number("completed_samples", value.CompletedSamples); Number("valid_samples", value.ValidSamples); Number("invalid_samples", value.InvalidSamples); Number("aborted_samples", value.AbortedSamples);
        Number("discarded_samples", value.DiscardedSamples);
        Number("completed_loops", value.CompletedLoops); Number("loop_ticks", value.LoopTicks); Number("loop_max_ticks", value.LoopMax);
        Number("pass_ticks", value.PassTicks); Number("pass_max_ticks", value.PassMax); Number("sample_ticks", value.SampleTicks); Number("sample_max_ticks", value.SampleMax); EndRow();
        EmitMetric(id, layer, "GetColor", ref value.Color); EmitMetric(id, layer, "GetTileDrawData", ref value.Data);
        EmitMetric(id, layer, "GetTileOutlineInfo", ref value.Outline); EmitMetric(id, layer, "DrawTiles_GetLightOverride", ref value.Override);
        EmitMetric(id, layer, "GetFinalLight", ref value.Final); EmitMetric(id, layer, "clock_pair", ref value.ClockPair);
    }

    private static bool Report(long now, bool final)
    {
        if (reporting || now < windowStart || now < 0 || interval == long.MaxValue) { RuntimeFault(); return false; }
        bool collecting = FrameCollecting, pending = Pending;
        reporting = true; FrameCollecting = false; Pending = false;
        long start = -1, reportBoundary = now;
        bool costRecorded = false, emitted = false, clockRead = false;
        Add(ref reportAttempts, 1);
        try
        {
            clockRead = true;
            start = Stopwatch.GetTimestamp();
            clockRead = false;
            if (start < now) RuntimeFault();
            else reportBoundary = start;
            reportBuffer!.Length = 0;
            long id = interval + 1;
            Row("BEGIN", id); Number("version", 53); Text("schema", "render_split"); Number("final", final ? 1 : 0);
            Number("frequency", frequency); Number("wall_ticks", now - windowStart); Number("start_tick", windowStart); Number("end_tick", now); Number("sample_denominator", 128); EndRow();
            Row("STATE", id);
            Number("frames", WindowState.Frames); Number("completed", WindowState.Completed); Number("eligible", WindowState.Eligible);
            Number("excluded", WindowState.Frames - WindowState.Eligible); Number("aborted", WindowState.Aborted); Number("state_changed", WindowState.Changed);
            Number("capture_failure_frames", captureFailureFrames);
            Number("invalid_player", WindowState.InvalidPlayer); Number("invalid_scene", WindowState.InvalidScene);
            Number("menu", WindowState.Menu); Number("paused", WindowState.Paused); Number("inventory", WindowState.Inventory); Number("map", WindowState.Map);
            Number("dead", WindowState.Dead); Number("ghost", WindowState.Ghost); Number("spectator", WindowState.Spectator); Number("autopause", WindowState.AutoPause);
            Number("first_flags", WindowState.FirstFlags); Number("last_flags", WindowState.LastFlags); Number("union_flags", WindowState.FlagsOr);
            Number("first_frame_id", WindowState.FirstFrameId); Number("last_frame_id", WindowState.LastFrameId);
            Number("ignored_threads_lifetime", Interlocked.Read(ref ignoredThreads)); Number("reentrant_frames_lifetime", reentrantFrames); Number("report_failures_lifetime", reportFailures);
            Number("snapshot_calls", snapshotCalls); Number("snapshot_ticks", snapshotTicks); Number("snapshot_max_ticks", snapshotMax); Number("measurement_invalid", MeasurementInvalid ? 1 : 0); EndRow();
            Row("SCENE", id); Number("samples", WindowState.SceneSamples);
            Number("player_id_first", WindowState.FirstPlayerId); Number("player_id_last", WindowState.LastPlayerId); Number("player_id_changes", WindowState.PlayerChanged);
            Number("player_position_changes", WindowState.PlayerMoved); Number("camera_position_changes", WindowState.CameraMoved);
            Number("day_samples", WindowState.Day); Number("night_samples", WindowState.Night); Number("day_changes", WindowState.DayChanged);
            EmitRange("player_x", ref WindowState.PlayerX); EmitRange("player_y", ref WindowState.PlayerY); EmitRange("camera_x", ref WindowState.CameraX); EmitRange("camera_y", ref WindowState.CameraY);
            EmitRange("zoom", ref WindowState.Zoom); EmitRange("width", ref WindowState.Width); EmitRange("height", ref WindowState.Height); EmitRange("skip", ref WindowState.FrameSkip); EmitRange("world_time", ref WindowState.WorldTime); EndRow();
            EmitLayer(id, "solid", ref WindowSolid); EmitLayer(id, "nonsolid", ref WindowNonSolid);
            EmitBatch(id);
            WriteBuffer();
            clockRead = true;
            long stop = Stopwatch.GetTimestamp();
            clockRead = false;
            long cost = start >= 0 && stop >= start ? stop - start : 0;
            if (start < 0 || stop < start) RuntimeFault();
            if (stop >= reportBoundary) reportBoundary = stop;
            Add(ref reportTicks, cost); if (cost > reportMax) reportMax = cost;
            costRecorded = true;
            clockRead = true;
            long finalStop = Stopwatch.GetTimestamp();
            clockRead = false;
            if (finalStop < stop || finalStop < reportBoundary) RuntimeFault();
            else reportBoundary = finalStop;
            Row("REPORT_COST", id); Number("ticks_before_footer", cost); Number("lifetime_ticks_before_footers", reportTicks);
            Number("lifetime_max_ticks", reportMax); Number("reports", id); Number("attempts", reportAttempts);
            Number("failures_lifetime", reportFailures); Number("measurement_invalid", MeasurementInvalid ? 1 : 0); EndRow();
            Row("END", id); EndRow(); WriteBuffer();
            interval = id;
            WindowState = default; WindowSolid = default; WindowNonSolid = default;
            WindowBatch = default;
            snapshotCalls = snapshotTicks = snapshotMax = captureFailureFrames = 0;
            emitted = true;
            return true;
        }
        catch
        {
            if (clockRead) RuntimeFault();
            Add(ref reportFailures, 1);
            if (!costRecorded && start >= 0)
            {
                try
                {
                    long stop = Stopwatch.GetTimestamp();
                    if (stop < start || stop < reportBoundary) RuntimeFault();
                    else
                    {
                        long cost = stop - start;
                        Add(ref reportTicks, cost); if (cost > reportMax) reportMax = cost;
                        reportBoundary = stop;
                    }
                }
                catch { RuntimeFault(); }
            }
            return false;
        }
        finally
        {
            // Anchor on a subsequent valid root-frame timestamp, after output has
            // returned. No unreportable clock read is allowed after a successful END.
            lastAttempt = reportBoundary; reportFloorPending = true;
            if (emitted) windowStart = reportBoundary;
            reporting = false; FrameCollecting = collecting; Pending = pending;
        }
    }
}
