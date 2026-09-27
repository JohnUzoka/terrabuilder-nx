using System.Diagnostics;

// Host-only clock, snapshot and writer boundaries for candidate-derived runtime IL.
// These types are never emitted into the game.
public struct RuntimeVector
{
    public float X, Y;
    public RuntimeVector(float x, float y) { X = x; Y = y; }
}
public class RuntimeEntity { public RuntimeVector position; }
public sealed class RuntimePlayer : RuntimeEntity { public bool dead, ghost; public int spectating = -1; }
public sealed class RuntimeItem { public int type; public byte prefix; }
public static class RuntimeMain
{
    public static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    public static int myPlayer, screenWidth, screenHeight, FrameSkipMode, mouseX, mouseY, numAvailableRecipes;
    public static int netMode, maxTilesX, maxTilesY;
    public static float GameZoomTarget, UIScaleValue;
    public static double time;
    public static RuntimeVector screenPosition;
    public static RuntimePlayer[]? player;
    public static RuntimeItem? HoverItem, mouseItem, guideItem;
    public static int SnapshotCalls, ThrowSnapshotAt = -1;
    public static float UIScale { get { SnapshotCalls++; if (SnapshotCalls == ThrowSnapshotAt) { RuntimeFixture.RecordFault("snapshot.throw"); throw RuntimeFixture.SnapshotFailure; } return UIScaleValue; } }
    public static void Reset()
    {
        gameMenu = gamePaused = playerInventory = mapFullscreen = autoPause = false; dayTime = true;
        myPlayer = 0; screenWidth = 1280; screenHeight = 720; FrameSkipMode = 0; mouseX = 100; mouseY = 200; numAvailableRecipes = 0;
        netMode = 0; maxTilesX = 4200; maxTilesY = 1200;
        GameZoomTarget = UIScaleValue = 1; time = 100; screenPosition = new(20, 40);
        player = new[] { new RuntimePlayer { position = new(120, 240) } }; HoverItem = new() { type = 1 }; mouseItem = new(); guideItem = null;
        SnapshotCalls = 0; ThrowSnapshotAt = -1;
    }
}
public static class RuntimeFixture
{
    [ThreadStatic] public static long Tick, Step, LastClock;
    [ThreadStatic] public static int ClockIndex, ClockCalls, ThrowClockAt;
    [ThreadStatic] public static long[]? ClockScript;
    public static bool RealClock, CaptureOutput = true;
    public static int WriteCalls, ThrowWriteAt = -1;
    public static readonly List<string> Lines = new();
    public static readonly List<object> InjectedFaults = new();
    public static string Scenario = "initial";
    public static void RecordFault(string kind) => InjectedFaults.Add(new { scenario = Scenario, kind, clockCall = ClockCalls, snapshotCall = RuntimeMain.SnapshotCalls, writeCall = WriteCalls });
    public static Action? OnClock;
    public static Action<int>? OnWrite;
    public static readonly Exception ClockFailure = new IOException("observer clock failure"), SnapshotFailure = new IOException("snapshot failure");
    public static long Frequency() => RealClock ? Stopwatch.Frequency : 1000000;
    public static long Clock()
    {
        ClockCalls++; OnClock?.Invoke();
        if (ClockCalls == ThrowClockAt) { RecordFault("clock.throw"); throw ClockFailure; }
        long value = RealClock ? Stopwatch.GetTimestamp() : ClockScript != null && ClockIndex < ClockScript.Length ? ClockScript[ClockIndex++] : Tick += Step;
        if (value < 0) RecordFault("clock.negative_consumed");
        else if (value < LastClock) RecordFault("clock.backward_consumed");
        LastClock = value;
        return value;
    }
    public static void Write(char[] buffer, int start, int count)
    {
        WriteCalls++; OnWrite?.Invoke(WriteCalls);
        if (WriteCalls == ThrowWriteAt) { RecordFault("writer.throw"); throw new IOException("output failure"); }
        if (CaptureOutput) Lines.Add(new string(buffer, start, count).TrimEnd('\r', '\n'));
    }
    public static void Reset()
    {
        Tick = 100000; Step = 10; LastClock = 0; ClockIndex = ClockCalls = 0; ThrowClockAt = -1; ClockScript = null;
        RealClock = false; CaptureOutput = true; WriteCalls = 0; ThrowWriteAt = -1; Lines.Clear();
        OnClock = null; OnWrite = null; RuntimeMain.Reset();
    }
}
