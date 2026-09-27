using System.Diagnostics;
using System.Text;

// Host-only external state, clock and output boundaries. Never cloned into Terraria.
public class Tile49EntityFixture { public CostVec position; }
public sealed class Tile49PlayerFixture : Tile49EntityFixture { public bool dead, ghost; public int spectating = -1; }
public static class Tile49WorldFixture
{
    public static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    public static int myPlayer, screenWidth, screenHeight, FrameSkipMode;
    public static float GameZoomTarget;
    public static double time;
    public static CostVec screenPosition;
    public static Tile49PlayerFixture[]? player;
    public static void Reset()
    {
        gameMenu = gamePaused = playerInventory = mapFullscreen = autoPause = false; dayTime = true;
        myPlayer = 0; screenWidth = 1280; screenHeight = 720; FrameSkipMode = 0; GameZoomTarget = 1;
        time = 100; screenPosition = new(20, 40); player = new[] { new Tile49PlayerFixture { position = new(120, 240) } };
    }
}
public static class Tile49ObserverFixture
{
    [ThreadStatic] public static long ClockCalls, Tick, Step, ThrowClockCall;
    [ThreadStatic] public static int ScriptIndex;
    [ThreadStatic] public static long[]? Script;
    public static bool RealClock, ThrowClock;
    public static long Frequency = 1000000;
    public static readonly Exception ClockFailure = new IOException("deterministic observer clock failure");
    public static long Clock()
    {
        ClockCalls++;
        if (ThrowClock || ClockCalls == ThrowClockCall) throw ClockFailure;
        if (RealClock) return Stopwatch.GetTimestamp();
        if (Script != null && ScriptIndex < Script.Length) return Script[ScriptIndex++];
        return Tick += Step;
    }
    public static void ResetClock() { ClockCalls = 0; Tick = 1000; Step = 100; ThrowClockCall = -1; ScriptIndex = 0; Script = null; RealClock = ThrowClock = false; }
}
public sealed class Tile49OutputFixture : StringWriter
{
    public int Writes, FailWrite = -1;
    public Action? Reenter;
    public Action<int>? OnWrite;
    public override void Write(char[] buffer, int index, int count)
    {
        Writes++; if (Writes == FailWrite) throw new IOException("deterministic partial packet failure");
        OnWrite?.Invoke(Writes);
        var callback = Reenter; Reenter = null; callback?.Invoke(); base.Write(buffer, index, count);
    }
    public override void Write(string? value)
    {
        Writes++; if (Writes == FailWrite) throw new IOException("deterministic partial packet failure");
        var callback = Reenter; Reenter = null; callback?.Invoke(); base.Write(value);
    }
    public override void WriteLine(string? value)
    {
        Writes++; if (Writes == FailWrite) throw new IOException("deterministic partial packet failure");
        var callback = Reenter; Reenter = null; callback?.Invoke(); base.WriteLine(value);
    }
}
public sealed class Tile49GameFixture : IDisposable
{
    public static bool dedServ;
    public static Action? OnEnginePreload, OnEngineLoad, OnRun;
    public static Exception? Displayed;
    public static void add_OnEnginePreload(Action action) { FixtureMain.Effect(32); OnEnginePreload += action; }
    public static void add_OnEngineLoad(Action action) { FixtureMain.Effect(33); OnEngineLoad += action; }
    public Tile49GameFixture() { FixtureMain.Effect(30); }
    public void DedServ() { FixtureMain.Effect(34); }
    public void Run() { FixtureMain.Effect(12); OnRun?.Invoke(); }
    public void Dispose() { FixtureMain.Effect(35); }
    public static void StartForceLoad() { FixtureMain.Effect(36); }
    public static void DisplayException(Exception error) { Displayed = error; FixtureMain.Effect(37); }
    public static void InitializeLegacyLocalization() { FixtureMain.Effect(38); }
    public static void Initialize(int? mode) { FixtureMain.Effect(39); }
    public static void LoadParameters(Tile49GameFixture game) { FixtureMain.Effect(40); }
    public static bool get_IsOSX() => false;
    public static bool get_IsWindows() => false;
    public static void Reset() { dedServ = false; OnEnginePreload = OnEngineLoad = OnRun = null; Displayed = null; }
}
public sealed class Tile49CultureFixture { public static Tile49CultureFixture get_DefaultCulture() => Default; static readonly Tile49CultureFixture Default = new(); }
public sealed class Tile49LanguageFixture
{
    public static Tile49LanguageFixture Instance = new();
    public void SetLanguage(Tile49CultureFixture culture) { FixtureMain.Effect(31); }
}
public sealed class Tile49ClosureFixture
{
    public static Action? Osx, Windows;
    public static Tile49ClosureFixture Instance = new();
    public void OsxLoad() { FixtureMain.Effect(41); }
    public void WindowsLoad() { FixtureMain.Effect(42); }
}
