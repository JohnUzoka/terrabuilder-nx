using System;
using System.Collections.Generic;

// Host-only environment around the extracted, serialized production IL.
public sealed class FixtureTime { }
public sealed class FixtureSeries
{
    public int[] values = new int[300];
    public bool[] used = new bool[300];
    public int next;
    public bool _nxProfileReset;
    public void Advance() { next = (next + 1) % 300; values[next] = 0; used[next] = false; _nxProfileReset = false; }
    public void Reset() { next = 0; _nxProfileReset = true; }
}
public sealed class FixtureMetric
{
    public string name = "";
    public int _nxProfileId;
    public FixtureSeries[] data = new[] { new FixtureSeries(), new FixtureSeries() };
    public void StartNextFrame() { foreach (var s in data) s.Advance(); }
}
public static class FixtureLogger
{
    public static int FrameCount = 300;
    public static List<FixtureMetric> entries = new();
    public static Queue<Action> _onNextFrame = new();
    public static int activeDataSeries;
    public static int nextSeries;
    public static void ABTest() { activeDataSeries = nextSeries; }
    public static void Populate()
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var s = entries[i].data[activeDataSeries];
            s.used[s.next] = i % 3 != 0; s.values[s.next] = i;
        }
    }
}
public sealed class FixtureAssets
{
    public void TransferCompletedAssets() { FixtureMain.Effect(4); }
}
public sealed class FixtureCinematic
{
    public static FixtureCinematic Instance = new();
    public void Update(FixtureTime time) { FixtureMain.Effect(7); }
}
public sealed class FixtureMain
{
    public static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, renderNow;
    public static int FrameSkipMode, renderCount;
    public bool _isDrawingOrUpdating;
    public bool IsActive { get; set; } = true;
    public static bool IsEnginePreloaded, GameAskedToQuit, GraphicsAvailable = true;
    public static Action? OnEnginePreload;
    public static Action<FixtureTime>? OnPostDraw;
    public static FixtureAssets Assets = new();
    public static int ThrowStage;
    public static bool ToggleInventory, Record = true;
    public static long Effects;
    public static readonly Exception Failure = new InvalidOperationException("fixture game exception");
    public static void Effect(int id) { if (Record) Effects = unchecked(Effects * 17 + id); if (ThrowStage == id) throw Failure; }
    public static bool IsGraphicsDeviceAvailable() { return GraphicsAvailable; }
    public void EnsureRenderTargetContent() { Effect(1); }
    public void DoDraw(FixtureTime time) { Effect(2); if (ToggleInventory) playerInventory = !playerInventory; }
    public void DoUpdate(ref FixtureTime time) { Effect(6); }
    public void BaseUpdate(FixtureTime time) { Effect(10); }
    public static void ConsumeAllMainThreadActions() { Effect(8); }
    public void QuitGame() { Effect(11); }
    public void Run() { Effect(12); }
    public static void DetailedBegin(int category) { Effect(20 + category); }
    public static void DetailedEnd() { Effect(25); }
}
public sealed class FailingWriter : System.IO.StringWriter
{
    public override void WriteLine(string? value) { throw new System.IO.IOException("controlled fixture output failure"); }
}
