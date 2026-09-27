using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;

public struct HintVector
{
    public float X, Y;
    public HintVector(float value) { X = Y = value; }
    public HintVector(float x, float y) { X = x; Y = y; }
    public override string ToString() => BitConverter.SingleToInt32Bits(X).ToString("x8") + ":" + BitConverter.SingleToInt32Bits(Y).ToString("x8");
}
public struct HintColor { public uint Packed; }
public sealed class HintAsset
{
    public object? Value
    {
        get
        {
            HintFixture.AssetReads++;
            HintFixture.Trace.Add("Font.Value:" + HintFixture.AssetReads);
            if (HintFixture.ThrowAssetAt == HintFixture.AssetReads) throw HintFixture.Failure;
            return HintFixture.Font;
        }
    }
}
// Explicit root boundaries only. Measurement executes the supplied, unmodified-on-disk pair.
public static class HintFixture
{
    public static bool drawingPlayerChat, gameMenu, GamepadDisableInstructionsDisplay, AllowExecutionOfGamepadInstructions;
    public static int menuMode, screenHeight;
    public static float GlyphsScale;
    public static object spriteBatch = new();
    public static HintAsset MouseText = new();
    public static object? Font;
    public static bool ShowHints, ThrowDraw, ThrowCompose, DisableDuringCompose;
    public static int ThrowAssetAt, AssetReads, ComposeCalls, MeasureCalls, DrawCalls, Effects;
    public static string? Instructions;
    public static HintVector DrawnPosition, DrawnOrigin, DrawnScale, Measured;
    public static float DrawnGlyphScale, DrawnWidth, DrawnSpread;
    public static string? DrawnText;
    public static readonly Exception Failure = new InvalidOperationException("controlled boundary failure");
    public static readonly List<string> Trace = new();
    internal static HintRuntime Runtime = null!;
    public static void Reset()
    {
        MouseText = new HintAsset();
        drawingPlayerChat = gameMenu = GamepadDisableInstructionsDisplay = false;
        AllowExecutionOfGamepadInstructions = ShowHints = true;
        menuMode = 1; screenHeight = 720; GlyphsScale = 1.25f;
        ThrowDraw = ThrowCompose = DisableDuringCompose = false;
        ThrowAssetAt = AssetReads = ComposeCalls = MeasureCalls = DrawCalls = Effects = 0;
        Instructions = "Press [g:0] to Jump";
        DrawnPosition = DrawnOrigin = DrawnScale = Measured = default;
        DrawnGlyphScale = DrawnWidth = DrawnSpread = 0; DrawnText = null;
        Trace.Clear();
    }
    public static bool get_ShowGamepadHints() { Trace.Add("ShowHints"); return ShowHints; }
    public static string ComposeInstructionsForGamepad()
    {
        ComposeCalls++; Trace.Add("Compose:" + AllowExecutionOfGamepadInstructions);
        Effects++;
        if (ThrowCompose) throw Failure;
        if (DisableDuringCompose) GamepadDisableInstructionsDisplay = true;
        return Instructions!;
    }
    public static HintColor get_White() => new() { Packed = uint.MaxValue };
    public static HintVector GetStringSize(object? font, string text, HintVector scale, float maximum)
    {
        MeasureCalls++; Trace.Add("Measure");
        Runtime.GlyphScale = GlyphsScale;
        try { Measured = Runtime.Measure(font, text, scale, maximum); }
        finally { GlyphsScale = Runtime.GlyphScale; }
        return Measured;
    }
    public static void DrawColorCodedStringWithShadow(object batch, object? font, string text, HintVector position, HintColor color, float rotation, HintVector origin, HintVector scale, float maximum, float spread)
    {
        Trace.Add("Draw");
        if (ThrowDraw) throw Failure;
        DrawCalls++;
        DrawnPosition = position; DrawnOrigin = origin; DrawnScale = scale;
        DrawnGlyphScale = GlyphsScale; DrawnWidth = maximum; DrawnSpread = spread; DrawnText = text;
    }
    public static void Swap(ref float first, ref float second) { float value = first; first = second; second = value; }
    // Called by a real ITagHandler implementation emitted into each isolated game context.
    public static object? Parse(string text)
    {
        Runtime.ParseCallbacks++;
        Runtime.CallbackTrace.Add("Parse:" + text);
        if (Runtime.ThrowParse) throw Failure;
        var result = Runtime.SnippetFactory(text);
        Runtime.LastParsed = result;
        return result;
    }
    public static bool Unique(object snippet, bool checking)
    {
        Runtime.UniqueCallbacks++;
        Runtime.CallbackTrace.Add("Unique:" + checking + ":" + ReferenceEquals(snippet, Runtime.LastParsed));
        if (Runtime.ThrowUnique) throw Failure;
        Runtime.Mutation++;
        Runtime.UniqueEffect?.Invoke();
        return Runtime.UniqueHandled;
    }
    public static void Morph(object snippet)
    {
        Runtime.MorphCallbacks++;
        Runtime.CallbackTrace.Add("Morph:" + ReferenceEquals(snippet, Runtime.LastParsed));
        if (Runtime.ThrowMorph) throw Failure;
    }
}

