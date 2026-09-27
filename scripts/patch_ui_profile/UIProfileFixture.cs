using System.Collections;
using System.Diagnostics;
using System.Globalization;

// Host-only observable game/graphics boundaries. None of these implementations ship.
public struct UIProofVector
{
    public float X, Y;
    public UIProofVector(float x, float y) { X = x; Y = y; }
    public static UIProofVector Zero => default;
    public static UIProofVector operator *(UIProofVector a, UIProofVector b) => new(a.X * b.X, a.Y * b.Y);
    public override string ToString() => FormattableString.Invariant($"{X},{Y}");
}
public struct UIProofColor
{
    public byte R, G, B, A;
    public UIProofColor(int r, int g, int b) { R = (byte)Math.Clamp(r, 0, 255); G = (byte)Math.Clamp(g, 0, 255); B = (byte)Math.Clamp(b, 0, 255); A = 255; }
    public byte get_R() => R;
    public byte get_G() => G;
    public byte get_B() => B;
    public static UIProofColor White => new(255, 255, 255);
    public override string ToString() => $"{R},{G},{B},{A}";
}
public struct UIProofMatrix { public int Tag; public static UIProofMatrix Identity => new() { Tag = 3 }; }
public class UIProofEntity { public UIProofVector position; }
public sealed class UIProofPlayer : UIProofEntity { public bool dead, ghost; public int spectating = -1; }
public sealed class UIProofItem { public int type; public byte prefix; public string Name = "Copper Sword"; public string get_Name() { UIProfileFixture.Effect("Item.Name"); return Name; } }
public sealed class UIProofTime { public int Tag; }
public struct UIProofMouseCache { public bool isValid, noOverride; public string? cursorText, buffTooltip; public int rare, X, Y, hackedScreenWidth, hackedScreenHeight; public byte diff; }
public sealed class UIProofMain
{
    public static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    public static int myPlayer, screenWidth, screenHeight, FrameSkipMode, mouseX, mouseY;
    public static float GameZoomTarget, UIScaleValue;
    public static double time;
    public static UIProofVector screenPosition;
    public static UIProofPlayer[]? player;
    public static UIProofItem? HoverItem, mouseItem;
    public static int SnapshotCalls, ThrowSnapshotAt = -1;
    public static float UIScale { get { SnapshotCalls++; if (SnapshotCalls == ThrowSnapshotAt) throw UIProfileFixture.SnapshotFailure; return UIScaleValue; } }
    public bool _isDrawingOrUpdating, _needToSetupDrawInterfaceLayers;
    public UIProofLayerList _gameInterfaceLayers = new();
    public UIProofMouseCache _mouseTextCache;
    public static UIProofMain instance = null!;
    public static UIProofTime? _drawInterfaceGameTime;
    public static Action<UIProofTime>? OnPostDraw;
    public static UIProofAssets Assets = new();
    public static UIProofBatch spriteBatch = new();
    public static UIProofView GameViewMatrix = new();
    public static int cursorOverride;
    public static bool GraphicsAvailable = true;
    public static float masterColor;
    public static int DiscoR, DiscoG, DiscoB;
    public static UIProofColor mcColor, hcColor;
    public static Action<UIProofMain, UIProofTime>? DrawBody;
    public static bool get_IsGraphicsDeviceAvailable() { UIProfileFixture.Effect("GraphicsAvailable"); return GraphicsAvailable; }
    public void EnsureRenderTargetContent() => UIProfileFixture.Effect("EnsureTargets");
    public void DoDraw(UIProofTime t) { UIProfileFixture.Effect("DoDraw:" + t.Tag); DrawBody?.Invoke(this, t); }
    public void SetupDrawInterfaceLayers() { UIProfileFixture.Effect("SetupLayers"); _needToSetupDrawInterfaceLayers = false; }
    public static UIProofMatrix get_UIScaleMatrix() { UIProfileFixture.Effect("UIMatrix"); return new() { Tag = 2 }; }
    public static void DrawGamepadInstructions() => UIProfileFixture.Effect("GamepadInstructions");
    public void MouseTextInner(UIProofMouseCache cache) => UIProfileFixture.Effect("MouseText:" + cache.cursorText + ":" + cache.noOverride);
    public static void DrawInterface_36_Cursor() => UIProfileFixture.Effect("Cursor");
    public static void Reset()
    {
        gameMenu = gamePaused = playerInventory = mapFullscreen = autoPause = false; dayTime = true;
        myPlayer = 0; screenWidth = 1280; screenHeight = 720; FrameSkipMode = 0; mouseX = 100; mouseY = 200;
        GameZoomTarget = UIScaleValue = 1; time = 100; screenPosition = new(20, 40);
        player = new[] { new UIProofPlayer { position = new(120, 240) } }; HoverItem = new() { type = 1 }; mouseItem = new();
        SnapshotCalls = 0; ThrowSnapshotAt = -1; instance = new(); _drawInterfaceGameTime = null; OnPostDraw = null;
        Assets = new(); spriteBatch = new(); GameViewMatrix = new(); cursorOverride = 77; GraphicsAvailable = true; DrawBody = null;
        masterColor = 0.5f; DiscoR = 21; DiscoG = 45; DiscoB = 83; mcColor = new(17, 18, 19); hcColor = new(31, 32, 33);
    }
}
public sealed class UIProofAssets { public void TransferCompletedAssets() => UIProfileFixture.Effect("TransferAssets"); }
public sealed class UIProofView { public UIProofMatrix get_ZoomMatrix() { UIProfileFixture.Effect("WorldMatrix"); return new() { Tag = 1 }; } }
public sealed class UIProofRenderState
{
    public static UIProofRenderState AlphaBlend = new(), LinearClamp = new(), None = new(), CullCounterClockwise = new();
}
public sealed class UIProofBatch
{
    public void Begin(int mode, UIProofRenderState? blend, UIProofRenderState? sampler, UIProofRenderState? depth, UIProofRenderState? raster, object? effect, UIProofMatrix matrix) => UIProfileFixture.Effect("Batch.Begin:" + matrix.Tag);
    public void End() => UIProfileFixture.Effect("Batch.End");
}
public sealed class UIProofDebug { public static UIProofDebug UI = new(); public void Draw(UIProofBatch batch) => UIProfileFixture.Effect("Debug.Draw"); }
public static class UIProofBoundary
{
    public static void Begin(int category) => UIProfileFixture.Effect("FPS.Begin:" + category);
    public static void End() => UIProfileFixture.Effect("FPS.End");
    public static void SetZoom_World() => UIProfileFixture.Effect("Zoom.World");
    public static void SetZoom_UI() => UIProfileFixture.Effect("Zoom.UI");
    public static void SetZoom_Unscaled() => UIProfileFixture.Effect("Zoom.Unscaled");
    public static void UpdateSlotAnims() => UIProfileFixture.Effect("Coin.Update");
    public static void DrawException(Exception error) { UIProfileFixture.Caught = error; UIProfileFixture.Effect("DrawException"); }
}
public delegate bool UIProofDrawMethod();
public sealed class UIProofLayer
{
    public string? Name;
    public int ScaleType;
    public UIProofDrawMethod _drawMethod = () => true;
}
public sealed class UIProofLayerList
{
    public readonly List<UIProofLayer> Items = new();
    public Enumerator GetEnumerator() { UIProfileFixture.Effect("Layers.GetEnumerator"); return new(this); }
    public struct Enumerator : IDisposable
    {
        readonly UIProofLayerList list; int index;
        public Enumerator(UIProofLayerList list) { this.list = list; index = -1; }
        public UIProofLayer Current => list.Items[index];
        public bool MoveNext() { UIProfileFixture.Effect("Layers.MoveNext"); return ++index < list.Items.Count; }
        public void Dispose() { UIProfileFixture.LayerDisposals++; UIProfileFixture.Effect("Layers.Dispose"); }
    }
}
public sealed class UIProofText
{
    public string Key = "item", Value = "Copper Sword";
    public string get_Value() { UIProfileFixture.Effect("Text.Value:" + Key); return Value; }
    public string FormatWith(object value) { UIProfileFixture.Effect("Text.FormatWith"); var pair = (UIProofCombiner)value; return pair.PrefixName + " " + pair.ItemName; }
}
public sealed class UIProofCombiner { public string PrefixName { get; set; } = ""; public string ItemName { get; set; } = ""; }
public sealed class UIProofCulture { public CultureInfo CultureInfo = CultureInfo.InvariantCulture; }
public static class UIProofLanguage
{
    public static UIProofText[] prefix = { new() { Key = "none", Value = "" }, new() { Key = "prefix", Value = "Sharp" } };
    public static UIProofText _prefixFormatText = new();
    public static bool Variations;
    public static UIProofText GetItemName(int type) { UIProfileFixture.Effect("GetItemName:" + type); return new() { Key = "item", Value = "Sword" }; }
    public static bool TryGetVariation(string key, string variation, out string value) { UIProfileFixture.Effect("Variation:" + key + ":" + variation); value = key == "item" ? "Feminine" : "Sharp-F"; return Variations; }
    public static UIProofCulture get_ActiveCulture() => Culture;
    public static readonly UIProofCulture Culture = new();
}
public delegate void UIProofDrawCharacter(UIProofBatch batch, UIProofVector position, UIProofColor color);
public sealed class UIProofFont
{
    public int get_LineSpacing() { UIProfileFixture.Effect("Font.LineSpacing"); return 12; }
    public UIProofVector MeasureString(string text) { UIProfileFixture.Effect("Font.Measure:" + text); return new(text.Length * 5, 10); }
    public string CreateWrappedText(string text, float width, CultureInfo culture) { UIProfileFixture.Effect("Font.Wrap3:" + text + ":" + width); return text; }
    public string CreateWrappedText(string text, float scale, float width, float offset, CultureInfo culture) { UIProfileFixture.Effect(FormattableString.Invariant($"Font.Wrap5:{text}:{scale}:{width}:{offset}:{culture.Name}")); return text; }
    public string CreateCroppedText(string text, float width) { UIProfileFixture.Effect("Font.Crop:" + text + ":" + width); return text; }
    public void DrawCustomFast(UIProofDrawCharacter draw, string text, UIProofVector position, UIProofVector scale) { UIProfileFixture.Effect("Font.Custom:" + text + ":" + position + ":" + scale); }
}
public static class UIProofFontExtensions
{
    public static void DrawString(UIProofBatch batch, UIProofFont font, string text, UIProofVector position, UIProofColor color, float rotation, UIProofVector origin, UIProofVector scale, int effects, float depth) => UIProfileFixture.Effect(FormattableString.Invariant($"Font.DrawV:{text}:{position}:{color.R}:{rotation}:{origin}:{scale}:{effects}:{depth}"));
    public static void DrawString(UIProofBatch batch, UIProofFont font, string text, UIProofVector position, UIProofColor color) => UIProfileFixture.Effect($"Font.Draw5:{text}:{position}:{color}");
    public static void DrawString(UIProofBatch batch, UIProofFont font, string text, UIProofVector position, UIProofColor color, float rotation, UIProofVector origin, float scale, int effects, float depth, UIProofVector[] positions, UIProofColor[] colors) => UIProfileFixture.Effect(FormattableString.Invariant($"Font.DrawA:{text}:{position}:{color.R}:{rotation}:{origin}:{scale}:{effects}:{depth}:{positions.Length}:{colors.Length}"));
}
public sealed class UIProofSnippet
{
    public string Text = ""; public bool Unique; public UIProofVector UniqueSize = new(8, 10);
    public bool UniqueDraw(bool justChecking, out UIProofVector size, UIProofBatch? batch, UIProofVector position, UIProofColor color, float scale) { UIProfileFixture.Effect("UniqueDraw:" + Text + ":" + justChecking); size = UniqueSize; return Unique; }
    public UIProofSnippet CopyMorph(string text) { UIProfileFixture.Effect("CopyMorph:" + text); return new() { Text = text }; }
}
public sealed class UIProofPositioned
{
    public UIProofSnippet Snippet; public int Index, Line; public UIProofVector Position, Size;
    public UIProofPositioned(UIProofSnippet snippet, int index, int line, UIProofVector position, UIProofVector size) { Snippet = snippet; Index = index; Line = line; Position = position; Size = size; }
    public override string ToString() => $"{Snippet.Text}|{Index}|{Line}|{Position}|{Size}";
}
public sealed class UIProofSnippets : IEnumerable<UIProofSnippet>
{
    public UIProofSnippet[] Items = Array.Empty<UIProofSnippet>();
    public IEnumerator<UIProofSnippet> GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    sealed class Enumerator : IEnumerator<UIProofSnippet>
    {
        readonly UIProofSnippets owner; int index = -1;
        public Enumerator(UIProofSnippets owner) { this.owner = owner; UIProfileFixture.Effect("Snippets.GetEnumerator"); }
        public UIProofSnippet Current => owner.Items[index]; object IEnumerator.Current => Current;
        public bool MoveNext() { UIProfileFixture.Effect("Snippets.MoveNext"); return ++index < owner.Items.Length; }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { UIProfileFixture.SnippetDisposals++; UIProfileFixture.Effect("Snippets.Dispose"); }
    }
}
public static class UIProfileFixture
{
    public static string CandidateSha256 = "";
    [ThreadStatic] public static long Tick, Step;
    [ThreadStatic] public static int ClockIndex, ClockCalls, ThrowClockAt;
    [ThreadStatic] public static long[]? ClockScript;
    public static bool RealClock, CaptureOutput = true;
    public static int WriteCalls, ThrowWriteAt = -1;
    public static readonly List<string> Lines = new(), Effects = new();
    public static Action? OnClock;
    public static Action<int>? OnWrite;
    public static string? ThrowEffect;
    public static readonly Exception Failure = new IOException("external boundary failure"), ClockFailure = new IOException("observer clock failure"), SnapshotFailure = new IOException("snapshot failure");
    public static Exception? Caught;
    public static int LayerDisposals, SnippetDisposals;
    public static long Frequency() => RealClock ? Stopwatch.Frequency : 1000000;
    public static long Clock()
    {
        ClockCalls++; OnClock?.Invoke();
        if (ClockCalls == ThrowClockAt) throw ClockFailure;
        if (RealClock) return Stopwatch.GetTimestamp();
        if (ClockScript != null && ClockIndex < ClockScript.Length) return ClockScript[ClockIndex++];
        return Tick += Step;
    }
    public static void Write(char[] buffer, int start, int count) => WriteLine(new string(buffer, start, count).TrimEnd('\r', '\n'));
    public static void WriteLine(string line)
    {
        WriteCalls++; OnWrite?.Invoke(WriteCalls); if (WriteCalls == ThrowWriteAt) throw new IOException("output failure");
        if (CaptureOutput) Lines.Add(line);
    }
    public static void Effect(string value) { Effects.Add(value); if (ThrowEffect == value) throw Failure; }
    public static void Reset()
    {
        Tick = 100000; Step = 10; ClockIndex = ClockCalls = 0; ThrowClockAt = -1; ClockScript = null;
        RealClock = false; CaptureOutput = true; WriteCalls = 0; ThrowWriteAt = -1; Lines.Clear(); Effects.Clear();
        OnClock = null; OnWrite = null; ThrowEffect = null; Caught = null; LayerDisposals = SnippetDisposals = 0;
        UIProofMain.Reset(); UIProofLanguage.Variations = false;
    }
    public static int Generic(ref int value, int mode)
    {
        Effect("Generic.Enter");
        try { value += 7; if (mode == 1) return value; if (mode == 2) throw Failure; return value * 2; }
        catch (IOException error) { Caught = error; Effect("Generic.Catch"); if (mode == 2) throw; return -1; }
        finally { value += 3; Effect("Generic.Finally"); }
    }
}
