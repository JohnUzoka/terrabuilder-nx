using System.Globalization;

// Public because the serialized probe calls this host-only fixture assembly.
// Every generated game/graphics boundary is fail-closed unless a scenario opts in.
public static class BehaviorLargeFixture
{
    public static readonly Exception Failure = new IOException("recipe55 large drawing boundary sentinel");
    public static readonly List<string> Events = new();
    public static readonly Dictionary<string, Func<object?[], object?>> Configured = new(StringComparer.Ordinal);
    public static Action<string, object?[]>? Observe;

    public static object? Dispatch(string source, object?[] arguments)
    {
        Events.Add(source + "(" + string.Join(",", arguments.Select(Describe)) + ")");
        Observe?.Invoke(source, arguments);
        if (!Configured.TryGetValue(source, out var implementation))
            throw new InvalidOperationException("UNCONFIGURED recipe55 drawing fixture boundary: " + source);
        return implementation(arguments);
    }

    public static void Reset()
    {
        Events.Clear(); Configured.Clear(); Observe = null;
    }

    static string Describe(object? value)
    {
        if (value == null) return "null";
        if (value is IFormattable formatted) return formatted.ToString(null, CultureInfo.InvariantCulture);
        return value.GetType().FullName!;
    }
}
