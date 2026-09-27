// Patch-time views map to pinned52 types; none of these view types ships.
internal struct Recipe55VectorShape { internal float X, Y; }
internal class Recipe55EntityShape { internal Recipe55VectorShape position; }
internal sealed class Recipe55PlayerShape : Recipe55EntityShape { internal bool dead, ghost; internal int spectating; }
internal sealed class Recipe55ItemShape { internal int type; internal byte prefix; }
internal static class Recipe55MainShape
{
    internal static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    internal static int myPlayer, screenWidth, screenHeight, FrameSkipMode, mouseX, mouseY, numAvailableRecipes;
    internal static float GameZoomTarget;
    internal static double time;
    internal static Recipe55VectorShape screenPosition;
    internal static Recipe55PlayerShape[]? player;
    internal static Recipe55ItemShape? HoverItem, mouseItem, guideItem;
    internal static float UIScale { get; }
}
internal struct Recipe55Metric { internal long Calls, InclusiveTicks, ExclusiveTicks, InclusiveMax, ExclusiveMax; }
internal struct Recipe55StackEntry { internal int Id, Cookie; internal long Start, ChildTicks; }
internal struct Recipe55State
{
    internal int Flags, PlayerId, Width, Height, FrameSkip, MouseX, MouseY;
    internal int HoverType, HoverPrefix, MouseItemType, MouseItemPrefix, GuideType, AvailableRecipes;
    internal float PlayerX, PlayerY, CameraX, CameraY, ZoomTarget, UIScale;
    internal double WorldTime;
    internal bool DayTime;
}
internal struct Recipe55Range { internal bool Used; internal double First, Last, Min, Max; }
internal struct Recipe55Scene
{
    internal long Samples, Day, Night, PlayerMoved, CameraMoved, MouseMoved, PlayerChanged, DayChanged, HoverChanged, MouseItemChanged;
    internal int FirstPlayerId, LastPlayerId, FirstHoverType, LastHoverType, FirstHoverPrefix, LastHoverPrefix, FirstMouseItemType, LastMouseItemType, FirstMouseItemPrefix, LastMouseItemPrefix;
    internal bool LastDay;
    internal Recipe55Range PlayerX, PlayerY, CameraX, CameraY, ZoomTarget, UIScale, Width, Height, FrameSkip, WorldTime, MouseX, MouseY, GuideType, AvailableRecipes;
}
internal struct Recipe55Cohort
{
    internal long Frames, Selected, Valid, Invalid, Aborted, NoUI;
    internal long ClockCalls, ClockTicks, ClockMax;
    internal Recipe55Scene Scene;
}
internal struct Recipe55StateTotals
{
    internal long Attempts, Frames, Completed, Aborted, Eligible, Changed, CaptureFailures, SelectedAttempts, DiscardedSelected;
    internal long InvalidPlayer, InvalidScene, Menu, Paused, Inventory, Map, Dead, Ghost, Spectator, AutoPause;
    internal long FirstFrameId, LastFrameId;
    internal bool HasFlags;
    internal int FirstFlags, LastFlags, FlagsOr;
}
internal static partial class Recipe55Template
{
    internal const int Version = 55, MetricCount = 11, CohortCount = 3, SampleMask = 15, MaxDepth = 64;
    internal const int FrameMetric = 0, UIMetric = 1, InventoryMetric = 2, RecipeMetric = 3,
        ClearMetric = 4, CollectItemsMetric = 5, CollectGuideMetric = 6, RefocusMetric = 7,
        RepositionMetric = 8, CraftingDrawMetric = 9, ItemSlotMetric = 10;
    internal const int MenuFlag = 1, PausedFlag = 2, InventoryFlag = 4, MapFlag = 8, DeadFlag = 16,
        GhostFlag = 32, SpectatorFlag = 64, InvalidPlayerFlag = 128, AutoPauseFlag = 256, InvalidSceneFlag = 512;
    internal const int IneligibleMask = MenuFlag | MapFlag | DeadFlag | GhostFlag | SpectatorFlag | InvalidPlayerFlag | InvalidSceneFlag;
    [System.ThreadStatic] internal static int FrameDepth;
    [System.ThreadStatic] internal static bool SampleSelected, Active;
    internal static bool MeasurementInvalid;
    internal static Recipe55Metric[]? FrameMetrics, WindowMetrics;
    internal static Recipe55Cohort[]? Cohorts;
    internal static Recipe55StateTotals WindowState;
    internal static bool FrameTimingInvalid, FrameScopeAborted;
    internal static long FrameClockPairTicks;
    internal static bool FrameClockPairValid;
}
