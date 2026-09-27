using System;
using System.Diagnostics;

internal static partial class UI51Template
{
    internal static long DepthOverflows, UnbalancedScopes, ScopeFailures, MaxObservedDepth;
    private static UI51StackEntry[]? timingStack;
    private static int timingDepth, nextCookie, uiRoots;
    private static long timingLast;
    private static bool timingClockSeen, sampleOpen, sampleEnded, rootCompleted, sampleCommitted;

    // State admits only the owner thread, suspending collection around reentrant roots.
    // Nothing here allocates until that protected root explicitly initializes timing.
    internal static void InitializeTiming()
    {
        try
        {
            if (timingStack != null)
            {
                if (!TimingArraysValid()) TimingFault();
                return;
            }
            UI51Metric[] frame = new UI51Metric[MetricCount];
            UI51Metric[] window = new UI51Metric[MetricCount * CohortCount];
            UI51StackEntry[] stack = new UI51StackEntry[MaxDepth];
            FrameMetrics = frame;
            WindowMetrics = window;
            timingStack = stack;
        }
        catch { TimingFault(); }
    }

    internal static void BeginSample()
    {
        if (!SampleSelected) return;
        try
        {
            if (sampleOpen)
            {
                UnbalancedTiming();
                return;
            }
            Active = false;
            FrameTimingInvalid = false;
            FrameScopeAborted = false;
            FrameStoppedLayers = 0;
            FrameClockPairTicks = 0;
            FrameClockPairValid = false;
            timingDepth = 0;
            uiRoots = 0;
            timingLast = frameSnapshotStop;
            timingClockSeen = true;
            sampleEnded = rootCompleted = sampleCommitted = false;
            sampleOpen = true;
            if (timingLast < 0 || !TimingArraysValid())
            {
                TimingFault();
                return;
            }
            Array.Clear(FrameMetrics!, 0, MetricCount);
            Array.Clear(timingStack!, 0, MaxDepth);
            PushScope(FrameMetric);
        }
        catch { TimingFault(); }
    }

    internal static int Enter(int id)
    {
        if (!SampleSelected || (id != UIMetric && !Active)) return 0;
        try
        {
            if (FrameTimingInvalid) return 0;
            if (!sampleOpen || sampleEnded || id <= FrameMetric || id >= MetricCount)
            {
                UnbalancedTiming();
                return 0;
            }
            if (id == UIMetric)
            {
                if (Active || uiRoots != 0 || timingDepth != 1)
                {
                    UnbalancedTiming();
                    return 0;
                }
                int cookie = PushScope(id);
                if (cookie != 0)
                {
                    uiRoots = 1;
                    Active = true;
                }
                return cookie;
            }
            if (timingDepth < 2)
            {
                UnbalancedTiming();
                return 0;
            }
            return PushScope(id);
        }
        catch
        {
            TimingFault();
            return 0;
        }
    }

    internal static int EnterLayer(UI51LayerShape layer)
    {
        if (!SampleSelected || !Active || FrameTimingInvalid) return 0;
        try
        {
            // Read the real readonly Name only on the sampled path, inside our guard.
            string? name = layer.Name;
            int id = name == "Vanilla: Inventory" ? InventoryMetric :
                name == "Vanilla: Hotbar" ? HotbarMetric :
                name == "Vanilla: Mouse Over" ? MouseOverMetric :
                name == "Vanilla: Mouse Text" ? MouseTextMetric : OtherLayerMetric;
            return Enter(id);
        }
        catch
        {
            TimingFault();
            return 0;
        }
    }

    internal static void Exit(int cookie, bool completed)
    {
        CloseScope(cookie, completed, false, false);
    }

    internal static void ExitLayer(int cookie, bool completed, bool result)
    {
        CloseScope(cookie, completed, true, result);
    }

    private static void CloseScope(int cookie, bool completed, bool layer, bool result)
    {
        if (cookie == 0 || !SampleSelected) return;
        try
        {
            if (!sampleOpen || sampleEnded || timingStack == null || timingDepth <= 1 ||
                timingDepth > timingStack.Length || cookie < 0)
            {
                UnbalancedTiming();
                return;
            }
            ref UI51StackEntry entry = ref timingStack[timingDepth - 1];
            if (entry.Cookie != cookie || entry.Id <= FrameMetric || entry.Id >= MetricCount ||
                (layer && (entry.Id < InventoryMetric || entry.Id > OtherLayerMetric)))
            {
                UnbalancedTiming();
                return;
            }
            if (!completed) ScopeAborted();
            if (layer && completed && !result) Add(ref FrameStoppedLayers, 1);
            bool ui = entry.Id == UIMetric;
            long now;
            bool clockValid = TryTimingClock(out now);
            if (clockValid && !FrameTimingInvalid) RecordScope(ref entry, now, completed);
            timingDepth--;
            if (ui) Active = false;
        }
        catch { TimingFault(); }
    }

