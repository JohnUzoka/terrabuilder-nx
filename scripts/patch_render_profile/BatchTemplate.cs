using System;
using System.Diagnostics;

internal static partial class Render53Template
{
    private static Render53BatchStackEntry[]? batchStack;
    private static int batchDepth, batchNextCookie;
    private static long batchLast;
    private static bool batchClockSeen, batchFrameOpen, batchFrameInvalid, batchScopeAborted, batchFramePrepared, batchFrameCommitted, batchCountersInvalid;
    private static Render53BatchTotals frameBatch;

    // The protected owner-thread root is the only caller that may allocate this stack.
    private static void InitializeBatch()
    {
        if (batchStack == null) batchStack = new Render53BatchStackEntry[BatchMaxDepth];
        else if (batchStack.Length != BatchMaxDepth) throw new InvalidOperationException();
    }

    private static void BeginBatchFrame()
    {
        frameBatch = default;
        batchDepth = 0;
        batchLast = frameSnapshotStop;
        batchClockSeen = true;
        batchFrameOpen = true;
        batchFrameInvalid = batchScopeAborted = batchFramePrepared = batchFrameCommitted = batchCountersInvalid = false;
        if (frameObserverInvalid || batchLast < 0 || batchStack == null || batchStack.Length != BatchMaxDepth)
            BatchFault();
    }

    private static void ResetBatchFrame()
    {
        // This is called only by an admitted owner root, never a nested or foreign root.
        frameBatch = default;
        batchDepth = 0;
        batchLast = 0;
        batchClockSeen = batchFrameOpen = batchFrameInvalid = batchScopeAborted = batchFramePrepared = batchFrameCommitted = batchCountersInvalid = false;
    }

    internal static int BatchBegin(int id)
    {
        if (!FrameCollecting) return 0;
        try
        {
            if (batchFrameInvalid) return 0;
            if (!batchFrameOpen || batchFrameCommitted || id < 0 || id >= BatchMetricCount ||
                batchStack == null || batchStack.Length != BatchMaxDepth || batchDepth < 0 ||
                batchDepth >= BatchMaxDepth || batchNextCookie < 0 || batchNextCookie == int.MaxValue)
            {
                BatchFault();
                return 0;
            }
            long now;
            if (!TryBatchClock(out now)) return 0;
            int cookie = ++batchNextCookie;
            ref Render53BatchStackEntry entry = ref batchStack[batchDepth];
            entry.Id = id;
            entry.Cookie = cookie;
            entry.Start = now;
            entry.ChildTicks = 0;
            batchDepth++;
            return cookie;
        }
        catch
        {
            BatchFault();
            return 0;
        }
    }

    internal static void BatchEnd(int cookie, bool completed)
    {
        if (cookie == 0 || !FrameCollecting) return;
        try
        {
            if (!batchFrameOpen || batchFrameCommitted || cookie < 0 || batchStack == null ||
                batchStack.Length != BatchMaxDepth || batchDepth <= 0 || batchDepth > BatchMaxDepth)
            {
                BatchFault();
                return;
            }
            ref Render53BatchStackEntry entry = ref batchStack[batchDepth - 1];
            if (entry.Cookie != cookie || entry.Id < 0 || entry.Id >= BatchMetricCount)
            {
                BatchFault();
                return;
            }
            if (!completed)
            {
                batchScopeAborted = true;
                if (Fits(frameBatch.AbortedScopes, 1)) frameBatch.AbortedScopes++;
                else { batchCountersInvalid = true; BatchFault(); }
            }
            long now;
            bool validClock = TryBatchClock(out now);
            if (validClock && !batchFrameInvalid) RecordBatchScope(ref entry, now, completed);
            batchDepth--;
        }
        catch { BatchFault(); }
    }

    private static bool TryBatchClock(out long now)
    {
        now = 0;
        try
        {
            if (!Fits(frameBatch.ClockCalls, 1))
            {
                batchCountersInvalid = true;
                BatchFault();
                return false;
            }
            // Count attempts, including a clock call that throws or later causes discard.
            frameBatch.ClockCalls++;
            now = Stopwatch.GetTimestamp();
            if (now < 0 || (batchClockSeen && now < batchLast))
            {
                BatchFault();
                return false;
            }
            batchLast = now;
            batchClockSeen = true;
            return true;
        }
        catch
        {
            BatchFault();
            return false;
        }
    }

