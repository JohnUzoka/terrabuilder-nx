// Patch-time shapes; these are mapped to the pinned game's existing types.
internal struct Tile49VectorShape { internal float X, Y; }
internal class Tile49EntityShape { internal Tile49VectorShape position; }
internal sealed class Tile49PlayerShape : Tile49EntityShape
{
    internal bool dead, ghost;
    internal int spectating;
}
internal static class Tile49MainShape
{
    internal static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    internal static int myPlayer, screenWidth, screenHeight, FrameSkipMode;
    internal static float GameZoomTarget;
    internal static double time;
    internal static Tile49VectorShape screenPosition;
    internal static Tile49PlayerShape[]? player;
}

internal struct Tile49Metric
{
    internal long Calls, Ticks, Max;
}
internal struct Tile49Context
{
    internal bool Active, SampleActive, SampleValid;
    internal int Layer, Open;
    internal long Start, Last, OpStart, PairTicks;
    internal Tile49Metric SampleColor, SampleData, SampleOutline, SampleOverride, SampleFinal;
    internal Tile49Metric Color, Data, Outline, Override, Final, ClockPair;
    internal long Selected, Completed, Valid, Invalid, Aborted, SampleTicks, SampleMax;
}
internal struct Tile49Pass
{
    internal Tile49Context Saved;
    internal bool SavedPending, Enabled, Nested, LoopStarted, LoopComplete, Normal, TimingValid;
    internal int Layer, Phase;
    internal long Visited, Eligible, Calls;
    internal long PassStart, LoopStart, LoopTicks, Last, PassTicks;
}
internal struct Tile49LayerTotals
{
    internal long Passes, CompletedPasses, AbortedPasses, NestedPasses, InvalidPasses;
    internal long Visited, Eligible, Calls, Selected, CompletedSamples, ValidSamples, InvalidSamples, DiscardedSamples, AbortedSamples;
    internal long CompletedLoops, LoopTicks, LoopMax, PassTicks, PassMax, SampleTicks, SampleMax;
    internal Tile49Metric Color, Data, Outline, Override, Final, ClockPair;
}
internal struct Tile49State
{
    internal int Flags, PlayerId, Width, Height, FrameSkip;
    internal float PlayerX, PlayerY, CameraX, CameraY, Zoom;
    internal double WorldTime;
    internal bool DayTime;
}
internal struct Tile49Range
{
    internal bool Used;
    internal double First, Last, Min, Max;
}
internal struct Tile49StateTotals
{
    internal long Frames, Completed, Eligible, Aborted, Changed;
    internal long InvalidPlayer, InvalidScene, Menu, Paused, Inventory, Map, Dead, Ghost, Spectator, AutoPause;
    internal long SceneSamples, Day, Night, PlayerMoved, CameraMoved, PlayerChanged, DayChanged;
    internal long FirstFrameId, LastFrameId;
    internal bool HasFlags;
    internal int FirstFlags, LastFlags, FlagsOr, FirstPlayerId, LastPlayerId;
    internal Tile49Range PlayerX, PlayerY, CameraX, CameraY, Zoom, WorldTime, Width, Height, FrameSkip;
}

internal static partial class Tile49Template
{
    internal const int SampleMask = 127, HelperCount = 5;
    internal const int MenuFlag = 1, PausedFlag = 2, InventoryFlag = 4, MapFlag = 8,
        DeadFlag = 16, GhostFlag = 32, SpectatorFlag = 64, InvalidPlayerFlag = 128,
        AutoPauseFlag = 256, InvalidSceneFlag = 512;
    internal const int IneligibleMask = MenuFlag | PausedFlag | InventoryFlag | MapFlag | DeadFlag | GhostFlag | SpectatorFlag | InvalidPlayerFlag | InvalidSceneFlag;
    [System.ThreadStatic] internal static Tile49Context current;
    [System.ThreadStatic] internal static bool Pending, FrameCollecting;
    [System.ThreadStatic] internal static int FrameDepth;
    [System.ThreadStatic] internal static Tile49LayerTotals FrameSolid, FrameNonSolid;
    internal static Tile49LayerTotals WindowSolid, WindowNonSolid;
    internal static Tile49StateTotals WindowState;
    internal static bool MeasurementInvalid;
}
