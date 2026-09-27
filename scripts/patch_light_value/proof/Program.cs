using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using static ColorProof.Projection;

namespace ColorProof;

internal static class Program
{
    static readonly JsonSerializerOptions Compact = new();
    static readonly uint[] Values = { 0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x807fffff, 0x00800000, 0x80800000, 0x3b808080, 0x3b808081, 0xbb808081, 0x3effffff, 0x3f000000, 0x3f7fffff, 0x3f800000, 0x3f800001, 0xbf800000, 0x40000000, 0xc0000000, 0x4b000000, 0x4f000000, 0xcf000000, 0x7f7fffff, 0xff7fffff, 0x7f800000, 0xff800000, 0x7fc00000, 0x7fc12345, 0xffc54321, 0x7f800001, 0xff800001 };
    static readonly int[] Coordinates = { int.MinValue, int.MinValue + 1, -1, 0, 1, int.MaxValue - 1, int.MaxValue };
    static uint Next(ref uint seed) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return seed; }
    static Case[] Cases(uint seed)
    {
        var all = new List<Case>();
        foreach (bool menu in new[] { false, true }) foreach (string engine in new[] { "active", "custom", "null" })
        foreach (int x in Coordinates) foreach (int y in Coordinates)
        foreach (int effects in new[] { 0, 1, 2, 3, 4, 7, 8, 11 }) foreach (int brightnessEffects in new[] { 0, 1, 2, 3 })
            all.Add(new("control-flow", menu, engine, x, y, 0x3e000000, 0x3f000000, 0x3f600000, 0x3f800000, effects, brightnessEffects));
        for (int c = 0; c < 3; c++) foreach (uint value in Values) foreach (uint brightness in Values)
        {
            uint[] v = { 0x3e000000, 0x3f000000, 0x3f600000 }; v[c] = value;
            all.Add(new("float-channel-" + c, false, "custom", int.MinValue, int.MaxValue, v[0], v[1], v[2], brightness, 0, 0));
        }
        foreach (uint x in Values) foreach (uint y in Values)
            all.Add(new("cross-channel-payload", false, "active", -177, 293, x, y, Values[(int)((x ^ y) % Values.Length)], 0x3f800000, 0, 1));
        for (int i = 0; i < 4096; i++)
            all.Add(new("deterministic-bits", (Next(ref seed) & 31) == 0, (Next(ref seed) & 1) == 0 ? "active" : "custom", unchecked((int)Next(ref seed)), unchecked((int)Next(ref seed)), Next(ref seed), Next(ref seed), Next(ref seed), Next(ref seed), (int)(Next(ref seed) & 11), (int)(Next(ref seed) & 3)));
        all.Add(new("clamp255", false, "active", 1, 2, Boundary.Bits(1), Boundary.Bits(1), Boundary.Bits(1), Boundary.Bits(1), 0, 0));
        all.Add(new("channel-order", false, "active", -31, 991, Boundary.Bits(0.125f), Boundary.Bits(0.5f), Boundary.Bits(0.875f), Boundary.Bits(1), 0, 0));
        return all.ToArray();
    }
    static string Encode(object value) => JsonSerializer.Serialize(value, Compact);
    static void Invariants(Case c, Observation o, bool stress)
    {
        if (c.Menu)
        {
            Require(o.PackedBits == uint.MaxValue && o.ExceptionType == null && o.EngineCalls == 0 && o.BrightnessCalls == 0 && o.Events.Length == 0, "menu boundary violated");
            Require(o.BrightnessBits == c.BrightnessBits && o.Menu && o.ActiveEngine == (c.Engine == "null" ? "null" : "initial"), "menu touched state");
            return;
        }
        if (c.Engine == "null")
        {
            Require(o.ExceptionType == typeof(NullReferenceException).AssemblyQualifiedName && o.PackedBits == null && o.EngineCalls == 0 && o.BrightnessCalls == 0 && !o.ExpectedExceptionIdentity, "null engine exception violated"); return;
        }
        Require(o.EngineCalls == 1 && o.LastX == c.X && o.LastY == c.Y, "engine calls or original coordinates changed");
        bool engineThrows = (c.EngineEffects & 4) != 0;
        bool brightnessThrows = stress && (c.BrightnessEffects & 2) != 0;
        Require(o.BrightnessCalls == (stress && !engineThrows ? 1 : 0), "brightness call count changed");
        if (engineThrows || brightnessThrows)
        {
            Require(o.PackedBits == null && o.ExceptionType == typeof(ProofException).AssemblyQualifiedName && o.ExpectedExceptionIdentity && o.ExceptionHResult == unchecked((int)0x81234567) && o.ExceptionMessage == (engineThrows ? "engine-fault" : "brightness-fault") && o.ExceptionDataBool == true && o.ExceptionDataChar == '\uffff', "exception identity/data/precedence changed");
        }
        else Require(o.PackedBits != null && o.ExceptionType == null, "unexpected exception");
        var names = o.Events.Select(e => e.Name).ToArray();
        string label = c.Engine == "custom" ? "custom" : "engine";
        string[] expected = engineThrows ? new[] { label + "-enter", label + "-throw" } : !stress ? new[] { label + "-enter", label + "-return" } : new[] { label + "-enter", label + "-return", "brightness-enter", brightnessThrows ? "brightness-throw" : "brightness-return" };
        Require(names.SequenceEqual(expected), "observable boundary order changed");
        var returned = o.Events.FirstOrDefault(e => e.Name == label + "-return");
        if (!engineThrows) Require(returned.XBits == c.XBits && returned.YBits == c.YBits && returned.ZBits == c.ZBits, "engine return bits or NaN payload changed");
    }
    static object FnaChecks(Loaded proof)
    {
        uint seed = 570052; int roundtrips = 0;
        foreach (uint bits in Values.Concat(Enumerable.Range(0, 4096).Select(_ => Next(ref seed))))
        {
            var c = new Color(); c.PackedValue = bits; Require(c.PackedValue == bits, "actual FNA packed setter/getter changed bits"); roundtrips++;
            var v = Boundary.Vector(bits, bits ^ 0x80000000, bits ^ 0x00012345);
            Require(Boundary.Bits(v.X) == bits && Boundary.Bits(v.Y) == (bits ^ 0x80000000) && Boundary.Bits(v.Z) == (bits ^ 0x00012345), "actual FNA Vector3 field bits changed");
            Boundary.Brightness = Boundary.Float(bits);
            Require(Boundary.Bits(proof.OriginalGetter()) == bits && Boundary.Bits(proof.CandidateGetter()) == bits, "actual brightness getter changed bits");
        }
        Require(Color.White.PackedValue == uint.MaxValue, "actual FNA White differs");
        Require(Marshal.SizeOf<Color>() == 4 && Marshal.SizeOf<Vector3>() == 12, "actual value type size unexpected");
        var a = new Case("manual-finite", false, "active", 11, -19, Boundary.Bits(0.125f), Boundary.Bits(0.5f), Boundary.Bits(0.875f), Boundary.Bits(1), 0, 0);
        var finite = Boundary.Run(proof.Bodies["Original"], a, proof.OriginalGetter);
        Require(finite.PackedBits == 0xffdf7f1f, "finite packed channel example failed");
        var clamp = Boundary.Run(proof.Bodies["Original"], a with { XBits = Boundary.Bits(2), YBits = Boundary.Bits(3), ZBits = Boundary.Bits(4) }, proof.OriginalGetter);
        Require(clamp.PackedBits == uint.MaxValue, "upper clamps example failed");
        var negative = Boundary.Run(proof.Bodies["Original"], a with { XBits = Boundary.Bits(-1), YBits = 0, ZBits = 0 }, proof.OriginalGetter);
        Require(negative.PackedBits == 0xffffff01, "original negative channel is not lower-clamped");
        return new { passed = true, roundtrips, actualFnaLoaded = typeof(Color).Assembly.FullName, colorSize = Marshal.SizeOf<Color>(), vector3Size = Marshal.SizeOf<Vector3>(), boolSerialization = Encode(true), charSerialization = Encode('\uffff'), manualFinitePacked = finite.PackedBits, manualUpperClampPacked = clamp.PackedBits, manualNegativeUnclampedPacked = negative.PackedBits };
    }
    static object Behavior(Loaded proof, Case[] cases, string output, int repeats)
    {
        var digests = new List<string>();
        using var file = new StreamWriter(Path.Combine(output, "scenarios.jsonl"), false, new UTF8Encoding(false));
        for (int round = 0; round < repeats; round++)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int n = 0; n < cases.Length; n++) foreach (bool stress in new[] { false, true })
            {
                var c = cases[n]; string aName = stress ? "OriginalStress" : "Original"; string bName = stress ? "CandidateStress" : "Candidate";
                var a = Boundary.Run(proof.Bodies[aName], c, proof.OriginalGetter); var b = Boundary.Run(proof.Bodies[bName], c, proof.CandidateGetter);
                Invariants(c, a, stress); Invariants(c, b, stress);
                string left = Encode(a), right = Encode(b);
                if (left != right)
                {
                    Json(Path.Combine(output, "behavior-failure.json"), new { round, index = n, stress, scenario = c, original = a, candidate = b });
                    throw new InvalidDataException("behavior mismatch @" + n + " stress=" + stress);
                }
                string row = Encode(new { index = n, lane = stress ? "instrumented-equal-brightness" : "actual-brightness-getter", input = c, original = a, candidate = b });
                hash.AppendData(Encoding.UTF8.GetBytes(row + "\n")); if (round == 0) file.WriteLine(row);
            }
            digests.Add(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        Require(digests.Distinct().Count() == 1, "deterministic repetition changed observations");
        return new { passed = true, cases = cases.Length, lanes = 2, repeats, pairedComparisons = cases.Length * 2 * repeats, observationDigests = digests, bitValues = Values.Select(v => v.ToString("x8")), coordinateValues = Coordinates, finiteOutOfRangeConversionPolicy = "Execute the original conv.i4 on this host; compare exact outputs, including NaNs and infinities. No imposed saturation or NaN normalization and no cross-architecture conversion claim.", exceptionComparison = "Exact runtime type, message, HResult, sentinel object identity, bool and char Data, no return, full fixture effects and trace. Stack trace and TargetSite necessarily identify different projected methods and are outside this boundary equivalence contract, not normalized.", primaryBoundaries = "Actual pinned FNA Color/Vector3 loaded directly. Actual two-instruction brightness getter copied from each image. Terraria menu, engine and brightness fields mapped to equal typed fixture fields. Engine is a declared equal input/environment boundary, not a surrogate GetColor algorithm or transitive game-engine proof.", stressBoundaries = "Same actual target bodies with an equal instrumented brightness-call boundary wrapping each actual getter; logs order/count, mutates retained engine storage and globals, or throws a sentinel. Such getter effects are adversarial boundary tests, not claims about the original field-only getter." };
    }
    static object NegativeControls(Loaded proof, Case[] cases, string output)
    {
        var controls = Controls(output); var results = new List<object>();
        foreach (var control in controls)
        {
            int mismatches = 0; object? first = null;
            foreach (var c in cases)
            {
                var a = Boundary.Run(proof.Bodies["OriginalStress"], c, proof.OriginalGetter);
                var b = Boundary.Run(control.Body, c, proof.OriginalGetter);
                if (Encode(a) != Encode(b)) { mismatches++; first ??= new { input = c, original = a, mutated = b }; }
            }
            Require(mismatches != 0, "negative control was not detected " + control.Name);
            results.Add(new { name = control.Name, detected = true, mismatches, firstDifference = first, projection = control.Evidence });
        }
        return new { passed = true, results };
    }
    readonly record struct Sample(long Ticks, long AllocatedBytes, uint Checksum);
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Sample Measure(ColorBody body, int iterations)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp(); uint checksum = 0;
        for (int i = 0; i < iterations; i++) checksum = unchecked(checksum + body(i, ~i).PackedValue + (uint)i);
        long stop = Stopwatch.GetTimestamp(); return new(stop - start, GC.GetAllocatedBytesForCurrentThread() - allocated, checksum);
    }
    static object Allocation(Loaded proof)
    {
        const int iterations = 10000, samples = 3;
        var rows = new List<object>();
        foreach (var workload in new[] { "menu", "active-finite", "active-clamped", "active-nonfinite", "custom-side-effects" })
        {
            Boundary.Reset(new("allocation", workload == "menu", workload == "custom-side-effects" ? "custom" : "active", 0, 0, 0, 0, 0, Boundary.Bits(1), 0, 0), proof.OriginalGetter, false);
            if (workload != "custom-side-effects") Boundary.ActiveEngine = new PlainEngine { Stored = workload == "active-clamped" ? new Vector3(3, 6, 9) : workload == "active-nonfinite" ? Boundary.Vector(0x7fc12345, 0xff800000, 0x7f800001) : new Vector3(0.125f, 0.5f, 0.875f) };
            else Boundary.Engine.Stored = new Vector3(0.125f, 0.5f, 0.875f);
            foreach (string name in new[] { "Original", "Candidate", "Replica" }) for (int w = 0; w < 3; w++) Measure(proof.Bodies[name], iterations);
            uint? expected = null;
            foreach (string name in new[] { "Original", "Candidate", "Replica" }) for (int s = 0; s < samples; s++)
            {
                var measured = Measure(proof.Bodies[name], iterations);
                expected ??= measured.Checksum;
                Require(measured.Checksum == expected, "allocation checksum mismatch");
                Require(measured.AllocatedBytes == 0, "measured body allocated");
                rows.Add(new { workload, body = name, sample = s, measuredBytes = measured.AllocatedBytes, checksum = measured.Checksum });
            }
        }
        return new { passed = true, workloads = 5, bodies = 3, samples, callsPerSample = iterations, measuredSamples = rows.Count, measuredInvocations = rows.Count * iterations, measuredBytes = 0, rows, hostOnly = true, limitation = "Warmed exact projected primary bodies on the host, excluding fixture setup and observation storage. No game construction/allocation policy change and no ARM64 allocation or performance claim." };
    }
    static object Timing(Loaded proof, int iterations, int samples, uint seed)
    {
        var rows = new List<object>();
        foreach (var workload in new[] { "menu", "active-finite", "active-clamped", "active-nonfinite", "custom-side-effects" })
        {
            Boundary.Reset(new("benchmark", workload == "menu", workload == "custom-side-effects" ? "custom" : "active", 0, 0, 0, 0, 0, Boundary.Bits(1), 0, 0), proof.OriginalGetter, false);
            if (workload != "custom-side-effects") Boundary.ActiveEngine = new PlainEngine { Stored = workload == "active-clamped" ? new Vector3(3, 6, 9) : workload == "active-nonfinite" ? Boundary.Vector(0x7fc12345, 0xff800000, 0x7f800001) : new Vector3(0.125f, 0.5f, 0.875f) };
            else Boundary.Engine.Stored = new Vector3(0.125f, 0.5f, 0.875f);
            foreach (string name in new[] { "Original", "Candidate", "Replica" }) for (int w = 0; w < 3; w++) Measure(proof.Bodies[name], Math.Max(10000, iterations / 10));
            foreach (string second in new[] { "Replica", "Candidate" })
            {
                var sampleRows = new List<object>(); var ratios = new List<double>();
                for (int s = 0; s < samples; s++)
                {
                    bool ab = (Next(ref seed) & 1) == 0; Sample a, b;
                    if (ab) { a = Measure(proof.Bodies["Original"], iterations); b = Measure(proof.Bodies[second], iterations); }
                    else { b = Measure(proof.Bodies[second], iterations); a = Measure(proof.Bodies["Original"], iterations); }
                    Require(a.Checksum == b.Checksum, "benchmark checksum mismatch");
                    Require(a.AllocatedBytes == 0 && b.AllocatedBytes == 0, "timed body allocated");
                    double ratio = (double)b.Ticks / a.Ticks; ratios.Add(ratio);
                    sampleRows.Add(new { sample = s, order = ab ? "AB" : "BA", original = a, paired = b, ratio });
                }
                ratios.Sort(); rows.Add(new { workload, comparison = "Original/" + second, unchangedBodyControl = second == "Replica", iterations, samples, medianPairedRatio = ratios[ratios.Count / 2], minRatio = ratios[0], maxRatio = ratios[^1], rows = sampleRows });
            }
        }
        return new { hostOnly = true, arm64Claim = false, fpsClaim = false, allocatedBytes = 0, stopwatchFrequency = Stopwatch.Frequency, runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), os = RuntimeInformation.OSDescription, cpuCount = Environment.ProcessorCount, tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), hardwareIntrinsics = Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic"), limitation = "Small paired x64 host microbenchmark, not game runtime or Switch ARM64. Equal fixture boundary overhead and JIT/code placement/noise may dominate; unchanged-body controls are collected for every workload. Nonfinite conv.i4 behavior is host-specific. Zero allocation applies only to warmed measured loops, not harness setup, tracing or exception cases.", comparisons = rows };
    }
    static int Main(string[] args)
    {
        string output = args[4];
        try
        {
            var proof = Build(args[0], args[1], args[2], args[3], output); Json(Path.Combine(output, "projection.json"), proof.Evidence);
            Json(Path.Combine(output, "boundary-checks.json"), FnaChecks(proof));
            uint seed = uint.Parse(args[5]); var cases = Cases(seed);
            var behavior = Behavior(proof, cases, output, int.Parse(args[8])); Json(Path.Combine(output, "behavior.json"), behavior);
            var negatives = NegativeControls(proof, cases, output); Json(Path.Combine(output, "negative-controls.json"), negatives);
            var allocation = Allocation(proof); Json(Path.Combine(output, "zero-allocation.json"), allocation);
            bool timingRequested = args.Length == 10 && args[9] == "--timing";
            if (timingRequested) Json(Path.Combine(output, "timing.json"), Timing(proof, int.Parse(args[6]), int.Parse(args[7]), seed));
            Json(Path.Combine(output, "summary.json"), new { passed = true, originalSha256 = OriginalSha, candidateSha256 = args[2], fnaSha256 = FnaSha, behavior, negativeControlCount = 5, zeroMeasuredAllocation = true, hostOnly = true, arm64Claim = false, fpsClaim = false, timingRequested });
            Console.WriteLine("PASS serialized actual bodies; " + cases.Length + " cases x 2 lanes x " + args[8] + " repeats; 5 negative controls; zero measured allocation; timing " + (timingRequested ? "collected (host-only, inconclusive)" : "not requested")); return 0;
        }
        catch (Exception e) { Json(Path.Combine(output, "failure.json"), new { passed = false, error = e.ToString() }); Console.Error.WriteLine(e); return 1; }
    }
}