internal sealed class HintRuntime : IDisposable
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal sealed class PairContext(string game, string library, string fna) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            string? path = name.Name switch { "Terraria" => game, "ReLogic" => library, "FNA" => fna, _ => null };
            if (name.Name == typeof(HintFixture).Assembly.GetName().Name) return typeof(HintFixture).Assembly;
            path ??= Path.Combine(Path.GetDirectoryName(fna)!, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(Path.GetFullPath(path)) : null;
        }
    }
    internal readonly PairContext Context;
    internal readonly Assembly Game, Library, Fna;
    internal readonly Type FontType, VectorType, SnippetType, ChatType, GlyphType;
    internal readonly MethodInfo OriginalMeasure, ChosenMeasure, ParseMethod, TypedMeasure;
    internal readonly FieldInfo GlyphField, SpacingField, LineField, LookupField, DefaultField;
    internal readonly object GoodFont;
    internal object? Font;
    internal object? LastParsed;
    internal Func<string, object?> SnippetFactory = null!;
    internal int ParseCallbacks, UniqueCallbacks, MorphCallbacks, Mutation;
    internal bool ThrowParse, ThrowUnique, ThrowMorph, UniqueHandled;
    internal Action? UniqueEffect;
    internal readonly List<string> CallbackTrace = new();
    internal readonly Type StatefulType, SubFontType;
    internal readonly Func<object?, object?, float, float, float, bool>? Guard;
    internal readonly Func<object?, string?, bool>? FontGuard;
    internal readonly Func<object?, string?, float, float, float, HintVector> MeasureCall;
    internal readonly Func<object?, string?, float, float, float, HintVector> OriginalCall;
    internal readonly Func<string?, object> ParseCall;
    internal readonly Func<object?, object?, float, float, float, HintVector> TypedCall;
    internal readonly object LanguageManager, Culture;
    internal readonly Action<object?> SetLanguageManager;
    internal readonly PropertyInfo ActiveCulture;
    internal readonly FieldInfo CultureInfoField;
    internal readonly object? SavedCultureInfo;
    internal readonly Array OriginalLookup;
    internal readonly object? OriginalDefault;
    internal readonly float OriginalSpacing;
    internal readonly int OriginalLine;
    internal readonly object[] OriginalCharacters;
    internal readonly object[] OriginalKernings, OriginalPaddings;
    internal readonly FieldInfo KerningField, PaddingField;
    internal readonly object MetricsReceipt;

    internal HintRuntime(string game, string library, string fna, string metrics, byte[] handlers, bool candidate)
    {
        Context = new PairContext(game, library, fna);
        Fna = Context.LoadFromAssemblyPath(Path.GetFullPath(fna));
        Library = Context.LoadFromAssemblyPath(Path.GetFullPath(library));
        Game = Context.LoadFromAssemblyPath(Path.GetFullPath(game));
        FontType = Library.GetType("ReLogic.Graphics.DynamicSpriteFont", true)!;
        VectorType = Fna.GetType("Microsoft.Xna.Framework.Vector2", true)!;
        SnippetType = Game.GetType("Terraria.UI.Chat.TextSnippet", true)!;
        ChatType = Game.GetType("Terraria.UI.Chat.ChatManager", true)!;
        GlyphType = Game.GetType("Terraria.GameContent.UI.Chat.GlyphTagHandler+GlyphSnippet", true)!;
        GlyphField = Game.GetType("Terraria.GameContent.UI.Chat.GlyphTagHandler", true)!.GetField("GlyphsScale")!;
        OriginalMeasure = ChatType.GetMethod("GetStringSize", new[] { FontType, typeof(string), VectorType, typeof(float) })!;
        ChosenMeasure = candidate ? ChatType.GetMethod("_NXHint52Measure", All)! : OriginalMeasure;
        ParseMethod = ChatType.GetMethod("ParseMessage")!;
        TypedMeasure = ChatType.GetMethod("GetStringSize", new[] { FontType, typeof(IEnumerable<>).MakeGenericType(SnippetType), VectorType, typeof(float) })!;
        MeasureCall = MakeMeasure(ChosenMeasure);
        OriginalCall = MakeMeasure(OriginalMeasure);
        TypedCall = MakeTyped();
        ParseCall = MakeParse();
        if (candidate) { Guard = MakeGuard(ChatType.GetMethod("_NXHint52CanSkip", All)!); FontGuard = MakeFontGuard(FontType.GetMethod("_NXHint52MetricsSafe", All)!); }
        SpacingField = FontType.GetField("_characterSpacing", All)!;
        LineField = FontType.GetField("_lineSpacing", All)!;
        LookupField = FontType.GetField("_spriteCharacters", All)!;
        DefaultField = FontType.GetField("_defaultCharacterData", All)!;
        GoodFont = LoadFont(metrics);
        OriginalLookup = (Array)LookupField.GetValue(GoodFont)!;
        OriginalDefault = DefaultField.GetValue(GoodFont);
        OriginalSpacing = (float)SpacingField.GetValue(GoodFont)!;
        OriginalLine = (int)LineField.GetValue(GoodFont)!;
        OriginalCharacters = OriginalLookup.Cast<object?>().Where(x => x != null).Cast<object>().ToArray();
        KerningField = OriginalCharacters[0].GetType().GetField("Kerning")!;
        PaddingField = OriginalCharacters[0].GetType().GetField("Padding")!;
        OriginalKernings = OriginalCharacters.Select(x => KerningField.GetValue(x)!).ToArray();
        OriginalPaddings = OriginalCharacters.Select(x => PaddingField.GetValue(x)!).ToArray();
        MetricsReceipt = new { path = metrics, sha256 = Common.Sha(File.ReadAllBytes(metrics)), characters = OriginalCharacters.Length, lineSpacing = OriginalLine, characterSpacing = OriginalSpacing, construction = "Decoded pinned Mouse_Text records installed by original FontPage constructor and DynamicSpriteFont.SetPages; no GPU textures" };
        var languageType = Game.GetType("Terraria.Localization.LanguageManager", true)!;
        LanguageManager = languageType.GetField("Instance")!.GetValue(null)!;
        var managerSetter = Dynamic("SetProofLanguageManager", typeof(void), typeof(object));
        var managerIl = managerSetter.GetILGenerator();
        managerIl.Emit(OpCodes.Ldarg_0); managerIl.Emit(OpCodes.Castclass, languageType); managerIl.Emit(OpCodes.Stsfld, languageType.GetField("Instance")!); managerIl.Emit(OpCodes.Ret);
        SetLanguageManager = managerSetter.CreateDelegate<Action<object?>>();
        ActiveCulture = languageType.GetProperty("ActiveCulture")!;
        Culture = Game.GetType("Terraria.Localization.GameCulture", true)!.GetMethod("FromLegacyId")!.Invoke(null, new object[] { 1 })!;
        ActiveCulture.SetValue(LanguageManager, Culture);
        CultureInfoField = Culture.GetType().GetField("CultureInfo")!;
        SavedCultureInfo = CultureInfoField.GetValue(Culture);
        var fixture = Context.LoadFromStream(new MemoryStream(handlers));
        StatefulType = fixture.GetType("Hint52Controlled.StatefulSnippet", true)!;
        SubFontType = fixture.GetType("Hint52Controlled.SubFont", true)!;
        Register("g", "Terraria.GameContent.UI.Chat.GlyphTagHandler");
        Register("color", "Terraria.GameContent.UI.Chat.ColorTagHandler");
        Register("c", "Terraria.GameContent.UI.Chat.ColorTagHandler");
        Register("name", "Terraria.GameContent.UI.Chat.NameTagHandler");
        Register("n", "Terraria.GameContent.UI.Chat.NameTagHandler");
        Register("plain", "Terraria.GameContent.UI.Chat.PlainTagHandler");
        Register("i", "Terraria.GameContent.UI.Chat.ItemTagHandler");
        Register("a", "Terraria.GameContent.UI.Chat.AchievementTagHandler");
        HandlerType = fixture.GetType("Hint52Controlled.Handler", true)!;
        Register("probe", HandlerType);
        Reset();
    }
    internal readonly Type HandlerType;
    internal void Register(string alias, string type) => Register(alias, Game.GetType(type, true)!);
    internal void Register(string alias, Type type) => ChatType.GetMethod("Register")!.MakeGenericMethod(type).Invoke(null, new object[] { new[] { alias } });
    internal float GlyphScale { get => (float)GlyphField.GetValue(null)!; set => GlyphField.SetValue(null, value); }
    internal void Reset()
    {
        HintFixture.Runtime = this;
        Font = GoodFont;
        SpacingField.SetValue(GoodFont, OriginalSpacing); LineField.SetValue(GoodFont, OriginalLine);
        LookupField.SetValue(GoodFont, OriginalLookup); DefaultField.SetValue(GoodFont, OriginalDefault);
        for (int i = 0; i < OriginalCharacters.Length; i++) { KerningField.SetValue(OriginalCharacters[i], OriginalKernings[i]); PaddingField.SetValue(OriginalCharacters[i], OriginalPaddings[i]); }
        SetLanguageManager(LanguageManager);
        ActiveCulture.SetValue(LanguageManager, Culture); CultureInfoField.SetValue(Culture, SavedCultureInfo);
        GlyphScale = 1.25f;
        ParseCallbacks = UniqueCallbacks = MorphCallbacks = Mutation = 0;
        ThrowParse = ThrowUnique = ThrowMorph = UniqueHandled = false;
        UniqueEffect = null;
        LastParsed = null; CallbackTrace.Clear();
        SnippetFactory = Text;
        Register("g", "Terraria.GameContent.UI.Chat.GlyphTagHandler");
    }
    internal object Text(string text) => Activator.CreateInstance(SnippetType, new object[] { text })!;
    internal object Stateful(string text) => Activator.CreateInstance(StatefulType, new object[] { text })!;
    internal IList List(params object?[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(SnippetType))!;
        foreach (var item in items) list.Add(item);
        return list;
    }
    internal object ParseText(string? text) => ParseCall(text);
    internal HintVector Measure(object? font, string? text, HintVector scale, float maximum) => MeasureCall(font, text, scale.X, scale.Y, maximum);
    internal object NewFont() => Activator.CreateInstance(FontType, new object[] { 0f, 28, '*' })!;
    internal void SetKerning(char c, int axis, float value)
    {
        object datum = OriginalLookup.GetValue((int)c)!;
        object vector = KerningField.GetValue(datum)!;
        vector.GetType().GetField(new[] { "X", "Y", "Z" }[axis])!.SetValue(vector, value);
        KerningField.SetValue(datum, vector);
    }
    internal void SetPadding(char c, int height)
    {
        object datum = OriginalLookup.GetValue((int)c)!;
        object rectangle = PaddingField.GetValue(datum)!;
        rectangle.GetType().GetField("Height")!.SetValue(rectangle, height);
        PaddingField.SetValue(datum, rectangle);
    }
    internal object LoadFont(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path)); var data = document.RootElement;
        var chars = data.GetProperty("Characters").EnumerateArray().Select(x => (char)x.GetInt32()).ToList();
        var rectangle = Fna.GetType("Microsoft.Xna.Framework.Rectangle", true)!;
        var vector3 = Fna.GetType("Microsoft.Xna.Framework.Vector3", true)!;
        IList MakeList(Type t) => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(t))!;
        var glyphs = MakeList(rectangle); var padding = MakeList(rectangle); var kerning = MakeList(vector3);
        foreach (var row in data.GetProperty("Padding").EnumerateArray())
        {
            var values = row.EnumerateArray().Select(x => (object)x.GetInt32()).ToArray();
            padding.Add(Activator.CreateInstance(rectangle, values)); glyphs.Add(Activator.CreateInstance(rectangle, values));
        }
        foreach (var row in data.GetProperty("Kerning").EnumerateArray()) kerning.Add(Activator.CreateInstance(vector3, row.EnumerateArray().Select(x => (object)x.GetSingle()).ToArray()));
        var pageType = Library.GetType("ReLogic.Graphics.FontPage", true)!;
        object page = Activator.CreateInstance(pageType, new object?[] { null, glyphs, padding, chars, kerning })!;
        var pages = Array.CreateInstance(pageType, 1); pages.SetValue(page, 0);
        object font = Activator.CreateInstance(FontType, new object[] { data.GetProperty("spacing").GetSingle(), data.GetProperty("line").GetInt32(), (char)data.GetProperty("Default").GetInt32() })!;
        FontType.GetMethod("SetPages", All)!.Invoke(font, new object[] { pages });
        return font;
    }
    DynamicMethod Dynamic(string name, Type result, params Type[] parameters) => new(name, result, parameters, typeof(HintRuntime).Module, true);
    void Vector(ILGenerator il, int x, int y) { il.Emit(OpCodes.Ldarg, x); il.Emit(OpCodes.Ldarg, y); il.Emit(OpCodes.Newobj, VectorType.GetConstructor(new[] { typeof(float), typeof(float) })!); }
    void Result(ILGenerator il)
    {
        var result = il.DeclareLocal(VectorType); il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldloca, result); il.Emit(OpCodes.Ldfld, VectorType.GetField("X")!);
        il.Emit(OpCodes.Ldloca, result); il.Emit(OpCodes.Ldfld, VectorType.GetField("Y")!);
        il.Emit(OpCodes.Newobj, typeof(HintVector).GetConstructor(new[] { typeof(float), typeof(float) })!); il.Emit(OpCodes.Ret);
    }
    Func<object?, string?, float, float, float, HintVector> MakeMeasure(MethodInfo method)
    {
        var d = Dynamic("MeasureActual", typeof(HintVector), typeof(object), typeof(string), typeof(float), typeof(float), typeof(float)); var il = d.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, FontType); il.Emit(OpCodes.Ldarg_1); Vector(il, 2, 3); il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Call, method); Result(il);
        return d.CreateDelegate<Func<object?, string?, float, float, float, HintVector>>();
    }
    Func<object?, object?, float, float, float, HintVector> MakeTyped()
    {
        var d = Dynamic("TypedActual", typeof(HintVector), typeof(object), typeof(object), typeof(float), typeof(float), typeof(float)); var il = d.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, FontType); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Castclass, TypedMeasure.GetParameters()[1].ParameterType); Vector(il, 2, 3); il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Call, TypedMeasure); Result(il);
        return d.CreateDelegate<Func<object?, object?, float, float, float, HintVector>>();
    }
    Func<string?, object> MakeParse()
    {
        var d = Dynamic("ParseActual", typeof(object), typeof(string)); var il = d.GetILGenerator(); il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, Fna.GetType("Microsoft.Xna.Framework.Color", true)!.GetProperty("White")!.GetMethod!); il.Emit(OpCodes.Call, ParseMethod); il.Emit(OpCodes.Ret);
        return d.CreateDelegate<Func<string?, object>>();
    }
    Func<object?, object?, float, float, float, bool> MakeGuard(MethodInfo method)
    {
        var d = Dynamic("GuardActual", typeof(bool), typeof(object), typeof(object), typeof(float), typeof(float), typeof(float)); var il = d.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, FontType); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Castclass, method.GetParameters()[1].ParameterType); Vector(il, 2, 3); il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
        return d.CreateDelegate<Func<object?, object?, float, float, float, bool>>();
    }
    Func<object?, string?, bool> MakeFontGuard(MethodInfo method)
    {
        var d = Dynamic("FontGuardActual", typeof(bool), typeof(object), typeof(string)); var il = d.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, FontType); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ret);
        return d.CreateDelegate<Func<object?, string?, bool>>();
    }
    public void Dispose() => Context.Unload();
}

