namespace StackStateProof;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static StackStateProofCommon;

public static class FieldFixture
{
    public static readonly List<long> Values = new();
    public static readonly List<string> Observations = new();
    public static readonly List<string> Methods = new();
    static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
    static readonly Dictionary<object,int> Ids = new(ReferenceEqualityComparer.Instance);
    public static bool Enabled, MutateData;
    public static string? MutationField;
    public static string MutationBoundary = "CacheSpecialDraws_Part2:after";
    public static int Mutations, FreshArrays;
    public static object? FirstArray;
    public static CostTexture ReplacementTexture = new() { Id = 17 };
    public static CostCell ReplacementCell = new() { type = 0, frameX = 36, frameY = 18 };
    public static string FinalState = "";
    public static void Reset()
    {
        Enabled = MutateData = false; MutationField = null; MutationBoundary = "CacheSpecialDraws_Part2:after"; Mutations = FreshArrays = 0;
        Values.Clear(); Observations.Clear(); Methods.Clear(); Ids.Clear(); FirstArray = null; FinalState = "";
    }
    public static void Register(object value) { if (!Ids.ContainsKey(value)) Ids[value] = Ids.Count + 1; }
    static int Id(object? value) { if (value == null) return 0; Register(value); return Ids[value]; }
    public static void Reference(string name, object? value) { if (Enabled && CostFixture.Record && !GcFixture.Enabled) Observations.Add(name + "#" + Id(value)); }
    public static string Value(object? value)
    {
        if (value == null) return "null";
        if (value is Array a) return "[" + string.Join(",", a.Cast<object?>().Select(Value)) + "]";
        return JsonSerializer.Serialize(value, value.GetType(), Options);
    }
    public static string Snapshot(object value) => string.Join(";", value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + "=" + Value(f.GetValue(value))));
    public static void Data(ref short fx, ref short fy, ref int width, ref int height, ref int top, ref int half, ref int addX, ref int addY, ref int effects, ref CostTexture? glow, ref CostRect rect, ref CostColor color)
    {
        if (!Enabled || !CostFixture.Record || !MutateData) return;
        fx = 36; fy = 18; width = 23; height = 25; top = -3; half = 5; addX = 7; addY = 9; effects = 2; glow = ReplacementTexture; rect = new(11, 13, 23, 25); color = new(31, 41, 51, 61);
        Observations.Add("Data:wrote_12_actual_byrefs");
    }
    public static void Observe<T>(ref T data, string method, bool after)
    {
        if (GcFixture.Enabled) { GcFixture.State(ref data,method,after); return; }
        if (!CostFixture.Record) return;
        Methods.Add(method + (after ? ":after" : ":before"));
        CostFixture.Effect(method + (after ? ":after" : ":before"));
        if (!Enabled) return;
        object boxed = data!;
        var fields = boxed.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
        var array = (CostVec3[])fields.Single(f => f.Name == "colorSlices").GetValue(boxed)!;
        if (FirstArray == null)
        {
            Require(array.Length == 9 && array.All(v => v.X == 0 && v.Y == 0 && v.Z == 0), "new scratch array not nine zero vectors");
            FirstArray = array; FreshArrays++;
        }
        else if (MutationField != "colorSlices") Require(ReferenceEquals(FirstArray, array), "array identity changed within call");
        foreach (var field in fields.Where(f => !f.FieldType.IsValueType)) Reference(method + "." + field.Name, field.GetValue(boxed));
        Observations.Add(method + (after ? ":after:" : ":before:") + Snapshot(boxed));
        if (method + (after ? ":after" : ":before") == MutationBoundary && MutationField != null && Mutations == 0)
        {
            var field = fields.Single(f => f.Name == MutationField);
            object value = MutationField switch {
                "tileCache" => ReplacementCell, "typeCache" => (ushort)1, "tileFrameX" => (short)36, "tileFrameY" => (short)18,
                "tileWidth" => 23, "tileHeight" => 25, "tileTop" => -3, "halfBrickHeight" => 5, "addFrX" => 7, "addFrY" => 9, "tileSpriteEffect" => 2,
                "glowTexture" or "drawTexture" => ReplacementTexture, "glowSourceRect" => new CostRect(11,13,23,25), "glowColor" => new CostColor(31,41,51,61), "tileLight" => new CostColor(9,12,15,17),
                "colorSlices" => Enumerable.Range(0,9).Select(i => new CostVec3 { X = i + 100, Y = i + 200, Z = i + 300 }).ToArray(),
                _ => throw new InvalidDataException("unknown mutation field") };
            var previous=field.GetValue(boxed);
            Require(field.FieldType.IsValueType?!Equals(previous,value):!ReferenceEquals(previous,value),"callback mutation was a no-op: "+MutationField);
            field.SetValue(boxed, value); data = (T)boxed; Mutations++;
            Observations.Add(method + ":mutated:" + Snapshot(boxed));
        }
        FinalState = Snapshot(boxed);
    }
}
