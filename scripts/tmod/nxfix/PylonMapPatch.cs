using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Pylon map tint. TeleportPylonsMapLayer.Draw runs on every map draw (the minimap every frame) and
// starts with TeleportPylonsSystem.IsPlayerNearAPylon(Main.LocalPlayer): for every type in
// TileID.Sets.CountsAsPylon, Player.IsTileTypeInInteractionRange walks each tile within the pylon
// reach (about 120 x 120 tiles). In run 44 that walk was 1.4% of all game-thread time and 0.46 ms
// of each frame late in the session. Draw uses the result only for the icons' tint (gray when no
// pylon is in range, or while the drone camera is in use); a click still goes through
// RequestTeleportation, whose HandleTeleportRequest calls IsPlayerNearAPylon itself.
//
// Draw now calls TeleportPylonsMapLayer.NxIsPlayerNearAPylonCached(player), which reruns the walk
// when the player object changes or 30 game updates (0.5 s) after the last walk, and otherwise
// returns the last result. The tint can lag a pylon placed, broken or walked into range by up to
// half a second; teleport checks are unchanged.
static class PylonMapPatch
{
    const uint RefreshUpdates = 30;

    public static void Apply(ModuleDefinition module)
    {
        var layer = module.GetType("Terraria.Map.TeleportPylonsMapLayer")
            ?? throw new InvalidOperationException("Terraria.Map.TeleportPylonsMapLayer not found");
        var draw = layer.Methods.Single(m => m.Name == "Draw" && m.HasBody);
        var calls = draw.Body.Instructions.Where(i => i.Operand is MethodReference
            { Name: "IsPlayerNearAPylon", DeclaringType.FullName: "Terraria.GameContent.TeleportPylonsSystem" }).ToList();
        if (calls.Count != 1 || calls[0].OpCode != OpCodes.Call
            || calls[0].Previous?.Operand is not MethodReference { Name: "get_LocalPlayer", DeclaringType.FullName: "Terraria.Main" })
            throw new InvalidOperationException("TeleportPylonsMapLayer.Draw: expected one IsPlayerNearAPylon(Main.LocalPlayer) call");
        var nearPylon = (MethodReference)calls[0].Operand;
        if (nearPylon.ReturnType.MetadataType != MetadataType.Boolean || nearPylon.HasThis || nearPylon.Parameters.Count != 1)
            throw new InvalidOperationException("unexpected IsPlayerNearAPylon signature: " + nearPylon.FullName);
        var player = nearPylon.Parameters[0].ParameterType;
        var updateCount = module.GetType("Terraria.Main").Methods.Single(m => m.Name == "get_GameUpdateCount" && m.IsStatic);
        if (updateCount.ReturnType.MetadataType != MetadataType.UInt32)
            throw new InvalidOperationException("unexpected Main.GameUpdateCount type: " + updateCount.ReturnType);

        var boolType = module.TypeSystem.Boolean;
        var cached = AddStaticField(layer, "_nxNearPylon", boolType);
        var valid = AddStaticField(layer, "_nxNearPylonValid", boolType);
        var walkedAt = AddStaticField(layer, "_nxNearPylonUpdate", module.TypeSystem.UInt32);
        var walkedFor = AddStaticField(layer, "_nxNearPylonPlayer", player);

        var helper = new MethodDefinition("NxIsPlayerNearAPylonCached",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, boolType);
        helper.Parameters.Add(new ParameterDefinition("player", ParameterAttributes.None, player));
        var now = new VariableDefinition(module.TypeSystem.UInt32);
        helper.Body.Variables.Add(now);
        helper.Body.InitLocals = true;
        var il = helper.Body.GetILProcessor();
        var walk = il.Create(OpCodes.Ldarg_0);
        var done = il.Create(OpCodes.Ldsfld, cached);
        foreach (var instruction in new[]
        {
            il.Create(OpCodes.Call, updateCount),
            il.Create(OpCodes.Stloc, now),
            il.Create(OpCodes.Ldsfld, valid),
            il.Create(OpCodes.Brfalse, walk),
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldsfld, walkedFor),
            il.Create(OpCodes.Bne_Un, walk),
            il.Create(OpCodes.Ldloc, now),
            il.Create(OpCodes.Ldsfld, walkedAt),
            il.Create(OpCodes.Sub),
            il.Create(OpCodes.Ldc_I4, (int)RefreshUpdates),
            il.Create(OpCodes.Blt_Un, done),
            walk,
            il.Create(OpCodes.Call, nearPylon),
            il.Create(OpCodes.Stsfld, cached),
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Stsfld, walkedFor),
            il.Create(OpCodes.Ldloc, now),
            il.Create(OpCodes.Stsfld, walkedAt),
            il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Stsfld, valid),
            done,
            il.Create(OpCodes.Ret),
        })
            il.Append(instruction);
        layer.Methods.Add(helper);
        VerifyPatched(helper, true, "");

        calls[0].Operand = helper;
        VerifyPatched(draw, true, "");
        Console.WriteLine($"FIX TeleportPylonsMapLayer.Draw: pylon-range tint walks the tiles once per {RefreshUpdates} updates (teleport checks unchanged)");
    }

    static FieldDefinition AddStaticField(TypeDefinition type, string name, TypeReference fieldType)
    {
        if (type.Fields.Any(f => f.Name == name))
            throw new InvalidOperationException($"{type.FullName} already has {name}");
        var field = new FieldDefinition(name, FieldAttributes.Private | FieldAttributes.Static, fieldType);
        type.Fields.Add(field);
        return field;
    }
}
