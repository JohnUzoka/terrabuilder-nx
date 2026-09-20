// Host-only deterministic external effects around the complete serialized Draw IL.
// These types are never shipped or referenced by the accepted game image.
public struct TileVec
{
    public float X, Y;
    public static TileVec operator -(TileVec a, TileVec b) => new() { X = a.X - b.X, Y = a.Y - b.Y };
}
public struct TileColor
{
    public byte R { get; set; } public byte G { get; set; } public byte B { get; set; }
    public TileColor(int r, int g, int b, int a) { R = (byte)r; G = (byte)g; B = (byte)b; }
}
public struct TileKey { public int TileType; }
public struct TileBlack
{
    public TileBlack(uint layer, TileVec position) { TileFixture.Effect(30, (int)layer); }
    public void EndStrip() => TileFixture.Effect(200);
}
public sealed class TileCell
{
    public ushort type, wall; public short frameX, frameY; public bool Active;
    public TileCell() { TileFixture.Created++; TileFixture.Effect(40); }
    public bool active() => Active;
}
public sealed class TileAsset { public bool IsLoaded => true; }
public sealed class TilePlayer
{
    public object hitReplace = new(), hitTile = new(); public bool cursorItemIconEnabled;
    public float GetPlacementPreviewOpacity() { TileFixture.Effect(207); return 0.5f; }
}
public sealed class TileCamera { public TileVec UnscaledPosition => new() { X = 10, Y = 20 }; }
public sealed class TileScene { public TilePlayer PerspectivePlayer => TileWorld.player[0]; }
public sealed class TileBatch { public void SetLayer(uint layer, byte state) => TileFixture.Effect(50, (int)layer, state); }
public sealed class TilePreview { public bool Active { get; set; } public ushort Type => 0; }
public sealed class TileCapture { public static TileCapture Instance = new(); public bool Active { get; set; } }
public static class TileDebug { public static bool devLightTilesCheat, ShowUnbreakableWall; }
public static class TileFocus { public static bool AllowTileDrawingToEmitEffects { get { TileFixture.Effect(1); return true; } } }
public static class TileTextures { public static TileAsset[] Tile = Enumerable.Range(0, 1000).Select(_ => new TileAsset()).ToArray(); }
public static class TileObject
{
    public static TilePreview objectPreview = new();
    public static void DrawPreview(object batch, TilePreview preview, TileVec position, float opacity) => TileFixture.Effect(208);
}
public sealed class TileWorld
{
    public static FixtureMetric FlushNonSolidTiles = new(), FlushSolidTiles = new(), NonSolidDrawCalls = new(), SolidDrawCalls = new();
    public static TileCamera Camera = new(); public static TileScene SceneMetrics => new();
    public static TileCell?[,] tile = new TileCell?[1, 1];
    public static bool critterCage, drawToScreen, placementPreview;
    public static float gfxQuality = 0.5f, martianLight = 0.2f;
    public static int mapTime, myPlayer; public static TileWorld instance = new();
    public static TilePlayer[] player = new[] { new TilePlayer() }; public static TilePlayer LocalPlayer => player[0];
    public static TileBatch tileBatch = new(); public static object spriteBatch = new();
    public void LoadTiles(int type) => TileFixture.Effect(60, type);
    public void DrawTileCracks(int layer, object hit) => TileFixture.Effect(202, layer, ReferenceEquals(hit, player[0].hitTile) ? 1 : 0);
}
public class TileDrawingFixture
{
    public FixtureMetric FlushLogData = new(), DrawCallLogData = new();
    public bool _isActiveAndNotPaused; public TilePlayer? _perspectivePlayer;
    public TileColor _highQualityLightingRequirement, _mediumQualityLightingRequirement, _martianGlow;
    public TileBlack drawBlackHelper; public TileKey _lastPaintLookupKey;
    public static uint Layer_Tiles = 1, Layer_LiquidBehindTiles = 2;
    public void EnsureWindGridSize() => TileFixture.Effect(10);
    public void ClearLegacyCachedDraws() => TileFixture.Effect(11);
    public void ClearCachedTileDraws(bool solid) => TileFixture.Effect(12, solid ? 1 : 0);
    public static void GetScreenDrawArea(bool target, out TileVec position, out int firstX, out int lastX, out int firstY, out int lastY)
    {
        TileFixture.Effect(20, target ? 1 : 0); position = new TileVec { X = 3, Y = 4 };
        firstX = TileFixture.X0; lastX = TileFixture.X1; firstY = TileFixture.Y0; lastY = TileFixture.Y1;
    }
    public bool IsTileDrawLayerSolid(ushort type) => (type & 1) == 0;
    public void DrawTile_LiquidBehindTile(bool solid, int water, TileVec a, TileVec b, int x, int y, TileCell tile) { TileFixture.Liquid++; TileFixture.Effect(70, x, y, water); }
    public void DrawSingleTile(TileVec a, TileVec b, int x, int y)
    {
        TileFixture.Draws++; TileFixture.Effect(100, x, y, (int)(a.X * 100 + a.Y * 10 + b.X + b.Y));
        if (TileFixture.Draws == TileFixture.ThrowDraw) throw TileFixture.Failure;
        if (TileFixture.Reenter != null) { var nested = TileFixture.Reenter; TileFixture.Reenter = null; nested(); }
    }
    public void AddSpecialPoint(int x, int y, int type) { TileFixture.Special++; TileFixture.Effect(110, x, y, type); }
    public void CrawlToTopOfVineAndAddSpecialPoint(int y, int x) { TileFixture.Special++; TileFixture.Effect(111, x, y); }
    public void CrawlToBottomOfReverseVineAndAddSpecialPoint(int y, int x) { TileFixture.Special++; TileFixture.Effect(112, x, y); }
    public void EmitLiquidDrops(int y, int x, TileCell tile, ushort type) { TileFixture.Special++; TileFixture.Effect(113, x, y, type); }
    public bool ShouldSwayInWind(int x, int y, TileCell tile) { TileFixture.Effect(114, x, y); return TileFixture.Sway; }
    public void RestartLayeredBatch() => TileFixture.Effect(201);
    public void RestartSpriteBatch() => TileFixture.Effect(203);
    public void DrawSpecialTilesLegacy(TileVec a, TileVec b) => TileFixture.Effect(204);
}
public static class TileFixture
{
    public static int X0 = 2, X1 = -2, Y0, Y1 = -4;
    public static int Created, Draws, Liquid, Special, ThrowEffect, ThrowDraw, ClockCalls;
    public static bool Sway;
    public static long Effects;
    public static readonly Exception Failure = new InvalidOperationException("deterministic tile target exception");
    public static Func<FixtureMetric, FixtureMetric> TimerRegister = null!, CounterRegister = null!;
    public static Action? Reenter;
    public static void Effect(int id, int a = 0, int b = 0, int c = 0)
    {
        Effects = unchecked(Effects * 31 + id); Effects = unchecked(Effects * 31 + a); Effects = unchecked(Effects * 31 + b); Effects = unchecked(Effects * 31 + c);
        if (ThrowEffect == id) throw Failure;
    }
    public static long Clock() => ++ClockCalls * 100L;
    public static FixtureMetric NewEntry(string name, TimeSpan? threshold)
    {
        var m = TimerRegister(new FixtureMetric { name = name }); FixtureLogger.entries.Add(m); return m;
    }
    public static FixtureMetric NewCounterEntry(string name, int threshold)
    {
        var m = CounterRegister(new FixtureMetric { name = name }); FixtureLogger.entries.Add(m); return m;
    }
    public static void Add(FixtureMetric metric, int value)
    {
        var s = metric.data[FixtureLogger.activeDataSeries]; s.values[s.next] += value; s.used[s.next] = true;
    }
    public static void Reset(int width, int height, Func<int, int, TileCell?> factory)
    {
        X0 = 2; X1 = width - 2; Y0 = 0; Y1 = height - 4;
        TileWorld.tile = new TileCell?[Math.Max(1, width), Math.Max(1, height)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) TileWorld.tile[x, y] = factory(x, y);
        Created = Draws = Liquid = Special = ClockCalls = 0; Effects = 0; TileWorld.mapTime = 0; TileWorld.critterCage = true;
        foreach (var m in FixtureLogger.entries) foreach (var s in m.data) { Array.Clear(s.values); Array.Clear(s.used); s.next = 0; }
    }
}
