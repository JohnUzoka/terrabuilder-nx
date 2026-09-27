using System.Collections;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using static Common;

internal static class UIProfileProof
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static object Run(string originalPath, string candidatePath, string fnaPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using var original = AssemblyDefinition.ReadAssembly(originalPath); using var candidate = AssemblyDefinition.ReadAssembly(candidatePath); using var fna = AssemblyDefinition.ReadAssembly(fnaPath);
        var receipts = new List<object>(); var checks = new List<string>();
        var bytes = UIProfileProbe.Build(original, candidate, fna, receipts);
        File.WriteAllBytes(Path.Combine(outputDirectory, "UIProfileExactProbe.dll"), bytes);
        var assembly = Assembly.Load(bytes); var hooks = assembly.GetType("Probe.Hooks", true)!; var runtime = assembly.GetType("Probe.UIRuntime", true)!;
        ProbeImages[assembly] = bytes; ExternalTargets.Clear();
        var session = new Session(runtime, hooks, checks);
        session.Behavior(); session.Generic(); session.Fonts();
        var mutants = new List<object>();
        foreach (var fault in new[] { "SkipCallback", "MissFinally", "MissReturn", "WrongScope", "FontSkip", "FontWrongScope", "FontReturn" })
        {
            var mutantBytes = UIProfileProbe.Build(original, candidate, fna, new(), fault);
            File.WriteAllBytes(Path.Combine(outputDirectory, "UIProfileMutant" + fault + ".dll"), mutantBytes);
            var mutantAssembly = Assembly.Load(mutantBytes);
            ProbeImages[mutantAssembly] = mutantBytes;
            var mutant = new Session(mutantAssembly.GetType("Probe.UIRuntime", true)!, mutantAssembly.GetType("Probe.Hooks", true)!, new());
            string rejection = "";
            try { if (fault.StartsWith("Font", StringComparison.Ordinal)) mutant.Fonts(); else mutant.Generic(); }
            catch (InvalidDataException error) { rejection = error.Message; }
            Require(rejection != "", "behavior proof accepted broken splice " + fault);
            checks.Add("reject-mutant:" + fault); mutants.Add(new { fault, rejected = true, reason = rejection, sha256 = Sha(mutantBytes) });
        }
        UIProfileFixture.CandidateSha256 = Sha(File.ReadAllBytes(candidatePath));
        var invariants = UIProfileRuntimeProof.Run(runtime, hooks, checks, outputDirectory);
        var manifestPath = Path.Combine(outputDirectory, "report-manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["candidateSha256"] = UIProfileFixture.CandidateSha256;
        File.WriteAllText(manifestPath, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        var result = new { passed = true, proofVersion = 51, originalSha256 = Sha(File.ReadAllBytes(originalPath)), candidateSha256 = UIProfileFixture.CandidateSha256, fnaSha256 = Sha(File.ReadAllBytes(fnaPath)), probeSha256 = Sha(bytes), checksSha256 = Sha(Encoding.UTF8.GetBytes(string.Join("\n", checks))), checkCount = checks.Count, checks, receipts, mutants, invariants,
            executionContract = "Full serialized original/candidate Main.Draw, DrawInterface, GameInterfaceLayer.Draw, LegacyGameInterfaceLayer.DrawSelf, DrawPendingMouseText, MouseText cache setter, Item.AffixName, Lang.GetPrefixedItemName, LocalizedText.Format, ChatManager.LayoutSnippets/GetStringSize and every iterator method, all actual emitted candidate helper/typed wrapper bodies. Generated exact helper is Probe.UIRuntime. Generic source envelopes use that same helper. Every listed source body is copied whole with branch/local/EH shape receipts and serialization fingerprint checks.",
            limits = "Game/graphics/font/state/time callees outside the listed full bodies are explicit deterministic host boundaries, not game/GPU implementations. DoDraw dispatch is a fixture boundary invoking the actual cloned DrawInterface. Observable enumerator disposal uses a typed list boundary. Native/private/ref-qualified original bindings and inherited50 render-gate preservation are independently required by static PatcherAudit, not host full-name rebinding. External exception identity is preserved; generated NullReferenceException stack frames differ. Not exhaustive game branch coverage, hardware timing, Switch allocation or visual proof." };
        Json(Path.Combine(outputDirectory, "ui-profile-proof.json"), result); return result;
    }
    sealed class Session
    {
        readonly Type runtime, hooks;
        readonly List<string> checks;
        internal Session(Type runtime, Type hooks, List<string> checks) { this.runtime = runtime; this.hooks = hooks; this.checks = checks; }
        void Check(bool condition, string name) { Require(condition, "ui51 behavior: " + name); checks.Add(name); }
        object? R(string name, params object?[] args) => InvokeMethod(runtime.GetMethod(name, Flags)!, null, args);
        object? H(string name, params object?[] args) => InvokeMethod(hooks.GetMethod(name, Flags)!, null, args);
        static object? InvokeMethod(MethodInfo method, object? target, object?[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException error) when (error.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        void Set(string name, object? value) => runtime.GetField(name, Flags)!.SetValue(null, value);
        void Reset()
        {
            foreach (var f in runtime.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral && !f.IsInitOnly)) f.SetValue(null, f.FieldType.IsValueType ? Activator.CreateInstance(f.FieldType) : null);
            UIProfileFixture.Reset();
        }
        int Start()
        {
            R("InitializeTiming"); Set("SampleSelected", true); R("BeginSample"); return (int)R("Enter", 1)!;
        }
        void Finish(int cookie)
        {
            R("Exit", cookie, true); R("EndSample", true);
        }
        long Calls(int id)
        {
            var metrics = (Array)runtime.GetField("FrameMetrics", Flags)!.GetValue(null)!;
            return (long)metrics.GetValue(id)!.GetType().GetField("Calls", Flags)!.GetValue(metrics.GetValue(id))!;
        }
        string Pair(string name, Func<string, object?> run, Action? validate = null)
        {
            string One(string prefix)
            {
                Reset(); object? value = null; Exception? thrown = null;
                try { value = run(prefix); } catch (Exception e) { thrown = e; }
                Check(thrown == null || thrown is IOException || thrown is FormatException && name == "localized-format:{bad}", name + ":no-unexpected-runtime-exception:" + prefix + ":" + thrown);
                if (thrown != null && thrown.GetType() == typeof(IOException)) Check(ReferenceEquals(thrown, UIProfileFixture.Failure), name + ":external-exception-identity:" + prefix);
                var state = $"{value}|{thrown?.GetType().FullName}|{ReferenceEquals(UIProfileFixture.Caught, UIProfileFixture.Failure)}|{UIProofMain.instance._isDrawingOrUpdating}|{UIProofMain.cursorOverride}|{UIProofMain.instance._mouseTextCache.noOverride}|{UIProfileFixture.LayerDisposals}|{UIProfileFixture.SnippetDisposals}";
                validate?.Invoke(); return state + "\n" + string.Join("\n", UIProfileFixture.Effects);
            }
            var before = One("Baseline"); var after = One("Patched"); Check(before == after, "original-candidate:" + name); return after;
        }
        internal void Behavior()
        {
            foreach (var variant in new[] { "normal", "false", "caught", "empty", "setup", "begin-throw", "end-throw" })
            {
                var trace = Pair("draw-interface:" + variant, prefix =>
                {
                    var main = UIProofMain.instance; main._needToSetupDrawInterfaceLayers = variant == "setup";
                    main._mouseTextCache = new() { isValid = true, noOverride = true, cursorText = "tooltip" };
                    if (variant != "empty")
                        for (int i = 0; i < 3; i++)
                        {
                            int index = i;
                            main._gameInterfaceLayers.Items.Add(new() { Name = i == 0 ? "Vanilla: Inventory" : i == 1 ? "Vanilla: Hotbar" : "Vanilla: Mouse Over", ScaleType = i,
                                _drawMethod = () => { UIProfileFixture.Effect("Layer:" + index); if (variant == "caught" && index == 1) throw UIProfileFixture.Failure; return variant != "false" || index != 1; } });
                        }
                    UIProfileFixture.ThrowEffect = variant == "begin-throw" ? "Batch.Begin:1" : variant == "end-throw" ? "Batch.End" : null;
                    R("InitializeTiming"); Set("SampleSelected", true); R("BeginSample");
                    try { return H(prefix + "Interface", main, new UIProofTime { Tag = 19 }); }
                    finally { R("EndSample", variant is not ("begin-throw" or "end-throw")); }
                }, () => Check(UIProfileFixture.LayerDisposals == 1, "interface-enumerator-disposed:" + variant));
                Check(variant is not ("false" or "caught") || !trace.Contains("Layer:2", StringComparison.Ordinal), "layer-false-stops:" + variant);
                Check(variant != "caught" || trace.Contains("DrawException", StringComparison.Ordinal), "layer-catch-preserved:" + variant);
            }
            foreach (var variant in new[] { "normal", "drawing-guard", "graphics-guard", "do-throw", "post-throw", "assets-throw" })
                Pair("main-draw:" + variant, prefix =>
                {
                    var main = UIProofMain.instance; main._isDrawingOrUpdating = variant == "drawing-guard"; UIProofMain.GraphicsAvailable = variant != "graphics-guard";
                    UIProofMain.DrawBody = (m, t) => H(prefix + "Interface", m, t);
                    UIProofMain.OnPostDraw = t => UIProfileFixture.Effect("Post:" + t.Tag);
                    UIProfileFixture.ThrowEffect = variant == "do-throw" ? "DoDraw:37" : variant == "post-throw" ? "Post:37" : variant == "assets-throw" ? "TransferAssets" : null;
                    return H(prefix + "Main", main, new UIProofTime { Tag = 37 });
                });
            foreach (var variant in new[] { "none", "valid", "map", "cursor-throw" })
                Pair("pending-tooltip:" + variant, prefix =>
                {
                    UIProofMain.instance._mouseTextCache = new() { isValid = variant != "none", noOverride = true, cursorText = "actual cached text" };
                    UIProofMain.mapFullscreen = variant == "map"; UIProfileFixture.ThrowEffect = variant == "cursor-throw" ? "Cursor" : null;
                    return H(prefix + "PendingTooltip");
                });
            foreach (bool locked in new[] { false, true })
                Pair("tooltip-cache-ref-write:" + locked, prefix =>
                {
                    var main = UIProofMain.instance; main._mouseTextCache.noOverride = locked;
                    H(prefix + "MouseText", main, "name", "buff", 6, (byte)2, 3, 100, 200, 800, 600, true);
                    var c = main._mouseTextCache; return $"{c.cursorText}:{c.buffTooltip}:{c.rare}:{c.diff}:{c.X}:{c.Y}:{c.hackedScreenWidth}:{c.hackedScreenHeight}:{c.isValid}:{c.noOverride}";
                });
            foreach (int rarity in new[] { -13, -12, -11, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 99 })
                foreach (byte difficulty in new byte[] { 0, 1, 2, 255 })
                    Pair("tooltip-color-value-return:" + rarity + ":" + difficulty, prefix => H(prefix + "TooltipColor", rarity, difficulty));
            foreach (byte prefixValue in new byte[] { 0, 1, 255 })
                foreach (bool variation in new[] { false, true })
                    Pair("item-affix:" + prefixValue + ":" + variation, prefix => { UIProofLanguage.Variations = variation; return H(prefix + "Affix", new UIProofItem { type = 7, prefix = prefixValue }); });
            foreach (string format in new[] { "value={0}", "{0:N2}", "{bad}" })
                Pair("localized-format:" + format, prefix => H(prefix + "Format", new UIProofText { Key = "format", Value = format }, 12.5));
            foreach (var variant in new[] { "complete", "dispose-early", "font-throw", "snippet-throw", "enumerator-throw" })
                Pair("deferred-layout:" + variant, prefix =>
                {
                    var snippets = new UIProofSnippets { Items = new[] { new UIProofSnippet { Text = "aa\nb" }, new UIProofSnippet { Text = "unique", Unique = true, UniqueSize = new(20, 13) }, new UIProofSnippet { Text = "end" } } };
                    var sequence = (IEnumerable<UIProofPositioned>)H(prefix + "Layout", new UIProofFont(), snippets, new UIProofVector(1, 2), 25f)!;
                    Check(UIProfileFixture.Effects.Count == 0, "layout-factory-is-deferred:" + prefix + ":" + variant);
                    UIProfileFixture.ThrowEffect = variant == "font-throw" ? "Font.Measure:aa" : variant == "snippet-throw" ? "UniqueDraw:aa\nb:True" : variant == "enumerator-throw" ? "Snippets.MoveNext" : null;
                    var values = new List<string>(); using (var it = sequence.GetEnumerator()) { while (it.MoveNext()) { values.Add(it.Current.ToString()); if (variant == "dispose-early") break; } }
                    return string.Join(";", values);
                }, () => Check(UIProfileFixture.SnippetDisposals == 1, "deferred-source-disposed:" + variant));
            Pair("layout-vector-return", prefix => H(prefix + "LayoutSize", new UIProofFont(), new UIProofSnippets { Items = new[] { new UIProofSnippet { Text = "aa\nb" } } }, new UIProofVector(2, 3), 80f));
        }
        delegate int RefOperation(ref int value, int mode);
        internal void Generic()
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                string One(string prefix)
                {
                    Reset(); int cookie = Start(); int argument = 5; object? value = null; Exception? thrown = null;
                    var callback = (RefOperation)hooks.GetMethod(prefix + "Generic", Flags)!.CreateDelegate(typeof(RefOperation));
                    try { value = callback(ref argument, mode); } catch (Exception e) { thrown = e; }
                    if (thrown != null && !ReferenceEquals(thrown, UIProfileFixture.Failure)) throw new InvalidOperationException("unexecutable generic probe, not a semantic rejection", thrown);
                    Finish(cookie);
                    if (prefix == "Patched") Check(mode == 2 ? Calls(9) == 0 : Calls(9) == 1, "generic-exact-scope:" + mode);
                    Check(thrown == null || ReferenceEquals(thrown, UIProfileFixture.Failure), "generic-exception-identity:" + prefix + ":" + mode);
                    Check(argument == 15, "generic-byref-finally-including-exception:" + prefix + ":" + mode);
                    return $"{value}|{argument}|{thrown != null}|{ReferenceEquals(UIProfileFixture.Caught, UIProfileFixture.Failure)}|" + string.Join(",", UIProfileFixture.Effects);
                }
                var before = One("Baseline"); var after = One("Patched"); Check(before == after, "generic-return-ref-existing-EH:" + mode);
            }
        }
        static object? Argument(Type type, int index)
        {
            if (type == typeof(string)) return "arg" + index;
            if (type == typeof(float)) return index + 0.25f;
            if (type == typeof(int)) return index + 3;
            if (type == typeof(UIProofVector)) return new UIProofVector(index + 1, index + 2);
            if (type == typeof(UIProofColor)) return new UIProofColor { R = (byte)(index + 10), G = 19, B = 27, A = 255 };
            if (type == typeof(UIProofDrawCharacter)) return (UIProofDrawCharacter)((b, p, c) => UIProfileFixture.Effect("DrawCharacter"));
            if (type == typeof(System.Globalization.CultureInfo)) return System.Globalization.CultureInfo.InvariantCulture;
            if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 2);
            return Activator.CreateInstance(type);
        }
        internal void Fonts()
        {
            var wrappers = runtime.GetMethods(Flags).Where(m => m.Name.StartsWith("Font", StringComparison.Ordinal)).OrderBy(m => m.Name).ToArray();
            Check(wrappers.Length == 8, "all-eight-external-font-signatures");
            foreach (var wrapper in wrappers)
            {
                var args = wrapper.GetParameters().Select((p, i) => Argument(p.ParameterType, i)).ToArray();
                var il = wrapper.GetMethodBody()!; Check(il.ExceptionHandlingClauses.Count > 0, "font-wrapper-finally:" + wrapper.Name);
                // Decode actual emitted call target rather than guessing overloads from names.
                if (!ExternalTargets.TryGetValue(wrapper.Name, out var external)) ExternalTargets.Add(wrapper.Name, external = UIProfileProofExternalTarget(wrapper));
                foreach (bool selected in new[] { false, true })
                {
                    string One(bool wrapped, bool throws, bool nullReceiver)
                    {
                        Reset(); int cookie = selected ? Start() : 0; var passed = args.ToArray();
                        if (nullReceiver) passed[0] = null;
                        object? result = null; Exception? failure = null;
                        if (throws)
                        {
                            // Establish exactly which external effect this typed target emits, then throw there.
                            InvokeMethod(external, external.IsStatic ? null : args[0], external.IsStatic ? args : args.Skip(1).ToArray());
                            UIProfileFixture.ThrowEffect = UIProfileFixture.Effects[0]; UIProfileFixture.Effects.Clear();
                        }
                        try { result = wrapped ? InvokeMethod(wrapper, null, passed) : InvokeMethod(external, external.IsStatic ? null : passed[0], external.IsStatic ? passed : passed.Skip(1).ToArray()); }
                        catch (Exception error) { failure = error is TargetException && nullReceiver ? new NullReferenceException() : error; }
                        if (selected) Finish(cookie);
                        if (failure != null && failure is not IOException && !(nullReceiver && failure is NullReferenceException)) throw new InvalidOperationException("unexecutable font probe, not a semantic rejection", failure);
                        Check(failure == null || failure is IOException || nullReceiver && failure is NullReferenceException, "font-no-unexpected-runtime-exception:" + wrapper.Name + ":" + failure);
                        if (selected && wrapped && failure == null) Check(Calls(external.DeclaringType == typeof(UIProofFontExtensions) || external.Name == "DrawCustomFast" ? 10 : 11) == 1, "font-exact-scope:" + wrapper.Name);
                        if (failure is IOException) Check(ReferenceEquals(failure, UIProfileFixture.Failure), "font-exception-identity:" + wrapper.Name);
                        return result + "|" + failure?.GetType().FullName + "|" + string.Join(";", UIProfileFixture.Effects);
                    }
                    foreach (bool throws in new[] { false, true }) Check(One(false, throws, false) == One(true, throws, false), "typed-wrapper-args-return-exception:" + wrapper.Name + ":" + selected + ":" + throws);
                    if (!external.IsStatic) Check(One(false, false, true) == One(true, false, true), "typed-wrapper-null-receiver:" + wrapper.Name + ":" + selected);
                }
            }
        }
        static MethodInfo UIProfileProofExternalTarget(MethodInfo wrapper)
        {
            // Cecil decodes exact serialized module metadata; reflection resolves its qualified token.
            using var module = ModuleDefinition.ReadModule(new MemoryStream(ProbeBytes(wrapper.Module.Assembly)));
            var definition = Types(module).Single(t => t.FullName == "Probe.UIRuntime").Methods.Single(m => m.Name == wrapper.Name);
            var reference = definition.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().First(m => m.DeclaringType.FullName is nameof(UIProofFont) or nameof(UIProofFontExtensions));
            return (MethodInfo)wrapper.Module.ResolveMethod(reference.MetadataToken.ToInt32())!;
        }
        static byte[] ProbeBytes(Assembly assembly) => UIProfileProof.ProbeImages[assembly];
    }
    internal static readonly Dictionary<Assembly, byte[]> ProbeImages = new();
    internal static readonly Dictionary<string, MethodInfo> ExternalTargets = new();
}
