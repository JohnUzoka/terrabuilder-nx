using System.Reflection;
using System.Text.Json;
using Mono.Cecil;
using static Common;

internal static class DrawGateProof
{
    internal static object Run(string originalPath, string candidatePath, string fnaPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Require(Sha(File.ReadAllBytes(originalPath)) == Program.InputHash, "gate50 proof original hash mismatch");
        Require(Sha(File.ReadAllBytes(fnaPath)) == Program.FnaHash, "gate50 proof FNA hash mismatch");
        using var original = AssemblyDefinition.ReadAssembly(originalPath); using var candidate = AssemblyDefinition.ReadAssembly(candidatePath); using var fna = AssemblyDefinition.ReadAssembly(fnaPath);
        var receipts = new List<object>(); byte[] bytes = DrawGateProbe.Build(original, candidate, fna, receipts);
        File.WriteAllBytes(Path.Combine(outputDirectory, "DrawGateExactProbe.dll"), bytes);
        var p = new Session(Assembly.Load(bytes)); p.Exercise();
        var negatives = new List<object>();
        foreach (string fault in new[] { "MissIncoming0676", "MissIncoming0683", "UseVisibilityInsteadOfV4", "SkipPriorCache" })
        {
            byte[] negative = DrawGateProbe.Build(original, candidate, fna, new(), fault);
            File.WriteAllBytes(Path.Combine(outputDirectory, fault + ".dll"), negative);
            var q = new Session(Assembly.Load(negative));
            var scenario = fault == "MissIncoming0683" ? new Case("negative_false_type72_lowframe", 72, d => CostFixture.Dark = true, false) : new Case("negative_false_dark", 0, d => CostFixture.Dark = true, false);
            var before = q.Execute(scenario, false); var after = q.Execute(scenario, true);
            p.Check(!before.Equivalent(after), "negative_" + fault + "_rejected_by_observed_effects");
            negatives.Add(new { fault, rejected = true, baseline = before.Evidence(), faulty = after.Evidence(), sha256 = Sha(negative) });
        }
        var result = new { passed = true, proofVersion = 50, originalSha256 = Sha(File.ReadAllBytes(originalPath)), candidateSha256 = Sha(File.ReadAllBytes(candidatePath)), fnaSha256 = Sha(File.ReadAllBytes(fnaPath)), probeSha256 = Sha(bytes), checkCount = p.Checks.Count, checks = p.Checks, scenarios = p.Scenarios, allocationRounds = p.Allocations, unsupportedArrayStates = p.Unsupported, negatives, receipts,
            executionContract = "Full serialized original/candidate Single and original/candidate Draw calling corresponding Single, and original TileDrawInfo constructor. Rectangle four-argument ctor, Vector2 two-argument ctor, addition, Zero getter and Vector2 type initializer execute copied pinned FNA IL. Nonallocating factory adapters only bridge newobj value results into existing CostVec/CostRect fixture signatures. Host-only entry/prep/return markers do not alter source branches except edge-preserving instrumentation. No production instrumentation.",
            boundaries = "Reuses cost45 and tile49 full-body mapper contracts. Lighting, game helper calls, RNG, particles, texture loading and graphics are deterministic external host boundaries, not copied game/GPU implementations. Additional wrappers record full changed-region byref and final geometry values, permit light/data/visibility callback scenarios, and preserve cost45 effects/failures. Effect order/hash, explicit snapshots, final state and boundary exception identity are compared.",
            limits = "Not exhaustive gameplay or arbitrary-hook equivalence. Malformed/null/replaced DoNotAdjustDrawPositionBasedOnTileWidth arrays can suppress old false-path exceptions and are characterized separately, never counted as equivalence passes. Fixtures cannot establish safety of arbitrary mod writes to scratch or arrays. Allocation equality is warmed host evidence only; source/native safety is independent. No host timing, Switch performance, GPU, FPS or hardware adoption claim." };
        Json(Path.Combine(outputDirectory, "draw-gate-proof.json"), result); return result;
    }

