using System;
using System.Diagnostics;

internal static partial class Tile49Template
{
    [ThreadStatic] private static int phaseSolid, phaseNonSolid;

    internal static void Enter(bool solidLayer, ref Tile49Pass pass)
    {
        pass = default;
        pass.Saved = current;
        pass.SavedPending = Pending;
        pass.Layer = solidLayer ? 0 : 1;
        pass.Nested = current.Active;
        pass.Enabled = FrameCollecting && !pass.Nested;
        current = default;
        current.Active = true;
        current.Layer = pass.Enabled ? pass.Layer : -1;
        current.Open = -1;
        Pending = false;
        if (!pass.Enabled) return;

        if (solidLayer)
        {
            pass.Phase = phaseSolid;
            phaseSolid = (phaseSolid + 1) & SampleMask;
        }
        else
        {
            pass.Phase = phaseNonSolid;
            phaseNonSolid = (phaseNonSolid + 1) & SampleMask;
        }
        try
        {
            pass.PassStart = Stopwatch.GetTimestamp();
            pass.Last = pass.PassStart;
            current.Last = pass.PassStart;
            pass.TimingValid = pass.PassStart >= 0;
            if (!pass.TimingValid) MeasurementInvalid = true;
        }
        catch
        {
            pass.TimingValid = false;
            MeasurementInvalid = true;
        }
    }

