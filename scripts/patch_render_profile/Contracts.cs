// Patch-time shapes map to the pinned game's existing types; no view type ships.
internal struct Render53VectorShape { internal float X, Y; }
internal class Render53EntityShape { internal Render53VectorShape position; }
internal sealed class Render53PlayerShape : Render53EntityShape
{
    internal bool dead, ghost;
    internal int spectating;
}
internal static class Render53MainShape
{
    internal static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    internal static int myPlayer, screenWidth, screenHeight, FrameSkipMode;
    internal static float GameZoomTarget;
    internal static double time;
    internal static Render53VectorShape screenPosition;
    internal static Render53PlayerShape[]? player;
}
internal struct Render53Metric
{
    internal long Calls, Ticks, Max;
}
internal struct Render53Context
{
    internal bool Active, SampleActive, SampleValid;
    internal int Layer, Open;
    internal long Start, Last, OpStart, PairTicks;
    internal Render53Metric SampleColor, SampleData, SampleOutline, SampleOverride, SampleFinal;
    internal Render53Metric Color, Data, Outline, Override, Final, ClockPair;
    internal long Selected, Completed, Valid, Invalid, Aborted, SampleTicks, SampleMax;
}
internal struct Render53Pass
{
    internal Render53Context Saved;
    internal bool SavedPending, Enabled, Nested, LoopStarted, LoopComplete, Normal, TimingValid;
    internal int Layer, Phase;
    internal long Visited, Eligible, Calls;
    internal long PassStart, LoopStart, LoopTicks, Last, PassTicks;
}
internal struct Render53LayerTotals
{
    internal long Passes, CompletedPasses, AbortedPasses, NestedPasses, InvalidPasses;
    internal long Visited, Eligible, Calls, Selected, CompletedSamples, ValidSamples, InvalidSamples, DiscardedSamples, AbortedSamples;
    internal long CompletedLoops, LoopTicks, LoopMax, PassTicks, PassMax, SampleTicks, SampleMax;
    internal Render53Metric Color, Data, Outline, Override, Final, ClockPair;
}
internal struct Render53State
{
    internal int Flags, PlayerId, Width, Height, FrameSkip;
    internal float PlayerX, PlayerY, CameraX, CameraY, Zoom;
    internal double WorldTime;
    internal bool DayTime;
}
internal struct Render53Range
{
    internal bool Used;
    internal double First, Last, Min, Max;
}
internal struct Render53StateTotals
{
    internal long Frames, Completed, Eligible, Aborted, Changed;
    internal long InvalidPlayer, InvalidScene, Menu, Paused, Inventory, Map, Dead, Ghost, Spectator, AutoPause;
    internal long SceneSamples, Day, Night, PlayerMoved, CameraMoved, PlayerChanged, DayChanged;
    internal long FirstFrameId, LastFrameId;
    internal bool HasFlags;
    internal int FirstFlags, LastFlags, FlagsOr, FirstPlayerId, LastPlayerId;
    internal Render53Range PlayerX, PlayerY, CameraX, CameraY, Zoom, WorldTime, Width, Height, FrameSkip;
}
internal struct Render53BatchMetric
{
    internal long Calls, InclusiveTicks, ExclusiveTicks, InclusiveMax, ExclusiveMax;
}
internal struct Render53BatchStackEntry
{
    internal int Id, Cookie;
    internal long Start, ChildTicks;
}
internal struct Render53BatchTotals
{
    internal long Frames, ValidFrames, DiscardedFrames, AbortedScopes, ObserverFailures, ClockCalls;
    internal Render53BatchMetric End, Upload, Submit;
}
internal static partial class Render53Template
{
    internal const int SampleMask = 127, HelperCount = 5, BatchMetricCount = 3, BatchMaxDepth = 64;
    internal const int MenuFlag = 1, PausedFlag = 2, InventoryFlag = 4, MapFlag = 8,
        DeadFlag = 16, GhostFlag = 32, SpectatorFlag = 64, InvalidPlayerFlag = 128,
        AutoPauseFlag = 256, InvalidSceneFlag = 512;
    internal const int IneligibleMask = MenuFlag | PausedFlag | InventoryFlag | MapFlag | DeadFlag | GhostFlag | SpectatorFlag | InvalidPlayerFlag | InvalidSceneFlag;
    [System.ThreadStatic] internal static Render53Context current;
    [System.ThreadStatic] internal static bool Pending, FrameCollecting;
    [System.ThreadStatic] internal static int FrameDepth;
    [System.ThreadStatic] internal static Render53LayerTotals FrameSolid, FrameNonSolid;
    internal static Render53LayerTotals WindowSolid, WindowNonSolid;
    internal static Render53StateTotals WindowState;
    internal static Render53BatchTotals WindowBatch;
    internal static bool MeasurementInvalid;
}
