using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Mono.Cecil;
using static Common;

internal static class RecipeProof
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static object Run(string baselinePath, string candidatePath, string fnaPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        byte[] baselineBytes = File.ReadAllBytes(baselinePath), candidateBytes = File.ReadAllBytes(candidatePath);
        using var baseline = AssemblyDefinition.ReadAssembly(new MemoryStream(baselineBytes));
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes));
        using var resolver = RecipePatcher.Resolver(baselinePath);
        using var regenerated = AssemblyDefinition.ReadAssembly(new MemoryStream(baselineBytes), new ReaderParameters { AssemblyResolver = resolver });
        var envelopes = InverseReceipts(regenerated.MainModule, candidate.MainModule.Types.Single(t => t.FullName == RecipePatcher.HelperName));
        Require(envelopes.Length == 14, "recipe behavior requires fourteen production envelopes and no wrappers");
        var inverse = envelopes.Select(r => RecipeAudit.Inverse(baseline.MainModule, candidate.MainModule, r)).ToArray();
        var receipts = new List<object>(); byte[] bytes = RecipeProbe.Build(baselinePath, candidatePath, receipts);
        var path = Path.Combine(outputDirectory, "Recipe55ExactBehaviorProbe.dll"); File.WriteAllBytes(path, bytes);
        var assembly = Assembly.Load(bytes); var runtime = assembly.GetType("Probe.Runtime", true)!;
        var session = new Session(assembly.GetType("Probe.Recipes", true)!, runtime);
        var recipes = session.Run(); var roots = BehaviorLarge.Run(assembly, runtime);
        Json(Path.Combine(outputDirectory, "clone-bindings.json"), receipts);
        var result = new { passed = true, proofVersion = 55, baselineSha256 = Sha(baselineBytes), candidateSha256 = Sha(candidateBytes), fnaSha256 = Sha(File.ReadAllBytes(fnaPath)), probeSha256 = Sha(bytes), executable = Path.GetFileName(path), inverse, recipes, roots,
            contract = "Exact serialized baseline/candidate UpdateRecipeList plus all five actual enveloped callees; AddToAvailableRecipes also copied whole. Candidate helper comes from RuntimeProbe actual emitted helper. All fourteen changed bodies are inverse-audited before execution. Receipts bind source-qualified instruction, parameter, field, control-flow and CLASS/VALUETYPE boundaries; every executable body is fingerprinted before/after serialization. Runtime state host is a separate declared deterministic snapshot fixture; recipe state lives in typed Recipe fixtures.",
            limits = new[] { "Nonempty full rendering/GPU/input/game simulation and Switch hardware/performance are not exercised.", "Recipe filter/environment/material, item property getters, RequiredItemEntry.Matches, inventory/chest/group/request collection internals are deterministic typed boundaries, not claimed game implementations. Their original call order, arguments, outcomes, mutations and thrown identity are exercised through the actual parent/callee IL.", "Reposition callvirt maps to a typed instance bridge that calls the selected full actual callee-body clone; null checks stay at callvirt, and receiver identity/previous focus are checked. Original exceptions are compared by exact injected object identity; generated CLR exception type/message is compared, not stack trace.", "Drawing/root bounded paths and untested paths are stated in roots. Observer snapshots are fixed host state; visual equality, native timing, allocation cost and representative workloads require separate hardware evidence." } };
        Json(Path.Combine(outputDirectory, "behavior.json"), result); return result;
    }
    static RecipePatcher.Receipt[] InverseReceipts(ModuleDefinition module, TypeDefinition runtime)
    {
        return RecipePatcher.Targets.Select(target => {
            var method = (MethodDefinition)module.LookupToken(target.Token);
            Require(method.Name == target.Name && method.Body.Instructions.Count == target.Instructions, "exact inverse source target");
            if (target.Kind != "flush") return RecipePatcher.Envelope(method, runtime, target.Scope, target.Kind);
            string beforeHash = Fingerprint(method);
            var instructions = method.Body.Instructions.ToArray();
            var call = instructions.Single(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt && i.Operand is MethodReference m && m.DeclaringType.FullName == "Microsoft.Xna.Framework.Game" && m.Name == "Run");
            method.Body.GetILProcessor().InsertAfter(call, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, runtime.Methods.Single(m => m.Name == "Flush")));
            return new RecipePatcher.Receipt(method.FullName, "flush", -1, instructions.Select(i => method.Body.Instructions.IndexOf(i)).ToArray(), method.Body.Variables.Count,
                method.Body.ExceptionHandlers.Count, method.Body.InitLocals, method.Body.MaxStackSize, instructions.Select((i, n) => (i, n)).Where(p => p.i.OpCode == Mono.Cecil.Cil.OpCodes.Ret).Select(p => p.n).ToArray(), beforeHash, Fingerprint(method));
        }).ToArray();
    }
    internal static object? Invoke(MethodInfo method, params object?[] arguments)
    {
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    sealed class Session
    {
        readonly Type hooks, runtime;
        readonly List<object> observations = new(), mutants = new(), observerFaults = new();
        readonly List<string> checks = new();
        internal Session(Type hooks, Type runtime) { this.hooks = hooks; this.runtime = runtime; }
        void Check(bool condition, string message) { Require(condition, "recipe behavior: " + message); checks.Add(message); }
        object? R(string name, params object?[] args) => Invoke(runtime.GetMethod(name, Flags)!, args);
        object? H(string prefix, string suffix, params object?[] args) => Invoke(hooks.GetMethod(prefix + suffix, Flags)!, args);
        object? Get(string name) => runtime.GetField(name, Flags)!.GetValue(null);
        void Set(string name, object? value) => runtime.GetField(name, Flags)!.SetValue(null, value);
        void Reset()
        {
            foreach (var field in runtime.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral && !f.IsInitOnly)) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
            RuntimeFixture.Reset(); RecipeFixture.Reset();
        }
        int Start()
        {
            R("InitializeTiming"); Set("SampleSelected", true); R("BeginSample"); int cookie = (int)R("Enter", 1)!;
            Check(cookie > 0 && (bool)Get("Active")!, "real observer activated"); return cookie;
        }
        long Calls(int metric)
        {
            object value = ((Array)Get("FrameMetrics")!).GetValue(metric)!;
            return (long)value.GetType().GetField("Calls", Flags)!.GetValue(value)!;
        }
        sealed record Outcome(string State, string? ErrorType, string? ErrorMessage, bool OriginalIdentity, string[] Events, int Focus, int Available, int[]? Recipes, [property: System.Text.Json.Serialization.JsonIgnore] float[]? Positions, int[]? MatchCopies)
        {
            public int[]? PositionBits => Positions?.Select(BitConverter.SingleToInt32Bits).ToArray();
        }
        Outcome Execute(string prefix, Action setup, string suffix = "Update", Func<object?[]>? arguments = null, int clockFaultOffset = 0)
        {
            Reset(); setup();
            RecipeCrafting.Reposition = (receiver, previous) => { Check(ReferenceEquals(receiver, RecipeMain.craftingUI), "reposition receiver identity"); H(prefix, "Reposition", receiver, previous); };
            int cookie = Start(); if (clockFaultOffset > 0) RuntimeFixture.ThrowClockAt = RuntimeFixture.ClockCalls + clockFaultOffset;
            Exception? error = null;
            try { H(prefix, suffix, arguments?.Invoke() ?? Array.Empty<object?>()); }
            catch (Exception caught) { error = caught; }
            Check(error is not InvalidProgramException && error is not TypeLoadException && error is not MissingMethodException && error is not MissingFieldException, "executable valid " + prefix + suffix);
            string state = RecipeFixture.Snapshot();
            var outcome = new Outcome(state, error?.GetType().FullName, error?.Message, ReferenceEquals(error, RecipeFixture.Failure), RecipeFixture.Events.ToArray(), RecipeMain.focusRecipe, RecipeMain.numAvailableRecipes,
                RecipeMain.availableRecipe?.ToArray(), RecipeCrafting.availableRecipeY?.ToArray(), RecipeMain.recipe?.Where(r => r?.requiredItemQuickLookup != null).SelectMany(r => r.requiredItemQuickLookup!.Select(e => e.MatchesCalls)).ToArray());
            if (clockFaultOffset == 0)
            {
                Check((int)Get("timingDepth")! == 2 && (bool)Get("Active")!, "all recipe scopes unwind " + prefix + suffix);
                if (prefix == "Patched")
                {
                    int metric = suffix switch { "Update" => 3, "Clear" => 4, "Collect" => 5, "Guide" => 6, "Refocus" => 7, "Reposition" => 8, _ => -1 };
                    Check(metric < 0 || Calls(metric) == (error == null ? 1 : 0), "completed call count " + suffix);
                    Check(error == null || (bool)Get("FrameScopeAborted")!, "original failure marks sample aborted " + suffix);
                    if (suffix == "Update" && error == null)
                    {
                        bool collectedPlayer = outcome.Events.Contains("LocalPlayer");
                        Check(Calls(4) == 1 && Calls(5) == (collectedPlayer ? 1 : 0) && Calls(6) == (collectedPlayer ? 0 : 1) && Calls(7) == 1 && Calls(8) == 1,
                            "actual five callee metric counts follow original branch");
                    }
                }
            }
            R("Exit", cookie, error == null); R("EndSample", error == null);
            Check(!(bool)Get("Active")! && (int)Get("timingDepth")! == 0, "no observer state leak " + prefix + suffix);
            if (clockFaultOffset > 0 && RuntimeFixture.ClockCalls >= RuntimeFixture.ThrowClockAt)
                Check((bool)Get("FrameTimingInvalid")! && (bool)Get("MeasurementInvalid")!, "observer fault sticky/discards sample");
            Check(RecipeFixture.AliasesIntact, "original reference arguments and null reasons preserved");
            return outcome;
        }
        static bool Equal(Outcome a, Outcome b) => a.State == b.State && a.ErrorType == b.ErrorType && a.ErrorMessage == b.ErrorMessage && a.OriginalIdentity == b.OriginalIdentity &&
            (a.PositionBits == null ? b.PositionBits == null : b.PositionBits != null && a.PositionBits.SequenceEqual(b.PositionBits));
        void Pair(string name, Action setup, Action<Outcome>? verify = null, string suffix = "Update", Func<object?[]>? arguments = null)
        {
            var before = Execute("Baseline", setup, suffix, arguments); var after = Execute("Patched", setup, suffix, arguments);
            Check(Equal(before, after), name + " baseline/candidate output, side-effect order and exception equality"); verify?.Invoke(after);
            observations.Add(new { scenario = name, method = suffix, before, after });
        }
        internal object Run()
        {
            static void None() { }
            Pair("ordinary-material-refresh", None, o => { Check(o.Available == 3 && o.Recipes!.Take(3).SequenceEqual(new[] { 0, 1, 2 }) && o.Focus == 1, "ordinary results/focus"); Check(o.Events.SequenceEqual(new[] { "IsAir:0", "LocalPlayer", "CollectItems:58:0", "Chests", "Groups", "Requests", "Filter", "Environment:1", "Material:1", "Environment:2", "Material:2", "Environment:3", "Material:3", "Reposition:1" }), "original ordinary predicate and collection ordering"); });
            Pair("filter-short-circuit", () => { RecipeCrafting.Filter = new(); RecipeCrafting.Filter.Rejected.Add(2); }, o => { Check(!o.Events.Contains("Environment:2") && !o.Events.Contains("Material:2") && o.Available == 2, "reject filter skips later predicates"); });
            Pair("environment-short-circuit", () => RecipeMain.recipe![1].Environment = false, o => Check(!o.Events.Contains("Material:2") && o.Available == 2, "reject environment skips material"));
            Pair("material-rejection", () => RecipeMain.recipe![1].Material = false, o => Check(o.Available == 2, "reject material omits add"));
            Pair("empty-recipe-bound", () => RecipeNode.maxRecipes = 0, o => Check(o.Available == 0 && o.Focus == 0, "zero recipe bound"));
            Pair("negative-recipe-bound", () => RecipeNode.maxRecipes = -1, o => Check(o.Available == 0 && o.Focus == 0, "negative recipe bound"));
            Pair("first-recipe-sentinel", () => RecipeMain.recipe![0].createItem!.type = 0, o => Check(o.Available == 0 && !o.Events.Any(e => e.StartsWith("Environment:")), "sentinel ends scan"));
            Pair("middle-recipe-sentinel", () => RecipeMain.recipe![1].createItem!.type = 0, o => Check(o.Available == 1 && !o.Events.Contains("Environment:3"), "middle sentinel ends scan"));
            Pair("guide-match", () => RecipeMain.guideItem = new() { type = 12, NameValue = "guide" }, o => { Check(o.Available == 1 && o.Recipes![0] == 1 && o.Focus == 0 && !o.Events.Contains("LocalPlayer"), "guide branch excludes normal collection"); Check(o.MatchCopies!.All(n => n == 0), "guide Matches byref mutates local copy not array entries"); Check(o.Positions!.SequenceEqual(new[] { 10f, 20f, 30f, 40f, 50f, 60f }), "reposition actual ref writes"); });
            Pair("guide-empty-name-normal", () => RecipeMain.guideItem = new() { type = 12, NameValue = "" }, o => Check(o.Events.Contains("LocalPlayer"), "empty guide name uses normal branch"));
            Pair("guide-null-name", () => RecipeMain.guideItem = new() { type = 12, NameValue = null }, o => Check(!o.Events.Contains("LocalPlayer"), "null not equal empty guide name"));
            Pair("guide-requirement-sentinel", () => { RecipeMain.guideItem = new() { type = 12, NameValue = "guide" }; RecipeMain.recipe![1].requiredItemQuickLookup![0].itemIdOrRecipeGroup = 0; }, o => Check(o.Available == 0, "zero requirement stops inner scan"));
            Pair("guide-late-requirement-match", () => { RecipeMain.guideItem = new() { type = 12, NameValue = "guide" }; RecipeMain.recipe![0].requiredItemQuickLookup = new[] { new RecipeRequired { itemIdOrRecipeGroup = 99 }, new RecipeRequired { itemIdOrRecipeGroup = 12 }, new RecipeRequired() }; }, o => Check(o.Available == 2, "late requirement matches once"));
            Pair("guide-no-requirements", () => { RecipeMain.guideItem = new() { type = 12, NameValue = "guide" }; RecipeNode.maxRequirements = 0; }, o => Check(o.Available == 0, "zero requirements bound"));
            Pair("callback-filter-mutates-environment", () => { RecipeCrafting.Filter = new(); RecipeFixture.Callback = e => { if (e == "Accepts:2") RecipeMain.recipe![1].Environment = false; }; }, o => Check(!o.Events.Contains("Material:2"), "same recipe alias visible after callback"));
            Pair("callback-material-mutates-next-recipe", () => RecipeFixture.Callback = e => { if (e == "Material:1") RecipeMain.recipe![1].createItem!.type = 0; }, o => Check(o.Available == 1, "scan observes next recipe mutation"));
            Pair("callback-chest-mutates-recipe-bound", () => RecipeFixture.Callback = e => { if (e == "Chests") RecipeNode.maxRecipes = 1; }, o => Check(o.Available == 1, "post-collection bound mutation preserved"));
            Pair("callback-reposition-mutates-shared-array", () => RecipeFixture.Callback = e => { if (e == "Reposition:1") RecipeCrafting.availableRecipeY![1] = 37; });
            foreach (string boundary in new[] { "IsAir:0", "LocalPlayer", "CollectItems:58:0", "Chests", "Groups", "Requests", "Filter", "Environment:2", "Material:2", "Reposition:1" })
                Pair("original-exception-" + boundary, () => RecipeFixture.ThrowAt = boundary, o => Check(o.OriginalIdentity, "original thrown object preserved " + boundary));
            Pair("original-filter-exception", () => { RecipeCrafting.Filter = new(); RecipeFixture.ThrowAt = "Accepts:2"; }, o => Check(o.OriginalIdentity, "filter exception identity"));
            Pair("original-guide-match-exception", () => { RecipeMain.guideItem = new() { type = 12, NameValue = "guide" }; RecipeFixture.ThrowAt = "Matches:12:12"; }, o => Check(o.OriginalIdentity, "guide exception identity"));
            var nullCases = new (string name, Action setup)[] {
                ("null-guide", () => RecipeMain.guideItem = null), ("null-player", () => RecipeMain.LocalPlayerValue = null), ("null-available", () => RecipeMain.availableRecipe = null),
                ("short-clear-array", () => RecipeMain.availableRecipe = new[] { 8, 1 }), ("out-of-range-focus", () => RecipeMain.focusRecipe = 99),
                ("null-owned-dictionary", () => RecipeNode._ownedItems = null), ("null-recipes", () => RecipeMain.recipe = null), ("null-recipe", () => RecipeMain.recipe![1] = null!),
                ("null-create-item", () => RecipeMain.recipe![1].createItem = null), ("null-crafting-receiver", () => RecipeMain.craftingUI = null),
                ("null-position-array", () => RecipeCrafting.availableRecipeY = null), ("short-position-array", () => RecipeCrafting.availableRecipeY = new[] { 1f }),
                ("null-guide-requirements", () => { RecipeMain.guideItem = new() { type = 12, NameValue = "g" }; RecipeMain.recipe![0].requiredItemQuickLookup = null; }) };
            foreach (var (name, setup) in nullCases) Pair(name, setup, o => Check(o.ErrorType != null, "original CLR failure " + name));
            Pair("direct-refocus-present", None, o => Check(o.Focus == 2, "focus finds existing index"), "Refocus", () => new object?[] { 9 });
            Pair("direct-refocus-missing-clamped", () => RecipeMain.focusRecipe = 99, o => Check(o.Focus == 2, "focus clamps missing high"), "Refocus", () => new object?[] { 12 });
            Pair("direct-refocus-negative-clamped", () => RecipeMain.focusRecipe = -2, o => Check(o.Focus == 0, "focus clamps negative"), "Refocus", () => new object?[] { 12 });
            Pair("direct-reposition-same-array-refwrites", None, o => Check(o.Positions!.SequenceEqual(new[] { -10f, 0f, 10f, 20f, 30f, 40f }), "reposition aliased element writes"), "Reposition", () => new object?[] { RecipeMain.craftingUI, 0 });
            Pair("direct-reposition-nan", () => RecipeCrafting.availableRecipeY![1] = float.NaN, o => Check(o.Positions!.All(float.IsNaN), "NaN arithmetic preserved"), "Reposition", () => new object?[] { RecipeMain.craftingUI, 0 });
            Pair("direct-clear-partial-state", None, o => Check(o.Available == 0 && o.Recipes!.SequenceEqual(new[] { 0, 0, 0, 7, 6, 5 }), "clear bounded writes"), "Clear");
            Pair("direct-collect", None, o => Check(o.Events.SequenceEqual(new[] { "CollectItems:58:0", "Chests", "Groups", "Requests" }), "collect original order and cleared dictionary"), "Collect", () => new object?[] { RecipeMain.LocalPlayerValue });
            var ordinary = Execute("Baseline", None);
            for (int offset = 1; offset <= 24; offset++)
            {
                var observed = Execute("Patched", None, clockFaultOffset: offset);
                Check(Equal(ordinary, observed), "observer clock fault cannot suppress original path " + offset);
                observerFaults.Add(new { clockOffset = offset, injected = RuntimeFixture.ClockCalls >= RuntimeFixture.ThrowClockAt, originalOutcomeEqual = true });
            }
            foreach (string mutant in RecipeProbe.Mutants)
            {
                Action setup = mutant switch {
                    "SkipFilter" or "MaterialBeforeFilter" => () => { RecipeCrafting.Filter = new(); RecipeCrafting.Filter.Rejected.Add(2); },
                    "WrongFocusArgument" or "LostRepositionWrite" => () => RecipeMain.guideItem = new() { type = 12, NameValue = "guide" },
                    "SwallowedOriginal" => () => RecipeFixture.ThrowAt = "Environment:2", _ => None };
                if (mutant == "WrongFocusArgument") setup = () => { RecipeMain.focusRecipe = 0; RecipeMain.availableRecipe![0] = 2; };
                var expected = Execute("Baseline", setup); var actual = Execute(mutant, setup);
                Check(!Equal(expected, actual), "meaningful mutant observed failure " + mutant);
                mutants.Add(new { mutant, rejected = true, expected, actual });
            }
            Check(observerFaults.Count >= 1 && observations.Count >= 40 && mutants.Count == RecipeProbe.Mutants.Length, "declared scenario coverage executed");
            return new { passed = true, scenarios = observations, observerFaults, mutants, checkCount = checks.Count, checksSha256 = Sha(Encoding.UTF8.GetBytes(string.Join("\n", checks))), checks,
                untested = new[] { "Production implementations behind typed predicate and collection boundaries", "Concurrent recipe mutation outside deterministic callbacks", "Unbounded recipe/requirement lengths and every floating point bit pattern", "Nonselected runtime sampling policy (covered by separate RuntimeProof), real rendering and hardware costs" } };
        }
    }
}
