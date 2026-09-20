using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

// These compile-time shapes are never shipped. Their references are strictly mapped to
// existing Terraria members; SeriesView.Read is injected into DataSeries for private access.
internal class MainView
{
    public static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, renderNow;
    public static int FrameSkipMode, renderCount;
    public bool IsActive { get; set; }
}
internal static class LoggerView
{
    public static List<MetricView> entries = new List<MetricView>();
    public static int activeDataSeries;
}
internal class MetricView
{
    public string name = "";
    public SeriesView[] data = new SeriesView[2];
    public int _nxProfileId;
}
internal class SeriesView
{
    public int[] values = new int[300];
    public bool[] used = new bool[300];
    public int next;
    public bool _nxProfileReset;
    public void NXProfileRead(int id)
    {
        if (next < 0 || next >= used.Length || next >= values.Length) { ProfileTemplate.Drop(); return; }
        if (_nxProfileReset) { if (used[next]) ProfileTemplate.ResetDrop(id); return; }
        if (used[next]) ProfileTemplate.Value(id, values[next]);
    }
}
internal static class ProfileTemplate
{
    const int Capacity = 512, Width = 518, Cohorts = 32;
    static readonly string[] names = new string[Width];
    static readonly int[] kinds = new int[Width];
    static readonly long[] used = new long[Width * Cohorts], invalid = new long[Width * Cohorts], raw = new long[Width * Cohorts], sum = new long[Width * Cohorts], maximum = new long[Width * Cohorts];
    static readonly long[] resetSamples = new long[Width * Cohorts];
    static readonly long[] frames = new long[Cohorts], updates = new long[Cohorts], active = new long[Cohorts], pause = new long[Cohorts], changed = new long[Cohorts], render = new long[Cohorts];
    static readonly int[] skipMin = new int[Cohorts], skipMax = new int[Cohorts], renderMin = new int[Cohorts], renderMax = new int[Cohorts];
    static readonly long[] stages = new long[4];
    static readonly bool[] stageUsed = new bool[4];
    static int count, attempts, cohort, state, startSkip, startRender, startSeries;
    static bool complete, stateChanged, startActive, startPause, startRenderNow;
    static MainView? game;
    static long pendingUpdates, intervalUpdates, noDraw, partial, repeated, dropped, droppedRegistrations, unregistered;
    static long resetDropped, reportFailures;
    static long bodyStart, stageStart, observerTicks, frameObserverTicks, captureTicks, captureMax, captures, reports, reportTicks, reportMax;
    static long intervalStart, lastReport, interval;
    static bool finalized;
    static readonly long frequency = Stopwatch.Frequency;
    static readonly CultureInfo culture = CultureInfo.InvariantCulture;

