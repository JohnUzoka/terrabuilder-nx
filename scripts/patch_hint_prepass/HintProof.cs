using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class HintProof
{
    internal static object Run(string originalGame, string candidateGame, string originalReLogic, string candidateReLogic, string fnaPath, string output)
    {
        Require(!Directory.Exists(output), "fresh hint proof output required"); Directory.CreateDirectory(output);
        Require(Sha(File.ReadAllBytes(originalGame)) == Program.InputHash && Sha(File.ReadAllBytes(originalReLogic)) == Program.ReLogicHash && Sha(File.ReadAllBytes(fnaPath)) == Program.FnaHash, "proof input pins changed");
        string metrics = Environment.GetEnvironmentVariable("HINT52_METRICS") ?? "/build/hint52-investigation/geometry/proof07/mouse-metrics.json";
        Require(Sha(File.ReadAllBytes(metrics)) == "50a420732eaaf03bae52c2ea6d41274cdc7fd1b9e8035313bba503d85776bcab", "decoded pinned Mouse_Text evidence changed");
        using var beforeGame = AssemblyDefinition.ReadAssembly(originalGame);
        using var afterGame = AssemblyDefinition.ReadAssembly(candidateGame);
        using var beforeFont = AssemblyDefinition.ReadAssembly(originalReLogic);
        using var afterFont = AssemblyDefinition.ReadAssembly(candidateReLogic);
        using var fna = AssemblyDefinition.ReadAssembly(fnaPath);
        var receipts = new List<object>();
        byte[] handlers = HintProbe.Handlers(beforeGame.MainModule, beforeFont.MainModule, fna.MainModule);
        File.WriteAllBytes(Path.Combine(output, "controlled-handlers.dll"), handlers);
        byte[] roots = HintProbe.Roots(Program.Target(beforeGame.MainModule), Program.Target(afterGame.MainModule), fna.MainModule, receipts);
        File.WriteAllBytes(Path.Combine(output, "actual-roots.dll"), roots);
        var rootType = Assembly.Load(roots).GetType("Probe.Root", true)!;
        var beforeRoot = rootType.GetMethod("Original")!.CreateDelegate<Action>();
        var afterRoot = rootType.GetMethod("Candidate")!.CreateDelegate<Action>();
        using var before = new HintRuntime(originalGame, originalReLogic, fnaPath, metrics, handlers, false);
        using var after = new HintRuntime(candidateGame, candidateReLogic, fnaPath, metrics, handlers, true);
        foreach (var module in new[] { beforeGame.MainModule, afterGame.MainModule, beforeFont.MainModule, afterFont.MainModule })
            foreach (var method in Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody && (m.Name.StartsWith("_NXHint52", StringComparison.Ordinal) || m.DeclaringType.FullName is "Terraria.UI.Chat.ChatManager" or "Terraria.UI.Chat.TextSnippet" or "Terraria.GameContent.UI.Chat.GlyphTagHandler/GlyphSnippet" or "ReLogic.Graphics.DynamicSpriteFont")))
                receipts.Add(HintProbe.Receipt(method, null, "direct-isolated-assembly-execution-source"));
        var checks = new List<string>(); var cases = new List<object>();
        void Check(bool ok, string name) { Require(ok, name); checks.Add(name); }
        void Case(string name, Action<HintRuntime> configure, bool? fast = null, int? callbacks = null)
        {
            var left = Observe(before, beforeRoot, configure); var right = Observe(after, afterRoot, configure);
            Check(left.Valid && right.Valid, name + ":runtime-valid");
            Check(left.State == right.State && left.Error == right.Error && left.Trace.SequenceEqual(right.Trace) && left.CallbackTrace.SequenceEqual(right.CallbackTrace), name + ":root-output-effects-error-order");
            if (fast == false) Check(left.Measured == right.Measured, name + ":exact-fallback-return-bits");
            if (callbacks.HasValue) Check(left.ParseCallbacks == callbacks && right.ParseCallbacks == callbacks, name + ":parse-exactly-once");
            if (fast.HasValue)
            {
                after.Reset(); HintFixture.Reset(); configure(after);
                object snippets = after.ParseText(HintFixture.Instructions);
                bool actual = after.Guard!(after.Font, snippets, 1, 1, -1);
                Check(actual == fast, name + ":guard-" + fast);
            }
            cases.Add(new { name, expectedFast = fast, original = left, candidate = right });
        }
        Case("common-controller-glyph-text", _ => { }, true);
        Case("pure-handler-still-called", _ => HintFixture.Instructions = "[probe:Press A]", true, 1);
        Case("unknown-tag-original-parser", _ => HintFixture.Instructions = "[unregistered:value]", true);
        Case("color-handler", _ => HintFixture.Instructions = "[c/00ff00:Green]", true);
        Case("name-handler-base-text", _ => HintFixture.Instructions = "[n:Player]", true);
        Case("plain-subclass-fallback", _ => HintFixture.Instructions = "[plain:Text]", false);
        Case("item-invalid-text-original-handler", _ => HintFixture.Instructions = "[i:not-an-item]", true);
        Case("actual-achievement-subclass-layout", r =>
        {
            HintFixture.Instructions = "[probe:achievement]";
            r.SnippetFactory = text =>
            {
                object snippet = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(r.Game.GetType("Terraria.GameContent.UI.Chat.AchievementTagHandler+AchievementSnippet", true)!);
                r.SnippetType.GetField("Text")!.SetValue(snippet, text);
                return snippet;
            };
        }, false, 1);
        Case("custom-subclass-fallback", r => { HintFixture.Instructions = "[probe:one two three]"; r.SnippetFactory = r.Stateful; }, false, 1);
        Case("stateful-unique-same-object", r => { HintFixture.Instructions = "[probe:effect]"; r.SnippetFactory = r.Stateful; r.UniqueHandled = true; }, false, 1);
        Case("plain-error-before-later-stateful-snippet", r => { HintFixture.Instructions = "AAA[probe:effect]"; r.ActiveCulture.SetValue(r.LanguageManager, null); r.SnippetFactory = r.Stateful; r.UniqueHandled = true; }, false, 1);
        Case("stateful-effect-before-later-plain-error", r => { HintFixture.Instructions = "[probe:effect]AAA"; r.ActiveCulture.SetValue(r.LanguageManager, null); r.SnippetFactory = r.Stateful; r.UniqueHandled = true; }, false, 1);
        Case("stateful-effect-mutates-later-font-metrics", r => { HintFixture.Instructions = "[probe:effect]AAAA"; r.SnippetFactory = r.Stateful; r.UniqueHandled = true; r.UniqueEffect = () => r.SpacingField.SetValue(r.GoodFont, float.NaN); }, false, 1);
        Case("unique-error-before-scale-swap", r => { HintFixture.Instructions = "[probe:error]"; r.SnippetFactory = r.Stateful; r.ThrowUnique = true; }, false, 1);
        Case("morph-error-before-scale-swap", r => { HintFixture.Instructions = "[probe:one two]"; r.SnippetFactory = r.Stateful; r.ThrowMorph = true; }, false, 1);
        Case("parser-error-before-scale-swap", r => { HintFixture.Instructions = "[probe:error]"; r.ThrowParse = true; }, null, 1);
        Case("parser-returns-null", r => { HintFixture.Instructions = "[probe:null]"; r.SnippetFactory = _ => null; }, null, 1);
        Case("parser-replaces-glyph-alias", r => { HintFixture.Instructions = "[g:effect]"; r.Register("g", r.HandlerType); r.SnippetFactory = r.Stateful; r.UniqueHandled = true; }, false, 1);
        Case("parser-returns-null-text", r => { HintFixture.Instructions = "[probe:nulltext]"; r.SnippetFactory = _ => { var s = r.Text("valid"); r.SnippetType.GetField("Text")!.SetValue(s, null); return s; }; }, false, 1);
        Case("parser-mutates-current-font-after-warm", r =>
        {
            Warm(r); HintFixture.Instructions = "[probe:AAAA]";
            r.SnippetFactory = text => { r.SpacingField.SetValue(r.GoodFont, float.NaN); return r.Text(text); };
        }, false, 1);
        Case("parser-mutates-current-glyph-scale", r =>
        {
            HintFixture.Instructions = "[probe:glyph]";
            r.SnippetFactory = _ => { r.GlyphScale = float.PositiveInfinity; return Activator.CreateInstance(r.GlyphType, new object[] { 0 })!; };
        }, false, 1);
        Case("empty-root", _ => HintFixture.Instructions = "");
        Case("null-root", _ => HintFixture.Instructions = null);
        Case("chat-gate", _ => HintFixture.drawingPlayerChat = true);
        Case("hint-display-gate", _ => HintFixture.ShowHints = false);
        Case("display-disabled", _ => HintFixture.GamepadDisableInstructionsDisplay = true);
        Case("composition-disables-display", _ => HintFixture.DisableDuringCompose = true);
        Case("composition-failure", _ => HintFixture.ThrowCompose = true);
        Case("first-font-access-error", _ => HintFixture.ThrowAssetAt = 1);
        Case("second-font-access-error", _ => HintFixture.ThrowAssetAt = 2);
        Case("null-font-asset", _ => HintFixture.MouseText = null!);
        Case("draw-error-original-no-finally", _ => HintFixture.ThrowDraw = true);
        Case("menu-zero", _ => { HintFixture.gameMenu = true; HintFixture.menuMode = 0; });
        Case("menu-nonzero", _ => { HintFixture.gameMenu = true; HintFixture.menuMode = 1; });
        foreach (int height in new[] { int.MinValue, 0, 35, 55, 67, 87, 720, int.MaxValue }) Case("height-" + height, _ => HintFixture.screenHeight = height);
        Case("null-font-plain", r => { r.Font = null; HintFixture.Instructions = "AAA"; }, false);
        Case("null-font-glyph", r => { r.Font = null; HintFixture.Instructions = "[g:0]"; }, false);
        Case("unready-font", r => { r.Font = r.NewFont(); HintFixture.Instructions = "AAA"; }, false);
        Case("font-subclass", r => { r.Font = Activator.CreateInstance(r.SubFontType, new object[] { 0f, 28, '*' }); HintFixture.Instructions = "AAA"; }, false);
        Case("null-culture", r => { r.ActiveCulture.SetValue(r.LanguageManager, null); HintFixture.Instructions = "AAA"; }, false);
        Case("null-culture-info", r => { r.CultureInfoField.SetValue(r.Culture, null); HintFixture.Instructions = "AAA"; }, false);
        Case("glyph-only-null-culture", r => { r.ActiveCulture.SetValue(r.LanguageManager, null); HintFixture.Instructions = "[g:0]"; }, true);
        Case("null-language-manager-plain", r => { r.SetLanguageManager(null); HintFixture.Instructions = "AAA"; }, false);
        Case("null-language-manager-glyph-only", r => { r.SetLanguageManager(null); HintFixture.Instructions = "[g:0]"; }, true);
        Case("null-lookup", r => { r.LookupField.SetValue(r.GoodFont, null); HintFixture.Instructions = "AAA"; }, false);
        Case("null-default-unused", r => { r.DefaultField.SetValue(r.GoodFont, null); HintFixture.Instructions = "AAA"; }, false);
        Case("null-default-used", r => { r.DefaultField.SetValue(r.GoodFont, null); HintFixture.Instructions = "\u0000"; }, false);
        Case("live-lookup-replacement-after-warm", r => { Warm(r); r.LookupField.SetValue(r.GoodFont, Array.CreateInstance(r.OriginalLookup.GetType().GetElementType()!, 1)); HintFixture.Instructions = "AAAA"; }, true);
        Case("live-default-poison-after-warm", r =>
        {
            Warm(r); r.SetKerning('*', 1, float.NaN);
            HintFixture.Instructions = "\u0000";
        }, false);
        foreach (int culture in new[] { 2, 4, 6, 7, 9, 10 })
            Case("active-culture-" + culture, r => { var c = r.Culture.GetType().GetMethod("FromLegacyId")!.Invoke(null, new object[] { culture }); r.ActiveCulture.SetValue(r.LanguageManager, c); HintFixture.Instructions = "A B 日本語"; }, true);
        foreach (string text in new[] { "A\nB\r\nC", "\r", "\n", "\u0000\ud800\uffff", "日本語 Ελληνικά العربية", new string('W', 4096), new string('W', 4097), new string('W', 8192) })
            Case("text-" + text.Length + "-" + Sha(Encoding.Unicode.GetBytes(text))[..8], _ => HintFixture.Instructions = text, text.Length <= 4096);
        for (int start = 0; start < 65536; start += 4096)
        {
            string text = new string(Enumerable.Range(start, 4096).Select(value => (char)value).ToArray());
            Case("all-utf16-block-" + start, _ => HintFixture.Instructions = text);
        }
        foreach (int count in new[] { 255, 256, 257 })
            Case("snippet-count-" + count, _ => HintFixture.Instructions = string.Concat(Enumerable.Repeat("[g:0]", count)), count <= 256);
        foreach (int total in new[] { 4096, 4097 })
            Case("combined-snippet-text-bound-" + total, _ => HintFixture.Instructions = "[probe:" + new string('A', 2048) + "][probe:" + new string('A', total - 2048) + "]", total <= 4096);
        foreach (float value in new[] { -1025f, -1024f, -0f, 0f, 1024f, 1025f, float.Epsilon, float.MaxValue, -float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            string bits = BitConverter.SingleToInt32Bits(value).ToString("x8");
            Case("live-spacing-" + bits, r => { Warm(r); r.SpacingField.SetValue(r.GoodFont, value); HintFixture.Instructions = "AAAA"; }, value >= -1024 && value <= 1024);
            for (int axis = 0; axis < 3; axis++) { int component = axis; Case("live-kerning-" + axis + "-" + bits, r => { Warm(r); r.SetKerning('A', component, value); HintFixture.Instructions = "AAAA"; }, value >= -1024 && value <= 1024); }
        }
        foreach (int value in new[] { -1, 0, 1024, 1025, int.MaxValue })
        {
            Case("live-line-" + value, r => { Warm(r); r.LineField.SetValue(r.GoodFont, value); HintFixture.Instructions = "A\nA"; }, value >= 0 && value <= 1024);
            Case("live-padding-" + value, r => { Warm(r); r.SetPadding('A', value); HintFixture.Instructions = "AAA"; }, value >= 0 && value <= 1024);
        }
        foreach (float value in new[] { -1f, -0f, 0f, 0.85f, 1f, 1.75f, 4f, 4.01f, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Case("glyph-scale-" + BitConverter.SingleToInt32Bits(value).ToString("x8"), r => { Warm(r); HintFixture.GlyphsScale = value; r.GlyphScale = value; HintFixture.Instructions = "[g:0]"; }, value >= 0 && value <= 4);
        var direct = DirectProof(before, after, Check);
        var allocations = AllocationProof(after, Check);
        var negatives = NegativeProof(candidateGame, candidateReLogic, originalGame, originalReLogic, fnaPath, metrics, handlers, beforeRoot, before, fna.MainModule, output, Check);
        var itemEffects = ItemProof(beforeGame.MainModule, afterGame.MainModule, before, after, beforeRoot, afterRoot, output, receipts, Check);
        var result = new
        {
            passed = true, checkCount = checks.Count, checks, cases, direct, allocations, negatives, itemEffects, receipts,
            inputs = new { originalGame = Sha(File.ReadAllBytes(originalGame)), candidateGame = Sha(File.ReadAllBytes(candidateGame)), originalReLogic = Sha(File.ReadAllBytes(originalReLogic)), candidateReLogic = Sha(File.ReadAllBytes(candidateReLogic)), fna = Sha(File.ReadAllBytes(fnaPath)) },
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            font = after.MetricsReceipt,
            scope = "Both actual emitted candidate assemblies execute directly in isolated AssemblyLoadContexts. Whole original/candidate root IL and six pinned FNA primitives execute in a source-qualified boundary fixture; root composition, Asset.Value, and final graphics draw are explicitly controlled edges. Actual parsing, mutable registration, snippet virtual dispatch, font lookup/wrapping, layout and aggregate arithmetic execute from the supplied pair; no template-only candidate is accepted.",
            limits = "No GPU rasterization, Switch AOT timing, hardware FPS or arbitrary races/runtime detours/resource-exhaustion claim. Fast helper intentionally returns finite zero, not the original measured size; equality is at the root's consumed zero-vector geometry. Fallback return bits/callback ordering are compared directly. Host allocation counts separate retained parsing from removed layout work."
        };
        Json(Path.Combine(output, "proof.json"), result);
        return result;
    }
    static void Warm(HintRuntime r)
    {
        HintFixture.Runtime = r; _ = r.Measure(r.GoodFont, "AAAA", new(1), -1);
        r.ParseCallbacks = r.UniqueCallbacks = r.MorphCallbacks = r.Mutation = 0; r.CallbackTrace.Clear();
    }
    internal sealed record Observation(bool Valid, string State, string? Error, string[] Trace, string[] CallbackTrace, int ParseCallbacks, int UniqueCallbacks, int MorphCallbacks, string Measured);
    static Observation Observe(HintRuntime runtime, Action root, Action<HintRuntime> configure)
    {
        runtime.Reset(); HintFixture.Reset(); configure(runtime); HintFixture.Font = runtime.Font; HintFixture.Runtime = runtime;
        Exception? error = null; try { root(); } catch (Exception e) { error = e; }
        string state = string.Join("|", HintFixture.DrawCalls, HintFixture.ComposeCalls, HintFixture.AllowExecutionOfGamepadInstructions, HintFixture.Effects, BitConverter.SingleToInt32Bits(HintFixture.GlyphsScale), HintFixture.DrawnPosition, HintFixture.DrawnOrigin, HintFixture.DrawnScale, BitConverter.SingleToInt32Bits(HintFixture.DrawnGlyphScale), HintFixture.DrawnText, HintFixture.DrawnWidth, HintFixture.DrawnSpread, HintFixture.AssetReads, HintFixture.MeasureCalls, runtime.ParseCallbacks, runtime.UniqueCallbacks, runtime.MorphCallbacks, runtime.Mutation);
        return new(!Invalid(error), state, Error(error), HintFixture.Trace.ToArray(), runtime.CallbackTrace.ToArray(), runtime.ParseCallbacks, runtime.UniqueCallbacks, runtime.MorphCallbacks, HintFixture.Measured.ToString());
    }
    static bool Invalid(Exception? e) => e != null && (e is InvalidProgramException or TypeLoadException or MissingMethodException or MissingFieldException or FileNotFoundException || Invalid(e.InnerException));
    static string? Error(Exception? e) => e == null ? null : e.GetType().FullName + (e.InnerException == null ? "" : ":" + Error(e.InnerException));
    static object DirectProof(HintRuntime before, HintRuntime after, Action<bool, string> check)
    {
        var rows = new List<object>();
        foreach (var scale in new[] { new HintVector(1), new HintVector(0), new HintVector(-1), new HintVector(0.85f), new HintVector(1.75f), new HintVector(float.NaN), new HintVector(float.PositiveInfinity), new HintVector(float.MaxValue), new HintVector(1, 2) })
            foreach (float width in new[] { -1f, 0f, 10f, float.PositiveInfinity, float.NaN })
            {
                before.Reset(); after.Reset(); HintFixture.Runtime = before;
                HintVector a = default, b = default; Exception? ea = null, eb = null;
                try { a = before.Measure(before.Font, "A B\nC", scale, width); } catch (Exception e) { ea = e; }
                HintFixture.Runtime = after;
                try { b = after.Measure(after.Font, "A B\nC", scale, width); } catch (Exception e) { eb = e; }
                bool safe = scale.X == 1 && scale.Y == 1 && width == -1;
                string name = "direct-scale-" + scale + "-width-" + BitConverter.SingleToInt32Bits(width).ToString("x8");
                check(!Invalid(ea) && !Invalid(eb), name + ":runtime-valid");
                check(Error(ea) == Error(eb) && (safe ? b.ToString() == new HintVector(0).ToString() && float.IsFinite(a.X) && float.IsFinite(a.Y) : a.ToString() == b.ToString()), name + ":exact-fallback-or-finite-zero");
                rows.Add(new { scale = scale.ToString(), width = width.ToString("R"), safe, original = a.ToString(), candidate = b.ToString(), error = Error(ea) });
            }
        after.Reset(); var empty = after.List(); var nullElement = after.List((object?)null);
        check(!after.Guard!(after.Font, null, 1, 1, -1), "null-parsed-list-rejected");
        check(!after.Guard!(after.Font, nullElement, 1, 1, -1), "null-parsed-element-rejected");
        check(after.Guard!(after.Font, empty, 1, 1, -1), "empty-list-safe");
        return rows;
    }
    static object ItemProof(ModuleDefinition original, ModuleDefinition candidate, HintRuntime before, HintRuntime after, Action beforeRoot, Action afterRoot, string output, List<object> receipts, Action<bool, string> check)
    {
        string fixture = "/build/hint52-investigation/tags/asset-proof/probe/Probe.dll";
        Require(Sha(File.ReadAllBytes(fixture)) == "0cb73bab16a26f399ba2fe8ebf7a2222b3b8294424ace60e254a9d2541cb7d4a", "prior explicit item boundary fixture changed");
        byte[] first = HintProbe.ItemFixture(original, fixture, receipts), second = HintProbe.ItemFixture(candidate, fixture, receipts);
        File.WriteAllBytes(Path.Combine(output, "original-item-effects.dll"), first);
        File.WriteAllBytes(Path.Combine(output, "candidate-item-effects.dll"), second);
        var leftBoundary = new HintItemBoundary(first); var rightBoundary = new HintItemBoundary(second);
        var rows = new List<object>();
        foreach (bool loaded in new[] { false, true }) foreach (bool dedicated in new[] { false, true })
        {
            void Configure(HintRuntime runtime, HintItemBoundary boundary)
            {
                boundary.Reset(loaded, dedicated);
                HintFixture.Instructions = "[probe:item-effect]";
                runtime.SnippetFactory = runtime.Stateful; runtime.UniqueHandled = true;
                runtime.UniqueEffect = () => { boundary.Execute(); runtime.CallbackTrace.Add("Item.Request:" + boundary.Requests); };
            }
            var a = Observe(before, beforeRoot, r => Configure(r, leftBoundary));
            var b = Observe(after, afterRoot, r => Configure(r, rightBoundary));
            int requests = !loaded && !dedicated ? 1 : 0;
            check(a.Valid && b.Valid && a.State == b.State && a.Error == b.Error && a.CallbackTrace.SequenceEqual(b.CallbackTrace), "exact-item-effect-fallback-equivalence-" + loaded + "-" + dedicated);
            check(leftBoundary.Requests == requests && rightBoundary.Requests == requests && a.ParseCallbacks == 1 && b.ParseCallbacks == 1, "legitimate-item-request-retained-" + loaded + "-" + dedicated);
            rows.Add(new { loaded, dedicated, requests, original = a, candidate = b });
        }
        after.Reset();
        object item = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(after.Game.GetType("Terraria.GameContent.UI.Chat.ItemTagHandler+ItemSnippet", true)!);
        after.SnippetType.GetField("Text")!.SetValue(item, "item");
        check(!after.Guard!(after.Font, after.List(item), 1, 1, -1), "actual-item-runtime-type-rejected-before-virtual-work");
        return new { rows, sourceFixtureSha256 = Sha(File.ReadAllBytes(fixture)), originalFixtureSha256 = Sha(first), candidateFixtureSha256 = Sha(second), initial = JsonSerializer.Deserialize<JsonElement>(rightBoundary.InitialReceipt), scope = "Actual current-pair ItemSnippet.UniqueDraw and Main.LoadItem bodies copied without dropped instructions into prior named item/asset/repository fixture. Their legitimate unloaded non-dedicated repository Request runs from a stateful parsed snippet's UniqueDraw during full same-list fallback. Actual ItemSnippet runtime identity is independently rejected. Item.SetDefaults, real GPU/item rendering and repository I/O are not simulated as executed production paths." };
    }

    static object AllocationProof(HintRuntime runtime, Action<bool, string> check)
    {
        runtime.Reset(); HintFixture.Runtime = runtime;
        const string text = "Press [g:0] to Jump";
        object snippets = runtime.ParseText(text);
        for (int i = 0; i < 2000; i++) { runtime.Guard!(runtime.Font, snippets, 1, 1, -1); runtime.FontGuard!(runtime.Font, "Press A"); runtime.MeasureCall(runtime.Font, text, 1, 1, -1); runtime.OriginalCall(runtime.Font, text, 1, 1, -1); }
        long Measure(int count, Action call) { long start = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < count; i++) call(); return GC.GetAllocatedBytesForCurrentThread() - start; }
        const int guardCalls = 100000, calls = 1000;
        long gameGuard = Measure(guardCalls, () => { if (!runtime.Guard!(runtime.Font, snippets, 1, 1, -1)) throw new Exception("lost common fast path"); });
        long fontGuard = Measure(guardCalls, () => { if (!runtime.FontGuard!(runtime.Font, "Press A")) throw new Exception("lost font predicate"); });
        object fallback = runtime.List(runtime.Stateful("custom"));
        for (int i = 0; i < 2000; i++) runtime.Guard!(runtime.Font, fallback, 1, 1, -1);
        long fallbackGuard = Measure(guardCalls, () => { if (runtime.Guard!(runtime.Font, fallback, 1, 1, -1)) throw new Exception("unsafe fallback shortcut"); });
        long parser = Measure(calls, () => runtime.ParseCall(text));
        long original = Measure(calls, () => runtime.OriginalCall(runtime.Font, text, 1, 1, -1));
        long candidate = Measure(calls, () => runtime.MeasureCall(runtime.Font, text, 1, 1, -1));
        long layout = Measure(calls, () => runtime.TypedCall(runtime.Font, snippets, 1, 1, -1));
        check(gameGuard == 0 && fontGuard == 0 && fallbackGuard == 0, "warmed-actual-game-and-font-guards-zero-allocation");
        check(parser > 0 && candidate == parser && original > candidate && layout == original - candidate, "retained-parse-and-removed-layout-allocation-separated");
        return new { guardCalls, gameGuardBytes = gameGuard, fontGuardBytes = fontGuard, fallbackGuardBytes = fallbackGuard, calls, retainedParserBytes = parser, candidateBytes = candidate, originalBytes = original, removedLayoutBytes = layout, tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), interpretation = "Warmed host allocation only; no FPS claim. Actual emitted helper retains original ParseMessage allocation; only layout allocation is omitted." };
    }
    static object NegativeProof(string candidateGame, string candidateFont, string originalGame, string originalFont, string fnaPath, string metrics, byte[] handlers, Action originalRoot, HintRuntime baseline, ModuleDefinition fna, string output, Action<bool, string> check)
    {
        var rows = new List<object>();
        using var resolver = HintPatcher.Resolver(originalGame);
        foreach (string kind in new[] { "guard-always-true", "font-guard-always-true", "fallback-reparses", "changed-root-position", "never-fast" })
        {
            using var game = AssemblyDefinition.ReadAssembly(candidateGame, new ReaderParameters { AssemblyResolver = resolver });
            using var font = AssemblyDefinition.ReadAssembly(candidateFont, new ReaderParameters { AssemblyResolver = resolver });
            var chat = Types(game.MainModule).Single(t => t.FullName == "Terraria.UI.Chat.ChatManager");
            void Boolean(MethodDefinition m, bool value) { m.Body = new Mono.Cecil.Cil.MethodBody(m); var il = m.Body.GetILProcessor(); il.Emit(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); }
            if (kind == "guard-always-true") Boolean(chat.Methods.Single(m => m.Name == "_NXHint52CanSkip"), true);
            if (kind == "never-fast") Boolean(chat.Methods.Single(m => m.Name == "_NXHint52CanSkip"), false);
            if (kind == "font-guard-always-true") Boolean(Types(font.MainModule).SelectMany(t => t.Methods).Single(m => m.Name == "_NXHint52MetricsSafe"), true);
            if (kind == "changed-root-position")
            {
                var row = Program.Target(game.MainModule).Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 12f); row.Operand = 13f;
            }
            if (kind == "fallback-reparses")
            {
                var method = chat.Methods.Single(m => m.Name == "_NXHint52Measure");
                var call = method.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "GetStringSize");
                var list = call.Previous.Previous.Previous;
                Require(list.OpCode.Code is Code.Ldloc_0 or Code.Ldloc or Code.Ldloc_S, "fallback mutation source shape changed");
                list.OpCode = OpCodes.Ldarg_1; list.Operand = null; call.Operand = Program.Method(game.MainModule, Program.SizeName);
            }
            string directory = Path.Combine(output, "negative-" + kind); Directory.CreateDirectory(directory);
            string gp = Path.Combine(directory, "Terraria.exe"), fp = Path.Combine(directory, "ReLogic.dll");
            File.WriteAllBytes(gp, HintProbe.Serialize(game)); File.WriteAllBytes(fp, HintProbe.Serialize(font));
            using var bad = new HintRuntime(gp, fp, fnaPath, metrics, handlers, true);
            var rootBytes = HintProbe.Roots(Program.Target(game.MainModule), Program.Target(game.MainModule), fna, new List<object>());
            var root = Assembly.Load(rootBytes).GetType("Probe.Root", true)!.GetMethod("Candidate")!.CreateDelegate<Action>();
            Action<HintRuntime> configure = kind == "font-guard-always-true" ? r => { r.SpacingField.SetValue(r.GoodFont, float.NaN); HintFixture.Instructions = "AAAA"; } : r => { HintFixture.Instructions = "[probe:state]"; r.SnippetFactory = r.Stateful; r.UniqueHandled = true; };
            bool rejected; string reason;
            if (kind == "never-fast")
            {
                bad.Reset(); object snippets = bad.ParseText("Press [g:0] to Jump"); rejected = !bad.Guard!(bad.Font, snippets, 1, 1, -1); reason = "positive common-case reachability contract";
            }
            else
            {
                var left = Observe(baseline, originalRoot, configure); var right = Observe(bad, root, configure);
                check(left.Valid && right.Valid, kind + ":semantic-negative-valid-il");
                rejected = left.State != right.State || left.Error != right.Error || !left.CallbackTrace.SequenceEqual(right.CallbackTrace); reason = kind == "fallback-reparses" ? "parser callback count and same-object effects" : kind == "font-guard-always-true" ? "nonfinite root position" : kind == "changed-root-position" ? "drawn root position" : "missing virtual snippet effects";
            }
            check(rejected, kind + ":rejected-by-semantics"); rows.Add(new { kind, rejected, reason, gameSha256 = Sha(File.ReadAllBytes(gp)), reLogicSha256 = Sha(File.ReadAllBytes(fp)) });
        }
        return rows;
    }
}
