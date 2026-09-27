using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class BehaviorRoots
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly (int Token, string Name)[] Targets = { (0x060010c3, "Draw"), (0x06000fba, "Interface"), (0x06000db5, "RunGame"), (0x06003a8d, "Crafting") };
    static string layerList = "", layerEnumerator = "";
    static readonly Exception DisplayFailure = new IOException("recipe55 DisplayException fixture failure");
    static readonly Exception DisposeFailure = new IOException("recipe55 Dispose fixture failure");

    internal static void Add(AssemblyDefinition baseline, AssemblyDefinition candidate, ModuleDefinition module, TypeDefinition runtime, List<object> receipts, BehaviorLarge.Mapper mapper)
    {
        var hooks = new TypeDefinition("Probe", "Roots", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
        var drawInterface = (MethodDefinition)baseline.MainModule.LookupToken(0x06000fba);
        var list = drawInterface.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>().Single(f => f.Name == "_gameInterfaceLayers").FieldType;
        var enumerator = drawInterface.Body.Variables[0].VariableType;
        layerList = list.FullName; layerEnumerator = enumerator.FullName;
        mapper.FixtureType(list, false);
        var enumHost = mapper.FixtureType(enumerator, true);
        var dispose = drawInterface.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Single(m => m.DeclaringType.FullName == "System.IDisposable");
        mapper.ImplementDisposable(enumHost, dispose);
        var main = (MethodDefinition)baseline.MainModule.LookupToken(0x060010c3);
        mapper.ImplementDisposable((TypeDefinition)mapper.Type(main.DeclaringType), dispose);
        foreach (var (token, name) in Targets)
        {
            var original = (MethodDefinition)baseline.MainModule.LookupToken(token);
            foreach (var (assembly, prefix) in new[] { (baseline, "Baseline"), (candidate, "Patched") })
            {
                var source = Types(assembly.MainModule).SelectMany(t => t.Methods).Single(m => m.FullName == original.FullName);
                var target = new MethodDefinition(prefix + name, MA.Public | MA.Static, mapper.Type(source.ReturnType));
                if (source.HasThis) target.Parameters.Add(new ParameterDefinition("receiver", Mono.Cecil.ParameterAttributes.None, mapper.Type(source.DeclaringType)));
                foreach (var parameter in source.Parameters) target.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, mapper.Type(parameter.ParameterType)));
                hooks.Methods.Add(target);
                Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)mapper.Type, (Func<object, object>)mapper.Member, source.HasThis ? 1 : 0);
                Widen(target);
                Require(source.Body.Instructions.Count == target.Body.Instructions.Count && source.Body.Variables.Count == target.Body.Variables.Count && source.Body.ExceptionHandlers.Count == target.Body.ExceptionHandlers.Count, "root whole-body shape " + source.FullName);
                BehaviorLarge.ValidateCopy(source, target, mapper);
                var bindings = source.Body.Instructions.Select((instruction, index) => new { instruction, index }).Where(x => x.instruction.Operand is MemberReference)
                    .Select(x => new { index = x.index, opcode = x.instruction.OpCode.Name, source = BehaviorLarge.Mapper.Qualified((MemberReference)x.instruction.Operand), fixture = BehaviorLarge.Mapper.Qualified((MemberReference)target.Body.Instructions[x.index].Operand) }).ToArray();
                receipts.Add(new { source = source.FullName, sourceMvid = source.Module.Mvid, sourceToken = source.MetadataToken.ToInt32(), sourceBody = Fingerprint(source), fixture = target.FullName, mappedBody = Fingerprint(target), instructions = source.Body.Instructions.Count, locals = source.Body.Variables.Count, handlers = source.Body.ExceptionHandlers.Count, kind = "ActualRootBody", bindings });
                if (prefix != "Patched") continue;
                foreach (string mutation in name == "RunGame" ? new[] { "MissingObserver", "FlushOnCatch", "MissingDispose" } : new[] { "MissingObserver" })
                {
                    var mutant = new MethodDefinition(mutation + name, MA.Public | MA.Static, target.ReturnType);
                    foreach (var parameter in target.Parameters) mutant.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
                    hooks.Methods.Add(mutant); Copy(target, mutant, t => t, m => m);
                    string observer = name == "Draw" ? "FrameEnd" : name == "RunGame" ? "Flush" : "Exit";
                    var call = mutant.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType == runtime && m.Name == observer);
                    if (mutation == "MissingDispose")
                    {
                        var originalDispose = mutant.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.IDisposable" && m.Name == "Dispose");
                        originalDispose.OpCode = OpCodes.Pop; originalDispose.Operand = null;
                    }
                    else if (mutation == "FlushOnCatch")
                    {
                        var flush = (MethodReference)call.Operand; call.OpCode = OpCodes.Nop; call.Operand = null;
                        var display = mutant.Body.Instructions.Single(i => i.Operand is MethodReference m && m.FullName == ((MethodReference)mapper.Member(source.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Single(m => m.Name == "DisplayException"))).FullName);
                        mutant.Body.GetILProcessor().InsertAfter(display, Instruction.Create(OpCodes.Call, flush));
                    }
                    else
                    {
                        call.OpCode = name == "RunGame" ? OpCodes.Nop : OpCodes.Pop; call.Operand = null;
                        if (name != "RunGame") mutant.Body.GetILProcessor().InsertAfter(call, Instruction.Create(OpCodes.Pop));
                    }
                    Widen(mutant);
                    receipts.Add(new { source = source.FullName, sourceMvid = source.Module.Mvid, sourceBody = Fingerprint(source), fixture = mutant.FullName, mappedBody = Fingerprint(mutant), instructions = mutant.Body.Instructions.Count, locals = mutant.Body.Variables.Count, handlers = mutant.Body.ExceptionHandlers.Count, kind = "Root" + mutation + "Mutant" });
                }
            }
        }
    }

    internal static object Run(Assembly assembly, Type runtime) => new Session(assembly, runtime).Run();

    sealed class Session
    {
        readonly Assembly assembly;
        readonly Type runtime, hooks;
        readonly Dictionary<string, object> instances = new(StringComparer.Ordinal);
        readonly List<object> scenarios = new(), mutants = new();
        readonly List<int> writePositions = new();
        int layerIndex, layerCalls, enumeratorDisposals, gameDisposals;
        object? constructedGame;
        string prefix = "", mode = "", root = "";
        internal Session(Assembly assembly, Type runtime) { this.assembly = assembly; this.runtime = runtime; hooks = assembly.GetType("Probe.Roots", true)!; }
        static object? Call(MethodInfo method, object?[] arguments)
        {
            try { return method.Invoke(null, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        object? R(string name, params object?[] arguments) => Call(runtime.GetMethod(name, Flags)!, arguments);
        object? Get(string name) => runtime.GetField(name, Flags)!.GetValue(null);
        void Set(string name, object? value) => runtime.GetField(name, Flags)!.SetValue(null, value);
        Type Host(string source) => assembly.GetType(BehaviorLarge.HostNames[source], true)!;
        object Instance(string source)
        {
            if (!instances.TryGetValue(source, out var value)) instances.Add(source, value = RuntimeHelpers.GetUninitializedObject(Host(source)));
            return value;
        }
        void Field(string type, string name, object? value, object? receiver = null) => Host(type).GetField(name, Flags)!.SetValue(receiver, value);
        object? ReadField(string type, string name, object? receiver = null) => Host(type).GetField(name, Flags)!.GetValue(receiver);
        void Configure(string signature, Func<object?[], object?> callback) => BehaviorLargeFixture.Configured.Add(BehaviorLarge.BoundaryNames[signature], callback);
        void ConfigureMember(string owner, string name, Func<object?[], object?> callback)
        {
            var matches = BehaviorLarge.BoundaryNames.Where(p => p.Key.Contains(" " + owner + "::" + name + "(", StringComparison.Ordinal)).ToArray();
            Require(matches.Length == 1, "root fixture signature ambiguity " + owner + "::" + name);
            BehaviorLargeFixture.Configured.Add(matches[0].Value, callback);
        }
        void Void(string owner, string name) => ConfigureMember(owner, name, _ => null);
        long Metric(int id)
        {
            var value = ((Array)Get("FrameMetrics")!).GetValue(id)!;
            return (long)value.GetType().GetField("Calls", Flags)!.GetValue(value)!;
        }
        long Window(string name)
        {
            var value = Get("WindowState")!;
            return (long)value.GetType().GetField(name, Flags)!.GetValue(value)!;
        }
        void Reset()
        {
            foreach (var field in runtime.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral && !f.IsInitOnly)) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
            foreach (var type in assembly.GetTypes().Where(t => t.Namespace == "Probe.LargeHost"))
                foreach (var field in type.GetFields(Flags).Where(f => f.IsStatic && !f.IsLiteral)) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
            RuntimeFixture.Reset(); BehaviorLargeFixture.Reset(); instances.Clear(); writePositions.Clear();
            layerIndex = -1; layerCalls = enumeratorDisposals = gameDisposals = 0; constructedGame = null;
            var main = Instance("Terraria.Main");
            Field("Terraria.Main", "_gameInterfaceLayers", Instance(layerList), main);
            Field("Terraria.Main", "spriteBatch", Instance("Microsoft.Xna.Framework.Graphics.SpriteBatch"));
            Field("Terraria.Main", "Assets", Instance("ReLogic.Content.IAssetRepository"));
            Field("Terraria.Testing.DebugVisualizer", "UI", Instance("Terraria.Testing.DebugVisualizer"));
            Field("Terraria.Main", "gameTimeCache", Instance("Microsoft.Xna.Framework.GameTime"));
            Field("Terraria.GameContent.UI.NewCraftingUI", "_ui", Instance("Terraria.UI.UserInterface"));
            Field("Terraria.Localization.LanguageManager", "Instance", Instance("Terraria.Localization.LanguageManager"));
            Configure("System.Void System.IDisposable::Dispose()", arguments =>
            {
                if (arguments[0]!.GetType() == Host(layerEnumerator)) enumeratorDisposals++;
                else
                {
                    Require(ReferenceEquals(arguments[0], constructedGame), "root dispose receiver identity"); gameDisposals++;
                    if (mode == "dispose-throws") throw DisposeFailure;
                }
                return null;
            });
        }
        void InterfaceBoundaries()
        {
            ConfigureMember("Terraria.Main", "SetupDrawInterfaceLayers", arguments => { Require(ReferenceEquals(arguments[0], Instance("Terraria.Main")), "root setup receiver"); Field("Terraria.Main", "_needToSetupDrawInterfaceLayers", false, arguments[0]); return null; });
            ConfigureMember(layerList, "GetEnumerator", arguments => { Require(ReferenceEquals(arguments[0], Instance(layerList)), "root enumeration receiver"); return Activator.CreateInstance(Host(layerEnumerator)); });
            ConfigureMember(layerEnumerator, "MoveNext", _ => ++layerIndex < (mode == "empty" ? 0 : 2));
            ConfigureMember(layerEnumerator, "get_Current", _ => { Require(layerIndex is >= 0 and < 2, "root enumeration index"); return Instance("Terraria.UI.GameInterfaceLayer"); });
            ConfigureMember("Terraria.UI.GameInterfaceLayer", "Draw", _ =>
            {
                layerCalls++;
                if (mode == "layer-throws") throw BehaviorLargeFixture.Failure;
                return mode != "layer-false";
            });
            Void("Terraria.UI.CoinSlot", "UpdateSlotAnims"); Void("Terraria.GameInput.PlayerInput", "SetZoom_UI");
            ConfigureMember("Terraria.Main", "get_UIScaleMatrix", _ => Activator.CreateInstance(Host("Microsoft.Xna.Framework.Matrix")));
            ConfigureMember("Microsoft.Xna.Framework.Graphics.SpriteBatch", "Begin", _ => null);
            Void("Terraria.Main", "DrawPendingMouseText"); Void("Microsoft.Xna.Framework.Graphics.SpriteBatch", "End");
            Void("Terraria.Testing.DebugVisualizer", "Draw"); Void("Terraria.GameInput.PlayerInput", "SetZoom_World");
            Field("Terraria.Main", "_needToSetupDrawInterfaceLayers", mode == "setup", Instance("Terraria.Main"));
        }
        void DrawBoundaries()
        {
            InterfaceBoundaries();
            Field("Terraria.Main", "_isDrawingOrUpdating", mode == "reentrant-return", Instance("Terraria.Main"));
            ConfigureMember("Terraria.Main", "get_IsGraphicsDeviceAvailable", _ => mode != "device-return");
            Void("Terraria.Testing.DetailedFPS", "Begin"); Void("Terraria.Main", "EnsureRenderTargetContent");
            ConfigureMember("Terraria.Main", "DoDraw", arguments =>
            {
                Require(ReferenceEquals(arguments[0], Instance("Terraria.Main")) && ReferenceEquals(arguments[1], Instance("Microsoft.Xna.Framework.GameTime")), "root DoDraw argument identity");
                if (mode == "original-throws") throw BehaviorLargeFixture.Failure;
                Call(hooks.GetMethod((prefix == "Baseline" ? "Baseline" : "Patched") + "Interface", Flags)!, arguments);
                return null;
            });
            Void("ReLogic.Content.IAssetRepository", "TransferCompletedAssets"); Void("Terraria.Testing.DetailedFPS", "End");
        }
        void CraftBoundaries()
        {
            ConfigureMember("Terraria.GameContent.UI.NewCraftingUI", "get_Visible", _ => mode != "invisible-return");
            Field("Terraria.Main", "inFancyUI", mode == "fancy-return");
            ConfigureMember("Terraria.UI.UserInterface", "Draw", arguments =>
            {
                Require(ReferenceEquals(arguments[0], Instance("Terraria.UI.UserInterface")) && ReferenceEquals(arguments[1], Instance("Microsoft.Xna.Framework.Graphics.SpriteBatch")) && ReferenceEquals(arguments[2], Instance("Microsoft.Xna.Framework.GameTime")), "root crafting argument identity");
                if (mode == "original-throws") throw BehaviorLargeFixture.Failure;
                return null;
            });
        }
        void GameBoundaries()
        {
            ConfigureMember("Terraria.Localization.GameCulture", "get_DefaultCulture", _ => Instance("Terraria.Localization.GameCulture"));
            ConfigureMember("Terraria.Localization.LanguageManager", "SetLanguage", arguments => { Require(ReferenceEquals(arguments[0], Instance("Terraria.Localization.LanguageManager")) && ReferenceEquals(arguments[1], Instance("Terraria.Localization.GameCulture")), "root language argument identity"); return null; });
            ConfigureMember("ReLogic.OS.Platform", "get_IsOSX", _ => false); ConfigureMember("ReLogic.OS.Platform", "get_IsWindows", _ => false);
            ConfigureMember("Terraria.Main", ".ctor", arguments => { constructedGame = arguments[0]; return null; });
            Void("Terraria.Lang", "InitializeLegacyLocalization"); Void("Terraria.Social.SocialAPI", "Initialize");
            ConfigureMember("Terraria.Initializers.LaunchInitializer", "LoadParameters", arguments => { Require(ReferenceEquals(arguments[0], constructedGame), "root LoadParameters receiver identity"); return null; });
            ConfigureMember("Terraria.Main", "add_OnEnginePreload", arguments => { Require(arguments[0] is Action, "root preload delegate signature"); return null; });
            Field("Terraria.Main", "dedServ", mode == "dedicated"); Void("Terraria.Main", "DedServ");
            ConfigureMember("Microsoft.Xna.Framework.Game", "Run", arguments =>
            {
                Require(ReferenceEquals(arguments[0], constructedGame), "root Game.Run receiver identity");
                if (mode is "run-throws" or "display-throws") throw BehaviorLargeFixture.Failure;
                return null;
            });
            ConfigureMember("Terraria.Program", "DisplayException", arguments =>
            {
                Require(ReferenceEquals(arguments[0], BehaviorLargeFixture.Failure), "root original catch exception identity");
                if (mode == "display-throws") throw DisplayFailure;
                return null;
            });
        }
        object?[] Arguments(string name) => name switch
        {
            "Draw" or "Interface" => new[] { Instance("Terraria.Main"), Instance("Microsoft.Xna.Framework.GameTime") },
            "Crafting" => new[] { Instance("Microsoft.Xna.Framework.Graphics.SpriteBatch") },
            _ => Array.Empty<object?>()
        };
        string State() => $"{ReadField("Terraria.Main", "_isDrawingOrUpdating", Instance("Terraria.Main"))}|{ReadField("Terraria.Main", "_needToSetupDrawInterfaceLayers", Instance("Terraria.Main"))}|{ReadField("Terraria.Main", "cursorOverride")}|{ReferenceEquals(ReadField("Terraria.Main", "_drawInterfaceGameTime"), Instance("Microsoft.Xna.Framework.GameTime"))}|{layerCalls}|{enumeratorDisposals}|{gameDisposals}";
        object? ExpectedFailure() => root == "RunGame" ? mode == "display-throws" ? DisplayFailure : mode == "dispose-throws" ? DisposeFailure : null : mode is "original-throws" or "layer-throws" ? BehaviorLargeFixture.Failure : null;
        (string Trace, int Boundaries, int Disposals, int Writes) One(string selectedPrefix, string selectedRoot, string selectedMode)
        {
            prefix = selectedPrefix; root = selectedRoot; mode = selectedMode; Reset();
            bool patched = prefix != "Baseline", observerFault = mode.StartsWith("observer-", StringComparison.Ordinal);
            if (root == "Draw") DrawBoundaries();
            else if (root == "Interface") InterfaceBoundaries();
            else if (root == "Crafting") CraftBoundaries();
            else GameBoundaries();
            int uiCookie = 0;
            if (root is "Interface" or "Crafting")
            {
                R("InitializeTiming"); Set("SampleSelected", true); R("BeginSample");
                if (root == "Crafting") uiCookie = (int)R("Enter", 1)!;
            }
            if (root == "RunGame")
            {
                int cookie = (int)R("FrameBegin")!; R("FrameEnd", cookie, true);
                Require(Window("Frames") == 1 && !(bool)Get("MeasurementInvalid")!, "root flush fixture seeded actual completed frame");
                RuntimeFixture.OnWrite = _ => writePositions.Add(BehaviorLargeFixture.Events.Count);
            }
            if (patched && mode == "observer-clock") RuntimeFixture.ThrowClockAt = RuntimeFixture.ClockCalls + 1;
            if (patched && mode == "observer-snapshot") RuntimeMain.ThrowSnapshotAt = 1;
            if (patched && mode == "observer-writer") RuntimeFixture.ThrowWriteAt = RuntimeFixture.WriteCalls + 1;
            BehaviorLargeFixture.Observe = (_, _) =>
            {
                if (!patched || observerFault) return;
                if (root == "Draw") Require((int)Get("FrameDepth")! == 1, "root actual frame observer active");
                if (root == "Interface") Require((bool)Get("Active")! && (int)Get("timingDepth")! == 2, "root actual UI observer active");
                if (root == "Crafting") Require((bool)Get("Active")! && (int)Get("timingDepth")! == 3, "root actual crafting observer active");
            };
            Exception? thrown = null;
            try { Call(hooks.GetMethod(prefix + root, Flags)!, Arguments(root)); }
            catch (Exception exception) { thrown = exception; }
            if (!ReferenceEquals(thrown, ExpectedFailure()))
                throw new InvalidDataException("root original result/exception identity " + root + "/" + mode + ": " + thrown + "\nObserved: " + string.Join("\n", BehaviorLargeFixture.Events));
            if (root == "Draw")
            {
                Require((int)Get("FrameDepth")! == 0 && !(bool)Get("Active")! && !(bool)Get("SampleSelected")!, "root frame finally cleanup");
                if (patched)
                {
                    Require((bool)Get("MeasurementInvalid")! == observerFault, "root observer fault contained");
                    if (!observerFault) Require(Window("Frames") == 1 && Window(mode == "original-throws" ? "Aborted" : "Completed") == 1, "root actual frame completion accounting");
                }
            }
            else if (root is "Interface" or "Crafting")
            {
                Require((int)Get("timingDepth")! == (root == "Crafting" ? 2 : 1), "root scope finally cleanup");
                if (root == "Crafting") R("Exit", uiCookie, true);
                R("EndSample", true);
                Require((int)Get("timingDepth")! == 0 && !(bool)Get("Active")!, "root no final timing leak");
                bool failure = ExpectedFailure() != null;
                Require((bool)R("ValidateSample")! == !(patched && (failure || observerFault)), "root sample validation reflects original/observer failure");
                Require(Metric(root == "Interface" ? 1 : 9) == (patched && !failure && !observerFault ? 1 : 0), "root completed observer calls");
            }
            else
            {
                Require(gameDisposals == 1, "root original IDisposable finally cleanup");
                bool returnedFromRun = mode is not "run-throws" and not "display-throws";
                bool expectedFlush = patched && returnedFromRun;
                int expectedWrites = expectedFlush ? observerFault ? 1 : 2 : 0;
                Require(RuntimeFixture.WriteCalls == expectedWrites && (long)Get("reportAttempts")! == (expectedFlush ? 1 : 0), "root Flush only after normal Game.Run return");
                Require((bool)Get("finalized")! == (expectedFlush && !observerFault), "root successful Flush state");
                if (expectedFlush)
                {
                    int runIndex = BehaviorLargeFixture.Events.FindIndex(e => e.Contains("::Run|", StringComparison.Ordinal));
                    int disposeIndex = BehaviorLargeFixture.Events.FindIndex(e => e.Contains("::Dispose|", StringComparison.Ordinal));
                    Require(writePositions.Count == expectedWrites && writePositions.All(position => position == runIndex + 1 && position <= disposeIndex), "root Run/Flush/Dispose ordering");
                }
                if (patched && observerFault) Require((bool)Get("MeasurementInvalid")! && (long)Get("reportFailures")! == 1, "root writer fault contained before original disposal");
                Require((int)Get("FrameDepth")! == 0 && !(bool)Get("Active")!, "root RunGame no active observer leak");
            }
            if (root == "Interface") Require(enumeratorDisposals == 1 && layerCalls == (mode == "empty" ? 0 : mode is "layer-false" or "layer-throws" ? 1 : 2), "root original enumerator finally/order");
            return (State() + "\n" + string.Join("\n", BehaviorLargeFixture.Events), BehaviorLargeFixture.Events.Count, enumeratorDisposals + gameDisposals, RuntimeFixture.WriteCalls);
        }
        internal object Run()
        {
            var cases = new[]
            {
                ("Draw", new[] { "normal", "reentrant-return", "device-return", "original-throws", "observer-clock", "observer-snapshot" }),
                ("Interface", new[] { "normal", "empty", "setup", "layer-false", "layer-throws", "observer-clock" }),
                ("Crafting", new[] { "normal", "invisible-return", "fancy-return", "original-throws", "observer-clock" }),
                ("RunGame", new[] { "normal", "dedicated", "run-throws", "display-throws", "dispose-throws", "observer-writer" })
            };
            foreach (var (name, modes) in cases)
                foreach (var variant in modes)
                {
                    var before = One("Baseline", name, variant); var after = One("Patched", name, variant);
                    Require(before.Trace == after.Trace, "root exact baseline/candidate boundary order/state " + name + "/" + variant);
                    scenarios.Add(new { method = name, scenario = variant, passed = true, exactExceptionIdentity = ExpectedFailure() != null, boundaries = before.Boundaries, originalDisposals = before.Disposals, candidateObserverWrites = after.Writes, trace = before.Trace });
                }
            foreach (var (mutation, name, variant, marker) in new[]
            {
                ("MissingObserver", "Draw", "original-throws", "root frame finally cleanup"),
                ("MissingObserver", "Interface", "layer-throws", "root scope finally cleanup"),
                ("MissingObserver", "Crafting", "original-throws", "root scope finally cleanup"),
                ("MissingObserver", "RunGame", "normal", "root Flush only after normal Game.Run return"),
                ("FlushOnCatch", "RunGame", "run-throws", "root Flush only after normal Game.Run return"),
                ("MissingDispose", "RunGame", "normal", "root original IDisposable finally cleanup")
            })
            {
                Exception? rejection = null;
                try { One(mutation, name, variant); } catch (InvalidDataException error) { rejection = error; }
                Require(rejection != null && rejection.Message.Contains(marker, StringComparison.Ordinal), "root mutant rejected by intended observable invariant " + mutation + name + ": " + rejection);
                mutants.Add(new { method = name, mutation, scenario = variant, rejected = true, oracle = marker });
            }
            Reset();
            return new { passed = true, scenarios, mutants, fixture = "All four actual baseline/candidate root bodies copied whole. Exact helper FrameBegin/FrameEnd/Enter/Exit/Flush executes. Game/graphics boundaries and layer list enumerator are typed deterministic fixtures; Dispose is an explicit observable interface implementation. Runtime observer faults use the supplied actual helper's clock/snapshot/writer fixture.", untested = new[] { "Real game construction, OS engine-load callbacks, server loop, renderer and graphics devices", "Actual layer-list implementation beyond its configured enumeration/Dispose contract", "Game branches outside listed scenarios, Switch/native behavior and performance" } };
        }
    }
}