    internal static void Region(ref Tile49Pass pass, int region)
    {
        if (!pass.Enabled)
        {
            if (region == 2) pass.Normal = true;
            return;
        }
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (now < 0 || now < pass.Last || now < current.Last || pass.Normal)
                pass.TimingValid = false;
            pass.Last = now;
            current.Last = now;
            if (region == 0)
            {
                if (pass.LoopStarted || pass.LoopComplete) pass.TimingValid = false;
                pass.LoopStarted = true;
                pass.LoopStart = now;
            }
            else if (region == 1)
            {
                if (!pass.LoopStarted || pass.LoopComplete || now < pass.LoopStart)
                    pass.TimingValid = false;
                pass.LoopComplete = true;
                if (pass.TimingValid) pass.LoopTicks = now - pass.LoopStart;
            }
            else if (region == 2)
            {
                if ((pass.LoopStarted && !pass.LoopComplete) || now < pass.PassStart)
                    pass.TimingValid = false;
                pass.Normal = true;
                if (pass.TimingValid) pass.PassTicks = now - pass.PassStart;
            }
            else pass.TimingValid = false;
            if (!pass.TimingValid) MeasurementInvalid = true;
        }
        catch
        {
            if (region == 2) pass.Normal = true;
            pass.TimingValid = false;
            MeasurementInvalid = true;
        }
    }

    internal static void BeginSelected()
    {
        if (!FrameCollecting || !current.Active || current.Layer < 0) return;
        if (current.SampleActive || Pending) Add(ref current.Aborted, 1);
        current.SampleActive = true;
        current.SampleValid = Fits(current.Selected, 1);
        current.SampleColor = default;
        current.SampleData = default;
        current.SampleOutline = default;
        current.SampleOverride = default;
        current.SampleFinal = default;
        current.Open = -1;
        current.PairTicks = 0;
        Add(ref current.Selected, 1);
        Pending = true;
        try
        {
            // Bare adjacent timestamps: diagnostic only, never subtracted from samples.
            long first = Stopwatch.GetTimestamp();
            long second = Stopwatch.GetTimestamp();
            if (first < 0 || first < current.Last || second < first)
                current.SampleValid = false;
            else current.PairTicks = second - first;
            long start = Stopwatch.GetTimestamp();
            if (start < 0 || start < second) current.SampleValid = false;
            current.Start = start;
            current.Last = start;
        }
        catch
        {
            current.SampleValid = false;
            MeasurementInvalid = true;
        }
    }

    internal static void ConsumePending()
    {
        Pending = false;
    }

    internal static void BeginOp(int id)
    {
        if (!FrameCollecting || !current.Active || current.Layer < 0 || !current.SampleActive) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (Pending || current.Open != -1 || id < 0 || id >= HelperCount || now < 0 || now < current.Last)
                current.SampleValid = false;
            current.Last = now;
            current.OpStart = now;
            current.Open = id;
        }
        catch
        {
            current.SampleValid = false;
            MeasurementInvalid = true;
        }
    }

    internal static void EndOp(int id)
    {
        if (!FrameCollecting || !current.Active || current.Layer < 0 || !current.SampleActive) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (Pending || id < 0 || id >= HelperCount || current.Open != id || now < 0 ||
                now < current.Last || current.OpStart < 0 || now < current.OpStart)
                current.SampleValid = false;
            current.Last = now;
            current.Open = -1;
            if (!current.SampleValid) return;
            long ticks = now - current.OpStart;
            switch (id)
            {
                case 0: RecordOperation(ref current.SampleColor, ticks); break;
                case 1: RecordOperation(ref current.SampleData, ticks); break;
                case 2: RecordOperation(ref current.SampleOutline, ticks); break;
                case 3: RecordOperation(ref current.SampleOverride, ticks); break;
                case 4: RecordOperation(ref current.SampleFinal, ticks); break;
            }
        }
        catch
        {
            current.SampleValid = false;
            MeasurementInvalid = true;
        }
    }

    private static void RecordOperation(ref Tile49Metric metric, long ticks)
    {
        if (!Fits(metric.Calls, 1) || !Fits(metric.Ticks, ticks))
        {
            current.SampleValid = false;
            MeasurementInvalid = true;
            return;
        }
        metric.Calls++;
        metric.Ticks += ticks;
        if (ticks > metric.Max) metric.Max = ticks;
    }

    internal static void EndSample()
    {
        if (!FrameCollecting || !current.Active || current.Layer < 0 || !current.SampleActive) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (Pending || current.Open != -1 || now < 0 || now < current.Last ||
                current.Start < 0 || now < current.Start || !Fits(current.Completed, 1))
                current.SampleValid = false;
            current.Last = now;
            long ticks = current.SampleValid ? now - current.Start : 0;
            long sum = 0;
            if (!SumMetric(ref sum, ref current.SampleColor) || !SumMetric(ref sum, ref current.SampleData) ||
                !SumMetric(ref sum, ref current.SampleOutline) || !SumMetric(ref sum, ref current.SampleOverride) ||
                !SumMetric(ref sum, ref current.SampleFinal) || sum > ticks ||
                !CanMergeMetric(ref current.Color, ref current.SampleColor) ||
                !CanMergeMetric(ref current.Data, ref current.SampleData) ||
                !CanMergeMetric(ref current.Outline, ref current.SampleOutline) ||
                !CanMergeMetric(ref current.Override, ref current.SampleOverride) ||
                !CanMergeMetric(ref current.Final, ref current.SampleFinal) ||
                !MetricValid(ref current.ClockPair) || !Fits(current.ClockPair.Calls, 1) ||
                !Fits(current.ClockPair.Ticks, current.PairTicks) ||
                !Fits(current.SampleTicks, ticks) || !Fits(current.Valid, 1))
                current.SampleValid = false;
            Add(ref current.Completed, 1);
            if (!current.SampleValid)
            {
                Add(ref current.Invalid, 1);
                MeasurementInvalid = true;
                return;
            }

            // All checks precede all writes: one invalid sample donates no helper metric.
            MergeMetric(ref current.Color, ref current.SampleColor);
            MergeMetric(ref current.Data, ref current.SampleData);
            MergeMetric(ref current.Outline, ref current.SampleOutline);
            MergeMetric(ref current.Override, ref current.SampleOverride);
            MergeMetric(ref current.Final, ref current.SampleFinal);
            current.ClockPair.Calls++;
            current.ClockPair.Ticks += current.PairTicks;
            if (current.PairTicks > current.ClockPair.Max) current.ClockPair.Max = current.PairTicks;
            current.Valid++;
            current.SampleTicks += ticks;
            if (ticks > current.SampleMax) current.SampleMax = ticks;
        }
        catch
        {
            Add(ref current.Completed, 1);
            Add(ref current.Invalid, 1);
            MeasurementInvalid = true;
        }
        finally
        {
            current.SampleActive = false;
            current.Open = -1;
            Pending = false;
        }
    }

    internal static void Finish(ref Tile49Pass pass)
    {
        try
        {
            if (!pass.Enabled && !(pass.Nested && FrameCollecting)) return;
            Tile49LayerTotals value = default;
            value.Passes = 1;
            if (pass.Nested)
            {
                value.NestedPasses = 1;
            }
            else
            {
                if (current.SampleActive || Pending) Add(ref current.Aborted, 1);
                value.Visited = pass.Visited;
                value.Eligible = pass.Eligible;
                value.Calls = pass.Calls;
                value.Selected = current.Selected;
                value.CompletedSamples = current.Completed;
                value.InvalidSamples = current.Invalid;
                value.AbortedSamples = current.Aborted;
                if (pass.Visited < 0 || pass.Eligible < 0 || pass.Calls < 0 || pass.Eligible > pass.Visited ||
                    pass.Calls > pass.Eligible || current.Selected > pass.Calls || !current.Active ||
                    current.Layer != pass.Layer || !ContextValid())
                    pass.TimingValid = false;
                if (pass.Normal) value.CompletedPasses = 1;
                else value.AbortedPasses = 1;
                if (!pass.TimingValid)
                {
                    value.InvalidPasses = 1;
                    MeasurementInvalid = true;
                }
                if (pass.Normal && pass.TimingValid)
                {
                    value.ValidSamples = current.Valid;
                    value.SampleTicks = current.SampleTicks;
                    value.SampleMax = current.SampleMax;
                    value.Color = current.Color;
                    value.Data = current.Data;
                    value.Outline = current.Outline;
                    value.Override = current.Override;
                    value.Final = current.Final;
                    value.ClockPair = current.ClockPair;
                    value.PassTicks = pass.PassTicks;
                    value.PassMax = pass.PassTicks;
                    if (pass.LoopStarted && pass.LoopComplete)
                    {
                        value.CompletedLoops = 1;
                        value.LoopTicks = pass.LoopTicks;
                        value.LoopMax = pass.LoopTicks;
                    }
                }
                else value.DiscardedSamples = current.Valid;
            }
            if (pass.Layer == 0) MergeLayer(ref FrameSolid, ref value);
            else MergeLayer(ref FrameNonSolid, ref value);
        }
        catch
        {
            MeasurementInvalid = true;
        }
        finally
        {
            current = pass.Saved;
            Pending = pass.SavedPending;
        }
    }

    private static bool Fits(long destination, long value)
    {
        return destination >= 0 && value >= 0 && destination <= long.MaxValue - value;
    }

    internal static void Add(ref long destination, long value)
    {
        if (!Fits(destination, value))
        {
            destination = long.MaxValue;
            MeasurementInvalid = true;
        }
        else destination += value;
    }

    private static bool ContextValid()
    {
        if (current.Selected < 0 || current.Completed < 0 || current.Valid < 0 || current.Invalid < 0 ||
            current.Aborted < 0 || current.Completed > current.Selected || current.Valid > current.Completed ||
            current.Aborted != current.Selected - current.Completed || current.Invalid != current.Completed - current.Valid ||
            current.ClockPair.Calls != current.Valid ||
            (current.Color.Calls > current.Valid && current.Color.Calls - current.Valid > current.Valid) ||
            current.Data.Calls > current.Valid || current.Outline.Calls > current.Valid ||
            current.Override.Calls > current.Valid || current.Final.Calls > current.Valid ||
            !AggregateMetricValid(ref current.Color) || !AggregateMetricValid(ref current.Data) ||
            !AggregateMetricValid(ref current.Outline) || !AggregateMetricValid(ref current.Override) ||
            !AggregateMetricValid(ref current.Final) || !AggregateMetricValid(ref current.ClockPair) ||
            !TotalWithinMax(current.Valid, current.SampleTicks, current.SampleMax)) return false;
        long sum = 0;
        return SumMetric(ref sum, ref current.Color) && SumMetric(ref sum, ref current.Data) &&
            SumMetric(ref sum, ref current.Outline) && SumMetric(ref sum, ref current.Override) &&
            SumMetric(ref sum, ref current.Final) && sum <= current.SampleTicks;
    }

    private static bool AggregateMetricValid(ref Tile49Metric metric)
    {
        return MetricValid(ref metric) && TotalWithinMax(metric.Calls, metric.Ticks, metric.Max);
    }

    private static bool TotalWithinMax(long calls, long ticks, long maximum)
    {
        if (calls < 0 || ticks < 0 || maximum < 0 || maximum > ticks) return false;
        if (calls == 0 || maximum == 0) return ticks == 0;
        // This division is per-pass only, never on the selected or unsampled tile path.
        return calls > long.MaxValue / maximum || ticks <= calls * maximum;
    }

    private static bool MetricValid(ref Tile49Metric metric)
    {
        return metric.Calls >= 0 && metric.Ticks >= 0 && metric.Max >= 0 && metric.Max <= metric.Ticks &&
            (metric.Calls != 0 || (metric.Ticks == 0 && metric.Max == 0)) &&
            (metric.Max != 0 || metric.Ticks == 0);
    }

    private static bool CanMergeMetric(ref Tile49Metric destination, ref Tile49Metric source)
    {
        return MetricValid(ref destination) && MetricValid(ref source) &&
            Fits(destination.Calls, source.Calls) && Fits(destination.Ticks, source.Ticks);
    }

    private static bool SumMetric(ref long sum, ref Tile49Metric metric)
    {
        if (!MetricValid(ref metric) || !Fits(sum, metric.Ticks)) return false;
        sum += metric.Ticks;
        return true;
    }

    internal static void MergeMetric(ref Tile49Metric destination, ref Tile49Metric source)
    {
        if (!MetricValid(ref destination) || !MetricValid(ref source)) MeasurementInvalid = true;
        Add(ref destination.Calls, source.Calls);
        Add(ref destination.Ticks, source.Ticks);
        MergeMax(ref destination.Max, source.Max);
    }

    private static void MergeMax(ref long destination, long value)
    {
        if (destination < 0 || value < 0)
        {
            destination = long.MaxValue;
            MeasurementInvalid = true;
        }
        else if (value > destination) destination = value;
    }

    internal static void MergeLayer(ref Tile49LayerTotals destination, ref Tile49LayerTotals source)
    {
        Add(ref destination.Passes, source.Passes);
        Add(ref destination.CompletedPasses, source.CompletedPasses);
        Add(ref destination.AbortedPasses, source.AbortedPasses);
        Add(ref destination.NestedPasses, source.NestedPasses);
        Add(ref destination.InvalidPasses, source.InvalidPasses);
        Add(ref destination.Visited, source.Visited);
        Add(ref destination.Eligible, source.Eligible);
        Add(ref destination.Calls, source.Calls);
        Add(ref destination.Selected, source.Selected);
        Add(ref destination.CompletedSamples, source.CompletedSamples);
        Add(ref destination.ValidSamples, source.ValidSamples);
        Add(ref destination.InvalidSamples, source.InvalidSamples);
        Add(ref destination.AbortedSamples, source.AbortedSamples);
        Add(ref destination.DiscardedSamples, source.DiscardedSamples);
        Add(ref destination.CompletedLoops, source.CompletedLoops);
        Add(ref destination.LoopTicks, source.LoopTicks);
        MergeMax(ref destination.LoopMax, source.LoopMax);
        Add(ref destination.PassTicks, source.PassTicks);
        MergeMax(ref destination.PassMax, source.PassMax);
        Add(ref destination.SampleTicks, source.SampleTicks);
        MergeMax(ref destination.SampleMax, source.SampleMax);
        MergeMetric(ref destination.Color, ref source.Color);
        MergeMetric(ref destination.Data, ref source.Data);
        MergeMetric(ref destination.Outline, ref source.Outline);
        MergeMetric(ref destination.Override, ref source.Override);
        MergeMetric(ref destination.Final, ref source.Final);
        MergeMetric(ref destination.ClockPair, ref source.ClockPair);
    }
}