    static ProfileTemplate()
    {
        names[512] = "outer.EnsureRenderTargetContent"; names[513] = "outer.DoDraw";
        names[514] = "outer.OnPostDraw"; names[515] = "outer.TransferCompletedAssets";
        names[516] = "outer.DrawBody.gross_including_observers"; names[517] = "outer.DrawBody.remainder_excluding_measured_observers";
        intervalStart = lastReport = Stopwatch.GetTimestamp();
    }
    internal static int Register(string name, int kind)
    {
        if (count == Capacity) { droppedRegistrations++; return 0; }
        int id = ++count;
        names[id - 1] = name;
        kinds[id - 1] = kind;
        return id;
    }
    static int State()
    {
        return (MainView.gameMenu ? 1 : 0) | (MainView.gamePaused ? 2 : 0) |
            (MainView.playerInventory ? 4 : 0) | (MainView.mapFullscreen ? 8 : 0);
    }
    static void Observe()
    {
        if (State() != state || game!.IsActive != startActive || MainView.autoPause != startPause ||
            MainView.FrameSkipMode != startSkip || MainView.renderCount != startRender ||
            MainView.renderNow != startRenderNow || LoggerView.activeDataSeries != startSeries) stateChanged = true;
    }
    internal static void Update()
    {
        pendingUpdates++; intervalUpdates++;
    }
    internal static void Begin(MainView instance)
    {
        long t = Stopwatch.GetTimestamp();
        game = instance;
        attempts++;
        complete = false;
        state = State(); startActive = game.IsActive; startPause = MainView.autoPause;
        startSkip = MainView.FrameSkipMode; startRender = MainView.renderCount;
        startRenderNow = MainView.renderNow; startSeries = LoggerView.activeDataSeries;
        stateChanged = false; frameObserverTicks = 0;
        for (int i = 0; i < 4; i++) { stages[i] = 0; stageUsed[i] = false; }
        observerTicks += Stopwatch.GetTimestamp() - t;
        bodyStart = Stopwatch.GetTimestamp();
    }
    internal static void StageBegin()
    {
        stageStart = Stopwatch.GetTimestamp();
    }
    internal static void StageEnd(int id)
    {
        long t = Stopwatch.GetTimestamp();
        stages[id] = t - stageStart; stageUsed[id] = true;
        Observe();
        long cost = Stopwatch.GetTimestamp() - t;
        frameObserverTicks += cost; observerTicks += cost;
    }
    static long bodyTicks;
    internal static void End()
    {
        long t = Stopwatch.GetTimestamp();
        bodyTicks = t - bodyStart;
        Observe(); complete = true;
        observerTicks += Stopwatch.GetTimestamp() - t;
    }
    internal static void Drop() { dropped++; }
    internal static void ResetDrop(int id)
    {
        resetDropped++;
        if (id > 0 && id <= count) resetSamples[cohort * Width + id - 1]++;
        else unregistered++;
    }
    internal static void Value(int id, int value)
    {
        if (id <= 0 || id > count) { unregistered++; return; }
        Add(id - 1, value);
    }
    static void Add(int id, long value)
    {
        int at = cohort * Width + id;
        used[at]++; raw[at] += value;
        if (value < 0) { invalid[at]++; return; }
        sum[at] += value;
        if (value > maximum[at]) maximum[at] = value;
    }
    static void Capture()
    {
        long t = Stopwatch.GetTimestamp();
        if (attempts == 1 && complete && LoggerView.activeDataSeries >= 0 && LoggerView.activeDataSeries < 2)
        {
            Observe();
            cohort = state + 16 * LoggerView.activeDataSeries;
            long n = ++frames[cohort]; updates[cohort] += pendingUpdates;
            if (startActive) active[cohort]++;
            if (startPause) pause[cohort]++;
            if (startRenderNow) render[cohort]++;
            if (stateChanged) changed[cohort]++;
            if (n == 1 || startSkip < skipMin[cohort]) skipMin[cohort] = startSkip;
            if (n == 1 || startSkip > skipMax[cohort]) skipMax[cohort] = startSkip;
            if (n == 1 || startRender < renderMin[cohort]) renderMin[cohort] = startRender;
            if (n == 1 || startRender > renderMax[cohort]) renderMax[cohort] = startRender;
            long total = 0;
            for (int i = 0; i < 4; i++) if (stageUsed[i]) { Add(512 + i, stages[i]); total += stages[i]; }
            Add(516, bodyTicks); Add(517, bodyTicks - total - frameObserverTicks);
            int series = LoggerView.activeDataSeries;
            for (int i = 0; i < LoggerView.entries.Count; i++)
            {
                MetricView entry = LoggerView.entries[i];
                if (entry.data == null || series >= entry.data.Length || entry.data[series] == null) { dropped++; continue; }
                entry.data[series].NXProfileRead(entry._nxProfileId);
            }
        }
        else if (attempts == 0) noDraw++;
        else if (attempts > 1) repeated++;
        else partial++;
        pendingUpdates = 0; attempts = 0; complete = false;
        long cost = Stopwatch.GetTimestamp() - t;
        captureTicks += cost; captures++;
        if (cost > captureMax) captureMax = cost;
    }
    internal static void Boundary()
    {
        if (finalized) return;
        Capture();
        long now = Stopwatch.GetTimestamp();
        if (now - lastReport >= frequency * 5) TryReport(now, false);
    }
    internal static void Flush()
    {
        if (finalized) return;
        if (attempts != 0 || pendingUpdates != 0) Capture();
        TryReport(Stopwatch.GetTimestamp(), true);
    }
    static void TryReport(long now, bool final)
    {
        try { Report(now, final); if (final) finalized = true; }
        catch (System.IO.IOException) { reportFailures++; lastReport = Stopwatch.GetTimestamp(); }
        catch (ObjectDisposedException) { reportFailures++; lastReport = Stopwatch.GetTimestamp(); }
        // Failed output never clears the interval. An incomplete block has no END;
        // consumers discard it. The next attempt retains its original interval start.
    }
    static string Escape(string value)
    {
        StringBuilder b = new StringBuilder();
        b.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"' || c == '\\') { b.Append('\\'); b.Append(c); }
            else if (c < 32 || c > 126) { b.Append("\\u"); b.Append(((int)c).ToString("x4", culture)); }
            else b.Append(c);
        }
        b.Append('"'); return b.ToString();
    }
    static string N(long value) { return value.ToString(culture); }
    static string Mean(long value, long denominator, bool time)
    {
        if (denominator == 0 || (time && frequency <= 0)) return "null";
        double result = (double)value / denominator;
        if (time) result = result * 1000 / frequency;
        return result.ToString("R", culture);
    }
    static void Line(string value) { Console.WriteLine(value); }
    static void Report(long now, bool final)
    {
        long t = Stopwatch.GetTimestamp();
        long id = interval + 1;
        Line("NX_PROFILE BEGIN interval=" + N(id) + " final=" + (final ? "1" : "0") + " frequency=" + N(frequency) + " wall_ticks=" + N(now - intervalStart) + " attribution=draw_begin series=sample_before_callbacks units=time_ms_or_count overlap=timelogger_nested_and_worker_wall original_timer=unchecked_int32_positive_wrap_undetectable omitted_metrics=unused");
        Line("NX_PROFILE STATUS interval=" + N(id) + " update_calls=" + N(intervalUpdates) + " no_draw_boundaries=" + N(noDraw) + " partial_excluded=" + N(partial) + " repeated_epochs_excluded=" + N(repeated) + " invalid_storage=" + N(dropped) + " reset_dirty_used_excluded=" + N(resetDropped) + " unregistered_used=" + N(unregistered) + " registry_dropped_lifetime=" + N(droppedRegistrations) + " report_failures_lifetime=" + N(reportFailures) + " registry_count=" + N(count));
        Line("NX_PROFILE OVERHEAD interval=" + N(id) + " capture_calls=" + N(captures) + " capture_ticks=" + N(captureTicks) + " capture_max_ticks=" + N(captureMax) + " observer_ticks=" + N(observerTicks));
        for (int c = 0; c < Cohorts; c++)
        {
            long n = frames[c];
            if (n == 0) continue;
            Line("NX_PROFILE STATE interval=" + N(id) + " cohort=" + N(c) + " bits=" + N(c % 16) + " series=" + N(c / 16) + " frames=" + N(n) + " updates=" + N(updates[c]) + " updates_per_frame=" + Mean(updates[c], n, false) + " active_frames=" + N(active[c]) + " auto_pause_frames=" + N(pause[c]) + " sampled_change_frames=" + N(changed[c]) + " skip_min=" + N(skipMin[c]) + " skip_max=" + N(skipMax[c]) + " render_min=" + N(renderMin[c]) + " render_max=" + N(renderMax[c]) + " render_now_frames=" + N(render[c]));
            for (int m = 0; m < Width; m++)
            {
                if (m >= count && m < 512) continue;
                int at = c * Width + m;
                if (used[at] == 0 && resetSamples[at] == 0) continue;
                long valid = used[at] - invalid[at];
                bool time = kinds[m] == 0;
                Line("NX_PROFILE METRIC interval=" + N(id) + " cohort=" + N(c) + " id=" + N(m) + " label=" + Escape(names[m]) + " unit=" + (time ? "ms" : "count") + " frames=" + N(n) + " used=" + N(used[at]) + " invalid_negative=" + N(invalid[at]) + " reset_dirty_used_excluded=" + N(resetSamples[at]) + " raw_total=" + N(raw[at]) + " valid_raw_total=" + N(sum[at]) + " mean_per_frame=" + (valid == 0 ? "null" : Mean(sum[at], n, time)) + " mean_per_valid_used_frame=" + Mean(sum[at], valid, time) + " max=" + (valid == 0 ? "null" : Mean(maximum[at], 1, time)));
            }
        }
        // This is the report's own current cost, not charged to the following frame.
        long cost = Stopwatch.GetTimestamp() - t;
        Line("NX_PROFILE REPORT_COST interval=" + N(id) + " ticks_before_footer=" + N(cost) + " lifetime_ticks_before_footers=" + N(reportTicks + cost) + " lifetime_max_ticks=" + N(cost > reportMax ? cost : reportMax) + " reports=" + N(reports + 1));
        Line("NX_PROFILE END interval=" + N(id));
        reportTicks += cost; reports++; interval = id; if (cost > reportMax) reportMax = cost;
        Array.Clear(resetSamples, 0, resetSamples.Length);
        Array.Clear(used, 0, used.Length); Array.Clear(invalid, 0, invalid.Length); Array.Clear(raw, 0, raw.Length); Array.Clear(sum, 0, sum.Length); Array.Clear(maximum, 0, maximum.Length);
        Array.Clear(frames, 0, frames.Length); Array.Clear(updates, 0, updates.Length); Array.Clear(active, 0, active.Length); Array.Clear(pause, 0, pause.Length); Array.Clear(changed, 0, changed.Length); Array.Clear(render, 0, render.Length);
        intervalUpdates = noDraw = partial = repeated = dropped = unregistered = resetDropped = captureTicks = captureMax = captures = observerTicks = 0;
        // A five-second floor between completed blocks, even if output is slow.
        intervalStart = lastReport = Stopwatch.GetTimestamp();
    }
}
