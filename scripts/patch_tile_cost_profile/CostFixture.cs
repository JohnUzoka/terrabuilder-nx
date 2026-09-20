using System.Reflection;

// Host-only boundaries. Draw, DrawSingleTile, TileDrawInfo's constructor and the
// observers are copied from serialized production images by CostProof.
public struct CostVec
{
    public float X, Y;
    public CostVec(float x, float y) { X = x; Y = y; }
    public static CostVec Zero => default;
    public static CostVec operator +(CostVec a, CostVec b) => new(a.X + b.X, a.Y + b.Y);
    public static CostVec operator -(CostVec a, CostVec b) => new(a.X - b.X, a.Y - b.Y);
    public static CostVec operator *(CostVec a, float b) => new(a.X * b, a.Y * b);
}
public struct CostVec3 { public float X, Y, Z; }
public struct CostVec4 { public float X, Y, Z, W; public CostVec4(float v) { X = Y = Z = W = v; } }
public struct CostPoint { public int X, Y; }
public struct CostRect
{
    public int X, Y, Width, Height;
    public CostRect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
    public static CostRect Empty => default;
    public CostPoint Center => new() { X = X + Width / 2, Y = Y + Height / 2 };
}
public struct CostColor
{
    public byte R { get; set; } public byte G { get; set; } public byte B { get; set; } public byte A { get; set; }
    public CostColor(int r, int g, int b, int a) { R = (byte)r; G = (byte)g; B = (byte)b; A = (byte)a; }
    public CostColor(int r, int g, int b) : this(r, g, b, 255) { }
    public CostColor(float r, float g, float b, float a) : this((int)(r * 255), (int)(g * 255), (int)(b * 255), (int)(a * 255)) { }
    public CostColor(CostVec4 v) : this(v.X, v.Y, v.Z, v.W) { }
    public static CostColor Transparent => default;
    public static CostColor White => new(255, 255, 255, 255);
    public static CostColor operator *(CostColor c, float f) => new((int)(c.R * f), (int)(c.G * f), (int)(c.B * f), (int)(c.A * f));
    public static CostColor Lerp(CostColor a, CostColor b, float f) => new((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f), (int)(a.A + (b.A - a.A) * f));
}
public struct CostVertices { public CostColor Color; public static implicit operator CostVertices(CostColor c) => new() { Color = c }; }
public struct CostBlack
{
    public CostBlack(uint layer, CostVec position) { CostFixture.Effect("Black.ctor"); CostFixture.Value(layer); CostFixture.Vector(position); }
    public void EndStrip() => CostFixture.Effect("EndStrip");
    public void DrawBlack(int x, int y) { CostFixture.Effect("DrawBlack"); CostFixture.Value(x); CostFixture.Value(y); }
}
public sealed class CostCell
{
    public ushort type, wall; public short frameX, frameY; public byte liquid, Slope; public bool Active = true, Hidden, Half, Inactive, Fullbright;
    public CostCell() => CostFixture.Effect("Tile.ctor");
    public bool active() => Active;
    public bool fullbrightWall() => Fullbright;
    public bool halfBrick() => Half;
    public bool inActive() => Inactive;
    public bool invisibleBlock() => Hidden;
    public byte slope() => Slope;
    public int blockType() => Slope == 0 ? 0 : Slope + 1;
    public CostColor actColor(CostColor c) => c * 0.5f;
}
public sealed class CostTexture { public int Id; }
public sealed class CostAsset { public bool IsLoaded => true; public CostTexture Value => CostFixture.Texture; }
public sealed class CostPlayer
{
    public object hitReplace = new(), hitTile = new(); public bool cursorItemIconEnabled, dangerSense, findTreasure, biomeSight;
    public float GetPlacementPreviewOpacity() { CostFixture.Effect("GetPlacementPreviewOpacity"); return 0.5f; }
}
public sealed class CostCamera { public CostVec UnscaledPosition => new(10, 20); }
public sealed class CostScene { public CostPlayer PerspectivePlayer => CostWorld.player[0]; }
public sealed class CostBatch
{
    public void SetLayer(uint layer, byte state) { CostFixture.Effect("SetLayer"); CostFixture.Value(layer); CostFixture.Value(state); }
    public void Draw(CostTexture texture, CostVec position, CostRect source, CostVertices color, CostVec origin, float scale, int effects)
    { CostFixture.Effect("Graphics.Draw"); CostFixture.Value(texture.Id); CostFixture.Vector(position); CostFixture.Rectangle(source); CostFixture.Color(color.Color); CostFixture.Vector(origin); CostFixture.Value(BitConverter.SingleToInt32Bits(scale)); CostFixture.Value(effects); }
}
public static class CostTextures { public static CostAsset[] Tile = Enumerable.Range(0, 1000).Select(_ => new CostAsset()).ToArray(), GlowMask = Tile; public static CostAsset ShroomCap = new(); }
public static class CostObject
{
    public static TilePreview objectPreview = new();
    public static void DrawPreview(object batch, TilePreview preview, CostVec position, float opacity) { CostFixture.Effect("DrawPreview"); CostFixture.Vector(position); }
}
public sealed class CostDust
{
    public float fadeIn; public CostVec velocity; public bool noLight, noGravity, noLightEmittance;
    public static int NewDust(CostVec position, int width, int height, int type, float sx, float sy, int alpha, CostColor color, float scale)
    { CostFixture.Effect("NewDust"); CostFixture.Vector(position); CostFixture.Value(type); return 0; }
}
public sealed class CostRandom { public int Next(int maximum) { CostFixture.Effect("Random.Next"); CostFixture.Value(maximum); return CostFixture.RandomValue % maximum; } }
public struct CostFastRandom
{
    public ulong Seed;
    public CostFastRandom(ulong seed) { Seed = seed; CostFixture.Effect("FastRandom.ctor"); CostFixture.Value((long)seed); }
    public CostFastRandom WithModifier(int x, int y) { CostFixture.Effect("FastRandom.WithModifier"); return new() { Seed = Seed + (uint)x + (uint)y }; }
    public int Next(int maximum) { CostFixture.Effect("FastRandom.Next"); return CostFixture.RandomValue % maximum; }
}
public sealed class CostFilter { public float Opacity = 0.5f; }
public sealed class CostFilterManager { public CostFilter get_Item(string key) { CostFixture.Effect("Filter.get_Item"); return CostFixture.Filter; } }
public static class CostFilters { public static CostFilterManager Scene = new(); }
public static class CostSets
{
    public static bool[] HasOutlines = new bool[1000], IgnoreDrawLightConditions = new bool[1000], DoNotAdjustDrawPositionBasedOnTileWidth = new bool[1000], HasSlopeFrames = new bool[1000], Platforms = new bool[1000], BlocksStairs = new bool[1000];
}
public static class CostFocus { public static bool AllowTileDrawingToEmitEffects { get { CostFixture.Effect("AllowTileDrawingToEmitEffects"); return true; } } }
public static class CostLighting
{
    public static CostColor GetColor(int x, int y) { CostFixture.Effect("GetColor"); CostFixture.Value(x); CostFixture.Value(y); return CostFixture.Dark ? default : new(40 + x, 60 + y, 80, 255); }
    public static bool UpdateEveryFrame => CostFixture.UpdateEveryFrame;
}
public static class CostUtils
{
    public static int ToDirectionInt(bool value) => value ? 1 : -1;
    public static bool IndexInRange(CostAsset[] array, int i) => (uint)i < (uint)array.Length;
    public static CostVec ToRotationVector2(float angle) => new((float)Math.Cos(angle), (float)Math.Sin(angle));
    public static CostVec ToVector2(CostPoint p) => new(p.X, p.Y);
    public static CostRect Frame(CostTexture t, int horizontal, int vertical, int x, int y, int ox, int oy) => new(x * 16, y * 16, 16, 16);
    public static void GetCactusType(int x, int y, int fx, int fy, ref bool evil, ref bool good, ref bool crimson) { CostFixture.Effect("GetCactusType"); evil = true; good = crimson = false; }
    public static CostColor GetShimmerGlitterColor(bool opacity, float x, float y) { CostFixture.Effect("GetShimmerGlitterColor"); return new(10, 20, 30, 255); }
    public static CostColor GetPortalColor(int player, int portal) { CostFixture.Effect("GetPortalColor"); return new(90, 80, 70, 255); }
}
public sealed class CostWorld
{
    public static FixtureMetric FlushNonSolidTiles = new(), FlushSolidTiles = new(), NonSolidDrawCalls = new(), SolidDrawCalls = new();
    public static CostCamera Camera = new(); public static CostScene SceneMetrics => Scene; static readonly CostScene Scene = new();
    public static CostCell?[,] tile = new CostCell?[1, 1];
    public static bool critterCage, drawToScreen, placementPreview;
    public static float gfxQuality = 0.5f, martianLight = 0.2f, GlobalTimeWrappedHourly;
    public static int mapTime, myPlayer, DiscoR = 70, DiscoG = 80, DiscoB = 90;
    public static ulong TileFrameSeed = 123; public static double timeForVisualEffects;
    public static CostWorld instance = new(); public static CostPlayer[] player = new[] { new CostPlayer() }; public static CostPlayer LocalPlayer => player[0];
    public static CostBatch tileBatch = new(); public static object spriteBatch = new();
    public static short[] tileGlowMask = Enumerable.Repeat((short)-1, 1000).ToArray(); public static bool[] tileFlame = new bool[1000], tileSolid = Enumerable.Repeat(true, 1000).ToArray(); public static int[] tileFrame = new int[1000];
    public void LoadTiles(int type) { CostFixture.Effect("LoadTiles"); CostFixture.Value(type); }
    public void DrawTileCracks(int layer, object hit) { CostFixture.Effect("DrawTileCracks"); CostFixture.Value(layer); CostFixture.Value(ReferenceEquals(hit, player[0].hitTile) ? 1 : 0); }
    public static bool IsTileSpelunkable(ushort type, short x, short y) { CostFixture.Effect("IsTileSpelunkable"); return true; }
    public static bool IsTileBiomeSightable(ushort type, short x, short y, ref CostColor c) { CostFixture.Effect("IsTileBiomeSightable"); c = new(120, 140, 160, 255); return true; }
    public static CostColor hslToRgb(float h, float s, float l, byte a) { CostFixture.Effect("hslToRgb"); return new(100, 110, 120, a); }
}
public sealed class CostDrawing
{
    public FixtureMetric FlushLogData = new(), DrawCallLogData = new();
    public bool _isActiveAndNotPaused, _shouldShowInvisibleBlocks; public CostPlayer _perspectivePlayer = CostWorld.player[0];
    public CostColor _highQualityLightingRequirement, _mediumQualityLightingRequirement, _martianGlow, _argonMossGlow, _kryptonMossGlow, _lavaMossGlow, _meteorGlow, _violetMossGlow, _xenonMossGlow;
    public CostBlack drawBlackHelper; public TileKey _lastPaintLookupKey; public CostRandom _rand = new();
    public static uint Layer_Tiles = 1, Layer_LiquidBehindTiles = 2, Layer_OverTiles = 3, Layer_BehindTiles = 4; public static CostVec _zero; public static bool DrawOwnBlacks = true;
    public CostDust[] _dust => CostFixture.Dust;
    public void EnsureWindGridSize() => CostFixture.Effect("EnsureWindGridSize");
    public void ClearLegacyCachedDraws() => CostFixture.Effect("ClearLegacyCachedDraws");
    public void ClearCachedTileDraws(bool solid) { CostFixture.Effect("ClearCachedTileDraws"); CostFixture.Value(solid ? 1 : 0); }
    public static void GetScreenDrawArea(bool target, out CostVec position, out int firstX, out int lastX, out int firstY, out int lastY)
    { CostFixture.Effect("GetScreenDrawArea"); position = new(3, 4); firstX = 2; lastX = CostFixture.Width - 2; firstY = 0; lastY = CostFixture.Height - 4; }
    public bool IsTileDrawLayerSolid(ushort type) => CostFixture.Solid;
    public void DrawTile_LiquidBehindTile(bool solid, int water, CostVec a, CostVec b, int x, int y, CostCell tile) { CostFixture.Effect("LiquidBehindTile"); CostFixture.Value(x); CostFixture.Value(y); CostFixture.Value(water); }
    public void AddSpecialPoint(int x, int y, int type) { CostFixture.Effect("AddSpecialPoint"); CostFixture.Value(type); }
    public void CrawlToTopOfVineAndAddSpecialPoint(int y, int x) => CostFixture.Effect("CrawlToTopOfVineAndAddSpecialPoint");
    public void CrawlToBottomOfReverseVineAndAddSpecialPoint(int y, int x) => CostFixture.Effect("CrawlToBottomOfReverseVineAndAddSpecialPoint");
    public void EmitLiquidDrops(int y, int x, CostCell tile, ushort type) => CostFixture.Effect("EmitLiquidDrops");
    public bool ShouldSwayInWind(int x, int y, CostCell tile) { CostFixture.Effect("ShouldSwayInWind"); return false; }
    public void RestartLayeredBatch() => CostFixture.Effect("RestartLayeredBatch");
    public void RestartSpriteBatch() => CostFixture.Effect("RestartSpriteBatch");
    public void DrawSpecialTilesLegacy(CostVec a, CostVec b) => CostFixture.Effect("DrawSpecialTilesLegacy");
    public void GetTileDrawData(int x, int y, CostCell cell, ushort type, ref short fx, ref short fy, ref int width, ref int height, ref int top, ref int half, ref int addX, ref int addY, ref int effects, ref CostTexture? glow, ref CostRect rect, ref CostColor color)
    {
        CostFixture.Effect("GetTileDrawData"); CostFixture.Value(x); CostFixture.Value(y); CostFixture.Value(type); CostFixture.Value(fx); CostFixture.Value(fy);
        width = 16; height = CostFixture.TileHeight; top = CostFixture.TileTop; half = cell.Half ? 8 : 0; addX = 2; addY = 4; effects = 1;
        glow = CostFixture.Glow ? CostFixture.Texture : null; rect = new(2, 4, width, height); color = new(20, 30, 40, 255);
    }
    public CostTexture GetTileDrawTexture(CostCell tile, int x, int y) { CostFixture.Effect("GetTileDrawTexture"); CostFixture.Value(tile.type); CostFixture.Value(x); CostFixture.Value(y); return CostFixture.Texture; }
    public void GetTileOutlineInfo(int x, int y, ushort type, ref CostColor light, ref CostTexture? texture, ref CostColor color)
    { CostFixture.Effect("GetTileOutlineInfo"); CostFixture.Color(light); light.R = 101; texture = CostFixture.Texture; color = new(200, 150, 100, 255); }
    public static bool IsTileDangerous(CostPlayer p, int x, int y, CostCell tile, ushort type) { CostFixture.Effect("IsTileDangerous"); return true; }
    public void DrawTiles_EmitParticles(int y, int x, CostCell tile, ushort type, short fx, short fy, CostColor light) { CostFixture.Effect("DrawTiles_EmitParticles"); CostFixture.Value(x); CostFixture.Value(y); CostFixture.Color(light); }
    public CostColor DrawTiles_GetLightOverride(int y, int x, CostCell tile, ushort type, short fx, short fy, CostColor light) { CostFixture.Effect("DrawTiles_GetLightOverride"); CostFixture.Value(x); CostFixture.Value(y); CostFixture.Color(light); return light; }
    public bool IsVisible(CostCell tile) => !tile.Hidden;
    public void CacheSpecialDraws_Part1(int x, int y, int type, int fx, int fy, bool dark) { CostFixture.Effect("CacheSpecialDraws_Part1"); CostFixture.Value(x); CostFixture.Value(y); CostFixture.Value(type); CostFixture.Value(dark ? 1 : 0); }
    public void CacheSpecialDraws_Part2(int x, int y, object info) { CostFixture.Effect("CacheSpecialDraws_Part2"); CostFixture.Scratch(info); if (CostFixture.Reenter != null) { var next = CostFixture.Reenter; CostFixture.Reenter = null; next(); } }
    public static CostColor GetFinalLight(CostCell tile, ushort type, CostColor light, CostColor tint) { CostFixture.Effect("GetFinalLight"); CostFixture.Color(light); CostFixture.Color(tint); return light; }
    public void DrawBasicTile(CostVec a, CostVec b, int x, int y, object info, CostRect rect, CostVec position) { CostFixture.Effect("DrawBasicTile"); CostFixture.Rectangle(rect); CostFixture.Vector(position); CostFixture.Scratch(info); }
    public void DrawTile_MinecartTrack(CostVec a, CostVec b, int x, int y, object info) { CostFixture.Effect("DrawTile_MinecartTrack"); CostFixture.Vector(a); CostFixture.Vector(b); CostFixture.Scratch(info); }
    public void DrawXmasTree(CostVec a, CostVec b, int x, int y, object info) { CostFixture.Effect("DrawXmasTree"); CostFixture.Vector(a); CostFixture.Vector(b); CostFixture.Scratch(info); }
    public static float LavaLightA(int x, int y) { CostFixture.Effect("LavaLightA"); return 0.7f; }
}
public static class CostFixture
{
    public static readonly CostTexture Texture = new() { Id = 7 }; public static readonly CostFilter Filter = new(); public static readonly CostDust[] Dust = new[] { new CostDust() };
    public static readonly Exception Failure = new InvalidOperationException("deterministic cost boundary exception");
    public static int Width, Height, TileTop, TileHeight = 16, RandomValue; public static bool Solid = true, Glow, Dark, UpdateEveryFrame, Record = true;
    public static bool RealClock;
    [ThreadStatic] public static long ClockCalls;
    [ThreadStatic] public static long ClockValue;
    [ThreadStatic] public static long ClockStep;
    [ThreadStatic] public static long[]? ClockScript;
    [ThreadStatic] public static int ClockIndex;
    public static long Effects; public static string? ThrowAt; public static Action? Reenter; public static object? LastScratch;
    public static readonly List<string> Trace = new();
    public static long Clock() { ClockCalls++; if (RealClock) return System.Diagnostics.Stopwatch.GetTimestamp(); if (ClockScript != null && ClockIndex < ClockScript.Length) return ClockScript[ClockIndex++]; return ClockValue += ClockStep; }
    public static void Effect(string name) { if (Record) { Trace.Add(name); foreach (char c in name) Value(c); } if (ThrowAt == name) throw Failure; }
    public static void Value(long v) { if (Record) Effects = unchecked(Effects * 31 + v); }
    public static void Vector(CostVec v) { Value(BitConverter.SingleToInt32Bits(v.X)); Value(BitConverter.SingleToInt32Bits(v.Y)); }
    public static void Rectangle(CostRect r) { Value(r.X); Value(r.Y); Value(r.Width); Value(r.Height); }
    public static void Color(CostColor c) { Value(c.R); Value(c.G); Value(c.B); Value(c.A); }
    public static void Scratch(object info)
    {
        if (!Record) return; LastScratch = info;
        foreach (var field in info.GetType().GetFields().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            object? value = field.GetValue(info);
            if (value is CostColor c) Color(c); else if (value is CostRect r) Rectangle(r); else if (value is CostVec v) Vector(v);
            else if (value is CostCell t) { Value(t.type); Value(t.frameX); Value(t.frameY); Value(t.liquid); }
            else if (value is CostTexture tex) Value(tex.Id); else if (value is Array a) Value(a.Length);
            else if (value is IConvertible number) Value(number.ToInt64(null)); else if (value != null) throw new InvalidDataException("unobserved scratch field " + field.Name);
        }
    }
    public static void Reset(int width = 1, int height = 1, ushort type = 0, bool solid = true)
    {
        Width = width; Height = height; Solid = solid; Glow = Dark = UpdateEveryFrame = false; TileTop = 0; TileHeight = 16; RandomValue = 0;
        Record = true; ThrowAt = null; Reenter = null; LastScratch = null; Trace.Clear(); Effects = 0; ClockCalls = ClockValue = 0; ClockStep = 100; ClockScript = null; ClockIndex = 0; TileFixture.ClockCalls = 0;
        RealClock = false;
        CostWorld.tile = new CostCell?[Math.Max(width, 1), Math.Max(height, 1)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) CostWorld.tile[x, y] = new CostCell { type = type };
        CostWorld.mapTime = 0; CostWorld.critterCage = true; CostWorld.player[0].dangerSense = CostWorld.player[0].findTreasure = CostWorld.player[0].biomeSight = false;
        CostObject.objectPreview.Active = false; Array.Clear(CostSets.HasOutlines); Array.Clear(CostSets.Platforms); Array.Clear(CostSets.HasSlopeFrames); Array.Fill(CostWorld.tileGlowMask, (short)-1); Array.Clear(CostWorld.tileFlame);
        Dust[0] = new CostDust();
        foreach (var metric in FixtureLogger.entries) foreach (var series in metric.data) { Array.Clear(series.values); Array.Clear(series.used); series.next = 0; }
        Trace.Clear(); Effects = 0;
    }
}
