using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class UpdateBehaviorWorld
{
    const int Liquid = 0x06000269, Wiring = 0x060012f9, Counts = 0x060015aa,
        World = 0x060015ae, Prioritized = 0x060015af, Houses = 0x060015b0,
        Entities = 0x06004fcf, WorldWrapper = 0x06000eb9;
    const string Main = "Terraria.Main", Gen = "Terraria.WorldGen", Wire = "Terraria.Wiring",
        Water = "Terraria.Liquid", Buffer = "Terraria.LiquidBuffer", Tile = "Terraria.Tile",
        Entity = "Terraria.DataStructures.TileEntity", Player = "Terraria.Player", Npc = "Terraria.NPC",
        Random = "Terraria.Utilities.UnifiedRandom", Point = "Microsoft.Xna.Framework.Point",
        Rectangle = "Microsoft.Xna.Framework.Rectangle", Vector = "Microsoft.Xna.Framework.Vector2";
    static string entityList = "", entityEnumerator = "";
    static readonly Exception WorkFailure = new IOException("update56 bounded world callback failure");
    static readonly Exception DisposeFailure = new IOException("update56 bounded random restoration failure");

    internal static void ConfigureProjection(AssemblyDefinition baseline, AssemblyDefinition candidate,
        ModuleDefinition module, TypeDefinition runtime, UpdateProbe.Mapper mapper)
    {
        var entities = (MethodDefinition)baseline.MainModule.LookupToken(Entities);
        var list = entities.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>()
            .Single(f => f.Name == "UpdateEntities").FieldType;
        var enumerator = entities.Body.Variables[0].VariableType;
        entityList = list.FullName; entityEnumerator = enumerator.FullName;
        mapper.FixtureType(list, false);
        var enumHost = mapper.FixtureType(enumerator, true);
        mapper.ImplementDisposable(enumHost, entities.Body.Instructions.Select(i => i.Operand)
            .OfType<MethodReference>().Single(m => m.DeclaringType.FullName == "System.IDisposable"));

        // Geometry is an external boundary, but its by-value and by-ref values remain typed.
        // Populate the exact source fields so callbacks can assert the rectangle passed downstream.
        var housing = (MethodDefinition)baseline.MainModule.LookupToken(Houses);
        foreach (string name in new[] { Point, Rectangle, Vector })
        {
            var source = housing.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Select(m => m.ReturnType).First(t => t.FullName == name);
            foreach (var field in source.Resolve().Fields.Where(f => !f.IsStatic && !f.IsLiteral)) mapper.Member(field);
        }
    }

    internal static void AddMutants(MethodDefinition source, MethodDefinition patched, TypeDefinition runtime,
        List<object> receipts, UpdateProbe.Mapper mapper)
    {
        int token = source.MetadataToken.ToInt32();
        void Add(string prefix, Action<MethodDefinition> change)
        {
            var mutant = new MethodDefinition(prefix + token.ToString("x8"), Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, patched.ReturnType);
            foreach (var parameter in patched.Parameters) mutant.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
            patched.DeclaringType.Methods.Add(mutant); Copy(patched, mutant, t => t, m => m); change(mutant); Widen(mutant);
            receipts.Add(new { kind = "WorldSemanticMutant", mutation = prefix, source = source.FullName, sourceMvid = source.Module.Mvid,
                sourceToken = token, sourceBody = Fingerprint(source), fixture = mutant.FullName, mappedBody = Fingerprint(mutant) });
        }
        Instruction CallTo(MethodDefinition method, string owner, string name)
        {
            var original = source.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(m => m.DeclaringType.FullName == owner && m.Name == name).DistinctBy(m => m.FullName).Single();
            string mapped = ((MethodReference)mapper.Member(original)).FullName;
            return method.Body.Instructions.Single(i => i.Operand is MethodReference m && m.FullName == mapped);
        }
        switch (token)
        {
            case WorldWrapper:
                Add("WorldReorderedInvasion", m =>
                {
                    var world = CallTo(m, Gen, "UpdateWorld"); var invasion = CallTo(m, Main, "UpdateInvasion");
                    (world.Operand, invasion.Operand) = (invasion.Operand, world.Operand);
                });
                break;
            case World:
                Add("WorldReorderedMaintenance", m =>
                {
                    var wiring = CallTo(m, Wire, "UpdateMech"); var entities = CallTo(m, Entity, "PerformUpdates");
                    (wiring.Operand, entities.Operand) = (entities.Operand, wiring.Operand);
                });
                Add("WorldMissingRandomDispose", m =>
                {
                    var dispose = CallTo(m, "System.IDisposable", "Dispose"); dispose.OpCode = OpCodes.Pop; dispose.Operand = null;
                });
                break;
            case Wiring:
                Add("WorldCooldownIncreases", m =>
                {
                    var write = m.Body.Instructions.Single(i => i.OpCode.Code == Code.Stsfld && i.Operand is FieldReference f && f.Name == "cannonCoolDown");
                    Require(write.Previous.OpCode.Code == Code.Sub, "cooldown mutant exact subtraction"); write.Previous.OpCode = OpCodes.Add;
                });
                break;
            case Counts:
                Add("WorldWrongSurfaceWeight", m => m.Body.Instructions.Single(i => i.OpCode.Code == Code.Ldc_I4_5).OpCode = OpCodes.Ldc_I4_4);
                break;
            case Entities:
                Add("WorldMissingEntityEnd", m => { var end = CallTo(m, Entity, "UpdateEnd"); end.OpCode = OpCodes.Nop; end.Operand = null; });
                Add("WorldMissingEntityDispose", m =>
                {
                    var dispose = CallTo(m, "System.IDisposable", "Dispose");
                    Require(dispose.Previous.OpCode.Code == Code.Constrained, "entity disposal mutant constrained prefix");
                    dispose.Previous.OpCode = OpCodes.Nop; dispose.Previous.Operand = null; dispose.OpCode = OpCodes.Pop; dispose.Operand = null;
                });
                break;
            case Liquid:
                Add("WorldLiquidIgnoreNeverCleared", m =>
                {
                    var call = source.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().First(r => r.Name == "tilesIgnoreWater");
                    string mapped = ((MethodReference)mapper.Member(call)).FullName;
                    var last = m.Body.Instructions.Last(i => i.Operand is MethodReference r && r.FullName == mapped);
                    Require(last.Previous.OpCode.Code == Code.Ldc_I4_0, "liquid final ignore-water constant"); last.Previous.OpCode = OpCodes.Ldc_I4_1;
                });
                break;
            case Prioritized:
                Add("WorldReversedPrioritySentinel", m =>
                {
                    var load = m.Body.Instructions.First(i => i.Operand is FieldReference f && f.Name == "prioritizedTownNPCType");
                    var branch = load.Next.Next;
                    Require(branch.OpCode.Code is Code.Beq or Code.Beq_S, "priority sentinel original branch"); branch.OpCode = OpCodes.Bne_Un;
                });
                break;
            case Houses:
                Add("WorldHousingSolidNotRestored", m =>
                {
                    var restore = m.Body.Instructions.Last(i => i.OpCode.Code == Code.Stelem_I1).Previous;
                    Require(restore.OpCode.Code is Code.Ldloc or Code.Ldloc_S, "housing original solidity restoration");
                    restore.OpCode = OpCodes.Ldc_I4_1; restore.Operand = null;
                });
                break;
        }
    }

    internal static object Run(Assembly assembly, Type runtime) => new Session(assembly, runtime).Run();

    sealed class RandomScope : IDisposable
    {
        readonly Action dispose;
        internal RandomScope(Action dispose) { this.dispose = dispose; }
        public void Dispose() => dispose();
    }

    sealed class Session : UpdateBehaviorSession
    {
        readonly List<string> trace = new();
        readonly Dictionary<object, bool> activeTiles = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<object, bool> skippedTiles = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<object, string> labels = new(ReferenceEqualityComparer.Instance);
        object[] tileEntities = Array.Empty<object>();
        int entityIndex, entityUpdates, entityDisposals, randomDepth, randomDisposals, randomCalls, housingAttempts;
        bool ignoringWater;
        HashSet<int>? originalChanges, originalSpare;

        internal Session(Assembly assembly, Type runtime) : base(assembly, runtime) { }
        protected override void Setup()
        {
            trace.Clear(); activeTiles.Clear(); skippedTiles.Clear(); labels.Clear();
            tileEntities = Array.Empty<object>(); entityIndex = -1; entityUpdates = entityDisposals = 0;
            randomDepth = randomDisposals = randomCalls = housingAttempts = 0; ignoringWater = false;
            originalChanges = originalSpare = null;
        }
        int Int(string owner, string name, object? receiver = null) => Convert.ToInt32(ReadField(owner, name, receiver));
        bool Bool(string owner, string name, object? receiver = null) => (bool)ReadField(owner, name, receiver)!;
        string State() => string.Join(";", trace) + "|enum=" + entityIndex + "," + entityUpdates + "," + entityDisposals
            + "|random=" + randomDepth + "," + randomDisposals + "," + randomCalls + "|housing=" + housingAttempts
            + "|ignore=" + ignoringWater + "|skip=" + string.Join(",", skippedTiles.Select(p => labels[p.Key] + ":" + p.Value).OrderBy(x => x))
            + "|changes=" + SetContents("_netChangeSet") + "|spare=" + SetContents("_swapNetChangeSet");
        string SetContents(string name) => ReadField(Water, name) is HashSet<int> set ? string.Join(",", set.Order()) : "null";
        static object?[] Args() => Array.Empty<object?>();
        void Mark(string name) => trace.Add(name);
        void Expect(params string[] expected) => Require(trace.SequenceEqual(expected), "world callback sequence: " + string.Join(";", trace));
        void Error(Outcome outcome, Exception expected) => Require(ReferenceEquals(outcome.Error, expected), "world exception identity");
        void NoError(Outcome outcome) => Require(outcome.Error == null, "world unexpected exception " + outcome.Error);
        object Value(string owner, params (string Name, object Value)[] fields)
        {
            object value = New(owner);
            foreach (var field in fields) Field(owner, field.Name, field.Value, value);
            return value;
        }
        Array ArrayOf(string owner, int count, Func<int, object>? create = null)
        {
            var array = Array.CreateInstance(Host(owner), count);
            for (int i = 0; i < count; i++) array.SetValue(create == null ? New(owner) : create(i), i);
            return array;
        }
        Array Tiles(int width, int height)
        {
            var tiles = Array.CreateInstance(Host(Tile), width, height);
            Field(Main, "tile", tiles);
            return tiles;
        }
        object PutTile(Array tiles, int x, int y, int type = 0, int wall = 0, bool active = true)
        {
            object tile = Value(Tile, ("type", (ushort)type), ("wall", (ushort)wall));
            labels.Add(tile, x + "," + y); activeTiles.Add(tile, active); skippedTiles.Add(tile, false);
            tiles.SetValue(tile, x, y); return tile;
        }
        void TileActivity() => Configure("System.Boolean Terraria.Tile::active()", a => activeTiles.TryGetValue(a[0]!, out bool active) && active);
        void Marker(string owner, string method) => ConfigureMember(owner, method, _ => { Mark(method); return null; });

        internal object Run()
        {
            WrapperScenarios(); WorldScenarios(); WiringScenarios(); EntityScenarios(); CountScenarios();
            LiquidScenarios(); PriorityScenarios(); HousingScenarios();
            SemanticScenarios();
            return new
            {
                passed = true, scenarios = Scenarios, mutants = Mutants, bounded = BoundarySummary,
                limitations = new[]
                {
                    "All eight target bodies execute unchanged baseline/candidate IL; no game simulation algorithm is reimplemented.",
                    "World random tile callbacks, wiring effects, liquid element updates, housing geometry/spawn, network sends, creative powers, and stock TimeLogger are deterministic fail-closed external boundaries, not whole-game execution.",
                    "TileEntity list enumeration is a typed bounded external enumerator with independently checked Current/MoveNext/Dispose order; target foreach/catch/finally IL remains actual.",
                    "BCL arrays, integer arithmetic, Math.Round/IEEERemainder and HashSet Count/Clear execute normally. Liquid Swap mutates both by-ref arguments, and struct receiver changes are required to copy back.",
                    "Bounded worlds omit rare rain-created water and full per-tile growth algorithms; normal/remix scheduling, infection policy, guard, RNG restoration and child failures are exercised."
                }
            };
        }

        void SemanticScenarios()
        {
            SemanticNegative("world-mutant-invasion-reordered", "WorldReorderedInvasion", WorldWrapper,
                () => ConfigureWrapper(), () => new[] { Instance(Main) }, State, o => { NoError(o); Expect("start", "world", "invasion", "time"); });
            SemanticNegative("world-mutant-maintenance-reordered", "WorldReorderedMaintenance", World,
                () => ConfigureWorld(0), Args, State, o => { NoError(o); Expect("power", "UpdateMech", "PerformUpdates", "UpdateLunarApocalypse", "SpawnStormLightning", "rate"); });
            SemanticNegative("world-mutant-original-rng-finally-removed", "WorldMissingRandomDispose", World,
                () => ConfigureWorld(0, liquid: true, fault: "liquid"), Args, State,
                o => { Error(o, WorkFailure); Require(randomDepth == 0 && randomDisposals == 1, "semantic RNG restoration oracle"); });
            SemanticNegative("world-mutant-cooldown-increments", "WorldCooldownIncreases", Wiring,
                () => ConfigureWiring(new int[1], new int[1], new int[1], 0), Args, State,
                o => { NoError(o); Require(Int(Wire, "cannonCoolDown") == 1, "semantic cooldown oracle"); });
            SemanticNegative("world-mutant-surface-weight-changed", "WorldWrongSurfaceWeight", Counts,
                () => ConfigureCounts(true), () => new object?[] { 1 }, State,
                o => { NoError(o); Require(((int[])ReadField(Gen, "tileCounts")!)[1] == 10, "semantic weighted tile-count oracle"); });
            SemanticNegative("world-mutant-entity-end-removed", "WorldMissingEntityEnd", Entities,
                () => ConfigureEntities(0), Args, State,
                o => { NoError(o); Expect("UpdateStart", "enumerator", "next:0", "enum-dispose", "UpdateEnd"); });
            SemanticNegative("world-mutant-entity-finally-removed", "WorldMissingEntityDispose", Entities,
                () => ConfigureEntities(2, true), Args, State,
                o => { Error(o, WorkFailure); Require(entityDisposals == 1 && entityUpdates == 1, "semantic entity disposal oracle"); });
            SemanticNegative("world-mutant-liquid-ignore-not-cleared", "WorldLiquidIgnoreNeverCleared", Liquid,
                () => ConfigureLiquid(), Args, State, o => { NoError(o); Require(!ignoringWater, "semantic ignore-water restoration oracle"); });
            SemanticNegative("world-mutant-priority-sentinel-reversed", "WorldReversedPrioritySentinel", Prioritized,
                () => ConfigurePriority(true), Args, State, o => { NoError(o); Require(Int(Gen, "prioritizedTownNPCType") == 22, "semantic priority selection oracle"); });
            SemanticNegative("world-mutant-housing-solidity-not-restored", "WorldHousingSolidNotRestored", Houses,
                () => ConfigureHousing(2), Args, State, o => { NoError(o); Require(!((bool[])ReadField(Main, "tileSolid")!)[379], "semantic housing solidity oracle"); });
        }

        void ConfigureWrapper(bool ignoreErrors = false, string? fault = null)
        {
            Field(Main, "ignoreErrors", ignoreErrors);
            Field("Terraria.TimeLogger", "UpdateWorld", New("Terraria.TimeLogger/TimeLogData"));
            ConfigureMember("Terraria.TimeLogger", "Start", _ => { Mark("start"); return New("Terraria.TimeLogger/StartTimestamp"); });
            ConfigureMember(Gen, "UpdateWorld", _ => { Mark("world"); if (fault == "world") throw WorkFailure; return null; });
            ConfigureMember(Main, "UpdateInvasion", _ => { Mark("invasion"); if (fault == "invasion") throw WorkFailure; return null; });
            ConfigureMember("Terraria.TimeLogger/TimeLogData", "AddTime", _ => { Mark("time"); return null; });
        }
        void WrapperScenarios()
        {
            Pair("world-wrapper-client-bypass", WorldWrapper, () => { Field(Main, "netMode", 1); ConfigureWrapper(); }, () => new[] { Instance(Main) }, State,
                o => { NoError(o); Expect(); });
            Pair("world-wrapper-normal-order", WorldWrapper, () => ConfigureWrapper(), () => new[] { Instance(Main) }, State,
                o => { NoError(o); Expect("start", "world", "invasion", "time"); });
            foreach (string fault in new[] { "world", "invasion" })
                foreach (bool ignore in new[] { false, true })
                    Pair("world-wrapper-" + fault + "-" + (ignore ? "caught" : "rethrow"), WorldWrapper,
                        () => ConfigureWrapper(ignore, fault), () => new[] { Instance(Main) }, State, o =>
                        {
                            if (ignore) NoError(o); else Error(o, WorkFailure);
                            var expected = new List<string> { "start", "world" };
                            if (fault == "invasion") expected.Add("invasion");
                            if (ignore) expected.Add("time");
                            Expect(expected.ToArray());
                        });
            Negative("world-wrapper-missing-observer", WorldWrapper, () => ConfigureWrapper(), () => new[] { Instance(Main) }, State,
                o => { NoError(o); Expect("start", "world", "invasion", "time"); });
        }

        void ConfigureWorld(int rate, bool remix = false, bool liquid = false, string? fault = null, bool disposeFault = false)
        {
            Field(Main, "maxTilesX", 200); Field(Main, "maxTilesY", 100); Field(Main, "worldSurface", 40d);
            Field(Main, "remixWorld", remix); Field(Main, "getGoodWorld", remix);
            Field(Main, "rand", New(Random)); Field(Water, "skipCount", liquid ? 1 : 0);
            Field(Gen, "homelessSpawnTimeout", 2);
            Field("Terraria.GameContent.Creative.CreativePowerManager", "Instance", New("Terraria.GameContent.Creative.CreativePowerManager"));
            Configure("T Terraria.GameContent.Creative.CreativePowerManager::GetPower<Terraria.GameContent.Creative.CreativePowers/StopBiomeSpreadPower>()", _ =>
            {
                Mark("power"); return New("Terraria.GameContent.Creative.CreativePowers/StopBiomeSpreadPower");
            });
            ConfigureMember("Terraria.GameContent.Creative.CreativePowers/ASharedTogglePower", "GetIsUnlocked", _ => true);
            ConfigureMember("Terraria.GameContent.Creative.CreativePowers/ASharedTogglePower", "get_Enabled", _ => true);
            foreach (var member in new[] { (Wire, "UpdateMech"), (Entity, "PerformUpdates"), (Gen, "UpdateLunarApocalypse"),
                (Gen, "SpawnStormLightning"), (Gen, "UpdatePrioritizedTownNPC"), (Gen, "CheckForHousesNearAPlayer"), (Gen, "SpawnFallingObjects") })
                ConfigureMember(member.Item1, member.Item2, _ => { Mark(member.Item2); if (fault == member.Item2) throw WorkFailure; return null; });
            ConfigureMember(Gen, "CountTiles", a => { Mark("count:" + a[0]); return null; });
            ConfigureMember(Main, "SwapRandom", a =>
            {
                Require((string)a[0]! == "UpdateLiquid" && randomDepth == 0, "world RNG swap boundary");
                randomDepth++; Mark("swap");
                return new RandomScope(() => { randomDepth--; randomDisposals++; Mark("dispose"); if (disposeFault) throw DisposeFailure; });
            });
            ConfigureMember(Water, "UpdateLiquid", _ =>
            {
                Require(randomDepth == 1, "liquid runs inside swapped random scope"); Mark("liquid");
                if (fault == "liquid") throw WorkFailure; return null;
            });
            ConfigureMember(Gen, "GetWorldUpdateRate", _ => { Mark("rate"); return rate; });
            ConfigureMember(Main, "get_isThereAWorldSurface", _ => true);
            ConfigureMember(Main, "get_IsItRaining", _ => false);
            Configure("T Terraria.Utils::Clamp<System.Double>(T,T,T)", a => Math.Clamp((double)a[0]!, (double)a[1]!, (double)a[2]!));
            Configure("System.Double Terraria.Utils::Lerp(System.Double,System.Double,System.Double)", a => (double)a[0]! + ((double)a[1]! - (double)a[0]!) * (double)a[2]!);
            Configure("System.Int32 Terraria.Utilities.UnifiedRandom::Next(System.Int32)", a =>
            {
                Require((int)a[1]! == 15100, "bounded alchemy RNG range"); randomCalls++; Mark("rng:15100"); return 1;
            });
            ConfigureMember(Gen, "get_genRand", _ => ReadField(Main, "rand"));
            Configure("System.Int32 Terraria.Utilities.UnifiedRandom::Next(System.Int32,System.Int32)", a =>
            {
                int lo = (int)a[1]!, hi = (int)a[2]!;
                Require((lo == 10 && (hi == 190 || hi == 39)) || (lo == 39 && hi == 80), "bounded world tile RNG range");
                randomCalls++; Mark("rng:" + lo + ":" + hi); return lo;
            });
            ConfigureMember(Gen, "UpdateWorld_OvergroundTile", a =>
            {
                Require((int)a[0]! == 10 && (int)a[2]! == 3, "overground callback coordinates/rate");
                Mark("over:" + a[1] + ":" + Bool(Gen, "growGrassUnderground"));
                if (fault == "over") throw WorkFailure; return null;
            });
            ConfigureMember(Gen, "UpdateWorld_UndergroundTile", a =>
            {
                Require((int)a[0]! == 10 && (int)a[1]! == 39 && (int)a[2]! == 3, "underground callback coordinates/rate");
                Mark("under:39:" + Bool(Gen, "growGrassUnderground"));
                if (fault == "under") throw WorkFailure; return null;
            });
        }
        void WorldScenarios()
        {
            Pair("world-generation-guard", World, () => Field(Gen, "isGeneratingOrLoadingWorld", true), Args, State,
                o => { NoError(o); Expect(); Require(!Bool(Gen, "AllowedToSpreadInfections"), "guard bypasses infection write"); });
            Pair("world-zero-rate-still-maintains-children", World, () => ConfigureWorld(0), Args, State, o =>
            {
                NoError(o); Expect("power", "UpdateMech", "PerformUpdates", "UpdateLunarApocalypse", "SpawnStormLightning", "rate");
                Require(Int(Gen, "totalD") == 1 && Int(Water, "skipCount") == 1 && Int(Gen, "homelessSpawnTimeout") == 2,
                    "zero rate preserves housing timeout after earlier maintenance");
                Require(!Bool(Gen, "AllowedToSpreadInfections"), "creative infection policy");
            });
            Pair("world-client-count-bypass", World, () => { ConfigureWorld(0); Field(Main, "netMode", 1); Field(Gen, "totalD", 29); }, Args, State,
                o => { NoError(o); Require(Int(Gen, "totalD") == 29 && trace.All(t => !t.StartsWith("count:")), "client does not count tiles"); });
            Pair("world-column-wrap-and-liquid-scope", World, () =>
            {
                ConfigureWorld(0, liquid: true); Field(Gen, "totalD", 29); Field(Gen, "totalX", 199);
            }, Args, State, o =>
            {
                NoError(o); Expect("power", "UpdateMech", "PerformUpdates", "UpdateLunarApocalypse", "count:199", "swap", "liquid", "dispose", "SpawnStormLightning", "rate");
                Require(Int(Gen, "totalD") == 0 && Int(Gen, "totalX") == 0 && Int(Water, "skipCount") == 0 && randomDepth == 0 && randomDisposals == 1, "column and RNG scope state");
            });
            foreach (bool remix in new[] { false, true })
                Pair("world-" + (remix ? "remix" : "normal") + "-actual-random-work-order", World, () => ConfigureWorld(1, remix), Args, State, o =>
                {
                    NoError(o);
                    var expected = new List<string> { "power", "UpdateMech", "PerformUpdates", "UpdateLunarApocalypse", "SpawnStormLightning", "rate", "UpdatePrioritizedTownNPC", "CheckForHousesNearAPlayer", "rng:15100", "rng:10:190", "rng:10:39", "over:10:False", "rng:10:190", "rng:39:80", "under:39:" + remix };
                    if (remix) expected.Add("over:39:True");
                    expected.Add("SpawnFallingObjects"); Expect(expected.ToArray());
                    Require(randomCalls == 5 && !Bool(Gen, "growGrassUnderground") && Bool(Gen, "hardModeWorldUpdates") == remix
                        && Int(Gen, "homelessSpawnTimeout") == 1 && Int(Gen, "npcSpawnPeriod") == 20, "world work state and random call count");
                });
            foreach (bool disposeFault in new[] { false, true })
                Pair("world-liquid-throw-finally" + (disposeFault ? "-dispose-replaces-error" : ""), World,
                    () => ConfigureWorld(0, liquid: true, fault: "liquid", disposeFault: disposeFault), Args, State, o =>
                    {
                        Error(o, disposeFault ? DisposeFailure : WorkFailure);
                        Expect("power", "UpdateMech", "PerformUpdates", "UpdateLunarApocalypse", "swap", "liquid", "dispose");
                        Require(randomDepth == 0 && randomDisposals == 1 && Int(Water, "skipCount") == 2, "original finally restores RNG but post-finally skip reset does not run on throw");
                    });
            Pair("world-remix-tile-throw-retains-grass-flag", World, () => ConfigureWorld(1, remix: true, fault: "under"), Args, State,
                o => { Error(o, WorkFailure); Require(Bool(Gen, "growGrassUnderground") && randomCalls == 5 && trace[^1] == "under:39:True", "no invented cleanup on original tile callback throw"); });
            Negative("world-missing-observer-after-rng-finally", World, () => ConfigureWorld(0, liquid: true, fault: "liquid"), Args, State,
                o => { Error(o, WorkFailure); Require(randomDepth == 0 && randomDisposals == 1, "game finally still runs in observer mutant"); });
        }

        void ConfigureWiring(int[] x, int[] y, int[] times, int count)
        {
            Field(Wire, "cannonCoolDown", 2); Field(Wire, "bunnyCannonCoolDown", 0); Field(Wire, "snowballCannonCoolDown", -1);
            Field(Wire, "_mechX", x); Field(Wire, "_mechY", y); Field(Wire, "_mechTime", times); Field(Wire, "_numMechs", count);
            ConfigureMember(Wire, "SetCurrentUser", a => { Require((int)a[0]! == -1, "wiring user reset"); Mark("user:-1"); return null; });
            ConfigureMember(Gen, "InWorld", a => { Require((int)a[2]! == 1, "wiring border"); return (int)a[0]! >= 0 && (int)a[1]! >= 0; });
            TileActivity();
        }
        void WiringScenarios()
        {
            Pair("wiring-no-mechs-cooldowns", Wiring, () => ConfigureWiring(new int[1], new int[1], new int[1], 0), Args, State, o =>
            {
                NoError(o); Expect("user:-1"); Require(Int(Wire, "cannonCoolDown") == 1 && Int(Wire, "bunnyCannonCoolDown") == 0 && Int(Wire, "snowballCannonCoolDown") == -1, "positive-only cooldown decrement");
            });
            foreach (bool fail in new[] { false, true })
                Pair("wiring-timers-reverse-order" + (fail ? "-throw" : ""), Wiring, () =>
                {
                    ConfigureWiring(new[] { 1, 2, 0 }, new[] { 1, 1, 0 }, new[] { 61, 16, 0 }, 2);
                    var tiles = Tiles(4, 4); var a = PutTile(tiles, 1, 1, 144); var b = PutTile(tiles, 2, 1, 144);
                    Field(Tile, "frameY", (short)18, a); Field(Tile, "frameY", (short)18, b); Field(Tile, "frameX", (short)72, b);
                    ConfigureMember(Wire, "TripWire", args => { Require((int)args[2]! == 1 && (int)args[3]! == 1, "timer trip size"); Mark("trip:" + args[0]); if (fail) throw WorkFailure; return null; });
                }, Args, State, o =>
                {
                    if (fail) Error(o, WorkFailure); else NoError(o);
                    Expect(fail ? new[] { "user:-1", "trip:2" } : new[] { "user:-1", "trip:2", "trip:1" });
                    var times = (int[])ReadField(Wire, "_mechTime")!;
                    Require(times[1] == 18000 && times[0] == (fail ? 61 : 18000), "timer reset precedes trip and reverse iteration stops on throw");
                });
            Pair("wiring-expired-timer-compacts-arrays", Wiring, () =>
            {
                ConfigureWiring(new[] { 1, 2, 9 }, new[] { 1, 1, 9 }, new[] { 1, 8, 99 }, 2);
                var tiles = Tiles(4, 4); PutTile(tiles, 1, 1, 144); PutTile(tiles, 2, 1, 1, active: false);
                Configure("System.Void Terraria.NetMessage::SendTileSquare(System.Int32,System.Int32,System.Int32,Terraria.ID.TileChangeType)", a => { Mark("square:" + a[1] + ":" + a[2]); return null; });
            }, Args, State, o =>
            {
                NoError(o); Expect("user:-1", "square:1:1");
                Require(Int(Wire, "_numMechs") == 1 && ((int[])ReadField(Wire, "_mechX")!).SequenceEqual(new[] { 2, 9, 9 })
                    && ((int[])ReadField(Wire, "_mechTime")!).SequenceEqual(new[] { 7, 99, 99 }), "expired mech full compaction and preserved trailing sentinel");
            });
            Pair("wiring-null-and-outside-pruning", Wiring, () =>
            {
                ConfigureWiring(new[] { -1, 1, 0 }, new[] { 1, 1, 0 }, new[] { 4, 8, 0 }, 2); Tiles(3, 3);
            }, Args, State, o => { NoError(o); Expect("user:-1"); Require(Int(Wire, "_numMechs") == 0 && ((int[])ReadField(Wire, "_mechTime")!).SequenceEqual(new[] { 3, 7, 0 }), "null/outside prune retains decremented storage"); });
            Pair("wiring-expired-two-by-two-toggle", Wiring, () =>
            {
                ConfigureWiring(new[] { 1, 0 }, new[] { 1, 0 }, new[] { 1, 0 }, 1);
                var tiles = Tiles(4, 4);
                for (int x = 1; x < 3; x++) for (int y = 1; y < 3; y++) PutTile(tiles, x, y, 411);
                Configure("System.Void Terraria.NetMessage::SendTileSquare(System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,Terraria.ID.TileChangeType)", a =>
                { Require((int)a[1]! == 1 && (int)a[2]! == 1 && (int)a[3]! == 2 && (int)a[4]! == 2, "toggle sends exact square"); Mark("square:2x2"); return null; });
            }, Args, State, o =>
            {
                NoError(o); Expect("user:-1", "square:2x2");
                var tiles = (Array)ReadField(Main, "tile")!;
                for (int x = 1; x < 3; x++) for (int y = 1; y < 3; y++) Require(Int(Tile, "frameX", tiles.GetValue(x, y)) == 36, "two-by-two frame mutation");
                Require(Int(Wire, "_numMechs") == 0, "toggle removes expired mech");
            });
            Negative("wiring-missing-observer", Wiring, () => ConfigureWiring(new int[1], new int[1], new int[1], 0), Args, State,
                o => { NoError(o); Require(Int(Wire, "cannonCoolDown") == 1, "mutant game cooldown still executes"); });
        }

        void ConfigureEntities(int count, bool failUpdate = false, bool failDispose = false)
        {
            Field(Entity, "UpdateEntities", New(entityList));
            tileEntities = Enumerable.Range(0, count).Select(i => { object value = New(Entity); labels.Add(value, "entity" + i); return value; }).ToArray();
            Marker(Entity, "UpdateStart"); Marker(Entity, "UpdateEnd");
            ConfigureMember(entityList, "GetEnumerator", _ => { Mark("enumerator"); return New(entityEnumerator); });
            ConfigureMember(entityEnumerator, "MoveNext", _ => { entityIndex++; Mark("next:" + entityIndex); return entityIndex < tileEntities.Length; });
            ConfigureMember(entityEnumerator, "get_Current", _ => { Require(entityIndex >= 0 && entityIndex < tileEntities.Length, "enumerator current bound"); Mark("current:" + entityIndex); return tileEntities[entityIndex]; });
            ConfigureMember(Entity, "Update", a => { Require(ReferenceEquals(a[0], tileEntities[entityIndex]), "entity callback receiver"); entityUpdates++; Mark("update:" + entityIndex); if (failUpdate) throw WorkFailure; return null; });
            Configure("System.Void System.IDisposable::Dispose()", a => { Require(a[0]!.GetType() == Host(entityEnumerator), "entity enumerator disposal receiver"); entityDisposals++; Mark("enum-dispose"); if (failDispose) throw DisposeFailure; return null; });
        }
        void EntityScenarios()
        {
            Pair("tile-entities-empty-disposes", Entities, () => ConfigureEntities(0), Args, State,
                o => { NoError(o); Expect("UpdateStart", "enumerator", "next:0", "enum-dispose", "UpdateEnd"); Require(entityDisposals == 1 && entityUpdates == 0, "empty enumeration finally"); });
            Pair("tile-entities-two-ordered-updates", Entities, () => ConfigureEntities(2), Args, State, o =>
            {
                NoError(o); Expect("UpdateStart", "enumerator", "next:0", "current:0", "update:0", "next:1", "current:1", "update:1", "next:2", "enum-dispose", "UpdateEnd");
                Require(entityDisposals == 1 && entityUpdates == 2, "all entities updated then disposed");
            });
            foreach (bool disposeFault in new[] { false, true })
                Pair("tile-entities-throw-finally" + (disposeFault ? "-dispose-replaces-error" : ""), Entities,
                    () => ConfigureEntities(2, true, disposeFault), Args, State, o =>
                    {
                        Error(o, disposeFault ? DisposeFailure : WorkFailure);
                        Expect("UpdateStart", "enumerator", "next:0", "current:0", "update:0", "enum-dispose");
                        Require(entityDisposals == 1 && entityUpdates == 1, "throw stops enumeration, disposes, omits UpdateEnd");
                    });
            Negative("tile-entities-missing-observer-on-throw", Entities, () => ConfigureEntities(2, true), Args, State,
                o => { Error(o, WorkFailure); Require(entityDisposals == 1, "original enum finally survives observer mutant"); });
        }

        void ConfigureCounts(bool work, int column = 1)
        {
            Field(Main, "maxTilesX", 3); Field(Main, "maxTilesY", work ? 85 : 80); Field(Main, "worldSurface", work ? 42d : 39d);
            Field(Gen, "tileCounts", new int[8]); Field(Gen + "/Skyblock", "hasWall", new bool[8]); Field(Gen + "/Skyblock", "hasTile", new bool[8]);
            TileActivity();
            Configure("System.Void Terraria.Tile::.ctor()", a => { Mark("new-tile"); activeTiles.Add(a[0]!, false); return null; });
            ConfigureMember(Gen, "AddUpAlignmentCounts", a => { Require(!(bool)a[0]!, "count alignment flag"); Mark("alignment"); return null; });
            Marker(Gen + "/Skyblock", "Calculate");
            if (!work) return;
            var tiles = Tiles(3, 85);
            PutTile(tiles, column, 40, 1, 1); PutTile(tiles, column, 41, 1, 2);
            PutTile(tiles, column, 42, 2, 3); PutTile(tiles, column, 43, 2, 4);
            // y44 is deliberately null: the actual body must create and store a tile.
        }
        void CountScenarios()
        {
            Pair("tile-counts-empty-column", Counts, () => ConfigureCounts(false), () => new object?[] { 1 }, State,
                o => { NoError(o); Expect("alignment"); Require(((int[])ReadField(Gen, "tileCounts")!).All(n => n == 0), "no-work count array unchanged"); });
            Pair("tile-counts-weighted-bands-and-null-creation", Counts, () => ConfigureCounts(true), () => new object?[] { 1 }, State, o =>
            {
                NoError(o); Expect("new-tile", "alignment");
                Require(((int[])ReadField(Gen, "tileCounts")!).SequenceEqual(new[] { 0, 10, 6, 0, 0, 0, 0, 0 }) && Int(Gen + "/Skyblock", "currentActiveTiles") == 4, "surface weight5 and underground weight1 accumulate exactly");
                var walls = (bool[])ReadField(Gen + "/Skyblock", "hasWall")!;
                Require(walls.Take(5).All(x => x) && walls.Skip(5).All(x => !x) && ((Array)ReadField(Main, "tile")!).GetValue(1, 44) != null, "wall flags and created tile persisted");
            });
            Pair("tile-counts-last-column-calculates", Counts, () => ConfigureCounts(true, 2), () => new object?[] { 2 }, State,
                o => { NoError(o); Expect("new-tile", "alignment", "Calculate"); });
            Pair("tile-counts-first-column-publishes-and-resets", Counts, () =>
            {
                ConfigureCounts(false); Field(Main, "netMode", 2); Field(Gen, "totalSolid2", 1000); Field(Gen, "totalGood2", 1); Field(Gen, "totalEvil2", 125); Field(Gen, "totalBlood2", 335);
                ConfigureMember("Terraria.NetMessage", "SendData", a =>
                {
                    Require((int)a[0]! == 57 && (int)a[1]! == -1 && (int)a[2]! == -1 && a[3] == null, "alignment broadcast arguments");
                    Require(Int(Gen, "totalSolid2") == 1000 && Int(Gen, "tGood") == 1 && Int(Gen, "tEvil") == 12 && Int(Gen, "tBlood") == 34, "publish before reset, banker rounding and positive floor"); Mark("send"); return null;
                });
            }, () => new object?[] { 0 }, State, o =>
            {
                NoError(o); Expect("send", "alignment");
                Require(Int(Gen, "totalSolid") == 1000 && Int(Gen, "totalGood") == 1 && Int(Gen, "totalSolid2") == 0 && Int(Gen, "totalGood2") == 0 && Int(Gen, "totalEvil2") == 0 && Int(Gen, "totalBlood2") == 0, "column-zero publication and all accumulator resets");
            });
            Negative("tile-counts-missing-observer", Counts, () => ConfigureCounts(true), () => new object?[] { 1 }, State,
                o => { NoError(o); Require(((int[])ReadField(Gen, "tileCounts")!)[1] == 10, "mutant actual counts execute"); });
        }

        void ConfigureLiquid(bool active = false, bool quick = false, bool fail = false)
        {
            Field(Water, "cycles", 2); Field(Water, "curMaxLiquid", 4); Field(Water, "maxLiquid", 40); Field(Water, "maxLiquidBuffer", 100);
            Field(Water, "numLiquid", active ? 2 : 0); Field(Water, "quickSettle", quick);
            Field(Water, "_netChangeSet", new HashSet<int>()); Field(Water, "_swapNetChangeSet", new HashSet<int>());
            ConfigureMember(Water, "tilesIgnoreWater", a => { ignoringWater = (bool)a[0]!; Mark("ignore:" + ignoringWater); return null; });
            var tiles = Tiles(4, 4); PutTile(tiles, 1, 1); PutTile(tiles, 2, 1);
            Field(Main, "liquid", ArrayOf(Water, 2, i => Value(Water, ("x", i + 1), ("y", 1))));
            Configure("System.Void Terraria.Liquid::Update()", a =>
            {
                Require(ignoringWater, "liquid element updates inside ignore-water scope");
                object value = a[0]!; int x = Int(Water, "x", value); Mark("update:" + x + ":" + Int(Water, "delay", value));
                if (fail) throw WorkFailure;
                Field(Water, "delay", Int(Water, "delay", value) + 1, value); a[0] = value; return null;
            });
            Configure("System.Boolean Terraria.Tile::skipLiquid()", a => skippedTiles[a[0]!]);
            Configure("System.Void Terraria.Tile::skipLiquid(System.Boolean)", a => { skippedTiles[a[0]!] = (bool)a[1]!; Mark("skip:" + labels[a[0]!] + ":" + a[1]); return null; });
        }
        void LiquidScenarios()
        {
            Pair("liquid-empty-cycle", Liquid, () => ConfigureLiquid(), Args, State,
                o => { NoError(o); Expect("ignore:True", "ignore:False"); Require(!ignoringWater && Int(Water, "wetCounter") == 0 && !Bool(Water, "quickFall"), "empty cycle clamps end and resets counter"); });
            Pair("liquid-normal-update-and-skip", Liquid, () =>
            {
                ConfigureLiquid(true); skippedTiles[((Array)ReadField(Main, "tile")!).GetValue(2, 1)!] = true;
            }, Args, State, o =>
            {
                NoError(o); Expect("ignore:True", "update:1:0", "skip:2,1:False", "ignore:False");
                var liquids = (Array)ReadField(Main, "liquid")!;
                Require(Int(Water, "delay", liquids.GetValue(0)) == 1 && Int(Water, "delay", liquids.GetValue(1)) == 0 && Int(Water, "wetCounter") == 1 && skippedTiles.Values.All(v => !v), "liquid struct receiver copyback and skipped entry");
            });
            Pair("liquid-quick-fall-forces-delay-and-updates", Liquid, () => ConfigureLiquid(true, true), Args, State, o =>
            {
                NoError(o); Expect("ignore:True", "update:1:10", "skip:1,1:False", "update:2:10", "skip:2,1:False", "ignore:False");
                var liquids = (Array)ReadField(Main, "liquid")!;
                Require(Bool(Water, "quickFall") && Int(Water, "delay", liquids.GetValue(0)) == 11 && Int(Water, "delay", liquids.GetValue(1)) == 11, "quick-fall writes then callback struct copyback");
            });
            Pair("liquid-element-throw-retains-original-partial-state", Liquid, () => ConfigureLiquid(true, true, true), Args, State, o =>
            {
                Error(o, WorkFailure); Expect("ignore:True", "update:1:10");
                Require(ignoringWater && Int(Water, "delay", ((Array)ReadField(Main, "liquid")!).GetValue(0)) == 10 && Int(Water, "wetCounter") == 1, "original liquid body has no ignore-water finally");
            });
            Pair("liquid-cycle-end-delete-and-buffer-refill", Liquid, () =>
            {
                ConfigureLiquid(true); Field(Water, "wetCounter", 1);
                var liquids = (Array)ReadField(Main, "liquid")!; object first = liquids.GetValue(0)!; Field(Water, "kill", 8, first); liquids.SetValue(first, 0);
                var tile = ((Array)ReadField(Main, "tile")!).GetValue(1, 1)!; Field(Tile, "liquid", (byte)254, tile);
                Field(Buffer, "numLiquidBuffer", 1); Field(Main, "liquidBuffer", ArrayOf(Buffer, 1, _ => Value(Buffer, ("x", 1), ("y", 1))));
                ConfigureMember(Water, "DelWater", a => { Require((int)a[0]! == 0 && Int(Tile, "liquid", tile) == 255, "delete index and 254 normalization order"); Mark("delete:0"); Field(Water, "numLiquid", Int(Water, "numLiquid") - 1); return null; });
                Configure("System.Void Terraria.Tile::checkingLiquid(System.Boolean)", a => { Require(ReferenceEquals(a[0], tile) && !(bool)a[1]!, "buffer tile checking flag"); Mark("checking:false"); return null; });
                ConfigureMember(Water, "AddWater", a => { Require((int)a[0]! == 1 && (int)a[1]! == 1, "buffer refill coordinates"); Mark("add:1:1"); Field(Water, "numLiquid", Int(Water, "numLiquid") + 1); return null; });
                ConfigureMember(Buffer, "DelBuffer", a => { Require((int)a[0]! == 0, "buffer removal index"); Mark("buffer:0"); Field(Buffer, "numLiquidBuffer", 0); return null; });
            }, Args, State, o =>
            {
                NoError(o); Expect("ignore:True", "delete:0", "checking:false", "add:1:1", "buffer:0", "ignore:False");
                Require(Int(Water, "numLiquid") == 2 && Int(Buffer, "numLiquidBuffer") == 0 && Int(Water, "wetCounter") == 0 && Int(Water, "stuckCount") == 1, "cycle deletion/refill and stuck tracking state");
            });
            Pair("liquid-server-network-swap-ref-writeback", Liquid, () =>
            {
                ConfigureLiquid(); Field(Main, "netMode", 2); Field(Main, "player", ArrayOf(Player, 15));
                originalChanges = new HashSet<int> { 9, 4 }; originalSpare = new HashSet<int>();
                Field(Water, "_netChangeSet", originalChanges); Field(Water, "_swapNetChangeSet", originalSpare);
                Configure("System.Void Terraria.Utils::Swap<System.Collections.Generic.HashSet`1<System.Int32>>(T&,T&)", a =>
                {
                    Require(ReferenceEquals(a[0], originalChanges) && ReferenceEquals(a[1], originalSpare), "network swap input aliases");
                    (a[0], a[1]) = (a[1], a[0]); Mark("swap-sets"); return null;
                });
                ConfigureMember("Terraria.GameContent.NetModules.NetLiquidModule", "CreateAndBroadcastByChunk", a =>
                {
                    Require(ReferenceEquals(a[0], originalChanges) && ((HashSet<int>)a[0]!).SetEquals(new[] { 9, 4 }) && ReferenceEquals(ReadField(Water, "_netChangeSet"), originalSpare), "broadcast observes both by-ref writes before send");
                    Mark("broadcast:4,9"); return null;
                });
            }, Args, State, o =>
            {
                NoError(o); Expect("ignore:True", "swap-sets", "broadcast:4,9", "ignore:False");
                Require(ReferenceEquals(ReadField(Water, "_netChangeSet"), originalSpare) && ReferenceEquals(ReadField(Water, "_swapNetChangeSet"), originalChanges)
                    && originalChanges!.Count == 0 && Int(Water, "cycles") == 10 && Int(Water, "curMaxLiquid") == 40, "server swap aliases, native HashSet.Clear and capacity adaptation");
            });
            Pair("liquid-panic-five-row-budget-early-return", Liquid, () =>
            {
                ConfigureLiquid(); Field(Water, "panicMode", true); Field(Water, "panicY", 12);
                ConfigureMember(Water, "QuickWater", a => { Require((int)a[0]! == 0 && Equals(a[1], a[2]), "panic scan row bounds"); Mark("quick:" + a[1]); return null; });
            }, Args, State, o =>
            {
                NoError(o); Expect("ignore:True", "quick:12", "quick:11", "quick:10", "quick:9", "quick:8");
                Require(Int(Water, "panicY") == 7 && ignoringWater && Int(Water, "wetCounter") == 0, "panic executes exactly five rows and original early return");
            });
            Negative("liquid-missing-observer-on-throw", Liquid, () => ConfigureLiquid(true, true, true), Args, State,
                o => { Error(o, WorkFailure); Require(ignoringWater, "game partial state retained by observer mutant"); });
        }

        void ConfigurePriority(bool eligible)
        {
            Field(Gen, "prioritizedTownNPCType", 99); Field(Main, "maxNPCs", 8);
            Field(Main, "npc", ArrayOf(Npc, 8, i => Value(Npc, ("active", i != 0), ("homeless", i != 1), ("townNPC", i != 2),
                ("lookForHomeTimeout", i == 3 || (!eligible && i >= 6) ? 1 : 0), ("type", i == 4 ? 368 : i == 5 ? 160 : i == 6 ? 22 : 54))));
        }
        void PriorityScenarios()
        {
            Pair("housing-priority-sentinel-bypass", Prioritized, () => Field(Gen, "prioritizedTownNPCType", 37), Args, State,
                o => { NoError(o); Require(Int(Gen, "prioritizedTownNPCType") == 37, "sentinel keeps priority without NPC access"); });
            Pair("housing-priority-no-eligible-npc", Prioritized, () => ConfigurePriority(false), Args, State,
                o => { NoError(o); Require(Int(Gen, "prioritizedTownNPCType") == 99, "all eligibility guards retain priority"); });
            Pair("housing-priority-first-eligible-after-exclusions", Prioritized, () => ConfigurePriority(true), Args, State,
                o => { NoError(o); Require(Int(Gen, "prioritizedTownNPCType") == 22, "first eligible wins after inactive/homed/nontown/timeout/368/160 exclusions"); });
            Negative("housing-priority-missing-observer", Prioritized, () => ConfigurePriority(true), Args, State,
                o => { NoError(o); Require(Int(Gen, "prioritizedTownNPCType") == 22, "priority mutant still executes actual selection"); });
        }

        void ConfigureHousing(int attempts, bool spawnFault = false)
        {
            Field(Main + "/CurrentFrameFlags", "ActivePlayersCount", 2);
            var players = ArrayOf(Player, 255, i => Value(Player, ("active", i == 2 || i == 5)));
            Field(Main, "player", players); Field(Main, "rand", New(Random));
            Field(Main, "MaxWorldViewSize", Value(Point, ("X", 320), ("Y", 640)));
            Field(Main, "wallHouse", new[] { false, true }); Field(Main, "tileSolid", new bool[400]);
            var tiles = Tiles(3, 3); PutTile(tiles, 1, 1, wall: 0); PutTile(tiles, 2, 1, wall: 1);
            ConfigureMember(Main, "get_GameUpdateCount", _ => 1u);
            ConfigureMember("Terraria.Entity", "get_Center", a =>
            {
                Require(ReferenceEquals(a[0], players.GetValue(5)), "round-robin selects second active player, not raw index"); Mark("center:5"); return Value(Vector, ("X", 64f), ("Y", 80f));
            });
            Configure("Microsoft.Xna.Framework.Point Terraria.Utils::ToTileCoordinates(Microsoft.Xna.Framework.Vector2)", a =>
            {
                Require((float)ReadField(Vector, "X", a[0])! == 64f && (float)ReadField(Vector, "Y", a[0])! == 80f, "center reaches tile-coordinate boundary");
                return Value(Point, ("X", 4), ("Y", 5));
            });
            ConfigureMember(Point, "get_Zero", _ => Value(Point, ("X", 0), ("Y", 0)));
            Configure("Microsoft.Xna.Framework.Rectangle Terraria.Utils::CenteredRectangle(Microsoft.Xna.Framework.Point,Microsoft.Xna.Framework.Point)", a =>
            {
                Require(Int(Point, "X", a[0]) == 4 && Int(Point, "Y", a[0]) == 5 && Int(Point, "X", a[1]) == 0 && Int(Point, "Y", a[1]) == 0, "centered rectangle boundary inputs");
                return Value(Rectangle, ("X", 4), ("Y", 5), ("Width", 0), ("Height", 0));
            });
            Configure("System.Void Microsoft.Xna.Framework.Rectangle::Inflate(System.Int32,System.Int32)", a =>
            {
                Require((int)a[1]! == 10 && (int)a[2]! == 20 && Int(Rectangle, "X", a[0]) == 4, "view size integer division and rectangle receiver");
                object rectangle = a[0]!; Field(Rectangle, "X", -6, rectangle); Field(Rectangle, "Y", -15, rectangle);
                Field(Rectangle, "Width", 20, rectangle); Field(Rectangle, "Height", 40, rectangle); a[0] = rectangle; Mark("inflate"); return null;
            });
            ConfigureMember("Terraria.WorldBuilding.WorldUtils", "ClampToWorld", a =>
            {
                Require((int)a[1]! == 10 && Int(Rectangle, "X", a[0]) == -6 && Int(Rectangle, "Y", a[0]) == -15
                    && Int(Rectangle, "Width", a[0]) == 20 && Int(Rectangle, "Height", a[0]) == 40, "rectangle struct receiver writes reach clamp");
                Mark("clamp"); return Value(Rectangle, ("X", 10), ("Y", 10), ("Width", 20), ("Height", 20));
            });
            ConfigureMember("Terraria.Utils", "NextFromRectangle", a =>
            {
                Require(ReferenceEquals(a[0], ReadField(Main, "rand")) && Int(Rectangle, "X", a[1]) == 10 && Int(Rectangle, "Width", a[1]) == 20, "housing RNG receives clamped rectangle");
                housingAttempts++; Require(housingAttempts <= 300, "housing candidate bound");
                return Value(Point, ("X", housingAttempts == attempts ? 2 : 1), ("Y", 1));
            });
            ConfigureMember(Gen, "SpawnTownNPC", a =>
            {
                Require((int)a[0]! == 2 && (int)a[1]! == 1 && !(bool)a[2]! && ((bool[])ReadField(Main, "tileSolid")!)[379], "housing temporary solidity and spawn coordinates");
                Mark("spawn:" + housingAttempts); if (spawnFault) throw WorkFailure;
                return Enum.ToObject(Host("Terraria.Enums.TownNPCSpawnResult"), 0);
            });
        }
        void HousingScenarios()
        {
            Pair("housing-no-active-players-modulo-safe", Houses, () =>
            {
                Field(Main + "/CurrentFrameFlags", "ActivePlayersCount", 0); Field(Main, "player", ArrayOf(Player, 255));
                ConfigureMember(Main, "get_GameUpdateCount", _ => uint.MaxValue);
            }, Args, State, o => { NoError(o); Expect(); Require(housingAttempts == 0, "zero active count avoids division by zero and all geometry"); });
            Pair("housing-round-robin-second-candidate-restores-solid", Houses, () => ConfigureHousing(2), Args, State,
                o => { NoError(o); Expect("center:5", "inflate", "clamp", "spawn:2"); Require(housingAttempts == 2 && !((bool[])ReadField(Main, "tileSolid")!)[379], "spawn breaks candidate loop and restores original solidity"); });
            Pair("housing-no-house-bounded-300-attempts", Houses, () => ConfigureHousing(301), Args, State,
                o => { NoError(o); Expect("center:5", "inflate", "clamp"); Require(housingAttempts == 300 && !((bool[])ReadField(Main, "tileSolid")!)[379], "housing loop has exact candidate budget"); });
            Pair("housing-spawn-throw-retains-original-solid-write", Houses, () => ConfigureHousing(1, true), Args, State,
                o => { Error(o, WorkFailure); Expect("center:5", "inflate", "clamp", "spawn:1"); Require(((bool[])ReadField(Main, "tileSolid")!)[379] && housingAttempts == 1, "no invented finally restores original housing throw state"); });
            Negative("housing-missing-observer-on-spawn-throw", Houses, () => ConfigureHousing(1, true), Args, State,
                o => { Error(o, WorkFailure); Require(((bool[])ReadField(Main, "tileSolid")!)[379], "observer mutant retains original partial tileSolid mutation"); });
        }
    }
}
