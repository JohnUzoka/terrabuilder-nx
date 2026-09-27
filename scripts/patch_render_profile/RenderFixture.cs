using System.Diagnostics;
using System.Runtime.InteropServices;

// Host-only state, clock, output, private-fill and graphics boundaries. Never shipped in Terraria.
public class RenderEntityFixture { public CostVec position; }
public sealed class RenderPlayerFixture : RenderEntityFixture { public bool dead, ghost; public int spectating = -1; }
public static class RenderWorldFixture
{
    public static bool gameMenu, gamePaused, playerInventory, mapFullscreen, autoPause, dayTime;
    public static int myPlayer, screenWidth, screenHeight, FrameSkipMode;
    public static float GameZoomTarget;
    public static double time;
    public static CostVec screenPosition;
    public static RenderPlayerFixture[]? player;
    public static void Reset()
    {
        gameMenu = gamePaused = playerInventory = mapFullscreen = autoPause = false; dayTime = true;
        myPlayer = 0; screenWidth = 1280; screenHeight = 720; FrameSkipMode = 0; GameZoomTarget = 1;
        time = 100; screenPosition = new(20, 40); player = new[] { new RenderPlayerFixture { position = new(120, 240) } };
    }
}
public static class RenderFixture
{
    [ThreadStatic] public static long ClockCalls, Tick, Step, ThrowClockCall;
    [ThreadStatic] public static int ScriptIndex;
    [ThreadStatic] public static long[]? Script;
    public static bool RealClock, ThrowClock;
    public static long Frequency = 1000000;
    public static readonly Exception ClockFailure = new IOException("deterministic observer clock failure");
    public static long Timestamp()
    {
        ClockCalls++;
        if (ThrowClock || ClockCalls == ThrowClockCall) throw ClockFailure;
        if (RealClock) return Stopwatch.GetTimestamp();
        if (Script != null && ScriptIndex < Script.Length) return Script[ScriptIndex++];
        return Tick += Step;
    }
    public static void ResetClock() { ClockCalls = 0; Tick = 1000; Step = 100; ThrowClockCall = -1; ScriptIndex = 0; Script = null; RealClock = ThrowClock = false; }
}
public sealed class RenderOutputFixture : StringWriter
{
    public int Writes, FailWrite = -1;
    public Action? Reenter;
    public Action<int>? OnWrite;
    public override void Write(char[] buffer, int index, int count)
    {
        Writes++; if (Writes == FailWrite) throw new IOException("deterministic partial packet failure");
        OnWrite?.Invoke(Writes);
        var callback = Reenter; Reenter = null; callback?.Invoke(); base.Write(buffer, index, count);
    }
    public override void Write(string? value)
    {
        Writes++; if (Writes == FailWrite) throw new IOException("deterministic partial packet failure");
        var callback = Reenter; Reenter = null; callback?.Invoke(); base.Write(value);
    }
    public override void WriteLine(string? value)
    {
        Writes++; if (Writes == FailWrite) throw new IOException("deterministic partial packet failure");
        var callback = Reenter; Reenter = null; callback?.Invoke(); base.WriteLine(value);
    }
}
public sealed class RenderGameFixture : IDisposable
{
    public static bool dedServ;
    public static Action? OnEnginePreload, OnEngineLoad, OnRun;
    public static Exception? Displayed;
    public static void add_OnEnginePreload(Action action) { FixtureMain.Effect(32); OnEnginePreload += action; }
    public static void add_OnEngineLoad(Action action) { FixtureMain.Effect(33); OnEngineLoad += action; }
    public RenderGameFixture() { FixtureMain.Effect(30); }
    public void DedServ() { FixtureMain.Effect(34); }
    public void Run() { FixtureMain.Effect(12); OnRun?.Invoke(); }
    public void Dispose() { FixtureMain.Effect(35); }
    public static void StartForceLoad() { FixtureMain.Effect(36); }
    public static void DisplayException(Exception error) { Displayed = error; FixtureMain.Effect(37); }
    public static void InitializeLegacyLocalization() { FixtureMain.Effect(38); }
    public static void Initialize(int? mode) { FixtureMain.Effect(39); }
    public static void LoadParameters(RenderGameFixture game) { FixtureMain.Effect(40); }
    public static bool get_IsOSX() => false;
    public static bool get_IsWindows() => false;
    public static void Reset() { dedServ = false; OnEnginePreload = OnEngineLoad = OnRun = null; Displayed = null; }
}
public sealed class RenderCultureFixture { public static RenderCultureFixture get_DefaultCulture() => Default; static readonly RenderCultureFixture Default = new(); }
public sealed class RenderLanguageFixture
{
    public static RenderLanguageFixture Instance = new();
    public void SetLanguage(RenderCultureFixture culture) { FixtureMain.Effect(31); }
}
public sealed class RenderClosureFixture
{
    public static Action? Osx, Windows;
    public static RenderClosureFixture Instance = new();
    public void OsxLoad() { FixtureMain.Effect(41); }
    public void WindowsLoad() { FixtureMain.Effect(42); }
}

