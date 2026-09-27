using System.Reflection;
using System.Text.Json;

// Host-only descriptor-consumer boundaries. They do not replace methods under test.
public static class ReuseFixture
{
    public static readonly List<string> Snapshots = new();
    public static readonly List<object> Scratch = new();
    public static readonly List<Array> Slices = new();
    public static Action<object>? OnScratch;
    static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    public static void Reset() { Snapshots.Clear(); Scratch.Clear(); Slices.Clear(); OnScratch = null; }
    public static string Value(object? value)
    {
        if (value == null) return "null";
        if (value is Array array) return "[" + string.Join(",", array.Cast<object?>().Select(Value)) + "]";
        return JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
    }
    public static string Snapshot(object value) => string.Join(";", value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + "=" + Value(f.GetValue(value))));
    static void Capture(string boundary, object info)
    {
        if (!CostFixture.Record) return;
        Snapshots.Add(boundary + ":" + Snapshot(info)); Scratch.Add(info);
        Slices.Add((Array)info.GetType().GetField("colorSlices")!.GetValue(info)!);
        OnScratch?.Invoke(info);
    }
    public static void CacheSpecialDraws_Part2(CostDrawing owner, int x, int y, object info)
    { Capture(nameof(CacheSpecialDraws_Part2), info); owner.CacheSpecialDraws_Part2(x, y, info); }
    public static void DrawBasicTile(CostDrawing owner, CostVec a, CostVec b, int x, int y, object info, CostRect rect, CostVec position)
    { Capture(nameof(DrawBasicTile), info); owner.DrawBasicTile(a, b, x, y, info, rect, position); }
    public static void DrawTile_MinecartTrack(CostDrawing owner, CostVec a, CostVec b, int x, int y, object info)
    { Capture(nameof(DrawTile_MinecartTrack), info); owner.DrawTile_MinecartTrack(a, b, x, y, info); }
    public static void DrawXmasTree(CostDrawing owner, CostVec a, CostVec b, int x, int y, object info)
    { Capture(nameof(DrawXmasTree), info); owner.DrawXmasTree(a, b, x, y, info); }
}
