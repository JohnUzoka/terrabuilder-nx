using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using static Common;

internal abstract class UpdateBehaviorSession
{
    protected const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    protected readonly Assembly Assembly;
    protected readonly Type Runtime, Hooks;
    protected string Prefix = "";
    protected readonly List<object> Scenarios = new(), Mutants = new();
    protected readonly SortedSet<string> BoundarySummary = new(StringComparer.Ordinal);
    readonly Dictionary<string, object> instances = new(StringComparer.Ordinal);
    protected UpdateBehaviorSession(Assembly assembly, Type runtime) { Assembly = assembly; Runtime = runtime; Hooks = assembly.GetType("Probe.UpdateBodies", true)!; }
    protected virtual void Setup() { }
    protected Type Host(string source) => Assembly.GetType(UpdateProbe.HostNames[source], true)!;
    protected object New(string source) => Host(source).IsValueType ? Activator.CreateInstance(Host(source))! : RuntimeHelpers.GetUninitializedObject(Host(source));
    protected object Instance(string source) { if (!instances.TryGetValue(source, out var value)) instances.Add(source, value = New(source)); return value; }
    protected void Field(string source, string name, object? value, object? receiver = null) => Host(source).GetField(name, Flags)!.SetValue(receiver, value);
    protected object? ReadField(string source, string name, object? receiver = null) => Host(source).GetField(name, Flags)!.GetValue(receiver);
    protected void Configure(string signature, Func<object?[], object?> callback) => UpdateFixture.Configured[UpdateProbe.BoundaryNames[signature]] = callback;
    protected void ConfigureMember(string owner, string name, Func<object?[], object?> callback)
    {
        var matches = UpdateProbe.BoundaryNames.Where(p => p.Key.Contains(" " + owner + "::" + name + "(", StringComparison.Ordinal)).ToArray();
        Require(matches.Length == 1, "unique declared update boundary " + owner + "::" + name + ": " + string.Join(";", matches.Select(p => p.Key)));
        UpdateFixture.Configured[matches[0].Value] = callback;
    }
    protected void Void(string owner, string name) => ConfigureMember(owner, name, _ => null);
    protected void Exact(string signature, int token) => Configure(signature, args => Invoke(Hooks.GetMethod((Prefix == "Baseline" ? "Baseline" : "Patched") + token.ToString("x8") + "Invoke", Flags)!, new object?[] { args }));
    int callDepth;
    protected object? Call(int token, params object?[] arguments)
    {
        string selected = callDepth == 0 ? Prefix : Prefix == "Baseline" ? "Baseline" : "Patched";
        callDepth++;
        try { return Invoke(Hooks.GetMethod(selected + token.ToString("x8") + "Invoke", Flags)!, new object?[] { arguments }); }
        finally { callDepth--; }
    }
    protected object? R(string name, params object?[] arguments) => Invoke(Runtime.GetMethod(name, Flags)!, arguments);
    protected object? Get(string name) => Runtime.GetField(name, Flags)!.GetValue(null);
    protected void Set(string name, object? value) => Runtime.GetField(name, Flags)!.SetValue(null, value);
    static object? Invoke(MethodInfo method, object?[] arguments)
    {
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    protected void Reset()
    {
        foreach (var field in Runtime.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral && !f.IsInitOnly)) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
        foreach (var type in Assembly.GetTypes().Where(t => t.Namespace == "Probe.UpdateHost"))
            foreach (var field in type.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral && !f.IsInitOnly)) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
        instances.Clear(); RuntimeFixture.Reset(); UpdateFixture.Reset(); Setup();
    }
    protected void BeginSample() { R("InitializeTiming"); Set("SampleSelected", true); R("BeginSample"); Require((bool)Get("Active")! && (int)Get("timingDepth")! == 1, "behavior synthetic enclosing sample active"); }
    protected void EndSample(bool completed) => R("EndSample", completed);
    protected long Metric(int id)
    {
        var metrics = (Array)Get("FrameMetrics")!; var value = metrics.GetValue(id)!;
        return (long)value.GetType().GetField("Calls", Flags)!.GetValue(value)!;
    }
    protected sealed record Outcome(object? Return, object?[] Arguments, Exception? Error, string[] Events, string State, int ClockCalls, int ObserverDepth, bool MeasurementInvalid);
    Outcome One(string prefix, int token, Action setup, Func<object?[]> arguments, Func<string> state, Action<Outcome>? verify, bool root, bool flush, int fault)
    {
        Prefix = prefix; Reset(); setup();
        if (!root && !flush) BeginSample();
        if (prefix != "Baseline" && fault > 0) RuntimeFixture.ThrowClockAt = RuntimeFixture.ClockCalls + fault;
        int faultAt = RuntimeFixture.ThrowClockAt;
        var args = arguments(); object? result = null; Exception? error = null;
        try { result = Call(token, args); } catch (Exception caught) { error = caught; }
        if (UpdateFixture.Unconfigured.Count != 0) throw new InvalidOperationException("undeclared update external boundary: " + string.Join("\n", UpdateFixture.Unconfigured));
        if (error is InvalidProgramException or TypeLoadException or MissingMemberException or BadImageFormatException) throw new InvalidOperationException("invalid executable projection " + prefix + token.ToString("x8"), error);
        foreach (var key in UpdateFixture.Configured.Keys.Where(key => UpdateFixture.Events.Any(e => e.StartsWith(key + "(", StringComparison.Ordinal)))) BoundarySummary.Add(key);
        var outcome = new Outcome(result, args, error, UpdateFixture.Events.ToArray(), state() + "\n" + Snapshot(args, result), RuntimeFixture.ClockCalls, (int)Get("timingDepth")!, (bool)Get("MeasurementInvalid")!);
        if (verify == null) Require(error == null, "unexpected original update exception " + token.ToString("x8") + ": " + error); else verify(outcome);
        if (root) Require((int)Get("FrameDepth")! == 0 && !(bool)Get("Active")!, "actual root observer finally cleanup");
        else if (!flush)
        {
            Require((int)Get("timingDepth")! == 1, "actual scope observer finally cleanup");
            EndSample(error == null);
            Require((int)Get("timingDepth")! == 0 && !(bool)Get("Active")!, "synthetic enclosing sample cleanup");
        }
        if (prefix != "Baseline" && fault > 0) Require(RuntimeFixture.ClockCalls >= faultAt && (bool)Get("MeasurementInvalid")!, "injected observer clock failure reached and contained");
        return outcome;
    }
    static bool Equal(Outcome a, Outcome b) => a.State == b.State && a.Events.SequenceEqual(b.Events) && (ReferenceEquals(a.Error, b.Error) || a.Error != null && b.Error != null && a.Error.GetType() == b.Error.GetType() && a.Error.Message == b.Error.Message);
    protected void Pair(string name, int token, Action setup, Func<object?[]> arguments, Func<string> state, Action<Outcome>? verify = null, bool root = false, bool flush = false, int observerClockOffset = 0)
    {
        var before = One("Baseline", token, setup, arguments, state, verify, root, flush, 0);
        var after = One("Patched", token, setup, arguments, state, verify, root, flush, observerClockOffset);
        Require(Equal(before, after), "baseline/candidate actual update behavior differs " + name + "\nBASE " + before.State + "\nCAND " + after.State + "\nBASE EVENTS " + string.Join("\n", before.Events) + "\nCAND EVENTS " + string.Join("\n", after.Events));
        Scenarios.Add(new { name, token = token.ToString("x8"), passed = true, boundaries = before.Events.Length, state = before.State, events = before.Events, exception = before.Error?.GetType().FullName, originalExceptionIdentity = before.Error != null && ReferenceEquals(before.Error, after.Error), candidateClockCalls = after.ClockCalls, observerClockOffset });
        if (!flush && observerClockOffset == 0)
        {
            var faulted = One("Patched", token, setup, arguments, state, verify, root, flush, 1);
            Require(Equal(before, faulted), "observer clock failure changed original behavior " + name);
            Scenarios.Add(new { name = name + "/observer-clock", token = token.ToString("x8"), passed = true, boundaries = faulted.Events.Length, observerClockFailureReached = true });
        }
    }
    protected void Negative(string name, int token, Action setup, Func<object?[]> arguments, Func<string> state, Action<Outcome>? verify = null, bool root = false, bool flush = false)
        => SemanticNegative(name, "MissingObserver", token, setup, arguments, state, verify, root, flush);
    protected void SemanticNegative(string name, string prefix, int token, Action setup, Func<object?[]> arguments, Func<string> state, Action<Outcome>? verify = null, bool root = false, bool flush = false)
    {
        var before = One("Baseline", token, setup, arguments, state, verify, root, flush, 0);
        string? reason = null;
        try { var mutant = One(prefix, token, setup, arguments, state, verify, root, flush, 0); if (!Equal(before, mutant)) reason = "actual state/callback/ref/exception mismatch"; }
        catch (InvalidDataException rejection) { reason = rejection.Message; }
        Require(reason != null, "plausible actual-body mutant escaped observable oracle " + name + "/" + prefix);
        Mutants.Add(new { name, mutation = prefix, token = token.ToString("x8"), rejected = true, oracle = reason });
    }
    string Snapshot(object?[] arguments, object? result)
    {
        var seen = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        string Describe(object? value)
        {
            if (value == null) return "null";
            var type = value.GetType();
            if (value is string text) return "string:" + text;
            if (value is bool boolean) return boolean ? "bool:true" : "bool:false";
            if (value is char character) return "char:" + ((int)character).ToString(CultureInfo.InvariantCulture);
            if (value is float f) return "float:" + BitConverter.SingleToInt32Bits(f);
            if (value is double d) return "double:" + BitConverter.DoubleToInt64Bits(d);
            if (value is IFormattable formatted) return type.FullName + ":" + formatted.ToString(null, CultureInfo.InvariantCulture);
            string identity = "";
            if (!type.IsValueType) { if (seen.TryGetValue(value, out int old)) return "@" + old; seen.Add(value, seen.Count + 1); identity = "#" + seen[value]; }
            if (value is Array array) return type.FullName + identity + "[" + string.Join(",", array.Cast<object?>().Select(Describe)) + "]";
            if (type.Namespace == "Probe.UpdateHost") return type.FullName + identity + "{" + string.Join(",", type.GetFields(Flags).Where(f => !f.IsStatic).OrderBy(f => f.Name).Select(f => f.Name + "=" + Describe(f.GetValue(value)))) + "}";
            if (value is IEnumerable enumerable && value is not Delegate) return type.FullName + identity + "[" + string.Join(",", enumerable.Cast<object?>().Select(Describe)) + "]";
            return type.FullName + identity;
        }
        var staticState = Assembly.GetTypes().Where(t => t.Namespace == "Probe.UpdateHost").OrderBy(t => t.FullName)
            .SelectMany(t => t.GetFields(Flags).Where(f => f.IsStatic).OrderBy(f => f.Name).Select(f => t.FullName + "." + f.Name + "=" + Describe(f.GetValue(null))));
        return string.Join("\n", staticState) + "\ninstances=" + string.Join(";", instances.OrderBy(p => p.Key).Select(p => p.Key + "=" + Describe(p.Value))) + "\narguments=" + Describe(arguments) + "\nreturn=" + Describe(result);
    }
}