    internal static void EndSample(bool completed)
    {
        if (!SampleSelected) return;
        try
        {
            if (!sampleOpen || sampleEnded)
            {
                UnbalancedTiming();
                return;
            }
            if (!completed) ScopeAborted();
            long now;
            bool clockValid = TryTimingClock(out now);
            if (timingStack == null || timingDepth != 1 || timingStack[0].Id != FrameMetric ||
                timingStack[0].Cookie <= 0 || Active)
                UnbalancedTiming();
            else if (clockValid && !FrameTimingInvalid)
            {
                RecordScope(ref timingStack[0], now, completed);
                rootCompleted = completed && !FrameTimingInvalid;
            }

            // This separate, bare pair is outside frame_draw. No validation work is
            // inserted between its calls, and invalid values never masquerade as zero.
            try
            {
                long before = Stopwatch.GetTimestamp();
                long after = Stopwatch.GetTimestamp();
                if (before < 0 || after < before || (timingClockSeen && before < timingLast))
                    TimingFault();
                else
                {
                    FrameClockPairTicks = after - before;
                    FrameClockPairValid = true;
                    timingLast = after;
                    timingClockSeen = true;
                }
            }
            catch { TimingFault(); }
        }
        catch { TimingFault(); }
        finally
        {
            Active = false;
            timingDepth = 0;
            sampleOpen = false;
            sampleEnded = true;
        }
    }

    private static int PushScope(int id)
    {
        if (timingStack == null || timingDepth < 0 || timingDepth > MaxDepth)
        {
            UnbalancedTiming();
            return 0;
        }
        if (timingDepth == MaxDepth)
        {
            Add(ref DepthOverflows, 1);
            TimingFault();
            return 0;
        }
        if (nextCookie < 0 || nextCookie == int.MaxValue ||
            MaxObservedDepth < 0 || MaxObservedDepth > MaxDepth)
        {
            TimingFault();
            return 0;
        }
        long now;
        if (!TryTimingClock(out now)) return 0;
        int cookie = ++nextCookie;
        ref UI51StackEntry entry = ref timingStack[timingDepth];
        entry.Id = id;
        entry.Cookie = cookie;
        entry.Start = now;
        entry.ChildTicks = 0;
        timingDepth++;
        if (timingDepth > MaxObservedDepth) MaxObservedDepth = timingDepth;
        return cookie;
    }

    private static bool TryTimingClock(out long now)
    {
        now = 0;
        try
        {
            now = Stopwatch.GetTimestamp();
            if (now < 0 || (timingClockSeen && now < timingLast))
            {
                TimingFault();
                return false;
            }
            timingLast = now;
            timingClockSeen = true;
            return true;
        }
        catch
        {
            TimingFault();
            return false;
        }
    }

    private static void RecordScope(ref UI51StackEntry entry, long now, bool completed)
    {
        if (FrameMetrics == null || entry.Id < 0 || entry.Id >= FrameMetrics.Length ||
            entry.Start < 0 || now < entry.Start || entry.ChildTicks < 0 ||
            entry.ChildTicks > now - entry.Start)
        {
            TimingFault();
            return;
        }
        long inclusive = now - entry.Start;
        long exclusive = inclusive - entry.ChildTicks;
        ref UI51Metric metric = ref FrameMetrics[entry.Id];
        if (!MetricValid(ref metric) || !Fits(metric.Calls, completed ? 1 : 0) ||
            !Fits(metric.InclusiveTicks, inclusive) || !Fits(metric.ExclusiveTicks, exclusive))
        {
            TimingFault();
            return;
        }
        if (timingDepth > 1)
        {
            ref UI51StackEntry parent = ref timingStack![timingDepth - 2];
            if (parent.Start < 0 || parent.Start > entry.Start ||
                !Fits(parent.ChildTicks, inclusive))
            {
                TimingFault();
                return;
            }
            parent.ChildTicks += inclusive;
        }
        // Failed original calls invalidate donation, but still close their interval
        // so balanced outer finally blocks retain their actual nesting semantics.
        if (!completed) return;
        metric.Calls++;
        metric.InclusiveTicks += inclusive;
        metric.ExclusiveTicks += exclusive;
        if (inclusive > metric.InclusiveMax) metric.InclusiveMax = inclusive;
        if (exclusive > metric.ExclusiveMax) metric.ExclusiveMax = exclusive;
    }