    private static void RecordBatchScope(ref Render53BatchStackEntry entry, long now, bool completed)
    {
        if (entry.Start < 0 || now < entry.Start || entry.ChildTicks < 0 || entry.ChildTicks > now - entry.Start)
        {
            BatchFault();
            return;
        }
        long inclusive = now - entry.Start, exclusive = inclusive - entry.ChildTicks;
        ref Render53BatchMetric metric = ref frameBatch.End;
        if (entry.Id == 1) metric = ref frameBatch.Upload;
        else if (entry.Id == 2) metric = ref frameBatch.Submit;
        if (!BatchMetricValid(ref metric) || !Fits(metric.Calls, completed ? 1 : 0) ||
            !Fits(metric.InclusiveTicks, inclusive) || !Fits(metric.ExclusiveTicks, exclusive))
        {
            BatchFault();
            return;
        }
        if (batchDepth > 1)
        {
            ref Render53BatchStackEntry parent = ref batchStack![batchDepth - 2];
            if (parent.Start < 0 || parent.Start > entry.Start || !Fits(parent.ChildTicks, inclusive))
            {
                BatchFault();
                return;
            }
            parent.ChildTicks += inclusive;
        }
        // A real callee throw closes its interval but invalidates donation for the whole
        // frame. It never becomes an observer fault or replaces the original exception.
        if (!completed) return;
        metric.Calls++;
        metric.InclusiveTicks += inclusive;
        metric.ExclusiveTicks += exclusive;
        if (inclusive > metric.InclusiveMax) metric.InclusiveMax = inclusive;
        if (exclusive > metric.ExclusiveMax) metric.ExclusiveMax = exclusive;
    }

    private static bool BatchMetricValid(ref Render53BatchMetric metric)
    {
        return metric.ExclusiveTicks <= metric.InclusiveTicks && metric.ExclusiveMax <= metric.InclusiveMax &&
            TotalWithinMax(metric.Calls, metric.InclusiveTicks, metric.InclusiveMax) &&
            TotalWithinMax(metric.Calls, metric.ExclusiveTicks, metric.ExclusiveMax);
    }

    private static bool CanMergeBatchMetric(ref Render53BatchMetric destination, ref Render53BatchMetric source)
    {
        return BatchMetricValid(ref destination) && BatchMetricValid(ref source) &&
            Fits(destination.Calls, source.Calls) && Fits(destination.InclusiveTicks, source.InclusiveTicks) &&
            Fits(destination.ExclusiveTicks, source.ExclusiveTicks);
    }

    private static void MergeBatchMetric(ref Render53BatchMetric destination, ref Render53BatchMetric source)
    {
        destination.Calls += source.Calls;
        destination.InclusiveTicks += source.InclusiveTicks;
        destination.ExclusiveTicks += source.ExclusiveTicks;
        if (source.InclusiveMax > destination.InclusiveMax) destination.InclusiveMax = source.InclusiveMax;
        if (source.ExclusiveMax > destination.ExclusiveMax) destination.ExclusiveMax = source.ExclusiveMax;
    }

    private static bool BatchMetricsValid(ref Render53BatchTotals value)
    {
        if (!BatchMetricValid(ref value.End) || !BatchMetricValid(ref value.Upload) || !BatchMetricValid(ref value.Submit) ||
            value.ClockCalls < 0) return false;
        // Divide the available clock attempts instead of overflowing twice the call sum.
        long available = value.ClockCalls / 2;
        if (value.End.Calls > available) return false;
        available -= value.End.Calls;
        if (value.Upload.Calls > available) return false;
        return value.Submit.Calls <= available - value.Upload.Calls;
    }

    private static bool BatchWindowValid()
    {
        return WindowBatch.Frames >= 0 && WindowBatch.ValidFrames >= 0 && WindowBatch.DiscardedFrames >= 0 &&
            WindowBatch.ValidFrames <= WindowBatch.Frames &&
            WindowBatch.DiscardedFrames == WindowBatch.Frames - WindowBatch.ValidFrames &&
            WindowBatch.AbortedScopes >= 0 && WindowBatch.ObserverFailures >= 0 &&
            (WindowBatch.AbortedScopes == 0 || WindowBatch.DiscardedFrames != 0) &&
            (WindowBatch.ObserverFailures == 0 || (WindowBatch.DiscardedFrames != 0 && MeasurementInvalid)) &&
            (WindowBatch.ValidFrames != 0 ||
                (WindowBatch.End.Calls == 0 && WindowBatch.Upload.Calls == 0 && WindowBatch.Submit.Calls == 0)) &&
            BatchMetricsValid(ref WindowBatch);
    }

