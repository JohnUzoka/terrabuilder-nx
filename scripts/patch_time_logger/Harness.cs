using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static class Harness
{
    private const int FrameCount = 300;
    private const int BenchmarkRounds = 5;
    private const ulong HashSeed = 14695981039346656037UL;
    private static readonly int[] Adversarial =
    {
        0, 0, 1, -1, 2, -2, int.MinValue, int.MaxValue,
        int.MinValue + 1, int.MaxValue - 1, int.MinValue + 127, int.MaxValue - 127,
        16777215, 16777216, 16777217, 16777218, 16777219,
        -16777215, -16777216, -16777217, -16777218, -16777219,
        33554431, 33554432, 33554433, -33554431, -33554432, -33554433,
        1073741823, 1073741824, 1073741825, -1073741823, -1073741824, -1073741825
    };

    internal static object Verify(Type baseline, Type optimized)
    {
        var left = new Schema(baseline);
        var right = new Schema(optimized);
        ValidateSchemas(left, right);
        var counts = new VerificationCounts();
        var reports = new List<object>();

        void Run(string name, Action<VerificationCase> execute)
        {
            long transitions = counts.Transitions;
            long adds = counts.Adds;
            long frames = counts.Frames;
            long resets = counts.Resets;
            execute(new VerificationCase(left, right, counts, name));
            reports.Add(new
            {
                Name = name,
                Transitions = counts.Transitions - transitions,
                AddAndSpikeChecks = counts.Adds - adds,
                StartNextFrames = counts.Frames - frames,
                Resets = counts.Resets - resets
            });
        }

        Run("empty-zero-duplicates-negative-and-overflow", c =>
        {
            var p = c.Create("empty");
            for (int i = 0; i < 2 * FrameCount + 3; i++) p.Frame();
            p.Reset();
            p.Reset();
            foreach (int value in Adversarial)
            {
                p.Add(value, false);
                p.Add(value, true);
                p.Add(unchecked(-value), false);
                p.Add(int.MaxValue, true);
                p.Add(1, false);
                p.Add(int.MinValue, true);
                p.Frame();
                p.Frame();
            }
            p.Reset();
            for (int i = 0; i < 4 * FrameCount; i++)
            {
                int value = i < FrameCount ? 0 : i < 2 * FrameCount ? 42 : -42;
                p.Add(value, (i & 1) != 0);
                if (i % 3 == 0) p.Add(value, (i & 1) == 0);
                p.Frame();
            }
            for (int i = 0; i < FrameCount + 2; i++) p.Frame();
        });

        Run("dense-sparse-wrapped-and-multiple-adds", c =>
        {
            var p = c.Create("wrapped");
            for (int frame = 0; frame < 12 * FrameCount; frame++)
            {
                int mode = frame / FrameCount % 4;
                int adds = mode == 0 ? 3 : mode == 1 ? (frame % 47 == 0 ? 1 : 0)
                    : mode == 2 ? (frame % 7 == 0 ? 4 : 1) : 0;
                for (int add = 0; add < adds; add++)
                    p.Add(Adversarial[(frame + add * 7) % Adversarial.Length], (add & 1) == 0);
                p.Frame();
            }
        });

        Run("reset-at-every-occupancy-with-stale-storage", c =>
        {
            for (int occupancy = 0; occupancy <= FrameCount; occupancy++)
            {
                var p = c.Create($"occupancy={occupancy}");
                for (int frame = 0; frame < occupancy; frame++)
                {
                    p.Add(Adversarial[(frame + occupancy) % Adversarial.Length], (frame & 1) == 0);
                    p.Frame();
                }
                // At capacity StartNextFrame has already cleared the new current slot.
                if (occupancy == FrameCount) p.Add(123, true);
                p.Reset();
                p.Reset();
                // Reset deliberately retains arrays: both immediate Add and no-Add paths matter.
                if ((occupancy & 1) == 0) p.Add(int.MaxValue, false);
                p.Frame();
                p.Add(1, true);
                p.Frame();
                for (int frame = 0; frame < FrameCount + 2; frame++) p.Frame();
            }
        });

        Run("reset-at-every-full-ring-cursor", c =>
        {
            for (int cursor = 0; cursor < FrameCount; cursor++)
            {
                var p = c.Create($"cursor={cursor}");
                for (int frame = 0; frame < FrameCount + cursor; frame++)
                {
                    if (frame % 5 != 0) p.Add(Adversarial[frame % Adversarial.Length], (frame & 1) != 0);
                    p.Frame();
                }
                if ((cursor & 1) != 0) p.Add(int.MinValue, true);
                p.Reset();
                p.Frame();
                p.Reset();
                p.Add(-1, false);
                p.Add(int.MinValue, true);
                p.Frame();
                for (int frame = 0; frame < FrameCount + 1; frame++)
                {
                    if (frame % 17 == 0) p.Add(16777217, (frame & 1) != 0);
                    p.Frame();
                }
            }
        });

        Run("percentile-ranks-and-float-precision-boundaries", c =>
        {
            // Exercise every quantile rank denominator, not a reimplementation of Quantile.
            for (int occupancy = 1; occupancy <= FrameCount; occupancy++)
            {
                var p = c.Create($"rank-count={occupancy}");
                for (int frame = 0; frame < occupancy; frame++)
                {
                    int value = Adversarial[(frame * 13 + occupancy * 7) % Adversarial.Length];
                    p.Add(value, (frame & 1) != 0);
                    p.Frame();
                }
                p.Add(16777217, false);
                p.Add(-16777217, true);
                p.Frame();
            }
            foreach (int value in Adversarial)
            {
                var p = c.Create($"constant={value}");
                for (int frame = 0; frame < FrameCount + 3; frame++)
                {
                    p.Add(value, false);
                    p.Frame();
                    p.Add(0, true);
                }
            }
        });

        uint[] seeds = { 1U, 0x12345678U, 0x9E3779B9U, 0xDEADBEEFU, 0xA5A5A5A5U, 0x10203040U, 0x80000001U, 0xFFFFFFFFU };
        Run("seeded-random-operation-mix", c =>
        {
            foreach (uint seed in seeds)
            {
                var random = new DeterministicRandom(seed);
                var p = c.Create($"seed=0x{seed:X8}");
                for (int step = 0; step < 40000; step++)
                {
                    uint operation = random.Next() % 1000;
                    if (operation < 640)
                    {
                        uint bits = random.Next();
                        int value = (bits & 3) == 0 ? unchecked((int)random.Next())
                            : Adversarial[(int)(bits % (uint)Adversarial.Length)];
                        p.Add(value, (random.Next() & 1) != 0);
                    }
                    else if (operation == 999 && step / 5000 % 2 == 0) p.Reset();
                    else p.Frame();
                }
                p.Reset();
                p.Reset();
                for (int frame = 0; frame <= FrameCount; frame++) p.Frame();
            }
        });

        Run("interleaved-independent-series", c =>
        {
            var pairs = new Comparison[12];
            for (int series = 0; series < pairs.Length; series++) pairs[series] = c.Create($"series={series}");
            var random = new DeterministicRandom(0xC001D00DU);
            for (int frame = 0; frame < 2 * FrameCount + 7; frame++)
            {
                for (int series = 0; series < pairs.Length; series++)
                {
                    var p = pairs[series];
                    if ((frame + series) % 113 == 0) p.Reset();
                    for (int add = 0; add < series % 4; add++)
                        p.Add(unchecked((int)random.Next()), (add & 1) != 0);
                    p.Frame();
                }
                // Also catch a mutation of a different series through accidental shared storage.
                foreach (var pair in pairs) pair.Check();
            }
        });

        RequireCoverage(counts.ResetCounts, "reset count");
        RequireCoverage(counts.ResetUsedCounts, "reset usedCount");
        RequireCoverage(counts.ResetMarkedSlots, "reset marked-slot occupancy");
        RequireCoverage(counts.ResetCursors, "reset cursor");
        if (counts.FloatRoundedMaximumObservations == 0 || counts.StaleResets == 0
            || counts.FalseSeeds == 0 || counts.TrueSeeds == 0 || counts.FalseSpikes == 0 || counts.TrueSpikes == 0)
            throw new InvalidOperationException("Verification missed required rounding, stale Reset, or spike coverage.");

        return new
        {
            Passed = true,
            Baseline = baseline.FullName,
            Optimized = optimized.FullName,
            FrameCount,
            ComparedTransitions = counts.Transitions,
            StateComparisons = counts.StateComparisons,
            ConstructorComparisons = counts.Constructors,
            AddTransitions = counts.Adds,
            StartNextFrameTransitions = counts.Frames,
            ResetTransitions = counts.Resets,
            SpikeChecks = counts.Adds,
            SpikeInputFalse = counts.FalseSeeds,
            SpikeInputTrue = counts.TrueSeeds,
            SpikeOutputFalse = counts.FalseSpikes,
            SpikeOutputTrue = counts.TrueSpikes,
            OriginalInstanceFields = left.Scalars.Select(f => f.Name).Concat(new[] { "values", "used" }).ToArray(),
            ArrayComparison = "Every element and stable per-instance identity after every transition",
            Getters = "HasData equality and frequency IEEE-754 Single bits after every transition",
            OrderedInvariant = right.Ordered is null ? "Not present" : "Stable non-aliasing array; bounded count and sorted active prefix",
            ResetCoverage = new
            {
                Counts = counts.ResetCounts.Count(x => x),
                UsedCounts = counts.ResetUsedCounts.Count(x => x),
                MarkedSlotOccupancies = counts.ResetMarkedSlots.Count(x => x),
                Cursors = counts.ResetCursors.Count(x => x),
                ResetsWithStaleBackingSlots = counts.StaleResets,
                Scope = "Each occupancy 0..300 and each cursor 0..299; independent coverage, not a Cartesian product"
            },
            FloatRoundedMaximumObservations = counts.FloatRoundedMaximumObservations,
            PercentileOracle = "Actual extracted baseline Quantile and FNA float Lerp, including max; no exact-integer substitute",
            RandomSeeds = seeds.Select(seed => $"0x{seed:X8}").ToArray(),
            RandomOperationsPerSeed = 40000,
            Scenarios = reports
        };
    }

    internal static object Benchmark(Type baseline, Type optimized)
    {
        var left = new Schema(baseline);
        var right = new Schema(optimized);
        ValidateSchemas(left, right);
        RuntimeHelpers.RunClassConstructor(baseline.TypeHandle);
        RuntimeHelpers.RunClassConstructor(optimized.TypeHandle);
        var results = new List<object>();
        foreach (int seriesCount in new[] { 128, 512 })
        {
            foreach (string mode in new[] { "dense", "sparse", "mixed" })
            {
                var workload = new Workload(seriesCount, mode);
                // A complete unreported pair warms constructors, delegate dispatch, getters,
                // sorting, the measured loop, and both implementations before measurements.
                RunRound(left, right, workload, -1, out _, out _);
                var baselineSamples = new Sample[BenchmarkRounds, 5];
                var optimizedSamples = new Sample[BenchmarkRounds, 5];
                for (int round = 0; round < BenchmarkRounds; round++)
                {
                    RunRound(left, right, workload, round, out var a, out var b);
                    for (int phase = 0; phase < a.Length; phase++)
                    {
                        baselineSamples[round, phase] = a[phase];
                        optimizedSamples[round, phase] = b[phase];
                    }
                }
                var phases = new List<object>();
                string[] names = { "constructors", "fill", "steady", "drain", "empty" };
                int[] frames = { 0, 300, 600, 300, 300 };
                for (int phase = 0; phase < names.Length; phase++)
                {
                    var a = Enumerable.Range(0, BenchmarkRounds).Select(r => baselineSamples[r, phase]).ToArray();
                    var b = Enumerable.Range(0, BenchmarkRounds).Select(r => optimizedSamples[r, phase]).ToArray();
                    if (a.Any(s => s.Checksum != a[0].Checksum) || b.Any(s => s.Checksum != a[0].Checksum))
                        throw new InvalidOperationException($"Non-deterministic benchmark checksum: {mode}/{seriesCount}/{names[phase]}.");
                    double baselineMedian = Median(a.Select(s => s.Milliseconds));
                    double optimizedMedian = Median(b.Select(s => s.Milliseconds));
                    phases.Add(new
                    {
                        Name = names[phase],
                        Frames = frames[phase],
                        SeriesFrameTransitions = (long)frames[phase] * seriesCount,
                        AddCalls = phase == 1 ? workload.FillAdds : phase == 2 ? workload.SteadyAdds : 0,
                        Baseline = Summarize(a),
                        Optimized = Summarize(b),
                        MedianSpeedup = optimizedMedian == 0 ? (double?)null : baselineMedian / optimizedMedian,
                        MedianBytesSaved = Median(a.Select(s => (double)s.AllocatedBytes)) - Median(b.Select(s => (double)s.AllocatedBytes)),
                        ChecksumEqual = true,
                        Checksum = a[0].Checksum.ToString("X16")
                    });
                }
                results.Add(new { SeriesCount = seriesCount, Mode = mode, WorkloadSeed = "0x6D2B79F5", Phases = phases });
            }
        }
        return new
        {
            Environment = new
            {
                Runtime = RuntimeInformation.FrameworkDescription,
                RuntimeVersion = Environment.Version.ToString(),
                OS = RuntimeInformation.OSDescription,
                OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.Is64BitProcess,
                Environment.ProcessorCount,
                ServerGC = GCSettings.IsServerGC,
                GCLatencyMode = GCSettings.LatencyMode.ToString(),
                GCHeapHardLimit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                TieredPGO = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                ReadyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun"),
                Stopwatch.Frequency,
                Stopwatch.IsHighResolution
            },
            Rounds = BenchmarkRounds,
            Warmup = "One complete baseline/optimized workload pair per case before measured rounds",
            MeasurementOrder = "Baseline first on even rounds; optimized first on odd rounds, including construction and each phase",
            AllocationScope = "GC.GetAllocatedBytesForCurrentThread; probe constructors separate, delegate binding and workload allocation excluded",
            TimingScope = "Actual Add/StartNextFrame/getter delegates plus equal observable checksum work; no reflection Invoke in frame loops",
            Correctness = "Per-phase checksums and every original field/array compared; arrays retain identity; no harness allocations inside phase loops",
            Interpretation = "JIT host microbenchmark, not Switch AOT timing or Switch FPS; no performance acceptance threshold applied here",
            Cases = results
        };
    }

    private static void RunRound(Schema left, Schema right, Workload workload, int round, out Sample[] a, out Sample[] b)
    {
        a = new Sample[5];
        b = new Sample[5];
        BoundSeries[] first;
        BoundSeries[] second;
        bool baselineFirst = (round & 1) == 0;
        if (baselineFirst)
        {
            first = ConstructBatch(left, workload.SeriesCount, out a[0]);
            second = ConstructBatch(right, workload.SeriesCount, out b[0]);
        }
        else
        {
            second = ConstructBatch(right, workload.SeriesCount, out b[0]);
            first = ConstructBatch(left, workload.SeriesCount, out a[0]);
        }
        var comparisons = new Comparison[first.Length];
        for (int i = 0; i < comparisons.Length; i++)
            comparisons[i] = new Comparison(first[i], second[i], null, $"benchmark/{workload.Mode}/{first.Length}/round={round}/series={i}");
        for (int phase = 0; phase < 4; phase++)
        {
            if (baselineFirst)
            {
                a[phase + 1] = MeasurePhase(first, workload, phase);
                b[phase + 1] = MeasurePhase(second, workload, phase);
            }
            else
            {
                b[phase + 1] = MeasurePhase(second, workload, phase);
                a[phase + 1] = MeasurePhase(first, workload, phase);
            }
            if (a[phase + 1].Checksum != b[phase + 1].Checksum)
                throw new InvalidOperationException($"Benchmark checksum mismatch: {workload.Mode}/{first.Length}/round={round}/phase={phase}.");
            foreach (var comparison in comparisons)
            {
                comparison.Operation = $"phase={phase}";
                comparison.Check();
            }
        }
        GC.KeepAlive(first);
        GC.KeepAlive(second);
    }

    private static BoundSeries[] ConstructBatch(Schema schema, int count, out Sample sample)
    {
        var instances = new object[count];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < instances.Length; i++) instances[i] = schema.Construct();
        long elapsed = Stopwatch.GetTimestamp() - start;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        sample = new Sample(elapsed, bytes, 0);
        var series = new BoundSeries[count];
        for (int i = 0; i < count; i++) series[i] = new BoundSeries(schema, instances[i]);
        return series;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sample MeasurePhase(BoundSeries[] series, Workload workload, int phase)
    {
        int frameCount = phase == 1 ? 600 : 300;
        int inputOffset = phase == 1 ? 300 * series.Length : 0;
        bool hasInput = phase < 2;
        ulong checksum = HashSeed;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int i = 0; i < series.Length; i++)
            {
                var current = series[i];
                if (hasInput)
                {
                    ref readonly Input input = ref workload.Inputs[inputOffset + frame * series.Length + i];
                    bool spike = input.Seed;
                    if (input.Count > 0)
                    {
                        current.Add(input.First, ref spike);
                        checksum = Mix(checksum, spike ? 1 : 0);
                    }
                    if (input.Count > 1)
                    {
                        spike = !input.Seed;
                        current.Add(input.Second, ref spike);
                        checksum = Mix(checksum, spike ? 1 : 0);
                    }
                    if (input.Count > 2)
                    {
                        spike = input.Seed;
                        current.Add(input.Third, ref spike);
                        checksum = Mix(checksum, spike ? 1 : 0);
                    }
                }
                current.NextFrame();
                checksum = Mix(checksum, current.Previous());
                checksum = Mix(checksum, current.Median());
                checksum = Mix(checksum, current.P90());
                checksum = Mix(checksum, current.Max());
                checksum = Mix(checksum, current.HasData() ? 1 : 0);
                checksum = Mix(checksum, BitConverter.SingleToInt32Bits(current.Frequency()));
            }
        }
        long elapsed = Stopwatch.GetTimestamp() - start;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return new Sample(elapsed, bytes, checksum);
    }

    private static object Summarize(Sample[] samples) => new
    {
        MedianMilliseconds = Median(samples.Select(s => s.Milliseconds)),
        MinimumMilliseconds = samples.Min(s => s.Milliseconds),
        MaximumMilliseconds = samples.Max(s => s.Milliseconds),
        MedianAllocatedBytes = Median(samples.Select(s => (double)s.AllocatedBytes)),
        MinimumAllocatedBytes = samples.Min(s => s.AllocatedBytes),
        MaximumAllocatedBytes = samples.Max(s => s.AllocatedBytes),
        Samples = samples.Select((s, round) => new { Round = round, s.Milliseconds, s.AllocatedBytes }).ToArray()
    };

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(x => x).ToArray();
        return sorted.Length % 2 != 0 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static ulong Mix(ulong hash, int value) => unchecked((hash ^ (uint)value) * 1099511628211UL);

    private static void RequireCoverage(bool[] values, string name)
    {
        for (int i = 0; i < values.Length; i++)
            if (!values[i]) throw new InvalidOperationException($"Missing {name} coverage at {i}.");
    }

    private static void ValidateSchemas(Schema baseline, Schema optimized)
    {
        if (!baseline.Scalars.Select(f => f.Name).SequenceEqual(optimized.Scalars.Select(f => f.Name)))
            throw new InvalidOperationException("Baseline and optimized original instance field sets differ.");
        if (baseline.Ordered is not null)
            throw new InvalidOperationException("Baseline unexpectedly contains optimized history fields.");
    }

    private delegate void AddDelegate(int value, ref bool spike);

    private sealed class FieldReader<T>
    {
        internal readonly string Name;
        private readonly DynamicMethod method;

        internal FieldReader(FieldInfo field)
        {
            Name = field.Name;
            if (field.FieldType != typeof(T)) throw new InvalidOperationException($"Unexpected type for {field.DeclaringType}.{Name}.");
            method = new DynamicMethod($"Read_{field.DeclaringType!.Name}_{Name}", typeof(T), new[] { typeof(object) }, typeof(Harness).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
        }

        internal Func<T> Bind(object instance) => (Func<T>)method.CreateDelegate(typeof(Func<T>), instance);
    }

    private sealed class Schema
    {
        internal readonly Type Type;
        internal readonly Func<object> Construct;
        internal readonly FieldReader<int>[] Scalars;
        internal readonly FieldReader<int[]> Values;
        internal readonly FieldReader<bool[]> Used;
        internal readonly FieldReader<int[]>? Ordered;
        internal readonly FieldReader<int>? OrderedCount;
        internal readonly MethodInfo Add;
        internal readonly MethodInfo NextFrame;
        internal readonly MethodInfo Reset;
        internal readonly MethodInfo HasData;
        internal readonly MethodInfo Frequency;

        internal Schema(Type type)
        {
            Type = type;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fields = type.GetFields(flags).ToDictionary(f => f.Name, StringComparer.Ordinal);
            FieldInfo Field(string name) => fields.TryGetValue(name, out var field) ? field
                : throw new InvalidOperationException($"Missing {type.FullName}.{name}.");
            Values = new FieldReader<int[]>(Field("values"));
            Used = new FieldReader<bool[]>(Field("used"));
            foreach (string name in new[] { "next", "count", "usedCount", "previous", "median", "p90", "max" })
                if (Field(name).FieldType != typeof(int)) throw new InvalidOperationException($"Unexpected field type: {type.FullName}.{name}.");
            bool ordered = fields.ContainsKey("_ordered");
            if (ordered != fields.ContainsKey("_orderedCount")) throw new InvalidOperationException("Incomplete ordered-history fields.");
            if (ordered)
            {
                Ordered = new FieldReader<int[]>(Field("_ordered"));
                OrderedCount = new FieldReader<int>(Field("_orderedCount"));
            }
            var scalarFields = fields.Values.Where(f => f.Name != "values" && f.Name != "used" && f.Name != "_ordered" && f.Name != "_orderedCount")
                .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
            Scalars = scalarFields.Select(f => new FieldReader<int>(f)).ToArray();
            MethodInfo Method(string name, Type returns, params Type[] parameters)
            {
                var method = type.GetMethod(name, flags, null, parameters, null)
                    ?? throw new InvalidOperationException($"Missing {type.FullName}.{name}.");
                if (method.ReturnType != returns) throw new InvalidOperationException($"Unexpected return type: {type.FullName}.{name}.");
                return method;
            }
            Add = Method("Add", typeof(void), typeof(int), typeof(bool).MakeByRefType());
            NextFrame = Method("StartNextFrame", typeof(void));
            Reset = Method("Reset", typeof(void));
            HasData = Method("get_HasData", typeof(bool));
            Frequency = Method("get_frequency", typeof(float));
            var getters = type.GetMethods(flags).Where(m => m.IsSpecialName || m.Name.StartsWith("get_", StringComparison.Ordinal))
                .Where(m => m.Name.StartsWith("get_", StringComparison.Ordinal)).Select(m => m.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!getters.SequenceEqual(new[] { "get_HasData", "get_frequency" }))
                throw new InvalidOperationException($"Unexpected getters on {type.FullName}; refusing incomplete comparison.");
            var constructor = type.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException($"Missing public parameterless constructor: {type.FullName}.");
            var factory = new DynamicMethod($"Construct_{type.Name}", typeof(object), Type.EmptyTypes, typeof(Harness).Module, true);
            ILGenerator il = factory.GetILGenerator();
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Ret);
            Construct = (Func<object>)factory.CreateDelegate(typeof(Func<object>));
        }
    }

    private sealed class BoundSeries
    {
        internal readonly Schema Schema;
        internal readonly object Instance;
        internal readonly AddDelegate Add;
        internal readonly Action NextFrame;
        internal readonly Action Reset;
        internal readonly Func<bool> HasData;
        internal readonly Func<float> Frequency;
        internal readonly Func<int>[] Scalars;
        internal readonly Func<int> Next;
        internal readonly Func<int> Count;
        internal readonly Func<int> UsedCount;
        internal readonly Func<int> Previous;
        internal readonly Func<int> Median;
        internal readonly Func<int> P90;
        internal readonly Func<int> Max;
        internal readonly Func<int[]> Values;
        internal readonly Func<bool[]> Used;
        internal readonly Func<int[]>? Ordered;
        internal readonly Func<int>? OrderedCount;
        internal readonly int[] InitialValues;
        internal readonly bool[] InitialUsed;
        internal readonly int[]? InitialOrdered;

        internal BoundSeries(Schema schema, object instance)
        {
            Schema = schema;
            Instance = instance;
            Add = schema.Add.CreateDelegate<AddDelegate>(instance);
            NextFrame = schema.NextFrame.CreateDelegate<Action>(instance);
            Reset = schema.Reset.CreateDelegate<Action>(instance);
            HasData = schema.HasData.CreateDelegate<Func<bool>>(instance);
            Frequency = schema.Frequency.CreateDelegate<Func<float>>(instance);
            Scalars = schema.Scalars.Select(f => f.Bind(instance)).ToArray();
            Func<int> Scalar(string name) => Scalars[Array.FindIndex(schema.Scalars, f => f.Name == name)];
            Next = Scalar("next");
            Count = Scalar("count");
            UsedCount = Scalar("usedCount");
            Previous = Scalar("previous");
            Median = Scalar("median");
            P90 = Scalar("p90");
            Max = Scalar("max");
            Values = schema.Values.Bind(instance);
            Used = schema.Used.Bind(instance);
            Ordered = schema.Ordered?.Bind(instance);
            OrderedCount = schema.OrderedCount?.Bind(instance);
            InitialValues = Values();
            InitialUsed = Used();
            InitialOrdered = Ordered?.Invoke();
        }
    }

    private sealed class Comparison
    {
        private readonly BoundSeries baseline;
        private readonly BoundSeries optimized;
        private readonly VerificationCounts? counts;
        private readonly string context;
        private long step;
        private int addValue;
        private bool spikeSeed;
        internal string Operation = "constructor";

        internal Comparison(BoundSeries baseline, BoundSeries optimized, VerificationCounts? counts, string context)
        {
            this.baseline = baseline;
            this.optimized = optimized;
            this.counts = counts;
            this.context = context;
            if (ReferenceEquals(baseline.InitialValues, optimized.InitialValues) || ReferenceEquals(baseline.InitialUsed, optimized.InitialUsed))
                Fail("baseline and optimized share backing arrays");
            Check();
            if (counts is not null) counts.Constructors++;
        }

        internal void Add(int value, bool seed)
        {
            Operation = "Add";
            addValue = value;
            spikeSeed = seed;
            step++;
            bool leftSpike = seed;
            bool rightSpike = seed;
            baseline.Add(value, ref leftSpike);
            optimized.Add(value, ref rightSpike);
            if (leftSpike != rightSpike) Fail($"spike: baseline={leftSpike}, optimized={rightSpike}");
            if (counts is not null)
            {
                counts.Adds++;
                if (seed) counts.TrueSeeds++; else counts.FalseSeeds++;
                if (leftSpike) counts.TrueSpikes++; else counts.FalseSpikes++;
            }
            Check();
        }

        internal void Frame()
        {
            Operation = "StartNextFrame";
            step++;
            int exactMaximum = int.MinValue;
            bool any = false;
            int scanLength = Math.Min(FrameCount, baseline.Count() + 1);
            for (int i = 0; i < scanLength; i++)
            {
                if (!baseline.InitialUsed[i]) continue;
                any = true;
                exactMaximum = Math.Max(exactMaximum, baseline.InitialValues[i]);
            }
            baseline.NextFrame();
            optimized.NextFrame();
            if (counts is not null)
            {
                counts.Frames++;
                if (any && baseline.Max() != exactMaximum) counts.FloatRoundedMaximumObservations++;
            }
            Check();
        }

        internal void Reset()
        {
            Operation = "Reset";
            step++;
            if (counts is not null)
            {
                counts.Resets++;
                counts.ResetCounts[baseline.Count()] = true;
                counts.ResetUsedCounts[baseline.UsedCount()] = true;
                counts.ResetCursors[baseline.Next()] = true;
                int marked = 0;
                bool stale = false;
                for (int i = 0; i < FrameCount; i++)
                {
                    if (baseline.InitialUsed[i]) marked++;
                    stale |= baseline.InitialUsed[i] || baseline.InitialValues[i] != 0;
                }
                counts.ResetMarkedSlots[marked] = true;
                if (stale) counts.StaleResets++;
            }
            baseline.Reset();
            optimized.Reset();
            Check();
        }

        internal void Check()
        {
            for (int i = 0; i < baseline.Scalars.Length; i++)
            {
                int a = baseline.Scalars[i]();
                int b = optimized.Scalars[i]();
                if (a != b) Fail($"{baseline.Schema.Scalars[i].Name}: baseline={a}, optimized={b}");
            }
            CheckIdentity(baseline);
            CheckIdentity(optimized);
            for (int i = 0; i < FrameCount; i++)
            {
                if (baseline.InitialValues[i] != optimized.InitialValues[i])
                    Fail($"values[{i}]: baseline={baseline.InitialValues[i]}, optimized={optimized.InitialValues[i]}");
                if (baseline.InitialUsed[i] != optimized.InitialUsed[i])
                    Fail($"used[{i}]: baseline={baseline.InitialUsed[i]}, optimized={optimized.InitialUsed[i]}");
            }
            bool leftHasData = baseline.HasData();
            bool rightHasData = optimized.HasData();
            if (leftHasData != rightHasData) Fail($"HasData: baseline={leftHasData}, optimized={rightHasData}");
            int leftFrequency = BitConverter.SingleToInt32Bits(baseline.Frequency());
            int rightFrequency = BitConverter.SingleToInt32Bits(optimized.Frequency());
            if (leftFrequency != rightFrequency) Fail($"frequency bits: baseline=0x{leftFrequency:X8}, optimized=0x{rightFrequency:X8}");
            if (optimized.Ordered is not null)
            {
                int[] ordered = optimized.Ordered();
                int count = optimized.OrderedCount!();
                if (!ReferenceEquals(ordered, optimized.InitialOrdered) || ReferenceEquals(ordered, optimized.InitialValues))
                    Fail("optimized ordered storage changed identity or aliases values");
                if (ordered.Length != FrameCount || count < 0 || count > FrameCount) Fail($"invalid ordered count/capacity: {count}/{ordered.Length}");
                for (int i = 1; i < count; i++)
                    if (ordered[i - 1] > ordered[i]) Fail($"ordered prefix unsorted at {i}");
            }
            if (counts is not null) counts.StateComparisons++;
        }

        private void CheckIdentity(BoundSeries series)
        {
            if (!ReferenceEquals(series.Values(), series.InitialValues) || !ReferenceEquals(series.Used(), series.InitialUsed))
                Fail($"{series.Schema.Type.Name} replaced an original backing array");
            if (series.InitialValues.Length != FrameCount || series.InitialUsed.Length != FrameCount)
                Fail($"{series.Schema.Type.Name} backing array length differs from {FrameCount}");
        }

        private void Fail(string reason) => throw new InvalidOperationException(
            $"{context}, step={step}, operation={Operation}"
            + (Operation == "Add" ? $", value={addValue}, spikeSeed={spikeSeed}" : "") + $": {reason}");
    }

    private sealed class VerificationCase
    {
        private readonly Schema baseline;
        private readonly Schema optimized;
        private readonly VerificationCounts counts;
        private readonly string name;

        internal VerificationCase(Schema baseline, Schema optimized, VerificationCounts counts, string name)
        {
            this.baseline = baseline;
            this.optimized = optimized;
            this.counts = counts;
            this.name = name;
        }

        internal Comparison Create(string label) => new(
            new BoundSeries(baseline, baseline.Construct()), new BoundSeries(optimized, optimized.Construct()), counts, $"{name}/{label}");
    }

    private sealed class VerificationCounts
    {
        internal long Adds;
        internal long Frames;
        internal long Resets;
        internal long Constructors;
        internal long StateComparisons;
        internal long TrueSeeds;
        internal long FalseSeeds;
        internal long TrueSpikes;
        internal long FalseSpikes;
        internal long StaleResets;
        internal long FloatRoundedMaximumObservations;
        internal long Transitions => Adds + Frames + Resets;
        internal readonly bool[] ResetCounts = new bool[FrameCount + 1];
        internal readonly bool[] ResetUsedCounts = new bool[FrameCount + 1];
        internal readonly bool[] ResetMarkedSlots = new bool[FrameCount + 1];
        internal readonly bool[] ResetCursors = new bool[FrameCount];
    }

    private struct DeterministicRandom
    {
        private uint state;
        internal DeterministicRandom(uint seed) => state = seed == 0 ? 1U : seed;
        internal uint Next()
        {
            uint value = state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            return state = value;
        }
    }

    private readonly struct Input
    {
        internal readonly int First;
        internal readonly int Second;
        internal readonly int Third;
        internal readonly int Count;
        internal readonly bool Seed;
        internal Input(int first, int second, int third, int count, bool seed)
        {
            First = first;
            Second = second;
            Third = third;
            Count = count;
            Seed = seed;
        }
    }

    private sealed class Workload
    {
        internal readonly int SeriesCount;
        internal readonly string Mode;
        internal readonly Input[] Inputs;
        internal readonly long FillAdds;
        internal readonly long SteadyAdds;

        internal Workload(int seriesCount, string mode)
        {
            SeriesCount = seriesCount;
            Mode = mode;
            Inputs = new Input[900 * seriesCount];
            var random = new DeterministicRandom(0x6D2B79F5U);
            for (int frame = 0; frame < 900; frame++)
            {
                for (int series = 0; series < seriesCount; series++)
                {
                    uint choice = random.Next();
                    int adds = mode == "dense" ? (choice % 10 == 0 ? 3 : 1)
                        : mode == "sparse" ? (choice % 32 == 0 ? 1 : 0)
                        : series % 4 == 0 ? 1 : series % 4 == 1 ? (choice % 8 == 0 ? 1 : 0)
                        : series % 4 == 2 ? (choice % 5 == 0 ? 3 : 1) : 0;
                    int first = series % 3 == 0 ? 120 + series % 17 : (int)(random.Next() % 18000);
                    int second = (int)(random.Next() % 2000);
                    int third = (int)(random.Next() % 2000);
                    Inputs[frame * seriesCount + series] = new Input(first, second, third, adds, (choice & 1) != 0);
                    if (frame < 300) FillAdds += adds; else SteadyAdds += adds;
                }
            }
        }
    }

    private readonly struct Sample
    {
        internal readonly long Ticks;
        internal readonly long AllocatedBytes;
        internal readonly ulong Checksum;
        internal double Milliseconds => Ticks * 1000.0 / Stopwatch.Frequency;
        internal Sample(long ticks, long allocatedBytes, ulong checksum)
        {
            Ticks = ticks;
            AllocatedBytes = allocatedBytes;
            Checksum = checksum;
        }
    }
}
