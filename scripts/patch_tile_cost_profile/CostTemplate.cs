using System.Diagnostics;

// Compile-time shapes only; every use is mapped to existing game members.
internal sealed class CostMetricShape
{
    internal void Add(int value) => throw new NotSupportedException();
    internal int NXCost45Current() => throw new NotSupportedException();
}
internal static class CostLoggerShape
{
    internal static CostMetricShape NewEntry(string name, TimeSpan? threshold) => throw new NotSupportedException();
    internal static CostMetricShape NewCounterEntry(string name, int threshold) => throw new NotSupportedException();
}
internal struct ContextShape
{
    internal bool Active;
    internal int Offset, Phase;
    internal long Calls, Selected, Completed, Invalid, Valid;
    internal long Alloc, Light, Texture, Base, Body, Other;
    internal long AllocOps, LightOps, TextureOps, BaseOps;
}
internal struct SampleShape
{
    internal bool Selected, Valid;
    internal int Open;
    internal long Start, Last, OpStart;
    internal long Alloc, Light, Texture, Base;
    internal long AllocOps, LightOps, TextureOps, BaseOps;
}
internal static class CostTemplate
{
    internal const int Width = 14, MetricCount = 28;
    private static CostMetricShape[]? metrics;
    [ThreadStatic] internal static ContextShape current;
    static CostTemplate() { }

