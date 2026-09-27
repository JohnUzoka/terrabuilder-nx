using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using FA = Mono.Cecil.FieldAttributes;
using MA = Mono.Cecil.MethodAttributes;

internal static class UpdateBehaviorRoots
{
    const int Update = 0x06000e9b, Body = 0x06000e9e, World = 0x06000eae, Queue = 0x06000e9a,
        Input = 0x06000ec5, UI = 0x06000ea6, Game = 0x06000db5;
    const string Main = "Terraria.Main", Time = "Microsoft.Xna.Framework.GameTime", Keyboard = "Microsoft.Xna.Framework.Input.KeyboardState";
    static readonly Exception DisposeFailure = new IOException("update56 original Dispose failure");
    static readonly Exception RestoreFailure = new IOException("update56 original Restore failure");
    static readonly Exception DisplayFailure = new IOException("update56 original DisplayException failure");

    internal static void ConfigureProjection(AssemblyDefinition baseline, AssemblyDefinition candidate, ModuleDefinition module, TypeDefinition runtime, UpdateProbe.Mapper mapper)
    {
        var body = (MethodDefinition)baseline.MainModule.LookupToken(Body);
        var input = (MethodDefinition)baseline.MainModule.LookupToken(Input);
        var time = ((ByReferenceType)body.Parameters[0].ParameterType).ElementType;
        var timeHost = (TypeDefinition)mapper.Type(time);
        timeHost.Fields.Add(new FieldDefinition("_proofTotal", FA.Public, module.ImportReference(typeof(TimeSpan))));
        timeHost.Fields.Add(new FieldDefinition("_proofElapsed", FA.Public, module.ImportReference(typeof(TimeSpan))));
        var keyboard = (TypeDefinition)mapper.Type(input.Body.Variables[0].VariableType);
        keyboard.Fields.Add(new FieldDefinition("_proofKey", FA.Public, module.TypeSystem.Int32));
        var stopwatch = body.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>().First(f => f.Name == "fpsTimer").FieldType;
        mapper.FixtureType(stopwatch, false);
        var game = (MethodDefinition)baseline.MainModule.LookupToken(Game);
        var dispose = game.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Single(m => m.DeclaringType.FullName == "System.IDisposable");
        mapper.ImplementDisposable((TypeDefinition)mapper.Type(body.DeclaringType), dispose);
    }