    sealed record Case(string Name, ushort Type = 0, Action<CostDrawing>? Configure = null, bool Gate = true, string? Failure = null, int Return = 0x1ac1, bool Early = false, int X = 0, int Y = 0, CostVec Screen = default, CostVec Offset = default, bool Parent = false, bool Solid = true, bool IntoTarget = false);
    sealed record Result(string Effects, string[] Trace, string Observations, Exception? Error, int Prep, int Entries, int[] Returns, bool ScratchIdentity)
    {
        internal bool Equivalent(Result other) => Effects == other.Effects && Trace.SequenceEqual(other.Trace) && Observations == other.Observations && ReferenceEquals(Error, other.Error) && Returns.SequenceEqual(other.Returns) && Entries == other.Entries && ScratchIdentity && other.ScratchIdentity;
        internal object Evidence() => new { Effects, Trace, Observations, error = Error?.GetType().FullName, exceptionIdentity = Error == null ? "none" : ReferenceEquals(Error, CostFixture.Failure) ? "CostFixture.Failure" : ReferenceEquals(Error, DrawGateBoundary.CallbackFailure) ? "CallbackFailure" : "runtime", Prep, Entries, returns = Returns.Select((n, i) => (n, i)).Where(x => x.n != 0).Select(x => new { offset = x.i.ToString("x4"), count = x.n }), ScratchIdentity };
    }
    sealed class Session
    {
        readonly Action<CostDrawing, CostVec, CostVec, int, int> beforeSingle, afterSingle;
        readonly Action<CostDrawing, bool, bool, int> beforeDraw, afterDraw;
        internal readonly List<string> Checks = new(); internal readonly List<object> Scenarios = new(), Allocations = new(), Unsupported = new();
        readonly HashSet<string> witnessedEffects = new(StringComparer.Ordinal);
        internal Session(Assembly assembly)
        {
            Type hooks = assembly.GetType("Probe.Hooks", true)!;
            beforeSingle = hooks.GetMethod("BaselineSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>(); afterSingle = hooks.GetMethod("PatchedSingle")!.CreateDelegate<Action<CostDrawing, CostVec, CostVec, int, int>>();
            beforeDraw = hooks.GetMethod("BaselineDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>(); afterDraw = hooks.GetMethod("PatchedDraw")!.CreateDelegate<Action<CostDrawing, bool, bool, int>>();
            var scratch = Activator.CreateInstance(assembly.GetType("Probe.Scratch", true)!)!;
            Check(scratch.GetType().GetField("colorSlices")!.GetValue(scratch) is CostVec3[] { Length: 9 } array && array.All(item => item.X == 0 && item.Y == 0 && item.Z == 0), "copied_original_constructor_allocates_nine_zero_vectors");
            var vector = hooks.GetMethod("VectorCtorValue")!.CreateDelegate<Func<float, float, CostVec>>();
            var rectangle = hooks.GetMethod("RectangleCtorValue")!.CreateDelegate<Func<int, int, int, int, CostRect>>();
            var add = hooks.GetMethod("VectorAdd")!.CreateDelegate<Func<CostVec, CostVec, CostVec>>();
            var zero = hooks.GetMethod("VectorZero")!.CreateDelegate<Func<CostVec>>();
            var v = vector(-0.0f, -123.75f); var r = rectangle(int.MinValue, int.MaxValue, -17, 0); var sum = add(v, new(0.0f, 0.25f));
            Check(BitConverter.SingleToInt32Bits(v.X) == int.MinValue && v.Y == -123.75f && r.X == int.MinValue && r.Y == int.MaxValue && r.Width == -17 && r.Height == 0, "copied_FNA_constructors_preserve_signed_zero_and_unvalidated_integer_values");
            Check(BitConverter.SingleToInt32Bits(sum.X) == 0 && sum.Y == -123.5f && BitConverter.SingleToInt32Bits(v.X) == int.MinValue && v.Y == -123.75f && zero().X == 0 && zero().Y == 0, "copied_FNA_addition_by_value_and_zero_initializer_semantics");
        }
        internal void Check(bool condition, string name) { Require(condition, "gate50 proof: " + name); Checks.Add(name); }
        CostDrawing Setup(Case c)
        {
            CostSets.HasOutlines = new bool[754]; CostSets.IgnoreDrawLightConditions = new bool[754]; CostSets.DoNotAdjustDrawPositionBasedOnTileWidth = new bool[754]; CostSets.DoNotAdjustDrawPositionBasedOnTileWidth[711] = true;
            CostSets.HasSlopeFrames = new bool[754]; CostSets.Platforms = new bool[754]; CostSets.BlocksStairs = new bool[754];
            CostWorld.tileGlowMask = Enumerable.Repeat((short)-1, 754).ToArray(); CostWorld.tileFlame = new bool[754]; CostWorld.tileSolid = Enumerable.Repeat(true, 754).ToArray(); CostWorld.tileFrame = new int[754];
            CostFixture.Reset(c.Parent ? 9 : Math.Max(1, c.X + 1), c.Parent ? 6 : Math.Max(1, c.Y + 1), c.Type, c.Solid);
            DrawGateBoundary.Reset(); CostDrawing.DrawOwnBlacks = true; CostDrawing._zero = default;
            CostWorld.drawToScreen = CostWorld.placementPreview = false; CostWorld.timeForVisualEffects = 0; CostWorld.GlobalTimeWrappedHourly = 0; CostWorld.myPlayer = 0;
            TileDebug.devLightTilesCheat = TileDebug.ShowUnbreakableWall = false; TileCapture.Instance.Active = false;
            var drawing = new CostDrawing(); c.Configure?.Invoke(drawing); CostFixture.ThrowAt = c.Failure; return drawing;
        }
        internal Result Execute(Case c, bool after)
        {
            var drawing = Setup(c); Exception? error = null;
            try { if (c.Parent) (after ? afterDraw : beforeDraw)(drawing, c.Solid, c.IntoTarget, 3); else (after ? afterSingle : beforeSingle)(drawing, c.Screen, c.Offset, c.X, c.Y); }
            catch (Exception e) { error = e; }
            string state = JsonSerializer.Serialize(new { CostFixture.Effects, CostWorld.mapTime, CostWorld.critterCage, dust = DrawGateBoundary.Snapshot(CostFixture.Dust[0]), tiles = DrawGateBoundary.Snapshot(CostWorld.tile), scratch = DrawGateBoundary.Snapshot(CostFixture.LastScratch), drawing._isActiveAndNotPaused, drawing._shouldShowInvisibleBlocks, drawing._lastPaintLookupKey.TileType });
            return new(state, CostFixture.Trace.ToArray(), JsonSerializer.Serialize(DrawGateBoundary.Observations), error, DrawGateBoundary.Prep, DrawGateBoundary.Entries, (int[])DrawGateBoundary.Returns.Clone(), DrawGateBoundary.ScratchIdentity);
        }
        void Scenario(Case c, int? beforePrep = null, int? afterPrep = null)
        {
            var before = Execute(c, false); var after = Execute(c, true);
            Check(before.Equivalent(after), c.Name + ":effects_order_values_byrefs_geometry_state_and_exception_identity");
            witnessedEffects.UnionWith(before.Trace);
            if (c.Failure != null) Check(ReferenceEquals(before.Error, CostFixture.Failure), c.Name + ":failure_boundary_exercised");
            else Check(before.Error == null, c.Name + ":completed_without_exception");
            if (!c.Parent)
            {
                Check(before.Entries == 1 && after.Entries == 1, c.Name + ":whole_single_entered");
                if (c.Failure == null) Check(before.Returns[c.Return] == 1 && after.Returns[c.Return] == 1, c.Name + ":original_return_" + c.Return.ToString("x4"));
                int bp = beforePrep ?? (c.Early ? 0 : 1), ap = afterPrep ?? (c.Early || !c.Gate ? 0 : 1);
                Check(before.Prep == bp && after.Prep == ap, c.Name + ":preparation_execution_counts");
            }
            else Check(before.Entries > 0 && before.Entries == after.Entries, c.Name + ":whole_parent_calls_corresponding_single");
            if (c.Name.StartsWith("geometry_", StringComparison.Ordinal))
            {
                int width = DrawGateBoundary.Width!.Value, half = CostWorld.tile[c.X, c.Y]!.Half ? 8 : 0;
                float shift = CostSets.DoNotAdjustDrawPositionBasedOnTileWidth[c.Type] ? 0 : (width - 16f) / 2f;
                var expectedPosition = new CostVec((c.X * 16 - (int)c.Screen.X) - shift + c.Offset.X, c.Y * 16 - (int)c.Screen.Y + CostFixture.TileTop + half + c.Offset.Y);
                var rect = DrawGateBoundary.LastRect; var position = DrawGateBoundary.LastPosition;
                Check(DrawGateBoundary.BasicDraws == 1 && rect.X == DrawGateBoundary.FrameX + DrawGateBoundary.AddX && rect.Y == DrawGateBoundary.FrameY + DrawGateBoundary.AddY && rect.Width == width && rect.Height == CostFixture.TileHeight - half && BitConverter.SingleToInt32Bits(position.X) == BitConverter.SingleToInt32Bits(expectedPosition.X) && BitConverter.SingleToInt32Bits(position.Y) == BitConverter.SingleToInt32Bits(expectedPosition.Y), c.Name + ":independent_rectangle_and_position_oracle");
            }
            Scenarios.Add(new { c.Name, c.Type, c.Gate, baseline = before.Evidence(), candidate = after.Evidence() });
        }
        internal void Exercise()
        {
            foreach (var (type, frame) in new[] { ((ushort)0, (short)0), ((ushort)72, (short)0), ((ushort)72, (short)35), ((ushort)72, (short)36), ((ushort)72, (short)90) })
                foreach (bool hidden in new[] { false, true })
                    foreach (bool light in new[] { false, true })
                    {
                        string name = $"entry_{type}_frame{frame}_hidden{hidden}_light{light}";
                        Scenario(new(name, type, d => { var cell = CostWorld.tile[0, 0]!; cell.frameX = frame; cell.frameY = 18; cell.Hidden = hidden; CostFixture.Dark = !light; }, light && !hidden));
                    }
            Scenario(new("false_dark_no_own_blacks", Configure: d => { CostFixture.Dark = true; CostDrawing.DrawOwnBlacks = false; }, Gate: false));
            Scenario(new("false_hidden_with_all_positive_predicates", Configure: d => { CostFixture.Glow = true; CostWorld.tileGlowMask[0] = 0; CostWorld.tileFlame[0] = true; CostSets.IgnoreDrawLightConditions[0] = true; CostWorld.tile[0, 0]!.Hidden = true; CostWorld.tile[0, 0]!.wall = 318; }, Gate: false));
            foreach (var (name, configure) in new (string, Action<CostDrawing>)[] {
                ("red", d => DrawGateBoundary.Light = new(1, 0, 0, 0)), ("green", d => DrawGateBoundary.Light = new(0, 1, 0, 0)), ("blue", d => DrawGateBoundary.Light = new(0, 0, 1, 0)),
                ("light_override", d => DrawGateBoundary.Override = new(0, 0, 1, 0)), ("glow_texture", d => CostFixture.Glow = true), ("glow_mask", d => CostWorld.tileGlowMask[0] = 0),
                ("flame", d => CostWorld.tileFlame[0] = true), ("ignore_light", d => CostSets.IgnoreDrawLightConditions[0] = true),
                ("fullbright_wall", d => { CostWorld.tile[0, 0]!.wall = 1; CostWorld.tile[0, 0]!.Fullbright = true; }), ("wall318", d => CostWorld.tile[0, 0]!.wall = 318) })
                Scenario(new("true_via_" + name, Configure: d => { CostFixture.Dark = true; configure(d); }));
            Scenario(new("false_alpha_only", Configure: d => DrawGateBoundary.Light = new(0, 0, 0, 255), Gate: false));
            foreach (bool adjust in new[] { false, true }) foreach (bool half in new[] { false, true })
                foreach (int width in new[] { 1, 16, 17, 48 })
                    Scenario(new($"geometry_flag{adjust}_half{half}_width{width}", 0, d => { DrawGateBoundary.Width = width; DrawGateBoundary.FrameX = -18; DrawGateBoundary.FrameY = 54; DrawGateBoundary.AddX = 7; DrawGateBoundary.AddY = -3; CostFixture.TileTop = -5; CostFixture.TileHeight = 24; CostSets.DoNotAdjustDrawPositionBasedOnTileWidth[0] = adjust; CostWorld.tile[2, 1]!.Half = half; }, X: 2, Y: 1, Screen: new(-23.75f, 103.5f), Offset: new(-0.5f, 7.25f)));
            Scenario(new("last_valid_type753", 753)); Scenario(new("builtin_no_adjust711", 711, d => DrawGateBoundary.Width = 33));
            Scenario(new("negative_screen_integer_geometry", Screen: new(-1048575.75f, -0.75f), Offset: new(-0.0f, -3.5f)));
            Scenario(new("first_return_liquid518", 518, d => CostWorld.tile[0, 0]!.liquid = 1, Return: 0x7a, Early: true));
            Scenario(new("return0916_type751", 751, d => CostWorld.tile[0, 0]!.frameX = 90, Return: 0x916));
            Scenario(new("return094c_type752", 752, d => CostWorld.tile[0, 0]!.frameX = 90, Return: 0x94c));
            Scenario(new("return0ce5_type323", 323, d => CostWorld.tile[0, 0]!.frameX = 90, Return: 0xce5));
            foreach (ushort type in new ushort[] { 80, 83, 129, 171, 314, 429, 725 }) Scenario(new("special_full_path_" + type, type, d => { if (type is 129 or 429 or 725) CostWorld.tileGlowMask[type] = 0; }));
            Scenario(new("false_particles_random_dust_special", Configure: d => { CostFixture.Dark = true; CostWorld.tile[0, 0]!.Hidden = true; d._isActiveAndNotPaused = true; CostWorld.player[0].dangerSense = CostWorld.player[0].findTreasure = CostWorld.player[0].biomeSight = true; }, Gate: false));
            foreach (int random in new[] { 0, 1, 3 }) Scenario(new("false_fast_random_" + random, Configure: d => { CostFixture.Dark = true; d._isActiveAndNotPaused = true; CostFixture.UpdateEveryFrame = true; CostFixture.RandomValue = random; }, Gate: false));
            Scenario(new("outline_prior_byrefs", Configure: d => CostSets.HasOutlines[0] = true));
            Scenario(new("computed_false_preserved_after_visibility_mutation", Configure: d => { CostWorld.tile[0, 0]!.Hidden = true; CostFixture.Reenter = () => { CostWorld.tile[0, 0]!.Hidden = false; }; }, Gate: false));
            Scenario(new("computed_true_preserved_after_visibility_mutation", Configure: d => CostFixture.Reenter = () => { CostWorld.tile[0, 0]!.Hidden = true; }));
            foreach (string boundary in new[] { "GetColor", "GetTileDrawData", "GetTileDrawTexture", "DrawTiles_GetLightOverride", "IsVisible", "DrawBlack", "CacheSpecialDraws_Part1", "CacheSpecialDraws_Part2" })
                Scenario(new("prior_throw_" + boundary, Configure: d => CostFixture.Dark = true, Gate: false, Failure: boundary), 0, 0);
            foreach (string boundary in new[] { "Random.Next", "NewDust", "DrawTiles_EmitParticles" })
                Scenario(new("prior_throw_" + boundary, Configure: d => { CostFixture.Dark = true; CostWorld.tile[0, 0]!.Hidden = true; d._isActiveAndNotPaused = true; CostWorld.player[0].dangerSense = true; }, Gate: false, Failure: boundary), 0, 0);
            Scenario(new("prior_throw_outline", Configure: d => CostSets.HasOutlines[0] = true, Failure: "GetTileOutlineInfo"), 0, 0);
            Scenario(new("prior_throw_shroom_draw", 72, d => { CostWorld.tile[0, 0]!.frameX = 36; CostFixture.Dark = true; }, false, "Graphics.Draw"), 0, 0);
            foreach (string boundary in new[] { "GetFinalLight", "DrawBasicTile" }) Scenario(new("true_throw_" + boundary, Failure: boundary));
            Scenario(new("true_throw_graphics", Configure: d => CostFixture.Glow = true, Failure: "Graphics.Draw"));
            Scenario(new("true_throw_minecart", 314, Failure: "DrawTile_MinecartTrack")); Scenario(new("true_throw_tree", 171, Failure: "DrawXmasTree"));
            foreach (bool gate in new[] { false, true })
            {
                var c = new Case("callback_failure_gate" + gate, Configure: d => { CostFixture.Dark = !gate; CostFixture.Reenter = () => throw DrawGateBoundary.CallbackFailure; }, Gate: gate);
                var before = Execute(c, false); var after = Execute(c, true);
                Check(before.Equivalent(after) && ReferenceEquals(before.Error, DrawGateBoundary.CallbackFailure) && before.Prep == 0 && after.Prep == 0, c.Name + ":same_prior_callback_exception_identity_order"); Scenarios.Add(new { c.Name, baseline = before.Evidence(), candidate = after.Evidence() });
            }
            foreach (bool solid in new[] { false, true }) foreach (bool target in new[] { false, true }) foreach (bool dark in new[] { false, true })
                Scenario(new($"parent_solid{solid}_target{target}_dark{dark}", Configure: d => { CostFixture.Dark = dark; CostWorld.tile[0, 0]!.type = 72; CostWorld.tile[0, 0]!.frameX = 36; CostWorld.tile[1, 0]!.Hidden = true; CostWorld.tile[2, 0]!.Half = true; }, Gate: !dark, Parent: true, Solid: solid, IntoTarget: target));
            foreach (string effect in new[] { "GetColor", "GetTileDrawData", "GetTileDrawTexture", "GetTileOutlineInfo", "Random.Next", "FastRandom.Next", "NewDust", "DrawTiles_EmitParticles", "DrawBlack", "CacheSpecialDraws_Part1", "CacheSpecialDraws_Part2", "Graphics.Draw", "DrawBasicTile" }) Check(witnessedEffects.Contains(effect), "witnessed_actual_boundary_" + effect);
            NestedScratch(); CorruptArrays(); Allocation();
        }
        void NestedScratch()
        {
            foreach (bool gate in new[] { false, true })
            {
                (Result result, bool separate) Run(bool after)
                {
                    bool separate = false;
                    var c = new Case("nested_scratch", Configure: drawing => { CostFixture.Dark = !gate; CostFixture.Reenter = () => {
                        var outer = CostFixture.LastScratch!; var outerArray = outer.GetType().GetField("colorSlices")!.GetValue(outer);
                        (after ? afterSingle : beforeSingle)(drawing, default, default, 0, 0);
                        var inner = CostFixture.LastScratch!; separate = !ReferenceEquals(outer, inner) && !ReferenceEquals(outerArray, inner.GetType().GetField("colorSlices")!.GetValue(inner));
                    }; }, Gate: gate);
                    return (Execute(c, after), separate);
                }
                var before = Run(false); var after = Run(true);
                Check(before.result.Equivalent(after.result) && before.separate && after.separate && before.result.Entries == 2 && after.result.Entries == 2, "nested_gate" + gate + ":distinct_scratch_and_color_array_parent_lifetime_preserved");
                Scenarios.Add(new { name = "nested_gate" + gate, baseline = before.result.Evidence(), candidate = after.result.Evidence() });
            }
        }
        void CorruptArrays()
        {
            foreach (bool nullArray in new[] { false, true }) foreach (bool gate in new[] { false, true })
            {
                var c = new Case($"unsupported_array_null{nullArray}_gate{gate}", 1, d => { CostFixture.Dark = !gate; CostSets.DoNotAdjustDrawPositionBasedOnTileWidth = nullArray ? null! : Array.Empty<bool>(); }, gate);
                var before = Execute(c, false); var after = Execute(c, true); Type expected = nullArray ? typeof(NullReferenceException) : typeof(IndexOutOfRangeException);
                Check(before.Error?.GetType() == expected && (gate ? after.Error?.GetType() == expected : after.Error == null), c.Name + ":unsupported_exception_delta_characterized");
                Check(before.Trace.SequenceEqual(after.Trace), c.Name + ":prior_boundary_order_preserved");
                Unsupported.Add(new { c.Name, equivalenceClaimed = false, classification = gate ? "both_fail_in_preparation_same_exception_type" : "baseline_fails_in_preparation_candidate_returns_without_preparation", baseline = before.Evidence(), candidate = after.Evidence() });
            }
        }
        void Allocation()
        {
            foreach (bool gate in new[] { false, true })
            {
                var drawing = Setup(new("allocation", Configure: d => CostFixture.Dark = !gate, Gate: gate)); CostFixture.Record = false;
                for (int i = 0; i < 2000; i++) { beforeSingle(drawing, default, default, 0, 0); afterSingle(drawing, default, default, 0, 0); }
                const int iterations = 10000;
                long Measure(Action<CostDrawing, CostVec, CostVec, int, int> action) { long start = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < iterations; i++) action(drawing, default, default, 0, 0); return GC.GetAllocatedBytesForCurrentThread() - start; }
                for (int round = 0; round < 3; round++) { long before = Measure(beforeSingle), after = Measure(afterSingle); Check(before > 0 && before == after, $"allocation_gate{gate}_round{round}:original_scratch_allocations_retained_zero_added_bytes"); Allocations.Add(new { gate, round, iterations, originalBytes = before, candidateBytes = after }); }
                CostFixture.Record = true;
            }
        }
    }
}

// Deterministic external boundary extensions, never shipped. Every game effect
// still goes through CostFixture. Reflection and rich snapshots are disabled in
// allocation runs, as in the original cost45 proof.
public static class DrawGateBoundary
{
    public static int Prep, Entries; public static readonly int[] Returns = new int[0x1ac2];
    public static readonly List<object> Observations = new(); public static readonly List<object> ScratchObjects = new();
    public static bool ScratchIdentity; public static CostColor? Light, Override; public static int? Width, AddX, AddY; public static short? FrameX, FrameY;
    public static int BasicDraws; public static CostRect LastRect; public static CostVec LastPosition;
    public static readonly Exception CallbackFailure = new IOException("deterministic gate50 callback failure");
    public static void Reset() { Prep = Entries = BasicDraws = 0; LastRect = default; LastPosition = default; Array.Clear(Returns); Observations.Clear(); ScratchObjects.Clear(); ScratchIdentity = true; Light = Override = null; Width = AddX = AddY = null; FrameX = FrameY = null; }
    public static void Mark(int id) { if (id == -1) Prep++; else if (id == -2) Entries++; else Returns[id]++; }
    public static object? Snapshot(object? value)
    {
        if (value == null) return null;
        if (value is float f) return new { floatBits = BitConverter.SingleToInt32Bits(f) };
        if (value is double d) return new { doubleBits = BitConverter.DoubleToInt64Bits(d) };
        if (value is string || value.GetType().IsPrimitive) return value;
        if (value is CostColor c) return new[] { (int)c.R, c.G, c.B, c.A };
        if (value is Array a) return a.Cast<object?>().Select(Snapshot).ToArray();
        return value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(f => f.Name, StringComparer.Ordinal).ToDictionary(f => f.Name, f => Snapshot(f.GetValue(value)));
    }
    static void Observe(string name, params object?[] values) { if (CostFixture.Record) Observations.Add(new { name, values = values.Select(Snapshot).ToArray() }); }
    public static CostColor GetColor(int x, int y) { var value = CostLighting.GetColor(x, y); return Light ?? value; }
    public static void GetTileDrawData(CostDrawing drawing, int x, int y, CostCell cell, ushort type, ref short fx, ref short fy, ref int width, ref int height, ref int top, ref int half, ref int addX, ref int addY, ref int effects, ref CostTexture? glow, ref CostRect rect, ref CostColor color)
    {
        if (CostFixture.Record) Observe("DrawDataInput", x, y, cell, type, fx, fy, width, height, top, half, addX, addY, effects, glow, rect, color);
        drawing.GetTileDrawData(x, y, cell, type, ref fx, ref fy, ref width, ref height, ref top, ref half, ref addX, ref addY, ref effects, ref glow, ref rect, ref color);
        if (Width is int w) width = w; if (FrameX is short xx) fx = xx; if (FrameY is short yy) fy = yy; if (AddX is int ax) addX = ax; if (AddY is int ay) addY = ay;
        if (CostFixture.Record) Observe("DrawDataOutput", fx, fy, width, height, top, half, addX, addY, effects, glow, rect, color);
    }
    public static CostColor DrawTiles_GetLightOverride(CostDrawing drawing, int y, int x, CostCell cell, ushort type, short fx, short fy, CostColor light)
    {
        var value = drawing.DrawTiles_GetLightOverride(y, x, cell, type, fx, fy, light); var result = Override ?? value;
        if (CostFixture.Record) Observe("OverrideArgumentsAndResult", y, x, cell, type, fx, fy, light, result); return result;
    }
    public static bool IsVisible(CostDrawing drawing, CostCell cell) { CostFixture.Effect("IsVisible"); return drawing.IsVisible(cell); }
    public static void CacheSpecialDraws_Part1(CostDrawing drawing, int x, int y, int type, int fx, int fy, bool dark)
    { drawing.CacheSpecialDraws_Part1(x, y, type, fx, fy, dark); if (CostFixture.Record) Observe("CachePart1", x, y, type, fx, fy, dark); }
    public static void CacheSpecialDraws_Part2(CostDrawing drawing, int x, int y, object info)
    {
        if (CostFixture.Record) { ScratchObjects.Add(info); Observe("CachePart2Input", x, y, info); }
        drawing.CacheSpecialDraws_Part2(x, y, info);
    }
    public static void DrawBasicTile(CostDrawing drawing, CostVec a, CostVec b, int x, int y, object info, CostRect rect, CostVec position)
    {
        if (CostFixture.Record)
        {
            bool found = false; foreach (object scratch in ScratchObjects) if (ReferenceEquals(scratch, info)) { found = true; break; }
            ScratchIdentity &= found; LastRect = rect; LastPosition = position; BasicDraws++;
            Observe("DrawBasicArguments", a, b, x, y, info, rect, position);
        }
        drawing.DrawBasicTile(a, b, x, y, info, rect, position);
    }
    public static void GraphicsDraw(CostBatch batch, CostTexture texture, CostVec position, CostRect source, CostVertices color, CostVec origin, float scale, int effects)
    {
        if (CostFixture.Record) Observe("GraphicsArguments", texture, position, source, color, origin, scale, effects);
        batch.Draw(texture, position, source, color, origin, scale, effects);
    }
}