    private static void Initialize()
    {
        if (metrics != null) return;
        metrics = new CostMetricShape[MetricCount];
        Register(0, "tile45.solid.");
        Register(Width, "tile45.nonsolid.");
    }
    private static void Register(int offset, string prefix)
    {
        RegisterGroup(offset, prefix + "alloc_init");
        RegisterGroup(offset + 2, prefix + "light_and_frame");
        RegisterGroup(offset + 4, prefix + "texture_lookup");
        RegisterGroup(offset + 6, prefix + "base_draw");
        metrics![offset + 8] = CostLoggerShape.NewEntry(prefix + "call_body.completed_samples", null);
        metrics[offset + 9] = CostLoggerShape.NewEntry(prefix + "other_body_including_observers.completed_samples", null);
        metrics[offset + 10] = CostLoggerShape.NewCounterEntry(prefix + "calls.selected", 0);
        metrics[offset + 11] = CostLoggerShape.NewCounterEntry(prefix + "calls.completed", 0);
        metrics[offset + 12] = CostLoggerShape.NewCounterEntry(prefix + "calls.invalid_timing", 0);
        metrics[offset + 13] = CostLoggerShape.NewCounterEntry(prefix + "time_metrics.omitted_out_of_range", 0);
    }
    private static void RegisterGroup(int offset, string name)
    {
        metrics![offset] = CostLoggerShape.NewEntry(name + ".completed_samples", null);
        metrics[offset + 1] = CostLoggerShape.NewCounterEntry(name + ".operations", 0);
    }
    internal static void Enter(ref ContextShape saved, int phase, int offset)
    {
        saved = current;
        Initialize();
        current = default;
        current.Active = true;
        current.Phase = phase;
        current.Offset = offset;
    }
    private static void Time(int id, long value, ref int omitted)
    {
        int previous = metrics![id].NXCost45Current();
        if (value < 0 || previous < 0 || value > int.MaxValue - (long)previous) { omitted++; return; }
        metrics[id].Add((int)value);
    }
    internal static void Finish(ContextShape saved)
    {
        var value = current;
        try
        {
            if (!value.Active) return;
            int offset = value.Offset, omitted = 0;
            if (value.AllocOps != 0) Time(offset, value.Alloc, ref omitted);
            metrics![offset + 1].Add(unchecked((int)value.AllocOps));
            if (value.LightOps != 0) Time(offset + 2, value.Light, ref omitted);
            metrics[offset + 3].Add(unchecked((int)value.LightOps));
            if (value.TextureOps != 0) Time(offset + 4, value.Texture, ref omitted);
            metrics[offset + 5].Add(unchecked((int)value.TextureOps));
            if (value.BaseOps != 0) Time(offset + 6, value.Base, ref omitted);
            metrics[offset + 7].Add(unchecked((int)value.BaseOps));
            if (value.Valid != 0)
            {
                Time(offset + 8, value.Body, ref omitted);
                Time(offset + 9, value.Other, ref omitted);
            }
            metrics[offset + 10].Add(unchecked((int)value.Selected));
            metrics[offset + 11].Add(unchecked((int)value.Completed));
            metrics[offset + 12].Add(unchecked((int)value.Invalid));
            metrics[offset + 13].Add(omitted);
        }
        finally { current = saved; }
    }
    internal static void BeginSample(ref SampleShape sample)
    {
        sample = default;
        if (!current.Active) return;
        long index = current.Calls++;
        if (((index + current.Phase) & 31) != 0) return;
        current.Selected++;
        sample.Selected = true;
        sample.Start = sample.Last = Stopwatch.GetTimestamp();
        sample.Valid = sample.Start >= 0;
    }
    private static long Now(ref SampleShape sample)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < 0 || now < sample.Last) sample.Valid = false;
        sample.Last = now;
        return now;
    }
    private static bool Fits(long previous, long value) => previous >= 0 && value >= 0 && previous <= long.MaxValue - value;
    internal static void BeginOp(ref SampleShape sample, int group)
    {
        if (!sample.Selected) return;
        if (sample.Open != 0 || group < 0 || group > 3) sample.Valid = false;
        sample.OpStart = Now(ref sample);
        sample.Open = group + 1;
    }
    internal static void EndOp(ref SampleShape sample, int group)
    {
        if (!sample.Selected) return;
        long now = Now(ref sample);
        if (sample.Open != group + 1 || sample.OpStart < 0 || now < sample.OpStart) sample.Valid = false;
        long elapsed = sample.Valid ? now - sample.OpStart : 0;
        switch (group)
        {
            case 0: if (Fits(sample.Alloc, elapsed)) { sample.Alloc += elapsed; sample.AllocOps++; } else sample.Valid = false; break;
            case 1: if (Fits(sample.Light, elapsed)) { sample.Light += elapsed; sample.LightOps++; } else sample.Valid = false; break;
            case 2: if (Fits(sample.Texture, elapsed)) { sample.Texture += elapsed; sample.TextureOps++; } else sample.Valid = false; break;
            case 3: if (Fits(sample.Base, elapsed)) { sample.Base += elapsed; sample.BaseOps++; } else sample.Valid = false; break;
            default: sample.Valid = false; break;
        }
        sample.Open = 0;
    }
    internal static void EndSample(ref SampleShape sample)
    {
        if (!sample.Selected) return;
        long now = Now(ref sample);
        current.Completed++;
        if (!sample.Valid || sample.Open != 0 || sample.Start < 0 || now < sample.Start ||
            !Fits(sample.Alloc, sample.Light) || !Fits(sample.Alloc + sample.Light, sample.Texture) ||
            !Fits(sample.Alloc + sample.Light + sample.Texture, sample.Base)) { current.Invalid++; return; }
        long body = now - sample.Start;
        long sum = sample.Alloc + sample.Light + sample.Texture + sample.Base;
        if (sum > body || !Fits(current.Alloc, sample.Alloc) || !Fits(current.Light, sample.Light) ||
            !Fits(current.Texture, sample.Texture) || !Fits(current.Base, sample.Base) ||
            !Fits(current.Body, body) || !Fits(current.Other, body - sum)) { current.Invalid++; return; }
        current.Valid++;
        current.Alloc += sample.Alloc; current.Light += sample.Light; current.Texture += sample.Texture; current.Base += sample.Base;
        current.Body += body; current.Other += body - sum;
        current.AllocOps += sample.AllocOps; current.LightOps += sample.LightOps; current.TextureOps += sample.TextureOps; current.BaseOps += sample.BaseOps;
    }
}