    internal static void AddMutants(MethodDefinition source, MethodDefinition patched, TypeDefinition runtime, List<object> receipts, UpdateProbe.Mapper mapper)
    {
        int token = source.MetadataToken.ToInt32();
        string[] names = token switch
        {
            Update => new[] { "NoPreloadFlag" }, Body => new[] { "NoGameTimeReplacement", "NoPartySkyWorkaroundFlag" },
            World => new[] { "NoRandomDispose" }, Input => new[] { "NoOldKeyboardCopy" },
            UI => new[] { "NoCreativeUpdate" }, Queue => new[] { "NoActionInvoke" },
            Game => new[] { "NoGameDispose", "FlushOnCatch" }, _ => Array.Empty<string>()
        };
        foreach (string name in names)
        {
            var mutant = new MethodDefinition(name + token.ToString("x8"), MA.Public | MA.Static, patched.ReturnType);
            foreach (var p in patched.Parameters) mutant.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            patched.DeclaringType.Methods.Add(mutant); Copy(patched, mutant, t => t, m => m);
            Instruction MappedCall(string owner, string member)
            {
                var original = source.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().First(m => m.DeclaringType.FullName == owner && m.Name == member);
                string full = ((MethodReference)mapper.Member(original)).FullName;
                return mutant.Body.Instructions.Single(i => i.Operand is MethodReference m && m.FullName == full);
            }
            void Drop(Instruction instruction, int consumed)
            {
                instruction.OpCode = consumed == 0 ? OpCodes.Nop : OpCodes.Pop; instruction.Operand = null;
                for (int n = 1; n < consumed; n++) mutant.Body.GetILProcessor().InsertAfter(instruction, Instruction.Create(OpCodes.Pop));
            }
            switch (name)
            {
                case "NoPreloadFlag": Drop(mutant.Body.Instructions.Single(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "IsEnginePreloaded"), 1); break;
                case "NoGameTimeReplacement": Drop(mutant.Body.Instructions.Single(i => i.OpCode == OpCodes.Stind_Ref), 2); break;
                case "NoPartySkyWorkaroundFlag": Drop(mutant.Body.Instructions.Single(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "MultipleSkyWorkaroundFix"), 1); break;
                case "NoRandomDispose": case "NoGameDispose": Drop(MappedCall("System.IDisposable", "Dispose"), 1); break;
                case "NoOldKeyboardCopy": Drop(mutant.Body.Instructions.Single(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "oldKeyState"), 1); break;
                case "NoCreativeUpdate": Drop(MappedCall("Terraria.GameContent.Creative.CreativeUI", "Update"), 2); break;
                case "NoActionInvoke": Drop(MappedCall("System.Action", "Invoke"), 1); break;
                case "FlushOnCatch":
                    var flush = mutant.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType == runtime && m.Name == "Flush");
                    var flushMethod = (MethodReference)flush.Operand; Drop(flush, 0);
                    mutant.Body.GetILProcessor().InsertAfter(MappedCall("Terraria.Program", "DisplayException"), Instruction.Create(OpCodes.Call, flushMethod));
                    break;
            }
            Widen(mutant);
            receipts.Add(new { kind = "ActualRootSemanticMutant", mutation = name, source = source.FullName, token, fixture = mutant.FullName, mappedBody = Fingerprint(mutant) });
        }
    }

    internal static object Run(Assembly assembly, Type runtime) => new Session(assembly, runtime).Run();

    sealed class Session : UpdateBehaviorSession
    {
        readonly List<string> flow = new();
        readonly List<int> writes = new();
        readonly List<int> actionState = new();
        ConcurrentQueue<Action> actions = new();
        List<IEnumerator> delayed = new(), delayedInGame = new();
        object? initialTime, replacementTime, constructedGame, menuUI, gameUI;
        int focusCalls, weatherCalls, innerCalls, randomSeed, savedSeed, disposals, restores, logs, thirdParty, internalTicks;
        ulong unpausedSeed;
        string mode = "";
        object MainObject => Instance(Main);
        object TimeObject => initialTime!;
        internal Session(Assembly assembly, Type runtime) : base(assembly, runtime) { }

        protected override void Setup()
        {
            flow.Clear(); writes.Clear(); actionState.Clear(); actions = new(); delayed = new(); delayedInGame = new();
            focusCalls = weatherCalls = innerCalls = disposals = restores = logs = thirdParty = internalTicks = 0;
            randomSeed = savedSeed = 41; unpausedSeed = 101; replacementTime = constructedGame = menuUI = gameUI = null;
            initialTime = New(Time); Field(Time, "_proofTotal", TimeSpan.FromSeconds(7217), initialTime); Field(Time, "_proofElapsed", TimeSpan.FromSeconds(0.125), initialTime);
        }

        void Mark(string value) => flow.Add(value);
        void V(string owner, params string[] members)
        {
            foreach (string member in members) ConfigureMember(owner, member, _ => { Mark(owner + "." + member); return null; });
        }
        void SetMain(string field, object? value) => Field(Main, field, value);
        T ReadMain<T>(string field) => (T)ReadField(Main, field)!;
        string State() => string.Join("|", flow) + ";actions=" + string.Join(",", actionState) + ";queue=" + actions.Count +
            ";delayed=" + delayed.Count + "," + delayedInGame.Count + ";rng=" + randomSeed + ";seed=" + unpausedSeed +
            ";counts=" + string.Join(",", focusCalls, weatherCalls, innerCalls, disposals, restores, logs, thirdParty, internalTicks);
        void NoError(Outcome result) => Require(result.Error == null, "root unexpected original exception " + result.Error);
        void SameError(Outcome result, Exception expected) => Require(ReferenceEquals(result.Error, expected), "root original exception identity");
        void Has(string entry) => Require(flow.Contains(entry), "root missing callback " + entry);
        void Absent(string entry) => Require(!flow.Contains(entry), "root unexpected callback " + entry);
        void Ordered(params string[] expected)
        {
            int previous = -1;
            foreach (string value in expected)
            {
                int next = flow.FindIndex(previous + 1, s => s == value);
                Require(next > previous, "root callback order " + string.Join(" -> ", expected)); previous = next;
            }
        }

        void TimeBoundaries()
        {
            ConfigureMember(Time, "get_TotalGameTime", a => { Mark("time.total"); return ReadField(Time, "_proofTotal", a[0]); });
            ConfigureMember(Time, "get_ElapsedGameTime", a => { Mark("time.elapsed"); return ReadField(Time, "_proofElapsed", a[0]); });
            Configure("System.Void Microsoft.Xna.Framework.GameTime::.ctor(System.TimeSpan,System.TimeSpan)", a =>
            {
                replacementTime = a[0]; Field(Time, "_proofTotal", a[1], a[0]); Field(Time, "_proofElapsed", a[2], a[0]); Mark("time.replace"); return null;
            });
            ConfigureMember("Terraria.TimeLogger", "Start", _ => { Mark("time.start"); return Activator.CreateInstance(Host("Terraria.TimeLogger/StartTimestamp")); });
            Field("Terraria.TimeLogger", "TotalUpdate", Instance("Terraria.TimeLogger/TimeLogData"));
            Field("Terraria.TimeLogger", "UpdatesInWorld", Instance("Terraria.TimeLogger/TimeLogData"));
            ConfigureMember("Terraria.TimeLogger/TimeLogData", "AddTime", _ => { logs++; Mark("time.log"); return null; });
        }

        void InputBoundaries()
        {
            V("Terraria.GameInput.PlayerInput", "UpdateInput", "SetZoom_Unscaled", "CacheMousePositionForZoom", "SetZoom_MouseInWorld");
            V(Main, "UpdateViewZoomKeys");
            ConfigureMember("Terraria.UI.Gamepad.UILinkPointNavigator", "Update", _ => { Mark("navigator"); if (mode == "input-throws") throw UpdateFixture.Failure; return null; });
            ConfigureMember("Terraria.FocusHelper", "get_AllowInputProcessing", _ => { Mark("input.allowed"); return mode != "input-unfocused"; });
            ConfigureMember("Microsoft.Xna.Framework.Input.Keyboard", "GetState", _ => { Mark("keyboard.read"); var key = New(Keyboard); Field(Keyboard, "_proofKey", 29, key); return key; });
            var old = New(Keyboard); Field(Keyboard, "_proofKey", 3, old); SetMain("oldKeyState", old);
            var current = New(Keyboard); Field(Keyboard, "_proofKey", 17, current); SetMain("keyState", current);
        }
        void VerifyInput(Outcome result)
        {
            if (mode == "input-throws") { SameError(result, UpdateFixture.Failure); Require((int)ReadField(Keyboard, "_proofKey", ReadMain<object>("oldKeyState"))! == 3, "keyboard copy not reached after navigator throw"); Absent("keyboard.read"); return; }
            NoError(result);
            Require((int)ReadField(Keyboard, "_proofKey", ReadMain<object>("oldKeyState"))! == 17, "old keyboard copied before new read");
            Require((int)ReadField(Keyboard, "_proofKey", ReadMain<object>("keyState"))! == (mode == "input-unfocused" ? 0 : 29), "focused key state or unfocused initobj");
            Ordered("Terraria.GameInput.PlayerInput.UpdateInput", "Terraria.Main.UpdateViewZoomKeys", "Terraria.GameInput.PlayerInput.SetZoom_Unscaled", "navigator", "Terraria.GameInput.PlayerInput.CacheMousePositionForZoom", "Terraria.GameInput.PlayerInput.SetZoom_MouseInWorld", "input.allowed");
            if (mode == "input-unfocused") Absent("keyboard.read"); else Has("keyboard.read");
        }

        void UIBoundaries()
        {
            menuUI = New("Terraria.UI.UserInterface"); gameUI = New("Terraria.UI.UserInterface");
            SetMain("MenuUI", mode == "ui-no-menu" || mode == "ui-neither" ? null : menuUI);
            SetMain("InGameUI", mode == "ui-no-game" || mode == "ui-neither" ? null : gameUI);
            SetMain("CreativeMenu", Instance("Terraria.GameContent.Creative.CreativeUI")); SetMain("BigBossProgressBar", Instance("Terraria.GameContent.UI.BigProgressBar.BigProgressBarSystem"));
            ConfigureMember("Terraria.UI.UserInterface", "Update", a =>
            {
                string name = ReferenceEquals(a[0], menuUI) ? "ui.menu" : ReferenceEquals(a[0], gameUI) ? "ui.game" : throw new InvalidOperationException("unexpected UI receiver");
                Require(ReferenceEquals(a[1], replacementTime ?? TimeObject), "UI GameTime alias"); Mark(name); if (mode == name + "-throws") throw UpdateFixture.Failure; return null;
            });
            ConfigureMember("Terraria.GameContent.Creative.CreativeUI", "Update", a => { Require(ReferenceEquals(a[1], replacementTime ?? TimeObject), "creative time alias"); Mark("ui.creative"); if (mode == "ui.creative-throws") throw UpdateFixture.Failure; return null; });
            ConfigureMember("Terraria.GameContent.UI.NewCraftingUI", "UpdateUI", a => { Require(ReferenceEquals(a[0], replacementTime ?? TimeObject), "crafting time alias"); Mark("ui.crafting"); return null; });
            ConfigureMember("Terraria.GameContent.UI.BigProgressBar.BigProgressBarSystem", "Update", _ => { Mark("ui.boss"); return null; });
        }
        void VerifyUI(Outcome result)
        {
            string[] expected = new[] { "ui.menu", "ui.game", "ui.creative", "ui.crafting", "ui.boss" }
                .Where(x => !(x == "ui.menu" && (mode == "ui-no-menu" || mode == "ui-neither")) && !(x == "ui.game" && (mode == "ui-no-game" || mode == "ui-neither"))).ToArray();
            if (mode.EndsWith("-throws", StringComparison.Ordinal)) { SameError(result, UpdateFixture.Failure); expected = expected.Take(Array.IndexOf(expected, mode[..^7]) + 1).ToArray(); }
            else NoError(result);
            Require(flow.SequenceEqual(expected), "UI optional callbacks and abrupt completion order");
        }

        void QueueBoundaries()
        {
            SetMain("_mainThreadActions", actions);
            if (mode == "queue-empty") return;
            actions.Enqueue(() => { Mark("action.1"); actionState.Add(11); if (mode is "queue-throws" or "update-action-throws") throw UpdateFixture.Failure; if (mode == "queue-appends") actions.Enqueue(() => { Mark("action.3"); actionState.Add(33); }); });
            actions.Enqueue(() => { Mark("action.2"); actionState.Add(22); });
        }
        void VerifyQueue(Outcome result)
        {
            if (mode == "queue-throws") { SameError(result, UpdateFixture.Failure); Require(actions.Count == 1 && actionState.SequenceEqual(new[] { 11 }), "queue exception leaves later action queued"); }
            else { NoError(result); Require(actions.Count == 0 && actionState.SequenceEqual(mode == "queue-empty" ? Array.Empty<int>() : mode == "queue-appends" ? new[] { 11, 22, 33 } : new[] { 11, 22 }), "FIFO drain includes callback enqueue"); }
        }

        sealed class Scope : IDisposable
        {
            readonly Action dispose;
            internal Scope(Action dispose) { this.dispose = dispose; }
            public void Dispose() => dispose();
        }
        void WorldBoundaries(bool clock = true)
        {
            if (clock) TimeBoundaries();
            ConfigureMember("Terraria.Testing.RecordReplay", "RecordOrReplayInput", _ => { Mark("replay.capture"); if (mode == "replay-throws") throw UpdateFixture.Failure; return mode == "world-null" ? null : Instance("Terraria.Testing.StateSnapshot"); });
            ConfigureMember(Main, "SwapRandom", a =>
            {
                Require((string)a[0]! == "DoUpdateInWorld", "RNG scope name"); Mark("rng.swap");
                if (mode == "swap-throws") throw UpdateFixture.Failure;
                if (mode == "world-null") return null;
                savedSeed = randomSeed; randomSeed = 701;
                return new Scope(() => { Mark("rng.dispose"); disposals++; randomSeed = savedSeed; if (mode == "dispose-throws") throw DisposeFailure; });
            });
            ConfigureMember(Main, "DoUpdateInWorld_Inner", a =>
            {
                Require(ReferenceEquals(a[0], MainObject), "in-world receiver"); Mark("world.inner"); innerCalls++;
                randomSeed = unchecked(randomSeed * 17 + 3); actionState.Add(randomSeed);
                if (mode == "world-throws") throw UpdateFixture.Failure;
                return null;
            });
            ConfigureMember("Terraria.Testing.StateSnapshot", "Restore", _ => { Mark("replay.restore"); restores++; if (mode == "restore-throws") throw RestoreFailure; return null; });
        }
        void VerifyWorld(Outcome result)
        {
            if (mode == "world-throws" || mode == "swap-throws" || mode == "replay-throws") SameError(result, UpdateFixture.Failure);
            else if (mode == "dispose-throws") SameError(result, DisposeFailure);
            else if (mode == "restore-throws") SameError(result, RestoreFailure);
            else NoError(result);
            if (mode == "replay-throws") { Require(logs == 0 && restores == 0 && disposals == 0 && innerCalls == 0, "replay throw is outside original finally"); return; }
            if (mode == "swap-throws") { Require(logs == 1 && restores == 1 && disposals == 0 && innerCalls == 0, "swap failure restores snapshot and logs"); Ordered("rng.swap", "replay.restore", "time.log"); return; }
            Require(innerCalls == 1, "in-world inner executes once");
            if (mode == "world-null") { Require(disposals == 0 && restores == 0 && logs == 1 && randomSeed == 700, "null RNG scope and replay snapshot"); return; }
            Require(disposals == 1 && restores == 1 && randomSeed == 41 && logs == (mode == "restore-throws" ? 0 : 1), "nested finally cleanup and original exception precedence");
            Ordered("replay.capture", "rng.swap", "world.inner", "rng.dispose", "replay.restore");
            if (mode != "restore-throws") Ordered("replay.restore", "time.log");
        }

        sealed class Process : IEnumerator
        {
            readonly Func<bool> move;
            internal Process(Func<bool> move) { this.move = move; }
            public object Current => throw new InvalidOperationException("DoUpdate must not read coroutine Current");
            public bool MoveNext() => move();
            public void Reset() => throw new InvalidOperationException("DoUpdate must not reset coroutines");
        }
        void BodyBoundaries()
        {
            TimeBoundaries();
            SetMain("showSplash", mode == "splash"); SetMain("Chroma", Instance("ReLogic.Peripherals.RGB.ChromaEngine"));
            ConfigureMember("Terraria.FocusHelper", "UpdateFocus", a => { focusCalls++; Mark("focus"); a[0] = mode is "focus-pause" or "focus-menu"; return null; });
            V(Main, "UpdateAudio"); V("Terraria.Initializers.ChromaInitializer", "UpdateEvents"); V("ReLogic.Peripherals.RGB.ChromaEngine", "Update");
            if (mode == "splash") return;
            ConfigureMember(Main, "get_LocalPlayer", _ => Instance("Terraria.Player"));
            Field("Terraria.Player", "cursorItemIconReversed", true, Instance("Terraria.Player")); Field("Terraria.Player", "BlockInteractionWithProjectiles", 2);
            SetMain("mouseRightRelease", true); SetMain("mouseLeftRelease", true); SetMain("GlobalTimerPaused", false);
            V(Main, "UpdateCreativeGameModeOverride", "UpdateWorldPreparationState"); V("Terraria.GameInput.PlayerInput", "SetZoom_UI", "ResetInputsOnActiveStateChange");
            delayed.Add(new Process(() => { Mark("process.keep"); return true; })); delayed.Add(new Process(() => { Mark("process.remove"); return false; }));
            SetMain("DelayedProcesses", delayed); SetMain("DelayedProcessesInGame", delayedInGame);
            SetMain("gameMenu", mode is "menu" or "focus-menu"); SetMain("menuMode", 0); SetMain("CurrentInputTextTakerOverride", new object());
            SetMain("MenuUI", Instance("Terraria.UI.UserInterface")); V("Terraria.UI.UserInterface", "SetState"); V("Terraria.UI.IngameUIWindows", "CloseAll");
            bool dedicated = mode is "dedicated-world" or "pause";
            SetMain("dedServ", dedicated); SetMain("dedServFPS", false); SetMain("fpsTimer", Instance("System.Diagnostics.Stopwatch"));
            ConfigureMember("System.Diagnostics.Stopwatch", "get_IsRunning", _ => false);
            ConfigureMember("System.Diagnostics.Stopwatch", "get_ElapsedMilliseconds", _ => 0L);
            V("System.Diagnostics.Stopwatch", "Restart", "Stop");
            ConfigureMember(Main, "get_AchievementAdvisor", _ => Instance("Terraria.UI.AchievementAdvisor")); V("Terraria.UI.AchievementAdvisor", "Update");
            SetMain("OnTickForThirdPartySoftwareOnly", (Action)(() => { Mark("tick.third-party"); thirdParty++; }));
            SetMain("OnTickForInternalCodeOnly", (Action)(() => { Mark("tick.internal"); internalTicks++; }));
            SetMain("_hasPendingNetmodeChange", true); SetMain("_targetNetMode", dedicated ? 2 : 0); SetMain("netMode", 7);
            Field("Terraria.Graphics.Capture.CaptureManager", "Instance", Instance("Terraria.Graphics.Capture.CaptureManager"));
            ConfigureMember("Terraria.Graphics.Capture.CaptureManager", "get_IsCapturing", _ => { Mark("capture.test"); return mode == "capture"; });
            V("Terraria.Netplay", "UpdateInMainThread"); V(Main, "SetTitle", "UpdateSettingUnlocks", "DoUpdate_AutoSave"); V("Terraria.NPC", "UpdateProtectedSpawnSlots");
            SetMain("changeTheTitle", true); SetMain("mapFullscreen", true); Field("Terraria.WorldGen", "destroyObject", true);
            Field("Terraria.WorldGen", "generatingWorld", false); Field("Terraria.WorldGen", "isGeneratingOrLoadingWorld", false);
            SetMain("graphics", Instance("Microsoft.Xna.Framework.GraphicsDeviceManager"));
            V("Microsoft.Xna.Framework.Game", "set_IsFixedTimeStep", "set_InactiveSleepTime", "SuppressDraw"); V("Microsoft.Xna.Framework.GraphicsDeviceManager", "set_SynchronizeWithVerticalRetrace");
            SetMain("FrameSkipMode", Enum.ToObject(Host("Terraria.Enums.FrameSkipMode"), 0)); SetMain("ThrottleWhenInactive", false);
            SetMain("TARGET_FRAME_TIME", 0.25); SetMain("UpdateTimeAccumulator", mode == "throttle" ? 0.0 : 0.375); SetMain("instance", MainObject);
            Configure("T Terraria.Utils::Clamp<System.Double>(T,T,T)", a => Math.Clamp((double)a[0]!, (double)a[1]!, (double)a[2]!));
            V("Terraria.Testing.DebugVisualizer", "PreUpdate"); V(Main, "MouseOversClear", "TryPlayingCreditsRoll");
            UIBoundaries(); Exact("System.Void Terraria.Main::UpdateUIStates(Microsoft.Xna.Framework.GameTime)", UI);
            Field("Terraria.Graphics.Effects.Filters", "Scene", Instance("Terraria.Graphics.Effects.FilterManager")); V("Terraria.Graphics.Effects.FilterManager", "Update");
            Field("Terraria.Graphics.Effects.Overlays", "Scene", Instance("Terraria.Graphics.Effects.OverlayManager")); V("Terraria.Graphics.Effects.OverlayManager", "Update");
            Field("Terraria.GameContent.Liquid.LiquidRenderer", "Instance", Instance("Terraria.GameContent.Liquid.LiquidRenderer")); V("Terraria.GameContent.Liquid.LiquidRenderer", "Update");
            V("Terraria.UI.InGameNotificationsTracker", "Update"); V("Terraria.UI.ItemSlot", "UpdateInterface"); V("Terraria.GameContent.CraftingEffects", "Update");
            V(Main, "DoUpdate_AnimateBackgrounds", "UpdateOldNPCShop"); V("Terraria.Animation", "UpdateAll");
            SetMain("teamCooldown", 3); SetMain("reforgeCooldown", 4); SetMain("qaStyle", 1); SetMain("gfxQuality", 0.5f); Field("Terraria.Liquid", "maxLiquid", 100);
            V(Main, "UpdateMenu"); Field("Terraria.Graphics.Effects.SkyManager", "Instance", Instance("Terraria.Graphics.Effects.SkyManager")); V("Terraria.Graphics.Effects.SkyManager", "Update");
            V("Terraria.GameContent.UI.EmoteBubble", "UpdateAll");
            V(Main, "DoUpdate_AnimateCursorColors", "DoUpdate_AnimateTileGlows", "DoUpdate_AnimateDiscoRGB", "DoUpdate_AnimateVisualPlayerAura", "DoUpdate_AnimateWaterfalls", "DoUpdate_AnimateWalls", "AnimateTiles", "DoUpdate_AnimateItemIcons", "DoUpdate_F10_ToggleFPS", "DoUpdate_F9_ToggleLighting", "DoUpdate_F8_ToggleNetDiagnostics", "DoUpdate_F7_ToggleGraphicsDiagnostics", "DoUpdate_F11_ToggleUI", "DoUpdate_AltEnter_ToggleFullscreen", "DoUpdate_HandleChat", "DoUpdate_Enter_ToggleChat", "DoDebugFunctions");
            InputBoundaries(); Exact("System.Void Terraria.Main::DoUpdate_HandleInput()", Input);
            V(Main, "UpdateParticleSystems_UI", "CheckInvasionProgressDisplay", "UpdateWindyDayState", "TrySyncingMyPlayer");
            SetMain("timeForVisualEffects", 215999.0); SetMain("EverLastingTicker", 7UL);
            ConfigureMember(Main, "get_CanUpdateGameplay", _ => mode != "gameplay-gate");
            ConfigureMember(Main, "CanPauseGame", _ => { Mark("pause.test"); return mode == "pause"; }); V(Main, "DoUpdate_WhilePaused");
            delayedInGame.Add(new Process(() => { Mark("ingame.remove"); return false; }));
            SetMain("AmbienceServer", Instance("Terraria.GameContent.Ambience.AmbienceServer")); V("Terraria.GameContent.Ambience.AmbienceServer", "Update");
            Field("Terraria.WorldGen", "BackgroundsCache", Instance("Terraria.GameContent.BackgroundChangeFlashInfo")); V("Terraria.GameContent.BackgroundChangeFlashInfo", "UpdateFlashValues");
            SetMain("LocalGolfState", Instance("Terraria.GameContent.Golf.GolfState")); V("Terraria.GameContent.Golf.GolfState", "Update");
            ConfigureMember("Terraria.FocusHelper", "get_AllowRain", _ => true); SetMain("maxRaining", 0.5f); SetMain("cloudAlpha", 0.5f); V("Terraria.Rain", "MakeRain"); V(Main, "updateCloudLayer");
            SetMain("dayRate", 2); ConfigureMember(Main, "UpdateWeather", a => { Require((int)a[2]! == weatherCalls++, "weather bounded index order"); Require(ReferenceEquals(a[1], replacementTime ?? TimeObject), "weather time alias"); Mark("weather." + a[2]); return null; });
            ConfigureMember(Main, "get_UnpausedUpdateSeed", _ => { Mark("seed.get"); return unpausedSeed; });
            ConfigureMember("Terraria.Utils", "RandomNextSeed", a => { Require((ulong)a[0]! == 101, "RNG seed input"); Mark("seed.next"); return 202UL; });
            ConfigureMember(Main, "set_UnpausedUpdateSeed", a => { Mark("seed.set"); unpausedSeed = (ulong)a[0]!; return null; });
            V(Main, "Ambience"); SetMain("ignoreErrors", mode == "snow-swallowed");
            ConfigureMember(Main, "snowing", _ => { Mark("snow"); if (mode is "snow-swallowed" or "snow-rethrow") throw UpdateFixture.Failure; return null; });
            V("Terraria.GameContent.Events.Sandstorm", "EmitDust"); V("Terraria.Star", "UpdateStars"); V("Terraria.Cloud", "UpdateClouds"); SetMain("worldSurface", 10.0);
            V("Terraria.GameContent.PortalHelper", "UpdatePortalPoints"); V("Terraria.GameContent.LucyAxeMessage", "UpdateMessageCooldowns");
            ConfigureMember(Main, "ShouldUpdateEntities", _ => true); ConfigureMember("Terraria.Testing.WorldUpdateStepper", "ShouldUpdateWorld", _ => true);
            WorldBoundaries(false); Exact("System.Void Terraria.Main::DoUpdateInWorld()", World);
            SetMain("chatMonitor", Instance("Terraria.GameContent.UI.Chat.IChatMonitor")); V("Terraria.GameContent.UI.Chat.IChatMonitor", "Update");
            SetMain("ChromaPainter", Instance("Terraria.GameContent.ChromaHotkeyPainter")); V("Terraria.GameContent.ChromaHotkeyPainter", "Update");
        }

        void VerifyBody(Outcome result)
        {
            if (mode == "snow-rethrow") SameError(result, UpdateFixture.Failure); else NoError(result);
            Require(ReferenceEquals(ReadMain<object>("gameTimeCache"), TimeObject), "gameTimeCache retains original reference");
            if (mode == "splash") { Require(ReadMain<float>("GlobalTimeWrappedHourly") == 17 && focusCalls == 1 && logs == 0, "splash wrapped time and early return"); Absent("capture.test"); return; }
            Require(delayed.Count == 1 && (int)ReadField("Terraria.Player", "BlockInteractionWithProjectiles")! == 1 && !ReadMain<bool>("_hasPendingNetmodeChange"), "pre-return coroutine, interaction and netmode state");
            Ordered("process.remove", "process.keep", "tick.third-party", "capture.test");
            if (mode == "capture") { Require(focusCalls == 1 && logs == 0 && replacementTime == null, "capture early return"); Absent("Terraria.Netplay.UpdateInMainThread"); return; }
            if (mode == "throttle") { Require(ReadMain<double>("UpdateTimeAccumulator") == 0.125 && replacementTime == null && logs == 0, "insufficient-time early return"); Absent("ui.creative"); return; }
            bool dedicated = mode is "dedicated-world" or "pause";
            if (!dedicated)
            {
                Require(replacementTime != null && ReferenceEquals(result.Arguments[1], replacementTime), "DoUpdate rewrites ref GameTime");
                Require((TimeSpan)ReadField(Time, "_proofElapsed", replacementTime)! == TimeSpan.FromSeconds(0.25), "replacement GameTime target step");
                Require(ReadMain<double>("UpdateTimeAccumulator") == 0.25 && ReadMain<int>("teamCooldown") == 2 && ReadMain<int>("reforgeCooldown") == 3, "accumulator and cooldown effects");
                Ordered("time.replace", "ui.menu", "ui.game", "ui.creative", "ui.crafting", "ui.boss", "focus");
            }
            if (mode is "focus-pause" or "focus-menu") { Require(ReadMain<bool>("gamePaused") && !ReadMain<bool>("mouseLeftRelease") && !ReadMain<bool>("mouseRightRelease") && logs == 1, "focus paused state"); Absent("navigator"); if (mode == "focus-menu") Has("Terraria.Main.UpdateMenu"); return; }
            if (mode == "menu") { Require(logs == 1 && internalTicks == 0 && ReadMain<ulong>("EverLastingTicker") == 7, "menu returns before gameplay ticker"); Has("Terraria.Main.UpdateMenu"); return; }
            if (mode == "gameplay-gate") { Require(logs == 1 && ReadMain<ulong>("EverLastingTicker") == 8 && internalTicks == 0, "gameplay gate after ticker"); Absent("pause.test"); return; }
            if (mode == "pause") { Require(ReadMain<bool>("gamePaused") && logs == 1 && internalTicks == 0 && innerCalls == 0, "CanPauseGame original pause branch"); Has("Terraria.Main.DoUpdate_WhilePaused"); return; }
            Require(weatherCalls == 2 && unpausedSeed == 202 && internalTicks == 1 && delayedInGame.Count == 0, "bounded weather, seed and coroutine effects");
            Ordered("weather.0", "weather.1", "seed.get", "seed.next", "seed.set");
            if (mode == "snow-rethrow") { Require(innerCalls == 0 && logs == 0 && thirdParty == 1, "snow catch rethrows before world and final callbacks"); Absent("Terraria.GameContent.Events.Sandstorm.EmitDust"); return; }
            if (mode == "snow-swallowed") Ordered("snow", "Terraria.GameContent.Events.Sandstorm.EmitDust", "world.inner");
            Require(innerCalls == 1 && disposals == 1 && restores == 1 && logs == 2 && thirdParty == 2 && !ReadMain<bool>("gamePaused") && randomSeed == 41, "complete bounded update and nested cleanup");
        }

        void UpdateBoundaries()
        {
            string requested = mode; mode = requested == "update-ref" ? "focus-pause" : "splash"; BodyBoundaries(); mode = requested;
            SetMain("IsEnginePreloaded", requested == "update-preloaded");
            SetMain("OnEnginePreload", (Action)(() => { Mark("preload"); if (mode == "preload-throws") throw UpdateFixture.Failure; }));
            Field(Main, "_isDrawingOrUpdating", requested == "update-reentrant", MainObject);
            ConfigureMember("Terraria.Testing.DetailedFPS", "Begin", a => { Require(Convert.ToInt32(a[0]) == 1, "Update FPS operation category"); Mark("fps.begin"); return null; });
            V("Terraria.Testing.DetailedFPS", "End");
            Configure("System.Void Terraria.Main::DoUpdate(Microsoft.Xna.Framework.GameTime&)", a =>
            {
                Mark("body.call"); if (mode == "update-throws") throw UpdateFixture.Failure;
                return Call(Body, a);
            });
            Field("Terraria.Cinematics.CinematicManager", "Instance", Instance("Terraria.Cinematics.CinematicManager"));
            ConfigureMember("Terraria.Cinematics.CinematicManager", "Update", a => { Require(ReferenceEquals(a[1], replacementTime ?? TimeObject), "Update passes DoUpdate ref replacement to cinematic"); Mark("cinematic"); return null; });
            QueueBoundaries(); Exact("System.Void Terraria.Main::ConsumeAllMainThreadActions()", Queue);
            Configure("System.Void Microsoft.Xna.Framework.Game::Update(Microsoft.Xna.Framework.GameTime)", a => { Require(ReferenceEquals(a[1], replacementTime ?? TimeObject), "Update passes local ref replacement to Game.Update"); Mark("base.update"); return null; });
            SetMain("GameAskedToQuit", requested == "update-quit"); V(Main, "QuitGame");
        }
        void VerifyUpdate(Outcome result)
        {
            Require(ReadMain<bool>("IsEnginePreloaded"), "preload flag set before callback");
            if (mode == "preload-throws") { SameError(result, UpdateFixture.Failure); Absent("fps.begin"); return; }
            if (mode == "update-throws") { SameError(result, UpdateFixture.Failure); Require((bool)ReadField(Main, "_isDrawingOrUpdating", MainObject)!, "original update has no finally clearing reentry flag"); Absent("cinematic"); Absent("base.update"); return; }
            if (mode == "update-action-throws") { SameError(result, UpdateFixture.Failure); Require((bool)ReadField(Main, "_isDrawingOrUpdating", MainObject)! && actions.Count == 1 && actionState.SequenceEqual(new[] { 11 }), "queue failure preserves original Update flag and remaining action"); Ordered("cinematic", "action.1"); Absent("Terraria.Testing.DetailedFPS.End"); Absent("base.update"); return; }
            NoError(result);
            Require(ReferenceEquals(result.Arguments[1], TimeObject), "Update by-value argument remains original despite local ref replacement");
            if (mode == "update-reentrant") { Require((bool)ReadField(Main, "_isDrawingOrUpdating", MainObject)!, "reentrant original flag remains set"); Absent("body.call"); Absent("cinematic"); Has("base.update"); return; }
            Require(!(bool)ReadField(Main, "_isDrawingOrUpdating", MainObject)!, "normal update clears original reentry flag");
            Require(actionState.TakeLast(2).SequenceEqual(new[] { 11, 22 }) && actions.Count == 0, "Update drains actual main-thread queue after cinematic");
            Ordered("fps.begin", "body.call", "cinematic", "action.1", "action.2", "Terraria.Testing.DetailedFPS.End", "base.update");
            if (mode == "update-ref") Require(replacementTime != null, "nested DoUpdate ref replacement reached");
            if (mode == "update-quit") Ordered("base.update", "Terraria.Main.QuitGame"); else Absent("Terraria.Main.QuitGame");
            if (mode == "update-preloaded") Absent("preload"); else Has("preload");
        }

        void GameBoundaries()
        {
            Field("Terraria.Localization.LanguageManager", "Instance", Instance("Terraria.Localization.LanguageManager"));
            ConfigureMember("Terraria.Localization.GameCulture", "get_DefaultCulture", _ => Instance("Terraria.Localization.GameCulture"));
            V("Terraria.Localization.LanguageManager", "SetLanguage");
            ConfigureMember("ReLogic.OS.Platform", "get_IsOSX", _ => false); ConfigureMember("ReLogic.OS.Platform", "get_IsWindows", _ => false);
            Configure("System.Void Terraria.Main::.ctor()", a => { constructedGame = a[0]; Mark("game.ctor"); return null; });
            V("Terraria.Lang", "InitializeLegacyLocalization"); V("Terraria.Social.SocialAPI", "Initialize");
            ConfigureMember("Terraria.Initializers.LaunchInitializer", "LoadParameters", a => { Require(ReferenceEquals(a[0], constructedGame), "RunGame constructed receiver"); Mark("game.parameters"); return null; });
            ConfigureMember(Main, "add_OnEnginePreload", a => { Require(a[0] is Action, "RunGame preload delegate shape"); Mark("game.preload-subscribe"); return null; });
            SetMain("dedServ", mode is "game-dedicated" or "game-dedicated-throws");
            ConfigureMember(Main, "DedServ", _ => { Mark("Terraria.Main.DedServ"); if (mode == "game-dedicated-throws") throw UpdateFixture.Failure; return null; });
            ConfigureMember("Microsoft.Xna.Framework.Game", "Run", a =>
            {
                Require(ReferenceEquals(a[0], constructedGame), "Game.Run receiver identity"); Mark("game.run");
                if (mode is "game-run-throws" or "game-display-throws") throw UpdateFixture.Failure;
                return null;
            });
            ConfigureMember("Terraria.Program", "DisplayException", a => { Require(ReferenceEquals(a[0], UpdateFixture.Failure), "RunGame original catch identity"); Mark("game.display"); if (mode == "game-display-throws") throw DisplayFailure; return null; });
            Configure("System.Void System.IDisposable::Dispose()", a => { Require(ReferenceEquals(a[0], constructedGame), "RunGame original disposal receiver"); Mark("game.dispose"); disposals++; if (mode == "game-dispose-throws") throw DisposeFailure; return null; });
            int cookie = (int)R("FrameBegin")!; R("FrameEnd", cookie, true);
            Require(!(bool)Get("MeasurementInvalid")!, "RunGame fixture seeded valid actual observed update");
            RuntimeFixture.OnWrite = _ => writes.Add(flow.Count);
            if (mode == "game-writer-throws") RuntimeFixture.ThrowWriteAt = 1;
        }
        void VerifyGame(Outcome result)
        {
            if (mode == "game-display-throws") SameError(result, DisplayFailure);
            else if (mode == "game-dispose-throws") SameError(result, DisposeFailure);
            else NoError(result);
            Require(disposals == 1, "RunGame original finally disposes once");
            bool ran = mode != "game-dedicated-throws";
            if (ran) Has("game.run"); else { Absent("game.run"); Has("Terraria.Main.DedServ"); }
            if (mode == "game-dedicated") Ordered("Terraria.Main.DedServ", "game.run");
            bool catchPath = mode is "game-run-throws" or "game-display-throws" or "game-dedicated-throws";
            if (catchPath) Ordered(ran ? "game.run" : "Terraria.Main.DedServ", "game.display", "game.dispose"); else Absent("game.display");
            if (Prefix == "Baseline" || catchPath) Require(writes.Count == 0, "no Flush on original catch or baseline path");
            else
            {
                Require(writes.Count > 0, "serialized observer actually flushes nonempty window");
                int runIndex = flow.IndexOf("game.run"), disposalIndex = flow.IndexOf("game.dispose");
                Require(writes.All(position => position == runIndex + 1 && position == disposalIndex), "Flush exactly after successful Run before original finally");
            }
        }

        void Cases(int token, string[] names, Action configure, Func<object?[]> arguments, Action<Outcome> verify, bool root = false, bool flush = false)
        {
            foreach (string name in names) Pair(name, token, () => { mode = name; configure(); }, arguments, State, verify, root: root, flush: flush);
        }
        internal object Run()
        {
            Cases(Input, new[] { "input-focused", "input-unfocused", "input-throws" }, InputBoundaries, () => new[] { MainObject }, VerifyInput);
            Cases(UI, new[] { "ui-both", "ui-no-menu", "ui-no-game", "ui-neither", "ui.menu-throws", "ui.game-throws", "ui.creative-throws" }, UIBoundaries, () => new[] { TimeObject }, VerifyUI);
            Cases(Queue, new[] { "queue-empty", "queue-two", "queue-appends", "queue-throws" }, QueueBoundaries, () => Array.Empty<object?>(), VerifyQueue);
            Cases(World, new[] { "world-normal", "world-null", "world-throws", "swap-throws", "replay-throws", "dispose-throws", "restore-throws" }, () => WorldBoundaries(), () => new[] { MainObject }, VerifyWorld);
            Cases(Body, new[] { "splash", "capture", "throttle", "focus-pause", "focus-menu", "menu", "gameplay-gate", "pause", "dedicated-world", "snow-swallowed", "snow-rethrow" }, BodyBoundaries, () => new[] { MainObject, TimeObject }, VerifyBody);
            Cases(Update, new[] { "update-normal", "update-preloaded", "update-reentrant", "update-ref", "update-quit", "update-throws", "update-action-throws", "preload-throws" }, UpdateBoundaries, () => new[] { MainObject, TimeObject }, VerifyUpdate, root: true);
            Cases(Game, new[] { "game-normal", "game-dedicated", "game-dedicated-throws", "game-run-throws", "game-display-throws", "game-dispose-throws", "game-writer-throws" }, GameBoundaries, () => Array.Empty<object?>(), VerifyGame, flush: true);
            Negative("input observer cleanup", Input, () => { mode = "input-focused"; InputBoundaries(); }, () => new[] { MainObject }, State, VerifyInput);
            Negative("UI observer cleanup", UI, () => { mode = "ui-both"; UIBoundaries(); }, () => new[] { TimeObject }, State, VerifyUI);
            Negative("queue observer cleanup", Queue, () => { mode = "queue-two"; QueueBoundaries(); }, () => Array.Empty<object?>(), State, VerifyQueue);
            Negative("world observer cleanup", World, () => { mode = "world-normal"; WorldBoundaries(); }, () => new[] { MainObject }, State, VerifyWorld);
            Negative("body observer cleanup", Body, () => { mode = "splash"; BodyBoundaries(); }, () => new[] { MainObject, TimeObject }, State, VerifyBody);
            Negative("root observer cleanup", Update, () => { mode = "update-normal"; UpdateBoundaries(); }, () => new[] { MainObject, TimeObject }, State, VerifyUpdate, root: true);
            Negative("successful RunGame missing Flush", Game, () => { mode = "game-normal"; GameBoundaries(); }, () => Array.Empty<object?>(), State, VerifyGame, flush: true);
            SemanticNegative("keyboard copy removed", "NoOldKeyboardCopy", Input, () => { mode = "input-focused"; InputBoundaries(); }, () => new[] { MainObject }, State, VerifyInput);
            SemanticNegative("creative callback removed", "NoCreativeUpdate", UI, () => { mode = "ui-both"; UIBoundaries(); }, () => new[] { TimeObject }, State, VerifyUI);
            SemanticNegative("queue action removed", "NoActionInvoke", Queue, () => { mode = "queue-two"; QueueBoundaries(); }, () => Array.Empty<object?>(), State, VerifyQueue);
            SemanticNegative("random scope finally removed", "NoRandomDispose", World, () => { mode = "world-normal"; WorldBoundaries(); }, () => new[] { MainObject }, State, VerifyWorld);
            SemanticNegative("GameTime ref replacement removed", "NoGameTimeReplacement", Body, () => { mode = "focus-pause"; BodyBoundaries(); }, () => new[] { MainObject, TimeObject }, State, VerifyBody);
            SemanticNegative("boolean-only party-sky write removed", "NoPartySkyWorkaroundFlag", Body, () => { mode = "capture"; BodyBoundaries(); }, () => new[] { MainObject, TimeObject }, State, VerifyBody);
            SemanticNegative("preload flag removed", "NoPreloadFlag", Update, () => { mode = "update-normal"; UpdateBoundaries(); }, () => new[] { MainObject, TimeObject }, State, VerifyUpdate, root: true);
            SemanticNegative("RunGame dispose removed", "NoGameDispose", Game, () => { mode = "game-normal"; GameBoundaries(); }, () => Array.Empty<object?>(), State, VerifyGame, flush: true);
            SemanticNegative("Flush moved into original catch", "FlushOnCatch", Game, () => { mode = "game-run-throws"; GameBoundaries(); }, () => Array.Empty<object?>(), State, VerifyGame, flush: true);
            return new
            {
                passed = true, scenarios = Scenarios, mutants = Mutants, bounded = BoundarySummary,
                limitations = new[]
                {
                    "Seven unchanged actual baseline/candidate bodies; deterministic external callback boundaries are fail-closed. This is not whole-game execution.",
                    "DoUpdate exercises splash, capture, insufficient accumulator, focus pause/menu, menu return, gameplay gate, CanPauseGame, dedicated world and swallowed/rethrown snow exceptions. Weather has two iterations; each coroutine list has at most two entries.",
                    "In-world inner simulation, input devices, UI implementations, graphics, audio, network and game construction/run are explicit external boundaries. Actual nested measured input, UI, queue and in-world wrapper bodies are invoked from DoUpdate/Update where declared.",
                    "No OS event-handler registration branch, FPS-second rollover, shimmer star loop, coroutine reentrancy, or full rendering/simulation performance claim. Stopwatch is a deterministic typed host; TimeSpan and bounded BCL collections execute normally."
                }
            };
        }
    }
}