    internal static bool ValidateSample()
    {
        try
        {
            if (FrameTimingInvalid || FrameScopeAborted) return false;
            if (!sampleEnded || sampleOpen || !rootCompleted || timingDepth != 0 || Active ||
                !FrameClockPairValid || FrameClockPairTicks < 0 || FrameStoppedLayers < 0 ||
                uiRoots < 0 || uiRoots > 1 || !TimingArraysValid())
                return InvalidSample();
            long totalExclusive = 0, childExclusive = 0;
            ref UI51Metric frame = ref FrameMetrics![FrameMetric];
            ref UI51Metric ui = ref FrameMetrics[UIMetric];
            if (frame.Calls != 1 || ui.Calls != uiRoots) return InvalidSample();
            for (int id = 0; id < MetricCount; id++)
            {
                ref UI51Metric metric = ref FrameMetrics[id];
                if (!MetricValid(ref metric) || !Fits(totalExclusive, metric.ExclusiveTicks))
                    return InvalidSample();
                totalExclusive += metric.ExclusiveTicks;
                if (id > UIMetric)
                {
                    if ((uiRoots == 0 && metric.Calls != 0) || metric.InclusiveMax > ui.InclusiveTicks ||
                        !Fits(childExclusive, metric.ExclusiveTicks)) return InvalidSample();
                    childExclusive += metric.ExclusiveTicks;
                }
            }
            if (totalExclusive != frame.InclusiveTicks ||
                !Fits(frame.ExclusiveTicks, ui.InclusiveTicks) ||
                frame.ExclusiveTicks + ui.InclusiveTicks != frame.InclusiveTicks ||
                !Fits(ui.ExclusiveTicks, childExclusive) ||
                ui.ExclusiveTicks + childExclusive != ui.InclusiveTicks)
                return InvalidSample();
            return true;
        }
        catch
        {
            TimingFault();
            return false;
        }
    }

    internal static bool CommitSample(int cohort)
    {
        try
        {
            if (!ValidateSample()) return false;
            if (sampleCommitted || cohort < 0 || cohort >= CohortCount || !TimingArraysValid())
                return InvalidSample();
            int offset = cohort * MetricCount;
            // Every destination is checked before even the first metric is touched.
            for (int id = 0; id < MetricCount; id++)
            {
                ref UI51Metric source = ref FrameMetrics![id];
                ref UI51Metric destination = ref WindowMetrics![offset + id];
                if (!MetricValid(ref destination) || !Fits(destination.Calls, source.Calls) ||
                    !Fits(destination.InclusiveTicks, source.InclusiveTicks) ||
                    !Fits(destination.ExclusiveTicks, source.ExclusiveTicks))
                    return InvalidSample();
            }
            for (int id = 0; id < MetricCount; id++)
            {
                ref UI51Metric source = ref FrameMetrics![id];
                ref UI51Metric destination = ref WindowMetrics![offset + id];
                destination.Calls += source.Calls;
                destination.InclusiveTicks += source.InclusiveTicks;
                destination.ExclusiveTicks += source.ExclusiveTicks;
                if (source.InclusiveMax > destination.InclusiveMax) destination.InclusiveMax = source.InclusiveMax;
                if (source.ExclusiveMax > destination.ExclusiveMax) destination.ExclusiveMax = source.ExclusiveMax;
            }
            sampleCommitted = true;
            return true;
        }
        catch
        {
            TimingFault();
            return false;
        }
    }

    internal static bool Fits(long destination, long value)
    {
        return destination >= 0 && value >= 0 && destination <= long.MaxValue - value;
    }

    internal static void Add(ref long destination, long value)
    {
        if (!Fits(destination, value))
        {
            destination = long.MaxValue;
            TimingFault();
        }
        else destination += value;
    }

    private static bool TimingArraysValid()
    {
        return FrameMetrics != null && FrameMetrics.Length == MetricCount &&
            WindowMetrics != null && WindowMetrics.Length == MetricCount * CohortCount &&
            timingStack != null && timingStack.Length == MaxDepth;
    }

    private static bool MetricValid(ref UI51Metric metric)
    {
        return metric.Calls >= 0 && metric.InclusiveTicks >= 0 && metric.ExclusiveTicks >= 0 &&
            metric.ExclusiveTicks <= metric.InclusiveTicks && metric.InclusiveMax >= 0 &&
            metric.ExclusiveMax >= 0 && metric.ExclusiveMax <= metric.InclusiveMax &&
            TotalWithinMax(metric.Calls, metric.InclusiveTicks, metric.InclusiveMax) &&
            TotalWithinMax(metric.Calls, metric.ExclusiveTicks, metric.ExclusiveMax);
    }

    private static bool TotalWithinMax(long calls, long ticks, long maximum)
    {
        if (maximum > ticks) return false;
        if (calls == 0 || maximum == 0) return ticks == 0 && maximum == 0;
        return calls > long.MaxValue / maximum || ticks <= calls * maximum;
    }

    private static void ScopeAborted()
    {
        FrameScopeAborted = true;
    }

    private static void UnbalancedTiming()
    {
        Add(ref UnbalancedScopes, 1);
        TimingFault();
    }

    private static void TimingFault()
    {
        FrameTimingInvalid = true;
        MeasurementInvalid = true;
        if (ScopeFailures >= 0 && ScopeFailures < long.MaxValue) ScopeFailures++;
        else ScopeFailures = long.MaxValue;
    }

    private static bool InvalidSample()
    {
        TimingFault();
        return false;
    }
}