public enum RenderSetDataOptions { None = 0, Discard = 1, NoOverwrite = 2 }
public enum RenderPrimitiveType { TriangleList = 0, TriangleStrip = 1, LineList = 2, LineStrip = 3, PointListEXT = 4 }
[StructLayout(LayoutKind.Sequential, Size = 24)]
public struct RenderVertex { public CostVec3 Position; public CostColor Color; public CostVec TextureCoordinate; }
public struct RenderVertexColors { public CostColor TopLeftColor, TopRightColor, BottomLeftColor, BottomRightColor; }
public struct RenderSpriteData
{
    public CostVec4 Source, Destination;
    public CostVec Origin;
    public RenderVertexColors Colors;
    public float Rotation;
    public int Effects;
}
public struct RenderDataSlice { public int Start, Length, Next; }
public struct RenderLayerBatch : IComparable<RenderLayerBatch>
{
    public uint LayerStack;
    public ushort Texture;
    public int Head, Tail, Length, NextSprite;
    // This is an explicit external sort boundary, not a claim to execute the game's comparer.
    public int CompareTo(RenderLayerBatch other)
    {
        int layer = LayerStack.CompareTo(other.LayerStack);
        return layer != 0 ? layer : Texture.CompareTo(other.Texture);
    }
}
public struct RenderLayerBatchKey { public uint LayerStack; public RenderTexture? Texture; }
public struct RenderRecentLayerCacheEntry { public RenderTextureBase? Texture; public int BatchIndex; }
public class RenderTextureBase { public int Id; }
public sealed class RenderTexture : RenderTextureBase { }

public enum RenderBoundary
{
    Textures, TextureSet, FillSprites, FillPartial, FillWhole, UploadOffset, UploadArray, Submit,
    FlushRenderState, Flush, FlushLayered
}
public readonly record struct RenderEvent(RenderBoundary Kind, object Receiver, object? Data = null, object? Texture = null,
    int A = 0, int B = 0, int C = 0, int D = 0, int E = 0, int F = 0, Type? Element = null);
