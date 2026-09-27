// Patch-time views map to pinned52 types; none of these view types ships.
internal struct Update56VectorShape { internal float X, Y; }
internal class Update56EntityShape { internal Update56VectorShape position; }
internal sealed class Update56PlayerShape : Update56EntityShape { internal bool dead, ghost; internal int spectating; }
internal sealed class Update56ItemShape { internal int type; internal byte prefix; }
internal static class Update56MainShape
{
    internal static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    internal static int myPlayer, screenWidth, screenHeight, FrameSkipMode, mouseX, mouseY, numAvailableRecipes;
    internal static int netMode, maxTilesX, maxTilesY;
    internal static float GameZoomTarget;
    internal static double time;
    internal static Update56VectorShape screenPosition;
    internal static Update56PlayerShape[]? player;
    internal static Update56ItemShape? HoverItem, mouseItem, guideItem;
    internal static float UIScale { get; }
}
internal struct Update56Metric { internal long Calls, InclusiveTicks, ExclusiveTicks, InclusiveMax, ExclusiveMax; }
internal struct Update56StackEntry { internal int Id, Cookie; internal long Start, ChildTicks; }
internal struct Update56State
{
    internal int Flags, PlayerId, Width, Height, FrameSkip, MouseX, MouseY;
    internal int HoverType, HoverPrefix, MouseItemType, MouseItemPrefix, GuideType, AvailableRecipes;
    internal int NetMode, WorldWidth, WorldHeight;
    internal float PlayerX, PlayerY, CameraX, CameraY, ZoomTarget, UIScale;
    internal double WorldTime;
    internal bool DayTime;
}
internal struct Update56Range { internal bool Used; internal double First, Last, Min, Max; }
internal struct Update56Scene
{
    internal long Samples, Day, Night, PlayerMoved, CameraMoved, MouseMoved, PlayerChanged, DayChanged, HoverChanged, MouseItemChanged;
    internal int FirstPlayerId, LastPlayerId, FirstHoverType, LastHoverType, FirstHoverPrefix, LastHoverPrefix, FirstMouseItemType, LastMouseItemType, FirstMouseItemPrefix, LastMouseItemPrefix;
    internal bool LastDay;
    internal Update56Range PlayerX, PlayerY, CameraX, CameraY, ZoomTarget, UIScale, Width, Height, FrameSkip, WorldTime, MouseX, MouseY, GuideType, AvailableRecipes;
    internal Update56Range NetMode, WorldWidth, WorldHeight;
}
internal struct Update56Cohort
{
    // Internal Frame names denote measured Main.Update invocations, never Draws.
    internal long Frames, Selected, Valid, Invalid, Aborted, NoBody;
    internal long ClockCalls, ClockTicks, ClockMax;
    internal Update56Scene Scene;
}
internal struct Update56StateTotals
{
    internal long Attempts, Frames, Completed, Aborted, Eligible, Changed, CaptureFailures, SelectedAttempts, DiscardedSelected;
    internal long InvalidPlayer, InvalidScene, Menu, Paused, Inventory, Map, Dead, Ghost, Spectator, AutoPause;
    internal long FirstFrameId, LastFrameId;
    internal bool HasFlags;
    internal int FirstFlags, LastFlags, FlagsOr;
}
internal static partial class Update56Template
{
    internal const int Version = 56, MetricCount = 19, CohortCount = 3, SampleMask = 63, MaxDepth = 64;
    internal const int UpdateMetric = 0, BodyMetric = 1, InWorldMetric = 2, WorldTilesMetric = 3,
        WorldGenMetric = 4, WiringMetric = 5, TileEntitiesMetric = 6, TileCountsMetric = 7,
        LiquidsMetric = 8, TownHousingMetric = 9, PlayersMetric = 10, NpcsMetric = 11,
        ProjectilesMetric = 12, ItemsMetric = 13, InputMetric = 14, UIUpdateMetric = 15,
        TileAnimationMetric = 16, WallAnimationMetric = 17, MainThreadActionsMetric = 18;
    internal const int MenuFlag = 1, PausedFlag = 2, InventoryFlag = 4, MapFlag = 8, DeadFlag = 16,
        GhostFlag = 32, SpectatorFlag = 64, InvalidPlayerFlag = 128, AutoPauseFlag = 256, InvalidSceneFlag = 512;
    internal const int IneligibleMask = MenuFlag | MapFlag | DeadFlag | GhostFlag | SpectatorFlag | InvalidPlayerFlag | InvalidSceneFlag;
    [System.ThreadStatic] internal static int FrameDepth;
    [System.ThreadStatic] internal static bool SampleSelected, Active;
    internal static bool MeasurementInvalid;
    internal static Update56Metric[]? FrameMetrics, WindowMetrics;
    internal static Update56Cohort[]? Cohorts;
    internal static Update56StateTotals WindowState;
    internal static bool FrameTimingInvalid, FrameScopeAborted;
    internal static long FrameClockPairTicks;
    internal static bool FrameClockPairValid;
}
