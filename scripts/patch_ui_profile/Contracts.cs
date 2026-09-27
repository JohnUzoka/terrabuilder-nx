// Patch-time views map to existing pinned50 game/FNA types; no view type ships.
internal struct UI51VectorShape { internal float X, Y; }
internal class UI51EntityShape { internal UI51VectorShape position; }
internal sealed class UI51PlayerShape : UI51EntityShape { internal bool dead, ghost; internal int spectating; }
internal sealed class UI51ItemShape { internal int type; internal byte prefix; }
internal sealed class UI51LayerShape { internal string? Name; }
internal static class UI51MainShape
{
    internal static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    internal static int myPlayer, screenWidth, screenHeight, FrameSkipMode, mouseX, mouseY;
    internal static float GameZoomTarget;
    internal static double time;
    internal static UI51VectorShape screenPosition;
    internal static UI51PlayerShape[]? player;
    internal static UI51ItemShape? HoverItem, mouseItem;
    internal static float UIScale { get; }
}

internal struct UI51Metric
{
    internal long Calls, InclusiveTicks, ExclusiveTicks, InclusiveMax, ExclusiveMax;
}
internal struct UI51StackEntry
{
    internal int Id, Cookie;
    internal long Start, ChildTicks;
}
internal struct UI51State
{
    internal int Flags, PlayerId, Width, Height, FrameSkip, MouseX, MouseY;
    internal int HoverType, HoverPrefix, MouseItemType, MouseItemPrefix;
    internal float PlayerX, PlayerY, CameraX, CameraY, ZoomTarget, UIScale;
    internal double WorldTime;
    internal bool DayTime;
}
internal struct UI51Range { internal bool Used; internal double First, Last, Min, Max; }
internal struct UI51Scene
{
    internal long Samples, Day, Night, PlayerMoved, CameraMoved, MouseMoved, PlayerChanged, DayChanged, HoverChanged, MouseItemChanged;
    internal int FirstPlayerId, LastPlayerId, FirstHoverType, LastHoverType, FirstHoverPrefix, LastHoverPrefix, FirstMouseItemType, LastMouseItemType, FirstMouseItemPrefix, LastMouseItemPrefix;
    internal bool LastDay;
    internal UI51Range PlayerX, PlayerY, CameraX, CameraY, ZoomTarget, UIScale, Width, Height, FrameSkip, WorldTime, MouseX, MouseY;
}
internal struct UI51Cohort
{
    internal long Frames, Selected, Valid, Invalid, Aborted, NoUI, StoppedUI;
    internal long ClockCalls, ClockTicks, ClockMax;
    internal UI51Scene Scene;
}
internal struct UI51StateTotals
{
    internal long Attempts, Frames, Completed, Aborted, Eligible, Changed, CaptureFailures, SelectedAttempts, DiscardedSelected;
    internal long InvalidPlayer, InvalidScene, Menu, Paused, Inventory, Map, Dead, Ghost, Spectator, AutoPause;
    internal long FirstFrameId, LastFrameId;
    internal bool HasFlags;
    internal int FirstFlags, LastFlags, FlagsOr;
}

internal static partial class UI51Template
{
    internal const int Version = 51, MetricCount = 12, CohortCount = 3, SampleMask = 15, MaxDepth = 64;
    internal const int FrameMetric = 0, UIMetric = 1, InventoryMetric = 2, HotbarMetric = 3,
        MouseOverMetric = 4, MouseTextMetric = 5, OtherLayerMetric = 6, ItemSlotMetric = 7,
        TooltipMetric = 8, FormattingMetric = 9, TextDrawMetric = 10, TextLayoutMetric = 11;
    internal const int MenuFlag = 1, PausedFlag = 2, InventoryFlag = 4, MapFlag = 8, DeadFlag = 16,
        GhostFlag = 32, SpectatorFlag = 64, InvalidPlayerFlag = 128, AutoPauseFlag = 256, InvalidSceneFlag = 512;
    internal const int IneligibleMask = MenuFlag | MapFlag | DeadFlag | GhostFlag | SpectatorFlag | InvalidPlayerFlag | InvalidSceneFlag;
    [System.ThreadStatic] internal static int FrameDepth;
    [System.ThreadStatic] internal static bool SampleSelected, Active;
    internal static bool MeasurementInvalid;
    internal static UI51Metric[]? FrameMetrics, WindowMetrics;
    internal static UI51Cohort[]? Cohorts;
    internal static UI51StateTotals WindowState;
    internal static bool FrameTimingInvalid, FrameScopeAborted;
    internal static long FrameStoppedLayers, FrameClockPairTicks;
    internal static bool FrameClockPairValid;
}