public sealed class RenderTrace
{
    public readonly List<RenderEvent> Events = new(256);
    public bool Capture = true;
    public long Calls;
    public RenderBoundary? ThrowAt;
    public int ThrowOccurrence = 1, MatchingCalls;
    public Exception? Failure;
    public Action<RenderEvent>? OnBoundary;
    public void Record(RenderEvent value)
    {
        Calls++; RenderFixture.Tick += 17;
        if (Capture) Events.Add(value);
        OnBoundary?.Invoke(value);
        if (value.Kind == ThrowAt && ++MatchingCalls == ThrowOccurrence) throw Failure!;
    }
    public void Reset()
    {
        Events.Clear(); Calls = 0; MatchingCalls = 0; OnBoundary = null; ThrowAt = null; Failure = null;
        ThrowOccurrence = 1; Capture = true;
    }
}
public sealed class RenderTextureCollection
{
    public readonly RenderTrace Trace;
    public readonly RenderTextureBase?[] Slots = new RenderTextureBase?[4];
    public RenderTextureCollection(RenderTrace trace) { Trace = trace; }
    public RenderTextureBase? this[int index]
    {
        get => Slots[index];
        set { Trace.Record(new(RenderBoundary.TextureSet, this, Texture: value, A: index)); Slots[index] = value; }
    }
}
public sealed class RenderGraphicsDevice
{
    public readonly RenderTrace Trace;
    public readonly RenderTextureCollection TextureSlots;
    public long Draws;
    public RenderGraphicsDevice(RenderTrace trace) { Trace = trace; TextureSlots = new(trace); }
    public RenderTextureCollection Textures { get { Trace.Record(new(RenderBoundary.Textures, this)); return TextureSlots; } }
    public void DrawIndexedPrimitives(RenderPrimitiveType primitiveType, int baseVertex, int minVertexIndex, int numVertices, int startIndex, int primitiveCount)
    {
        Trace.Record(new(RenderBoundary.Submit, this, A: (int)primitiveType, B: baseVertex, C: minVertexIndex, D: numVertices, E: startIndex, F: primitiveCount));
        Draws++;
    }
}
public sealed class RenderDynamicVertexBuffer
{
    public readonly RenderTrace Trace;
    public long Uploads;
    public RenderDynamicVertexBuffer(RenderTrace trace) { Trace = trace; }
    public void SetData<T>(int offsetInBytes, T[] data, int startIndex, int elementCount, int vertexStride, RenderSetDataOptions options) where T : struct
    {
        Trace.Record(new(RenderBoundary.UploadOffset, this, data, A: offsetInBytes, B: startIndex, C: elementCount, D: vertexStride, E: (int)options, Element: typeof(T)));
        Uploads++;
    }
    public void SetData<T>(T[] data, int startIndex, int elementCount, RenderSetDataOptions options) where T : struct
    {
        Trace.Record(new(RenderBoundary.UploadArray, this, data, A: startIndex, B: elementCount, C: (int)options, Element: typeof(T)));
        Uploads++;
    }
}
public sealed class RenderBatch
{
    public bool _layeredSortingEnabled;
    public int _queuedSpriteCount, _passTextureCount, _drawCalls, _vertexBufferPosition, _batchCount, _batchDataCount;
    public RenderGraphicsDevice _graphicsDevice;
    public RenderDynamicVertexBuffer _vertexBuffer;
    public RenderVertex[] _vertices = new RenderVertex[8192];
    public RenderLayerBatch[] _batches = Array.Empty<RenderLayerBatch>();
    public RenderTexture[] _passTextures = Array.Empty<RenderTexture>();
    public RenderSpriteData[] _spriteDataQueue = Array.Empty<RenderSpriteData>();
    public RenderDataSlice[] _batchData = Array.Empty<RenderDataSlice>();
    public Dictionary<RenderLayerBatchKey, int> _batchLookup = new();
    public RenderRecentLayerCacheEntry[] _batchLookupCache = new RenderRecentLayerCacheEntry[2];
    public Dictionary<RenderTexture, ushort> _textureIdLookup = new();
    public RenderLayerBatchKey _currentBatchKey;
    public readonly RenderTrace Trace;
    public Action<RenderBatch>? OnFlush, OnFlushLayered;
    public RenderBatch(RenderTrace? trace = null)
    {
        Trace = trace ?? new(); _graphicsDevice = new(Trace); _vertexBuffer = new(Trace);
    }
    public void FlushRenderState() => Trace.Record(new(RenderBoundary.FlushRenderState, this));
    public void Flush() { Trace.Record(new(RenderBoundary.Flush, this)); OnFlush?.Invoke(this); }
    public void FlushLayered() { Trace.Record(new(RenderBoundary.FlushLayered, this)); OnFlushLayered?.Invoke(this); }
    public void FillVertexBuffer(RenderTexture texture, RenderSpriteData[] sprites, int offset, int count, int vbSpriteOffset)
    {
        Trace.Record(new(RenderBoundary.FillSprites, this, sprites, texture, offset, count, vbSpriteOffset));
        // Distinct deterministic sentinel output tests data flow; this is not the private game's geometry implementation.
        for (int i = 0; i < count; i++)
        {
            float marker = sprites[offset + i].Rotation;
            for (int corner = 0; corner < 4; corner++)
                _vertices[(vbSpriteOffset + i) * 4 + corner].Position.X = marker + corner;
        }
    }
    public bool FillVertexBuffer(RenderLayerBatch batch, ref RenderDataSlice slice, ref int consumed, ref int vbCount)
    {
        Trace.Record(new(RenderBoundary.FillPartial, this, A: batch.Head, B: batch.Length, C: consumed, D: vbCount, E: batch.Texture));
        int take = Math.Min(batch.Length - consumed, 2048 - vbCount);
        slice.Start = batch.Head + consumed; slice.Length = batch.Length - consumed;
        for (int i = 0; i < take * 4; i++) _vertices[vbCount * 4 + i].Position.X = batch.Head + consumed + i;
        consumed += take; vbCount += take; slice.Start += take; slice.Length -= take;
        return consumed == batch.Length;
    }
    public void FillVertexBuffer(RenderLayerBatch batch, ref int vbCount)
    {
        Trace.Record(new(RenderBoundary.FillWhole, this, A: batch.Head, B: batch.Length, C: vbCount, D: batch.Texture));
        for (int i = 0; i < batch.Length * 4; i++) _vertices[vbCount * 4 + i].Position.X = batch.Head + i;
        vbCount += batch.Length;
    }
}
public delegate void RenderUploadOffset(RenderDynamicVertexBuffer receiver, int offsetInBytes, RenderVertex[] data, int startIndex, int elementCount, int vertexStride, RenderSetDataOptions options);
public delegate void RenderUploadArray(RenderDynamicVertexBuffer receiver, RenderVertex[] data, int startIndex, int elementCount, RenderSetDataOptions options);
public delegate void RenderSubmit(RenderGraphicsDevice receiver, RenderPrimitiveType primitiveType, int baseVertex, int minVertexIndex, int numVertices, int startIndex, int primitiveCount);