internal sealed class HintItemBoundary
{
    readonly Type MainType, AssetType;
    readonly object Repository, ItemSnippet, Asset;
    readonly MethodInfo UniqueMethod;
    readonly object Zero, White;
    internal readonly string InitialReceipt;
    internal HintItemBoundary(byte[] bytes)
    {
        var assembly = Assembly.Load(bytes);
        var saved = Console.Out;
        using var capture = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try { Console.SetOut(capture); assembly.EntryPoint!.Invoke(null, new object[] { Array.Empty<string>() }); }
        finally { Console.SetOut(saved); }
        InitialReceipt = capture.ToString();
        MainType = assembly.GetType("Terraria.Main", true)!;
        Repository = MainType.GetField("Assets")!.GetValue(null)!;
        var assetArray = (Array)assembly.GetType("Terraria.GameContent.TextureAssets", true)!.GetField("Item")!.GetValue(null)!;
        Asset = assetArray.GetValue(1)!; AssetType = Asset.GetType();
        var itemType = assembly.GetType("Terraria.Item", true)!;
        object item = Activator.CreateInstance(itemType)!; itemType.GetField("type")!.SetValue(item, 1);
        ItemSnippet = Activator.CreateInstance(assembly.GetType("Terraria.GameContent.UI.Chat.ItemTagHandler+ItemSnippet", true)!, new[] { item })!;
        UniqueMethod = ItemSnippet.GetType().GetMethod("UniqueDraw")!;
        Zero = Activator.CreateInstance(assembly.GetType("Microsoft.Xna.Framework.Vector2", true)!, new object[] { 0f })!;
        White = assembly.GetType("Microsoft.Xna.Framework.Color", true)!.GetProperty("White")!.GetValue(null)!;
    }
    internal void Reset(bool loaded, bool dedicated)
    {
        Repository.GetType().GetField("Requests")!.SetValue(Repository, 0);
        MainType.GetField("dedServ")!.SetValue(null, dedicated);
        AssetType.GetProperty("State")!.SetValue(Asset, Enum.ToObject(AssetType.GetProperty("State")!.PropertyType, loaded ? 2 : 0));
    }
    internal int Requests => (int)Repository.GetType().GetField("Requests")!.GetValue(Repository)!;
    internal void Execute()
    {
        object?[] arguments = { true, Zero, null, Zero, White, 1f };
        bool handled = (bool)UniqueMethod.Invoke(ItemSnippet, arguments)!;
        object size = arguments[1]!;
        Common.Require(handled && (float)size.GetType().GetField("X")!.GetValue(size)! == 24 && (float)size.GetType().GetField("Y")!.GetValue(size)! == 24, "actual ItemSnippet checking result changed");
    }
}
