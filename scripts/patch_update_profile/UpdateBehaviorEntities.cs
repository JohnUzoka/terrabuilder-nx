using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class UpdateBehaviorEntities
{
    const int Players = 0x06000eb0, Npcs = 0x06000eb2, Projectiles = 0x06000eb4,
        Items = 0x06000eb5, Tiles = 0x06000ecc, Walls = 0x06000ece;
    const string Main = "Terraria.Main", Frame = "Terraria.Main/CurrentFrameFlags",
        Player = "Terraria.Player", Npc = "Terraria.NPC", Projectile = "Terraria.Projectile",
        Item = "Terraria.WorldItem", Entity = "Terraria.Entity", Vector = "Microsoft.Xna.Framework.Vector2",
        Anchors = "Terraria.DataStructures.AnchoredEntitiesCollection", Logger = "Terraria.TimeLogger",
        LogData = "Terraria.TimeLogger/TimeLogData", Stamp = "Terraria.TimeLogger/StartTimestamp",
        Random = "Terraria.Utilities.UnifiedRandom";
    static readonly Exception WorkFailure = new IOException("update56 original entity callback failure");
    static readonly Exception ConstructorFailure = new IOException("update56 original replacement constructor failure");

    internal static void AddMutants(MethodDefinition source, MethodDefinition patched, TypeDefinition runtime,
        List<object> receipts, UpdateProbe.Mapper mapper)
    {
        int token = source.MetadataToken.ToInt32();
        void Add(string prefix, Action<MethodDefinition> change)
        {
            var mutant = new MethodDefinition(prefix + token.ToString("x8"), Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, patched.ReturnType);
            foreach (var parameter in patched.Parameters) mutant.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
            patched.DeclaringType.Methods.Add(mutant); Copy(patched, mutant, t => t, m => m); change(mutant); Widen(mutant);
            receipts.Add(new { kind = "EntitySemanticMutant", mutation = prefix, source = source.FullName,
                sourceMvid = source.Module.Mvid, sourceToken = token, sourceBody = Fingerprint(source), fixture = mutant.FullName, mappedBody = Fingerprint(mutant) });
        }
        Instruction[] Calls(MethodDefinition method, string owner, string name)
        {
            var original = source.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(m => m.DeclaringType.FullName == owner && m.Name == name).DistinctBy(m => m.FullName).Single();
            string mapped = ((MethodReference)mapper.Member(original)).FullName;
            return method.Body.Instructions.Where(i => i.Operand is MethodReference m && m.FullName == mapped).ToArray();
        }
        if (token is Players or Npcs or Projectiles or Items)
            Add("EntitiesInvertedErrorPolicy", m =>
            {
                var branch = m.Body.Instructions.Single(i => i.Operand is FieldReference f && f.Name == "ignoreErrors").Next;
                Require(branch.OpCode.Code is Code.Brtrue or Code.Brtrue_S, "entity original ignoreErrors branch");
                branch.OpCode = OpCodes.Brfalse;
            });
        switch (token)
        {
            case Players:
                Add("EntitiesSkipLastPlayer", m => m.Body.Instructions.Single(i => i.OpCode.Code == Code.Ldc_I4 && Equals(i.Operand, 255)).Operand = 254);
                break;
            case Npcs:
                Add("EntitiesNpcDelayIncreases", m =>
                {
                    var subtract = m.Body.Instructions.Single(i => i.OpCode.Code == Code.Stsfld && i.Operand is FieldReference f && f.Name == "offSetDelayTime").Previous;
                    Require(subtract.OpCode.Code == Code.Sub, "NPC original delay decrement"); subtract.OpCode = OpCodes.Add;
                });
                break;
            case Projectiles:
                Add("EntitiesProjectileIndexNotRestored", m =>
                {
                    var value = m.Body.Instructions.Last(i => i.OpCode.Code == Code.Stsfld && i.Operand is FieldReference f && f.Name == "ProjectileUpdateLoopIndex").Previous;
                    Require(value.OpCode.Code == Code.Ldc_I4_M1, "projectile original final index sentinel"); value.OpCode = OpCodes.Ldc_I4_0;
                });
                break;
            case Items:
                Add("EntitiesReplacementItemIndexLost", m =>
                {
                    var store = m.Body.Instructions.Single(i => i.OpCode.Code == Code.Stfld && i.Operand is FieldReference f && f.Name == "whoAmI");
                    store.OpCode = OpCodes.Pop; store.Operand = null; m.Body.GetILProcessor().InsertAfter(store, Instruction.Create(OpCodes.Pop));
                });
                break;
            case Tiles:
                Add("EntitiesTileCounterDecreases", m => m.Body.Instructions.First(i => i.OpCode.Code == Code.Add).OpCode = OpCodes.Sub);
                Add("EntitiesTileRandomDrawLost", m =>
                {
                    var call = Calls(m, Random, "Next").First(); var il = m.Body.GetILProcessor();
                    call.OpCode = OpCodes.Pop; call.Operand = null;
                    var pop = Instruction.Create(OpCodes.Pop); il.InsertAfter(call, pop); il.InsertAfter(pop, Instruction.Create(OpCodes.Ldc_I4_0));
                });
                Add("EntitiesTileCallbacksReordered", m =>
                {
                    var vane = Calls(m, Main, "AnimateTiles_WeatherVane").Single(); var cages = Calls(m, Main, "AnimateTiles_CritterCages").Single();
                    (vane.Operand, cages.Operand) = (cages.Operand, vane.Operand);
                });
                break;
            case Walls:
                Add("EntitiesWallCounterDecreases", m => m.Body.Instructions.First(i => i.OpCode.Code == Code.Add).OpCode = OpCodes.Sub);
                break;
        }
    }

    internal static object Run(Assembly assembly, Type runtime) => new Session(assembly, runtime).Run();

    sealed class Session : UpdateBehaviorSession
    {
        readonly List<string> flow = new();
        readonly List<int> visited = new(), draws = new();
        readonly List<object> replacements = new();
        Array? entities, players;
        object[] originals = Array.Empty<object>();
        int logs, sleeping, sections, currentPlayer, windCalls, clampCalls, strikes;
        int[] counters = Array.Empty<int>(), frames = Array.Empty<int>();
        byte[] wallCounters = Array.Empty<byte>(), wallFrames = Array.Empty<byte>();

        internal Session(Assembly assembly, Type runtime) : base(assembly, runtime) { }
        protected override void Setup()
        {
            flow.Clear(); visited.Clear(); draws.Clear(); replacements.Clear(); entities = players = null;
            originals = Array.Empty<object>(); logs = sleeping = sections = windCalls = clampCalls = strikes = 0; currentPlayer = -1;
            counters = frames = Array.Empty<int>(); wallCounters = wallFrames = Array.Empty<byte>();
        }
        int Int(string owner, string name, object? receiver = null) => Convert.ToInt32(ReadField(owner, name, receiver));
        bool Bool(string owner, string name, object? receiver = null) => (bool)ReadField(owner, name, receiver)!;
        void Mark(string value) => flow.Add(value);
        void Marker(string owner, string method, string label) => ConfigureMember(owner, method, _ => { Mark(label); return null; });
        string State() => string.Join(";", flow) + "|visited=" + string.Join(",", visited) + "|draws=" + string.Join(",", draws)
            + "|counts=" + string.Join(",", logs, sleeping, sections, windCalls, clampCalls, strikes, replacements.Count);
        object?[] Receiver() => new[] { Instance(Main) };
        static object?[] Args() => Array.Empty<object?>();
        void NoError(Outcome outcome) => Require(outcome.Error == null, "entity unexpected original exception " + outcome.Error);
        void Error(Outcome outcome, Exception expected) => Require(ReferenceEquals(outcome.Error, expected), "entity original exception identity");
        void Expect(params string[] expected) => Require(flow.SequenceEqual(expected), "entity exact callback order: " + string.Join(";", flow));
        void Visits(IEnumerable<int> expected) => Require(visited.SequenceEqual(expected), "entity exact slot visitation: " + string.Join(",", visited));
        object Value(string owner, params (string Name, object Value)[] fields)
        {
            object value = New(owner); foreach (var field in fields) Field(owner, field.Name, field.Value, value); return value;
        }
        Array Slots(string owner, int count)
        {
            var array = Array.CreateInstance(Host(owner), count);
            for (int i = 0; i < count; i++) { object value = New(owner); Field(Entity, "whoAmI", i, value); array.SetValue(value, i); }
            return array;
        }
        void Time(string field)
        {
            object log = New(LogData); Field(Logger, field, log);
            ConfigureMember(Logger, "Start", _ => { Mark("start"); return New(Stamp); });
            ConfigureMember(LogData, "AddTime", a =>
            {
                Require(ReferenceEquals(a[0], log) && a[1]!.GetType() == Host(Stamp), "original stock timer receiver and timestamp");
                logs++; Mark("time"); return null;
            });
        }
        void AnchorBoundaries(string method)
        {
            object sitting = New(Anchors), resting = New(Anchors);
            Field(Main, "sittingManager", sitting); Field(Main, "sleepingManager", resting);
            ConfigureMember(Anchors, method, a =>
            {
                Require(ReferenceEquals(a[0], sitting) || ReferenceEquals(a[0], resting), "original anchor receiver");
                Mark(ReferenceEquals(a[0], sitting) ? "sitting" : "sleeping"); return null;
            });
        }

        internal object Run()
        {
            PlayerScenarios(); NpcScenarios(); ProjectileScenarios(); ItemScenarios(); TileScenarios(); WallScenarios(); MutantScenarios();
            return new
            {
                passed = true, scenarios = Scenarios, mutants = Mutants, bounded = BoundarySummary,
                limitations = new[]
                {
                    "All six original/candidate full bodies execute; arrays, loop bounds, guards, original catches, rethrows, field writes, animation arithmetic and early returns are actual cloned IL.",
                    "Per-player/NPC/projectile/item updates, replacement constructors, anchor managers, sleeping/section queries, NPC subsystems, stock timers and animation child methods are deterministic configured fail-closed boundaries, not whole-game execution.",
                    "Player loops visit all 255 slots, projectile loops all 1000 and item loops all 400; NPCs use a bounded nine-slot world. Activity skipping is exercised in actual player/NPC guards; projectile/item per-entity activity remains inside their explicitly bounded child callbacks.",
                    "UnifiedRandom.Next is a scripted two-draw boundary: actual AnimateTiles generation guards, draw order, bounds and counter consumption execute; the PRNG implementation and weather-vane/critter-cage algorithms are not exercised.",
                    "Animation arrays are nonempty 1000-tile/400-wall fixtures. Selected wrap, copy, range-loop, wind modulo and wall144 ascending/descending return paths have independent value oracles; this is not an exhaustive initial-state proof for every tile or wall type.",
                    "Observer-clock entry and exit failures, original callback errors, ignoreErrors catches/rethrows and replacement-constructor errors are compared against baseline. No hardware timing or full Terraria simulation claim is made."
                }
            };
        }

        void ConfigurePlayers(bool ignore = false, int failure = -1, bool inactive = false, int netMode = 0, bool god = true)
        {
            Time("UpdatePlayers"); AnchorBoundaries("ClearPlayerAnchors"); Marker(Main, "CheckBossIndexes", "boss-indexes");
            players = Slots(Player, 255); Field(Main, "player", players); Field(Main, "netMode", netMode); Field(Main, "myPlayer", 0); Field(Main, "ignoreErrors", ignore);
            Field(Frame, "ActivePlayersCount", 99); Field(Frame, "SleepingPlayersCount", 88);
            foreach (int i in new[] { 0, 1, 2, 3, 254 }) Field(Player, "active", !inactive, players.GetValue(i));
            Field(Player, "ghost", true, players.GetValue(1)); Field(Player, "ghost", true, players.GetValue(3));
            object local = players.GetValue(0)!;
            Field(Player, "creativeGodMode", god, local); Field(Player, "statLifeMax2", 400, local); Field(Player, "statManaMax2", 200, local); Field(Player, "breathMax", 300, local);
            Field(Player, "statLife", 10, local); Field(Player, "statMana", 11, local); Field(Player, "breath", 12, local);
            Field(Entity, "position", Value(Vector, ("X", 17f), ("Y", 29f)), local);
            Configure("System.Void Terraria.Player::Update(System.Int32)", a =>
            {
                int i = (int)a[1]!; Require(ReferenceEquals(a[0], players.GetValue(i)) && Bool(Player, "active", a[0]), "player active slot receiver");
                visited.Add(i); currentPlayer = i; Mark("player:" + i); Field(Player, "statLife", Int(Player, "statLife", a[0]) + i + 1, a[0]);
                if (i == failure) throw WorkFailure;
                if (i == 2) Field(Player, "active", false, a[0]);
                if (i == 3) Field(Player, "ghost", false, a[0]);
                return null;
            });
            ConfigureMember("Terraria.GameContent.PlayerSleepingHelper", "get_FullyFallenAsleep", a =>
            {
                Require(a[0]!.GetType() == Host("Terraria.GameContent.PlayerSleepingHelper"), "sleep helper typed receiver");
                sleeping++; Mark("asleep:" + currentPlayer); return currentPlayer is 0 or 254;
            });
            ConfigureMember(Main, "get_LocalPlayer", _ => { Mark("local"); return local; });
            ConfigureMember(Player, "get_SpectatingCameraPosition", a =>
            {
                Require(ReferenceEquals(a[0], local), "spectating local receiver"); Mark("spectating"); return Value(Vector, ("X", 91f), ("Y", 12f));
            });
            ConfigureMember("Terraria.DataStructures.ActiveSections", "CheckSection", a =>
            {
                Require((int)a[1]! == 1 && (float)ReadField(Vector, "X", a[0])! == (sections == 0 ? 17f : 91f)
                    && (float)ReadField(Vector, "Y", a[0])! == (sections == 0 ? 29f : 12f), "section position and section radius");
                sections++; Mark("section:" + sections); return null;
            });
        }
        void VerifyPlayers(Outcome outcome, bool ignore = false, int failure = -1, bool inactive = false, int netMode = 0, bool god = true)
        {
            bool aborted = failure >= 0 && !ignore;
            if (aborted) Error(outcome, WorkFailure); else NoError(outcome);
            Visits(inactive ? Array.Empty<int>() : aborted ? new[] { 0 } : new[] { 0, 1, 2, 3, 254 });
            Require(Int(Frame, "ActivePlayersCount") == (aborted ? 99 : inactive ? 0 : failure >= 0 ? 2 : 3), "player active count after callback activity/ghost changes");
            Require(Int(Frame, "SleepingPlayersCount") == (aborted ? 88 : inactive ? 0 : failure >= 0 ? 1 : 2), "player asleep count");
            Require(logs == (aborted ? 0 : 1) && sections == (aborted || netMode == 2 ? 0 : 2), "player timer and client section guards");
            object local = players!.GetValue(0)!;
            Require(Int(Player, "statLife", local) == (!aborted && netMode != 2 && god ? 400 : inactive ? 10 : 11)
                && Int(Player, "statMana", local) == (!aborted && netMode != 2 && god ? 200 : 11)
                && Int(Player, "breath", local) == (!aborted && netMode != 2 && god ? 300 : 12), "player creative restoration fields");
            var expected = new List<string> { "start", "boss-indexes", "sitting", "sleeping" };
            foreach (int i in visited) { expected.Add("player:" + i); if (!aborted && i != failure && i is 0 or 3 or 254) expected.Add("asleep:" + i); }
            if (!aborted && netMode != 2) expected.AddRange(new[] { "local", "section:1", "local", "spectating", "section:2" });
            if (!aborted) expected.Add("time"); Expect(expected.ToArray());
        }
        void PlayerScenarios()
        {
            Pair("players-active-inactive-ghost-sleep-and-creative", Players, () => ConfigurePlayers(), Receiver, State, o => VerifyPlayers(o));
            Pair("players-all-inactive-client", Players, () => ConfigurePlayers(inactive: true), Receiver, State, o => VerifyPlayers(o, inactive: true));
            Pair("players-server-no-sections-or-creative", Players, () => ConfigurePlayers(netMode: 2), Receiver, State, o => VerifyPlayers(o, netMode: 2));
            Pair("players-client-no-creative", Players, () => ConfigurePlayers(god: false), Receiver, State, o => VerifyPlayers(o, god: false));
            Pair("players-ignore-original-error-continues", Players, () => ConfigurePlayers(true, 0), Receiver, State, o => VerifyPlayers(o, true, 0));
            Pair("players-rethrow-original-error", Players, () => ConfigurePlayers(false, 0), Receiver, State, o => VerifyPlayers(o, false, 0));
            Pair("players-observer-exit-clock", Players, () => ConfigurePlayers(), Receiver, State, o => VerifyPlayers(o), observerClockOffset: 2);
            Negative("players-missing-observer", Players, () => ConfigurePlayers(), Receiver, State, o => VerifyPlayers(o));
        }

        void ConfigureNpcs(bool ignore = false, int failure = -1, bool inactive = false, int netMode = 0, bool dangerOnly = false, bool constructorFailure = false, bool preserveBrain = false)
        {
            Time("UpdateNPCs"); AnchorBoundaries("ClearNPCAnchors");
            players = Slots(Player, 255); Field(Main, "player", players);
            foreach (object p in players) Field(Player, "nearbyActiveNPCs", 9f, p);
            ConfigureMember(Main, "CheckBossIndexes", _ =>
            {
                Require(players.Cast<object>().All(p => (float)ReadField(Player, "nearbyActiveNPCs", p)! == 0), "NPC prepass resets all 255 player proximity fields");
                Mark("boss-indexes"); return null;
            });
            entities = Slots(Npc, 9); originals = entities.Cast<object>().ToArray(); Field(Main, "npc", entities); Field(Main, "maxNPCs", 9);
            Field(Main, "netMode", netMode); Field(Main, "ignoreErrors", ignore); Field(Main, "remixWorld", true); Field(Main, "afterPartyOfDoom", true);
            Field(Npc, "taxCollector", true); Field(Npc, "offSetDelayTime", 2); Field(Npc, "empressRageMode", true); Field(Npc, "brainOfGravity", preserveBrain ? 5 : 0);
            Field(Frame, "AnyActiveBossNPC", true);
            foreach (string field in new[] { "savedMech", "unlockedPartyGirlSpawn", "unlockedPrincessSpawn", "unlockedSlimeRainbowSpawn", "unlockedSlimeGreenSpawn", "boughtBunny" }) Field(Npc, field, true);
            int[] types = { 1, 37, 1, 2, 453, 266, 20, 368, 680 };
            for (int i = 0; i < originals.Length; i++)
            {
                object npc = originals[i]; Field(Npc, "active", !inactive && i != 2, npc); Field(Npc, "type", types[i], npc);
                Field(Npc, "townNPC", i != 3 && i != 5, npc); Field(Npc, "boss", i == 5 && !dangerOnly, npc); Field(Entity, "direction", -1, npc);
            }
            var danger = new bool[1000]; danger[2] = true; Field("Terraria.ID.NPCID/Sets", "DangerThatPreventsOtherDangers", danger);
            object tracker = New("Terraria.GameContent.Bestiary.BestiaryUnlocksTracker");
            Field("Terraria.GameContent.Bestiary.BestiaryUnlocksTracker", "Sights", New("Terraria.GameContent.Bestiary.NPCWasNearPlayerTracker"), tracker); Field(Main, "BestiaryTracker", tracker);
            ConfigureMember(Npc, "ClearFoundActiveNPCs", _ => { Require(!Bool(Npc, "taxCollector"), "NPC tax collector reset before clearing"); Mark("clear-found"); return null; });
            Marker(Npc, "UpdateFoundActiveNPCs", "found"); Marker("Terraria.GameContent.FixExploitManEaters", "Update", "exploit");
            Marker("Terraria.GameContent.Bestiary.NPCWasNearPlayerTracker", "ScanWorldForFinds", "bestiary"); Marker("Terraria.GameContent.NPCDamageTracker", "Update", "damage");
            Configure("System.Boolean Terraria.NPC::AnyNPCs(System.Int32)", a => { Require((int)a[0]! == 636, "NPC remix rage queried type"); Mark("rage-query"); return preserveBrain; });
            ConfigureMember("Terraria.GameContent.Events.BirthdayParty", "get_PartyIsUp", _ => { Mark("party"); return false; });
            ConfigureMember(Npc, "StrikeNPCNoInteraction", a =>
            {
                Require(ReferenceEquals(a[0], originals[0]) && (int)a[1]! == 9999 && (float)a[2]! == 10f && (int)a[3]! == 1, "after-party active town NPC exclusions and strike arguments");
                strikes++; Mark("strike:0"); Field(Npc, "active", false, a[0]); Field(Entity, "direction", 7, a[0]); return 17;
            });
            ConfigureMember(Npc, "UpdateNPC", a =>
            {
                int i = (int)a[1]!; Require(ReferenceEquals(a[0], originals[i]), "NPC original slot receiver"); visited.Add(i); Mark("npc:" + i);
                Field(Entity, "whoAmI", 1000 + i, a[0]); Field(Player, "nearbyActiveNPCs", (float)visited.Count, players.GetValue(0));
                if (i == failure) throw WorkFailure;
                return null;
            });
            Configure("System.Void Terraria.NPC::.ctor()", a =>
            {
                replacements.Add(a[0]!); Mark("npc-new"); Field(Entity, "whoAmI", -99, a[0]); if (constructorFailure) throw ConstructorFailure; return null;
            });
        }
        void VerifyNpcs(Outcome o, bool ignore = false, int failure = -1, bool inactive = false, int netMode = 0, bool constructorFailure = false, bool preserveBrain = false)
        {
            bool aborted = failure >= 0 && (!ignore || constructorFailure);
            if (aborted) Error(o, constructorFailure ? ConstructorFailure : WorkFailure); else NoError(o);
            Visits(Enumerable.Range(0, aborted ? failure + 1 : 9));
            Require(Int(Npc, "offSetDelayTime") == 1 && Bool(Npc, "empressRageMode") == preserveBrain && Int(Npc, "brainOfGravity") == (preserveBrain && !inactive ? 5 : -1), "NPC delay/rage/brain state");
            Require(Bool(Frame, "AnyActiveBossNPC") == (aborted || !inactive), "NPC active boss/danger aggregate");
            Require(logs == (aborted ? 0 : 1) && strikes == (inactive || netMode == 1 ? 0 : 1), "NPC completion and after-party effects");
            Require(Bool(Main, "afterPartyOfDoom") == (netMode == 1), "NPC after-party clear/client guard");
            foreach (string field in new[] { "savedMech", "unlockedPartyGirlSpawn", "unlockedPrincessSpawn", "unlockedSlimeRainbowSpawn", "unlockedSlimeGreenSpawn", "boughtBunny" })
                Require(Bool(Npc, field) == (netMode == 1), "NPC after-party saved flag " + field);
            Require((float)ReadField(Player, "nearbyActiveNPCs", players!.GetValue(0))! == visited.Count, "NPC callback proximity state retained");
            Require(replacements.Count == (failure >= 0 && ignore ? 1 : 0), "NPC replacement constructor count");
            if (failure >= 0 && ignore)
            {
                Require(ReferenceEquals(entities!.GetValue(failure), constructorFailure ? originals[failure] : replacements[0]), "NPC replacement publication/throw order");
                if (!constructorFailure) Require(Int(Entity, "whoAmI", entities.GetValue(failure)) == -99, "NPC constructor state retained");
            }
            var expected = new List<string> { "boss-indexes", "sitting", "sleeping", "start", "clear-found", "found", "exploit" };
            if (netMode != 1) expected.Add("bestiary"); expected.AddRange(new[] { "damage", "rage-query" });
            if (netMode != 1) { expected.Add("party"); if (!inactive) expected.Add("strike:0"); }
            foreach (int i in visited) { expected.Add("npc:" + i); if (i == failure && ignore) expected.Add("npc-new"); }
            if (!aborted) expected.Add("time"); Expect(expected.ToArray());
        }
        void NpcScenarios()
        {
            Pair("npcs-active-inactive-boss-party-exclusions", Npcs, () => ConfigureNpcs(), Receiver, State, o => VerifyNpcs(o));
            Pair("npcs-all-inactive", Npcs, () => ConfigureNpcs(inactive: true), Receiver, State, o => VerifyNpcs(o, inactive: true));
            Pair("npcs-danger-without-boss", Npcs, () => ConfigureNpcs(dangerOnly: true), Receiver, State, o => VerifyNpcs(o));
            Pair("npcs-client-valid-brain-and-rage-retained", Npcs, () => ConfigureNpcs(netMode: 1, preserveBrain: true), Receiver, State, o => VerifyNpcs(o, netMode: 1, preserveBrain: true));
            Pair("npcs-ignore-original-error-replaces-and-continues", Npcs, () => ConfigureNpcs(true, 2), Receiver, State, o => VerifyNpcs(o, true, 2));
            Pair("npcs-rethrow-original-error", Npcs, () => ConfigureNpcs(false, 2), Receiver, State, o => VerifyNpcs(o, false, 2));
            Pair("npcs-replacement-constructor-error", Npcs, () => ConfigureNpcs(true, 2, constructorFailure: true), Receiver, State, o => VerifyNpcs(o, true, 2, constructorFailure: true));
            Pair("npcs-observer-exit-clock", Npcs, () => ConfigureNpcs(), Receiver, State, o => VerifyNpcs(o), observerClockOffset: 2);
            Negative("npcs-missing-observer", Npcs, () => ConfigureNpcs(), Receiver, State, o => VerifyNpcs(o));
        }

        void ConfigureSlots(bool projectile, bool ignore = false, int failure = -1, bool constructorFailure = false, string outerFailure = "")
        {
            string owner = projectile ? Projectile : Item; int count = projectile ? 1000 : 400;
            Time(projectile ? "UpdateProjectiles" : "UpdateItems"); entities = Slots(owner, count); originals = entities.Cast<object>().ToArray();
            Field(Main, projectile ? "projectile" : "item", entities); Field(Main, "ignoreErrors", ignore);
            if (projectile)
            {
                Field(Main, "ProjectileUpdateLoopIndex", 41); Field(Frame, "HadAnActiveInteractableProjectile", true);
                Marker("Terraria.GameInput.LockOnHelper", "SetUP", "lock-up"); Marker("Terraria.GameInput.LockOnHelper", "SetDOWN", "lock-down");
                ConfigureMember(Main, "PreUpdateAllProjectiles", a =>
                {
                    Require(ReferenceEquals(a[0], Instance(Main)) && !Bool(Frame, "HadAnActiveInteractableProjectile"), "projectile pre callback after flag reset");
                    Mark("pre"); if (outerFailure == "pre") throw WorkFailure; return null;
                });
                ConfigureMember(Main, "PostUpdateAllProjectiles", a =>
                {
                    Require(ReferenceEquals(a[0], Instance(Main)) && Int(Main, "ProjectileUpdateLoopIndex") == -1, "projectile post callback after loop sentinel reset");
                    Mark("post"); if (outerFailure == "post") throw WorkFailure; return null;
                });
            }
            ConfigureMember(owner, projectile ? "Update" : "UpdateItem", a =>
            {
                int i = (int)a[1]!; Require(ReferenceEquals(a[0], originals[i]), "entity loop slot receiver");
                if (projectile) Require(Int(Main, "ProjectileUpdateLoopIndex") == i, "projectile live loop index");
                visited.Add(i); Mark("slot:" + i);
                if (i == 0 || i == 7 || i == count - 1) Field(Entity, "whoAmI", i + 4000, a[0]);
                if (projectile && i == 7) Field(Frame, "HadAnActiveInteractableProjectile", true);
                if (i == failure) throw WorkFailure; return null;
            });
            Configure("System.Void " + owner + "::.ctor()", a =>
            {
                replacements.Add(a[0]!); Mark("replacement"); Field(Entity, "whoAmI", -99, a[0]);
                if (constructorFailure) throw ConstructorFailure; return null;
            });
        }
        void VerifySlots(Outcome o, bool projectile, bool ignore = false, int failure = -1, bool constructorFailure = false, string outerFailure = "")
        {
            int count = projectile ? 1000 : 400; bool childAbort = failure >= 0 && (!ignore || constructorFailure);
            bool aborted = childAbort || outerFailure.Length != 0;
            if (aborted) Error(o, constructorFailure ? ConstructorFailure : WorkFailure); else NoError(o);
            Visits(Enumerable.Range(0, outerFailure == "pre" ? 0 : childAbort ? failure + 1 : count));
            Require(logs == (aborted ? 0 : 1) && replacements.Count == (failure >= 0 && ignore ? 1 : 0), "entity log/replacement counts");
            if (failure >= 0 && ignore)
            {
                Require(ReferenceEquals(entities!.GetValue(failure), constructorFailure ? originals[failure] : replacements[0]), "entity replacement publication and exception order");
                if (!constructorFailure) Require(Int(Entity, "whoAmI", entities.GetValue(failure)) == (projectile ? -99 : failure), "replacement item index versus projectile constructor state");
            }
            for (int i = 0; i < count; i++)
            {
                bool replaced = i == failure && ignore && !constructorFailure;
                if (replaced) continue;
                int expected = visited.Contains(i) && (i == 0 || i == 7 || i == count - 1) ? i + 4000 : i;
                Require(Int(Entity, "whoAmI", originals[i]) == expected, "entity active/inactive bounded callback field at " + i);
            }
            if (projectile)
            {
                Require(Int(Main, "ProjectileUpdateLoopIndex") == (outerFailure == "pre" ? 41 : childAbort ? failure : -1), "projectile completion/error loop sentinel");
                Require(Bool(Frame, "HadAnActiveInteractableProjectile") == (visited.Count > 7), "projectile interactable flag state");
            }
            var expectedFlow = new List<string> { "start" };
            if (projectile) expectedFlow.AddRange(new[] { "lock-up", "pre" });
            foreach (int i in visited) { expectedFlow.Add("slot:" + i); if (i == failure && ignore) expectedFlow.Add("replacement"); }
            if (projectile && !childAbort && outerFailure != "pre") { expectedFlow.Add("post"); if (outerFailure != "post") expectedFlow.Add("lock-down"); }
            if (!aborted) expectedFlow.Add("time"); Expect(expectedFlow.ToArray());
        }
        void ProjectileScenarios()
        {
            Pair("projectiles-all-slots-active-inactive-order", Projectiles, () => ConfigureSlots(true), Receiver, State, o => VerifySlots(o, true));
            Pair("projectiles-ignore-original-error-replaces", Projectiles, () => ConfigureSlots(true, true, 7), Receiver, State, o => VerifySlots(o, true, true, 7));
            Pair("projectiles-rethrow-original-error", Projectiles, () => ConfigureSlots(true, false, 7), Receiver, State, o => VerifySlots(o, true, false, 7));
            Pair("projectiles-replacement-constructor-error", Projectiles, () => ConfigureSlots(true, true, 7, true), Receiver, State, o => VerifySlots(o, true, true, 7, true));
            foreach (string fault in new[] { "pre", "post" })
                Pair("projectiles-original-" + fault + "-error", Projectiles, () => ConfigureSlots(true, outerFailure: fault), Receiver, State, o => VerifySlots(o, true, outerFailure: fault));
            Pair("projectiles-observer-exit-clock", Projectiles, () => ConfigureSlots(true), Receiver, State, o => VerifySlots(o, true), observerClockOffset: 2);
            Negative("projectiles-missing-observer", Projectiles, () => ConfigureSlots(true), Receiver, State, o => VerifySlots(o, true));
        }
        void ItemScenarios()
        {
            Pair("items-all-slots-active-inactive-order", Items, () => ConfigureSlots(false), Receiver, State, o => VerifySlots(o, false));
            Pair("items-ignore-original-error-replaces-index", Items, () => ConfigureSlots(false, true, 7), Receiver, State, o => VerifySlots(o, false, true, 7));
            Pair("items-rethrow-original-error", Items, () => ConfigureSlots(false, false, 7), Receiver, State, o => VerifySlots(o, false, false, 7));
            Pair("items-replacement-constructor-error", Items, () => ConfigureSlots(false, true, 7, true), Receiver, State, o => VerifySlots(o, false, true, 7, true));
            Pair("items-observer-exit-clock", Items, () => ConfigureSlots(false), Receiver, State, o => VerifySlots(o, false), observerClockOffset: 2);
            Negative("items-missing-observer", Items, () => ConfigureSlots(false), Receiver, State, o => VerifySlots(o, false));
        }

        void ConfigureTiles(bool generating = false, bool reverseDraws = false, float wind = -0.83f, string failure = "")
        {
            counters = Enumerable.Repeat(2, 1000).ToArray(); frames = Enumerable.Repeat(1, 1000).ToArray();
            Field(Main, "tileFrameCounter", counters); Field(Main, "tileFrame", frames);
            foreach (var value in new[] { (12, 5, 9), (639, 5, 3), (739, 10, 3), (748, 7, 7), (133, 3, 5), (31, 7, 3) })
            { counters[value.Item1] = value.Item2; frames[value.Item1] = value.Item3; }
            for (int i = 340; i <= 344; i++) { counters[i] = 4; frames[i] = 3; }
            counters[453] = 58; counters[456] = 78; frames[412] = frames[456] = 239; counters[597] = 63;
            frames[999] = 123; counters[999] = 456;
            Field("Terraria.WorldGen", "isGeneratingOrLoadingWorld", generating); object random = New(Random); Field(Main, "rand", random);
            Configure("System.Int32 Terraria.Utilities.UnifiedRandom::Next(System.Int32)", a =>
            {
                Require(!generating && ReferenceEquals(a[0], random) && (int)a[1]! == 3 && draws.Count < 2, "tile exact RNG receiver/bound/count");
                int result = reverseDraws ? (draws.Count == 0 ? 0 : 2) : (draws.Count == 0 ? 2 : 0);
                draws.Add(result); Mark("rng:" + result);
                if (failure == "rng" + draws.Count) throw WorkFailure;
                return result;
            });
            ConfigureMember(Main, "get_WindForVisuals", _ => { windCalls++; Mark("wind:" + windCalls); return wind; });
            Configure("System.Single Microsoft.Xna.Framework.MathHelper::Clamp(System.Single,System.Single,System.Single)", a =>
            {
                Require((float)a[1]! == -5f && (float)a[2]! == 5f, "tile wind clamp bounds");
                float expected = wind < 0 ? -8f : 8f; Require((float)a[0]! == expected, "actual tile wind arithmetic before clamp");
                clampCalls++; Mark("clamp:" + clampCalls); return wind < 0 ? -5f : 5f;
            });
            ConfigureMember(Main, "AnimateTiles_WeatherVane", _ =>
            {
                Require(counters[489] == (wind < 0 ? 317 : 7) && counters[493] == 2 && windCalls == 2 && clampCalls == 1, "weather callback after first wind animation only");
                frames[999] = 777; Mark("weather"); if (failure == "weather") throw WorkFailure; return null;
            });
            ConfigureMember(Main, "AnimateTiles_CritterCages", _ =>
            {
                Require(counters[493] == (wind < 0 ? 117 : 7) && frames[999] == 777 && windCalls == 4 && clampCalls == 2, "critter callback after weather and second wind animation");
                counters[999] = 991; Mark("cages"); if (failure == "cages") throw WorkFailure; return null;
            });
        }
        void VerifyTiles(Outcome o, bool generating = false, bool reverseDraws = false, float wind = -0.83f, string failure = "")
        {
            if (failure.Length == 0) NoError(o); else Error(o, WorkFailure);
            Require(counters[12] == 0 && frames[12] == 0 && counters[665] == 0 && frames[665] == 0, "tile12 wrap and tile665 alias copy");
            Require(counters[639] == 0 && frames[639] == 4 && frames[739] == 0 && frames[748] == 0 && frames[133] == 0 && frames[31] == 0 && frames[696] == 0, "tile selected wrap/nonwrap/copy values");
            Require(Enumerable.Range(340, 5).All(i => counters[i] == 0 && frames[i] == 0), "tile340-through344 inclusive range loop");
            int count = generating ? 0 : failure == "rng1" ? 1 : 2;
            Require(draws.SequenceEqual((reverseDraws ? new[] { 0, 2 } : new[] { 2, 0 }).Take(count)), "tile exact random sequence");
            int first = generating || reverseDraws ? 59 : 0, second = !generating && reverseDraws ? 0 : 79;
            Require(counters[453] == (failure == "rng1" ? 58 : first), "tile453 actual first random consumption and wrap");
            Require(counters[456] == (failure is "rng1" or "rng2" ? 78 : second), "tile456 actual second random consumption and wrap");
            bool early = failure is "rng1" or "rng2";
            Require(frames[412] == (failure == "rng1" ? 239 : 0) && frames[456] == (early ? 239 : 0), "tile state preceding original random failure");
            Require(windCalls == (early ? 0 : failure == "weather" ? 2 : 4) && clampCalls == (early ? 0 : failure == "weather" ? 1 : 2), "tile wind call order/count");
            Require(counters[489] == (early ? 2 : wind < 0 ? 317 : 7) && counters[493] == (early || failure == "weather" ? 2 : wind < 0 ? 117 : 7), "tile signed modulo correction");
            Require(frames[999] == (early ? 123 : 777) && counters[999] == (early || failure == "weather" ? 456 : 991), "tile child callback side effects");
            if (!early) Require(counters[597] == 0, "tile long counter wrap");
            var expected = draws.Select(x => "rng:" + x).ToList();
            if (!early) { expected.AddRange(new[] { "wind:1", "wind:2", "clamp:1", "weather" }); if (failure != "weather") expected.AddRange(new[] { "wind:3", "wind:4", "clamp:2", "cages" }); }
            Expect(expected.ToArray());
        }
        void TileScenarios()
        {
            Pair("tiles-nonempty-rollovers-rng-negative-wind", Tiles, () => ConfigureTiles(), Args, State, o => VerifyTiles(o));
            Pair("tiles-opposite-rng-values-positive-wind", Tiles, () => ConfigureTiles(reverseDraws: true, wind: 0.83f), Args, State, o => VerifyTiles(o, reverseDraws: true, wind: 0.83f));
            Pair("tiles-generating-skips-both-random-draws", Tiles, () => ConfigureTiles(generating: true), Args, State, o => VerifyTiles(o, generating: true));
            foreach (string fault in new[] { "rng1", "rng2", "weather", "cages" })
                Pair("tiles-original-" + fault + "-error", Tiles, () => ConfigureTiles(failure: fault), Args, State, o => VerifyTiles(o, failure: fault));
            Pair("tiles-observer-exit-clock", Tiles, () => ConfigureTiles(), Args, State, o => VerifyTiles(o), observerClockOffset: 2);
            Negative("tiles-missing-observer", Tiles, () => ConfigureTiles(), Args, State, o => VerifyTiles(o));
        }

        void ConfigureWalls(byte phase = 4)
        {
            wallCounters = new byte[400]; wallFrames = Enumerable.Repeat((byte)6, 400).ToArray();
            Field(Main, "wallFrameCounter", wallCounters); Field(Main, "wallFrame", wallFrames);
            wallCounters[357] = 4; wallFrames[357] = 7; wallCounters[365] = 7;
            wallCounters[136] = 4; wallFrames[136] = 7; wallCounters[137] = 9;
            wallCounters[226] = 9; wallCounters[227] = 4; wallCounters[225] = 4; wallFrames[225] = 1;
            wallCounters[172] = 9; wallCounters[347] = 9; wallCounters[168] = 4; wallCounters[169] = 4;
            wallCounters[242] = 159; wallCounters[243] = 158; wallCounters[144] = phase;
            wallCounters[399] = 77; wallFrames[399] = 88;
        }
        void VerifyWalls(Outcome o, byte phase = 4, byte expectedFrame = 2)
        {
            NoError(o);
            Require(wallCounters[357] == 0 && wallFrames[357] == 0 && wallCounters[365] == 0 && wallFrames[365] == 7, "wall357 wrap and wall365 advance");
            Require(wallCounters[136] == 0 && wallFrames[136] == 0 && wallCounters[137] == 0 && wallFrames[137] == 7, "wall136 wrap and wall137 advance");
            foreach (int i in new[] { 226, 227, 172, 347, 168, 169 }) Require(wallCounters[i] == 0 && wallFrames[i] == 7, "wall threshold counter/frame " + i);
            Require(wallCounters[225] == 0 && wallFrames[225] == 0 && wallCounters[242] == 0 && wallCounters[243] == 159, "wall225 short wrap and wall242/243 long counters");
            Require(wallFrames[144] == expectedFrame && wallCounters[144] == (phase is 180 or 255 ? 0 : phase + 1), "wall144 actual early-return ladder and byte wrap");
            Require(wallCounters[399] == 77 && wallFrames[399] == 88 && flow.Count == 0, "wall unrelated sentinels/no external calls");
        }
        void WallScenarios()
        {
            foreach (var sample in new (byte Counter, byte Frame)[] { (0, 0), (4, 2), (9, 3), (14, 4), (19, 5), (24, 6), (29, 7), (34, 8), (89, 7), (94, 6), (99, 5), (104, 4), (109, 3), (114, 2), (119, 1), (124, 0), (179, 0), (180, 0), (255, 0) })
                Pair("walls-counter-phase-" + sample.Counter, Walls, () => ConfigureWalls(sample.Counter), Args, State, o => VerifyWalls(o, sample.Counter, sample.Frame));
            Pair("walls-observer-exit-clock", Walls, () => ConfigureWalls(), Args, State, o => VerifyWalls(o), observerClockOffset: 2);
            Negative("walls-missing-observer", Walls, () => ConfigureWalls(), Args, State, o => VerifyWalls(o));
        }

        void MutantScenarios()
        {
            SemanticNegative("players-mutant-last-slot-skipped", "EntitiesSkipLastPlayer", Players, () => ConfigurePlayers(), Receiver, State, o => VerifyPlayers(o));
            SemanticNegative("players-mutant-rethrow-policy-inverted", "EntitiesInvertedErrorPolicy", Players, () => ConfigurePlayers(false, 0), Receiver, State, o => VerifyPlayers(o, false, 0));
            SemanticNegative("npcs-mutant-delay-increments", "EntitiesNpcDelayIncreases", Npcs, () => ConfigureNpcs(), Receiver, State, o => VerifyNpcs(o));
            SemanticNegative("npcs-mutant-rethrow-policy-inverted", "EntitiesInvertedErrorPolicy", Npcs, () => ConfigureNpcs(false, 2), Receiver, State, o => VerifyNpcs(o, false, 2));
            SemanticNegative("projectiles-mutant-index-not-restored", "EntitiesProjectileIndexNotRestored", Projectiles, () => ConfigureSlots(true), Receiver, State, o => VerifySlots(o, true));
            SemanticNegative("projectiles-mutant-rethrow-policy-inverted", "EntitiesInvertedErrorPolicy", Projectiles, () => ConfigureSlots(true, false, 7), Receiver, State, o => VerifySlots(o, true, false, 7));
            SemanticNegative("items-mutant-replacement-index-lost", "EntitiesReplacementItemIndexLost", Items, () => ConfigureSlots(false, true, 7), Receiver, State, o => VerifySlots(o, false, true, 7));
            SemanticNegative("items-mutant-rethrow-policy-inverted", "EntitiesInvertedErrorPolicy", Items, () => ConfigureSlots(false, false, 7), Receiver, State, o => VerifySlots(o, false, false, 7));
            SemanticNegative("tiles-mutant-first-counter-decrements", "EntitiesTileCounterDecreases", Tiles, () => ConfigureTiles(), Args, State, o => VerifyTiles(o));
            SemanticNegative("tiles-mutant-first-random-draw-lost", "EntitiesTileRandomDrawLost", Tiles, () => ConfigureTiles(), Args, State, o => VerifyTiles(o));
            SemanticNegative("tiles-mutant-child-callbacks-reordered", "EntitiesTileCallbacksReordered", Tiles, () => ConfigureTiles(), Args, State, o => VerifyTiles(o));
            SemanticNegative("walls-mutant-first-counter-decrements", "EntitiesWallCounterDecreases", Walls, () => ConfigureWalls(), Args, State, o => VerifyWalls(o));
        }
    }
}
