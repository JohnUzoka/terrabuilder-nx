using System;
using System.Buffers;
using System.Diagnostics.Tracing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

public static class Probe
{
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern void Report(int stage);
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern void ReportText(string text);

    // No managed output, exception construction, or resource lookup precedes these allocations.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run()
    {
        char[] chars = ArrayPool<char>.Shared.Rent(37);
        chars[0] = 'Q';
        if (chars[0] != 'Q' || chars.Length < 37) return 11;
        ArrayPool<char>.Shared.Return(chars, clearArray: true);
        Report(1);

        byte[] bytes = ArrayPool<byte>.Shared.Rent(41);
        bytes[0] = 93;
        if (bytes[0] != 93 || bytes.Length < 41) return 12;
        ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        Report(2);

        bool found = AppContext.TryGetSwitch("System.Diagnostics.Tracing.EventSource.IsSupported", out bool supported);
        Report(found ? (supported ? 111 : 110) : 100);
        object raw = AppContext.GetData("System.Diagnostics.Tracing.EventSource.IsSupported");
        ReportText(raw == null ? "AppContext=<missing>" : "AppContext=" + raw);
        bool effective = (bool)typeof(EventSource).GetProperty("IsSupported", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        Report(effective ? 201 : 200);

        var match = Regex.Match("switch-37", @"^(?<name>[a-z]+)-(?<number>\d+)$");
        if (!match.Success || match.Groups["name"].Value != "switch" || match.Groups["number"].Value != "37") return 13;
        if (Regex.Replace("a12b34", @"\d+", "#") != "a#b#") return 14;
        Report(3);

        // Real default constructors force CoreLib resource-string initialization.
        var notImplemented = new NotImplementedException();
        var nullReference = new NullReferenceException();
        if (string.IsNullOrEmpty(notImplemented.Message) || string.IsNullOrEmpty(nullReference.Message)) return 15;
        ReportText(notImplemented.ToString());
        ReportText(nullReference.ToString());
        try { throw new InvalidOperationException("probe exception"); }
        catch (InvalidOperationException error)
        {
            string formatted = error.ToString();
            if (!formatted.Contains("probe exception") || !formatted.Contains("Probe.Run")) return 16;
            ReportText(formatted);
        }
        Report(4);

        using (var stream = new MemoryStream(new byte[] { 0x25, 0, 0, 0, 3, 0x61, 0x62, 0x63 }))
        using (var reader = new BinaryReader(stream, Encoding.UTF8))
        {
            if (reader.ReadInt32() != 37 || reader.ReadString() != "abc") return 17;
        }
        Report(5);
        var poolEvents = (EventSource)typeof(object).Assembly.GetType("System.Buffers.ArrayPoolEventSource")
            .GetField("Log", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        Exception constructionError = poolEvents.ConstructionException;
        ReportText(constructionError == null ? "ArrayPool.ConstructionException=<null>" :
            "ArrayPool.ConstructionException=" + constructionError.GetType().FullName);
        if (constructionError != null) ReportText(constructionError.ToString());
        if (!effective && constructionError != null) return 18;
        ReportText("PASS allocation regex exception BinaryReader");
        return 0;
    }
}
