using System.Globalization;

// Public only so serialized proof assemblies can call this host-only boundary.
public static class UpdateFixture
{
    public static readonly Exception Failure = new IOException("update56 original boundary sentinel");
    public static readonly List<string> Events = new(), Unconfigured = new();
    public static readonly Dictionary<string, Func<object?[], object?>> Configured = new(StringComparer.Ordinal);
    public static Action<string, object?[]>? Observe;
    static readonly Dictionary<object, int> Identities = new(ReferenceEqualityComparer.Instance);
    public static object? Dispatch(string source, object?[] arguments)
    {
        Events.Add(source + "(" + string.Join(",", arguments.Select(Describe)) + ")");
        Observe?.Invoke(source, arguments);
        if (!Configured.TryGetValue(source, out var callback))
        {
            Unconfigured.Add(source); throw new InvalidOperationException("UNCONFIGURED update56 boundary: " + source);
        }
        return callback(arguments);
    }
    public static void Reset() { Events.Clear(); Unconfigured.Clear(); Configured.Clear(); Identities.Clear(); Observe = null; }
    static string Describe(object? value)
    {
        if (value == null) return "null";
        if (value is bool boolean) return boolean ? "bool:true" : "bool:false";
        if (value is char character) return "char:" + ((int)character).ToString(CultureInfo.InvariantCulture);
        if (value is IFormattable number) return number.ToString(null, CultureInfo.InvariantCulture);
        if (value is string text) return text;
        var type = value.GetType();
        if (type.IsValueType) return type.FullName + "{" + string.Join(",", type.GetFields().Select(f => f.Name + "=" + Describe(f.GetValue(value)))) + "}";
        if (!Identities.TryGetValue(value, out int identity)) Identities.Add(value, identity = Identities.Count + 1);
        return type.FullName + "#" + identity;
    }
}
