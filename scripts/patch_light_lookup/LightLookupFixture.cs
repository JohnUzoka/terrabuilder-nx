// Host-only dependency substitutes for exact serialized IL slices. No game method is reimplemented here.
public struct LightLookupColor
{
    public byte R, G, B, A;
    public LightLookupColor(int r, int g, int b, int a) { R = (byte)r; G = (byte)g; B = (byte)b; A = (byte)a; }
    public static LightLookupColor Transparent { get { LightLookupFixture.Visit("Transparent"); return default; } }
    public static LightLookupColor White { get { LightLookupFixture.Visit("White"); return new(255, 255, 255, 255); } }
    public static LightLookupColor Lerp(LightLookupColor a, LightLookupColor b, float amount)
    {
        LightLookupFixture.Visit("Lerp");
        return new((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount), (int)(a.A + (b.A - a.A) * amount));
    }
    public override string ToString() => $"{R},{G},{B},{A}";
}
public struct LightLookupRectangle
{
    public int X, Y, Width, Height;
    public LightLookupRectangle(int x, int y, int width, int height) { LightLookupFixture.Visit("Rectangle"); X = x; Y = y; Width = width; Height = height; }
    public static LightLookupRectangle Empty { get { LightLookupFixture.Visit("Empty"); return default; } }
    public override string ToString() => $"{X},{Y},{Width},{Height}";
}
public sealed class LightLookupTile { public ushort type; }
public sealed class LightLookupTexture { public int Id; }
public sealed class LightLookupAsset
{
    public LightLookupTexture Value { get { LightLookupFixture.Visit("Asset.Value"); return LightLookupFixture.GlowTexture; } }
}
public sealed class LightLookupDrawing
{
    public LightLookupTexture GetTileDrawTexture(LightLookupTile tile, int x, int y)
    {
        LightLookupFixture.Visit("GetTileDrawTexture");
        LightLookupFixture.LastTextureArguments = $"{tile.type}:{x}:{y}";
        return LightLookupFixture.DrawTexture;
    }
}
public static class LightLookupFixture
{
    public static int[] tileFrame = new int[65536];
    public static LightLookupAsset[] GlowMask = Enumerable.Range(0, 328).Select(_ => new LightLookupAsset()).ToArray();
    public static readonly LightLookupTexture DrawTexture = new() { Id = 637 }, GlowTexture = new() { Id = 638 };
    public static readonly Exception Failure = new InvalidOperationException("injected light lookup boundary failure");
    public static readonly List<string> Trace = new();
    public static string? ThrowAt, LastQueryArguments, LastTextureArguments;
    public static int Queries;
    public static LightLookupColor QueryColor = new(24, 80, 160, 255);
    public static void Reset(string? throwAt = null) { Trace.Clear(); ThrowAt = throwAt; Queries = 0; LastQueryArguments = LastTextureArguments = null; }
    public static void Visit(string name) { Trace.Add(name); if (ThrowAt == name) throw Failure; }
    public static LightLookupColor GetColor(int x, int y)
    {
        Queries++; LastQueryArguments = $"{x}:{y}"; Visit("GetColor"); return QueryColor;
    }
}
