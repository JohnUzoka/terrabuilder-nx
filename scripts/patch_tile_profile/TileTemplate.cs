using System.Diagnostics;

// Compile-time signatures only. The patch maps every use to existing target members.
internal sealed class MetricShape { internal void Add(int value) => throw new NotSupportedException(); }
internal static class LoggerShape
{
    internal static MetricShape NewEntry(string name, TimeSpan? threshold) => throw new NotSupportedException();
    internal static MetricShape NewCounterEntry(string name, int threshold) => throw new NotSupportedException();
}
internal struct PassShape
{
    internal int Offset, Phase, Visited, Eligible, Calls, Samples;
    internal long RegionStart, SampleTicks;
    internal bool Completed;
}
internal static class TileTemplate
{
    internal const int Width = 10, MetricCount = 20, SampleMask = 31;
    static readonly MetricShape[] metrics;
    static int solidPhase, nonSolidPhase;
    // Explicit cctor: NOT beforefieldinit. First Begin is inside the first Draw call,
    // after original Main.DoDraw has already initialized TimeLogger using Start().
    static TileTemplate()
    {
        metrics = new MetricShape[MetricCount];
        Register(0, "tile44.solid.");
        Register(Width, "tile44.nonsolid.");
    }
    static void Register(int offset, string prefix)
    {
        metrics[offset] = LoggerShape.NewEntry(prefix + "setup.completed_region", null);
        metrics[offset + 1] = LoggerShape.NewEntry(prefix + "loop.completed_region", null);
        metrics[offset + 2] = LoggerShape.NewEntry(prefix + "post.completed_region", null);
        metrics[offset + 3] = LoggerShape.NewCounterEntry(prefix + "passes.started", 0);
        metrics[offset + 4] = LoggerShape.NewCounterEntry(prefix + "passes.completed", 0);
        metrics[offset + 5] = LoggerShape.NewCounterEntry(prefix + "tiles.visited_attempted", 0);
        metrics[offset + 6] = LoggerShape.NewCounterEntry(prefix + "tiles.layer_eligible", 0);
        metrics[offset + 7] = LoggerShape.NewCounterEntry(prefix + "DrawSingleTile.calls_attempted", 0);
        metrics[offset + 8] = LoggerShape.NewCounterEntry(prefix + "DrawSingleTile.samples_completed_1in32", 0);
        metrics[offset + 9] = LoggerShape.NewEntry(prefix + "DrawSingleTile.sampled_time_only_1in32", null);
    }
    internal static void Begin(bool solid, ref PassShape pass)
    {
        pass.Offset = solid ? 0 : Width;
        if (solid) { pass.Phase = solidPhase; solidPhase = (solidPhase + 1) & SampleMask; }
        else { pass.Phase = nonSolidPhase; nonSolidPhase = (nonSolidPhase + 1) & SampleMask; }
        metrics[pass.Offset + 3].Add(1);
        pass.RegionStart = Stopwatch.GetTimestamp();
    }
    internal static void Region(ref PassShape pass, int region)
    {
        long end = Stopwatch.GetTimestamp();
        metrics[pass.Offset + region].Add(unchecked((int)(end - pass.RegionStart)));
        if (region != 2) pass.RegionStart = Stopwatch.GetTimestamp();
        else pass.Completed = true;
    }
    internal static void Finish(ref PassShape pass)
    {
        int offset = pass.Offset;
        metrics[offset + 4].Add(pass.Completed ? 1 : 0);
        metrics[offset + 5].Add(pass.Visited);
        metrics[offset + 6].Add(pass.Eligible);
        metrics[offset + 7].Add(pass.Calls);
        metrics[offset + 8].Add(pass.Samples);
        // An unsampled pass has no sampled-duration observation, not a zero-duration sample.
        if (pass.Samples != 0) metrics[offset + 9].Add(unchecked((int)pass.SampleTicks));
    }
}
