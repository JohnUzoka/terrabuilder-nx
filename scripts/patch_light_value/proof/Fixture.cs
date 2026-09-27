using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;

namespace ColorProof;

public interface IEngine { Vector3 GetColor(int x, int y); }
public sealed class ProofException : Exception
{
    public ProofException(string message) : base(message) { HResult = unchecked((int)0x81234567); Data["bool"] = true; Data["char"] = '\uffff'; }
}
public readonly record struct Event(int Order, string Name, int X, int Y, uint BrightnessBits, uint XBits, uint YBits, uint ZBits, uint? ReturnBits);
public sealed record Case(string Name, bool Menu, string Engine, int X, int Y, uint XBits, uint YBits, uint ZBits, uint BrightnessBits, int EngineEffects, int BrightnessEffects);
public sealed record Observation(uint? PackedBits, string? ExceptionType, int? ExceptionHResult, string? ExceptionMessage, bool ExpectedExceptionIdentity, bool? ExceptionDataBool, char? ExceptionDataChar, bool Menu, string ActiveEngine, uint BrightnessBits, uint StoredXBits, uint StoredYBits, uint StoredZBits, int EngineCalls, int BrightnessCalls, int LastX, int LastY, Event[] Events);

public static class Boundary
{
    public static bool Menu;
    public static IEngine? ActiveEngine;
    public static float Brightness;
    public static BrightnessBody Getter = null!;
    public static RecordingEngine Engine = null!;
    public static IEngine Replacement = new PlainEngine();
    public static ProofException EngineException = new("engine-fault");
    public static ProofException BrightnessException = new("brightness-fault");
    public static int EngineEffects, BrightnessEffects, EngineCalls, BrightnessCalls, LastX, LastY, EventCount;
    public static bool Trace;
    public static readonly Event[] Events = new Event[32];
    public static float Float(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));
    public static uint Bits(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));
    public static Vector3 Vector(uint x, uint y, uint z) => new(Float(x), Float(y), Float(z));
    public static void Record(string name, int x, int y, Vector3 value, uint? returnBits = null)
    {
        if (Trace) Events[EventCount++] = new(EventCount, name, x, y, Bits(Brightness), Bits(value.X), Bits(value.Y), Bits(value.Z), returnBits);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float ReadBrightness()
    {
        BrightnessCalls++;
        Record("brightness-enter", LastX, LastY, Engine.Stored);
        float value = Getter();
        if ((BrightnessEffects & 1) != 0) { Engine.Stored = Vector(0x7fc54321, 0xff800000, 0x80000000); ActiveEngine = Replacement; Menu = !Menu; Brightness = Float(0xbf800000); }
        if ((BrightnessEffects & 2) != 0) { Record("brightness-throw", LastX, LastY, Engine.Stored); throw BrightnessException; }
        Record("brightness-return", LastX, LastY, Engine.Stored, Bits(value));
        return value;
    }
    public static void Reset(Case c, BrightnessBody getter, bool trace = true)
    {
        Menu = c.Menu; Brightness = Float(c.BrightnessBits); Getter = getter;
        Engine = c.Engine == "custom" ? new CustomEngine() : new RecordingEngine();
        Engine.Stored = Vector(c.XBits, c.YBits, c.ZBits); ActiveEngine = c.Engine == "null" ? null : Engine;
        EngineEffects = c.EngineEffects; BrightnessEffects = c.BrightnessEffects;
        EngineCalls = BrightnessCalls = LastX = LastY = EventCount = 0; Trace = trace;
    }
    public static Observation Run(ColorBody body, Case c, BrightnessBody getter)
    {
        Reset(c, getter); uint? packed = null; Exception? failure = null;
        try { packed = body(c.X, c.Y).PackedValue; }
        catch (Exception e) { failure = e; }
        return new(packed, failure?.GetType().AssemblyQualifiedName, failure?.HResult, failure?.Message,
            failure != null && (ReferenceEquals(failure, EngineException) || ReferenceEquals(failure, BrightnessException)),
            failure?.Data["bool"] is bool b ? b : null, failure?.Data["char"] is char ch ? ch : null,
            Menu, ActiveEngine == null ? "null" : ReferenceEquals(ActiveEngine, Engine) ? "initial" : ReferenceEquals(ActiveEngine, Replacement) ? "replacement" : throw new InvalidOperationException("unknown engine identity"),
            Bits(Brightness), Bits(Engine.Stored.X), Bits(Engine.Stored.Y), Bits(Engine.Stored.Z), EngineCalls, BrightnessCalls, LastX, LastY, Events[..EventCount]);
    }
}

public class RecordingEngine : IEngine
{
    public Vector3 Stored;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual Vector3 GetColor(int x, int y) => Execute(x, y, "engine");
    protected Vector3 Execute(int x, int y, string label)
    {
        Boundary.EngineCalls++; Boundary.LastX = x; Boundary.LastY = y;
        if (Boundary.Trace) Boundary.Record(label + "-enter", x, y, Stored);
        Vector3 value = Stored;
        if ((Boundary.EngineEffects & 1) != 0) Boundary.Brightness = Boundary.Float(Boundary.Bits(Boundary.Brightness) ^ 0x80000000);
        if ((Boundary.EngineEffects & 2) != 0) { Boundary.Menu = !Boundary.Menu; Boundary.ActiveEngine = null; }
        if ((Boundary.EngineEffects & 8) != 0) Stored = Boundary.Vector(0x7fc12345, 0x00000001, 0x80000000);
        if ((Boundary.EngineEffects & 4) != 0) { if (Boundary.Trace) Boundary.Record(label + "-throw", x, y, Stored); throw Boundary.EngineException; }
        if (Boundary.Trace) Boundary.Record(label + "-return", x, y, value); return value;
    }
}
public sealed class CustomEngine : RecordingEngine, IEngine
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    Vector3 IEngine.GetColor(int x, int y) => Execute(x, y, "custom");
}
public sealed class PlainEngine : IEngine
{
    public Vector3 Stored = new(0.125f, 0.5f, 0.875f);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public Vector3 GetColor(int x, int y) => Stored;
}