    private static bool PrepareBatchFrame()
    {
        try
        {
            if (!batchFrameOpen || batchFramePrepared || batchFrameCommitted || batchDepth != 0 || batchStack == null ||
                batchStack.Length != BatchMaxDepth || frameSnapshotStop < batchLast ||
                frameBatch.AbortedScopes < 0 || frameBatch.ObserverFailures < 0 || !BatchMetricsValid(ref frameBatch))
                BatchFault();
            bool valid = !batchFrameInvalid && !batchScopeAborted;
            if (valid && (!CanMergeBatchMetric(ref WindowBatch.End, ref frameBatch.End) ||
                !CanMergeBatchMetric(ref WindowBatch.Upload, ref frameBatch.Upload) ||
                !CanMergeBatchMetric(ref WindowBatch.Submit, ref frameBatch.Submit)))
            {
                BatchFault();
            }
            // Reserve both final outcomes before any eligible scene/tile donation.
            // Their observers can still fail, so a healthy preflight also requires
            // room for one discarded frame and its coalesced observer failure.
            if (batchCountersInvalid || !BatchWindowValid() || WindowBatch.Frames != WindowState.Eligible ||
                !Fits(WindowState.Eligible, 1) || !Fits(WindowBatch.Frames, 1) ||
                !Fits(WindowBatch.ValidFrames, 1) || !Fits(WindowBatch.DiscardedFrames, 1) ||
                !Fits(WindowBatch.AbortedScopes, frameBatch.AbortedScopes) ||
                !Fits(WindowBatch.ObserverFailures, 1) ||
                !Fits(WindowBatch.ClockCalls, frameBatch.ClockCalls))
            {
                BatchFault();
                return false;
            }
            batchFramePrepared = true;
            return true;
        }
        catch
        {
            BatchFault();
            return false;
        }
    }

    private static void CommitBatchFrame()
    {
        if (!batchFramePrepared || batchFrameCommitted)
        {
            BatchFault();
            return;
        }
        // PrepareBatchFrame reserved every possible counter delta and metric sum.
        // No window write occurs until the scene/tile observers have all returned.
        bool valid = !batchFrameInvalid && !batchScopeAborted && !frameObserverInvalid;
        WindowBatch.Frames++;
        if (valid)
        {
            WindowBatch.ValidFrames++;
            MergeBatchMetric(ref WindowBatch.End, ref frameBatch.End);
            MergeBatchMetric(ref WindowBatch.Upload, ref frameBatch.Upload);
            MergeBatchMetric(ref WindowBatch.Submit, ref frameBatch.Submit);
        }
        else WindowBatch.DiscardedFrames++;
        WindowBatch.AbortedScopes += frameBatch.AbortedScopes;
        // One diagnostic per eligible frame containing any observer failure,
        // rather than one diagnostic for every cascading failed hook.
        if (frameObserverInvalid) WindowBatch.ObserverFailures++;
        WindowBatch.ClockCalls += frameBatch.ClockCalls;
        batchFrameCommitted = true;
    }

    private static void BatchFault()
    {
        batchFrameInvalid = true;
        RuntimeFault();
    }

    private static void RuntimeFault()
    {
        MeasurementInvalid = true;
        frameObserverInvalid = true;
        if (rootBatchOwned && batchFrameOpen && !batchFrameCommitted) batchFrameInvalid = true;
    }

    private static void EmitBatchMetric(long id, string label, ref Render53BatchMetric metric)
    {
        Row("BATCH_METRIC", id); Text("label", label); Number("calls", metric.Calls);
        Number("inclusive_ticks", metric.InclusiveTicks); Number("exclusive_ticks", metric.ExclusiveTicks);
        Number("inclusive_max_ticks", metric.InclusiveMax); Number("exclusive_max_ticks", metric.ExclusiveMax); EndRow();
    }

    private static void EmitBatch(long id)
    {
        Row("BATCH_STATE", id); Number("sample_denominator", 1); Number("frames", WindowBatch.Frames);
        Number("valid_frames", WindowBatch.ValidFrames); Number("discarded_frames", WindowBatch.DiscardedFrames);
        Number("aborted_scopes", WindowBatch.AbortedScopes); Number("observer_failures", WindowBatch.ObserverFailures);
        Number("clock_calls", WindowBatch.ClockCalls); EndRow();
        EmitBatchMetric(id, "tile_batch_end", ref WindowBatch.End);
        EmitBatchMetric(id, "vertex_upload", ref WindowBatch.Upload);
        EmitBatchMetric(id, "indexed_submit", ref WindowBatch.Submit);
    }
}
