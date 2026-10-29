using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Lighting-cycle RenderBlack. Main.RenderTiles runs on the renderCount 3 frame of each legacy
// lighting cycle (about five a second in run 41) and starts with RenderBlack, whose DrawBlack walks
// every on-screen tile through Lighting.Brightness: about 5 ms of the 6.5 ms RenderTiles frame cost
// (2.3% of game-thread time). blackTarget is drawn at sceneTilePos, the position captured for
// tileTarget, so it could not be refreshed on another frame.
//
// blackTarget now keeps its own capture position (Main._nxSceneBlackPos, set whenever RenderBlack
// runs) and DoDraw_WallsAndBlacks draws it there. On the renderCount 3 frame, when the legacy engine
// is active and LegacyLighting.RenderPhases > 4 (so renderCount 4 is not the PreRenderPhase frame),
// RenderTiles leaves RenderBlack pending; Main.DoDraw renders it on the next frame whose renderCount
// is not 3 (renderCount 4, which does no other render or lighting work). Both frames read the same
// processed lighting (CopyFullyProcessedDataOver runs on the renderCount 1 frame). Like tileTarget,
// blackTarget is also refreshed at once when the camera is more than offScreenRange from its capture
// position. renderNow refreshes, Color lighting and RenderPhases 4 render it inside RenderTiles as before.
static class LightingCyclePatch
{
    public static void Apply(ModuleDefinition module)
    {
        var main = module.GetType("Terraria.Main");
        var renderTiles = main.Methods.Single(m => m.Name == "RenderTiles" && m.HasBody && !m.IsStatic && m.Parameters.Count == 0);
        var renderBlack = main.Methods.Single(m => m.Name == "RenderBlack" && m.HasBody && !m.IsStatic && m.Parameters.Count == 0);
        var wallsAndBlacks = main.Methods.Single(m => m.Name == "DoDraw_WallsAndBlacks" && m.HasBody && !m.IsStatic);
        var doDraw = main.Methods.Single(m => m.Name == "DoDraw" && m.HasBody);
        if (!IsVoid(renderTiles.ReturnType) || !IsVoid(renderBlack.ReturnType))
            throw new InvalidOperationException("unexpected RenderTiles/RenderBlack signature");

        FieldDefinition Field(TypeDefinition type, string name) => type.Fields.SingleOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException($"{type.Name}.{name} not found");
        var renderCount = Field(main, "renderCount");
        var drawToScreen = Field(main, "drawToScreen");
        var screenPosition = Field(main, "screenPosition");
        var offScreenRange = Field(main, "offScreenRange");
        var sceneTilePos = Field(main, "sceneTilePos");
        var sceneTile2Pos = Field(main, "sceneTile2Pos");
        var blackTarget = Field(main, "blackTarget");
        var renderPhases = Field(module.GetType("Terraria.Graphics.Light.LegacyLighting"), "RenderPhases");
        var usingNewLighting = module.GetType("Terraria.Lighting").Methods.Single(m => m.Name == "get_UsingNewLighting");
        foreach (var f in new[] { renderCount, drawToScreen, screenPosition, offScreenRange, sceneTilePos, sceneTile2Pos, renderPhases })
            if (!f.IsStatic)
                throw new InvalidOperationException($"{f.FullName} is not static");
        if (!usingNewLighting.IsStatic || usingNewLighting.Parameters.Count != 0 || usingNewLighting.ReturnType.MetadataType != MetadataType.Boolean
            || renderCount.FieldType.MetadataType != MetadataType.Int32 || offScreenRange.FieldType.MetadataType != MetadataType.Int32
            || renderPhases.FieldType.MetadataType != MetadataType.Int32 || drawToScreen.FieldType.MetadataType != MetadataType.Boolean
            || sceneTilePos.FieldType.FullName != "Microsoft.Xna.Framework.Vector2" || screenPosition.FieldType.FullName != sceneTilePos.FieldType.FullName)
            throw new InvalidOperationException("unexpected Main/Lighting member types");
        var vector2 = sceneTilePos.FieldType;
        FieldReference VectorAxis(string name) => doDraw.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>()
            .First(f => f.Name == name && f.DeclaringType.FullName == vector2.FullName && f.FieldType.MetadataType == MetadataType.Single);
        var vectorX = VectorAxis("X");
        var vectorY = VectorAxis("Y");

        // DoDraw's out-of-range refresh of tileTarget: RenderTiles(), sceneTilePos = screenPosition -
        // offScreenRange, then the tile2Target range check, which every path through the tile check reaches.
        var tileCalls = doDraw.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand == renderTiles).ToList();
        if (tileCalls.Count != 3)
            throw new InvalidOperationException($"Main.DoDraw: {tileCalls.Count} RenderTiles calls, expected 3");
        var anchors = new List<Instruction>();
        foreach (var call in tileCalls)
        {
            var next = call.Next;
            for (int i = 0; i < 14; i++, next = next?.Next)
                if (next == null || i is 0 or 7 && !(next.OpCode == OpCodes.Ldsflda && next.Operand == sceneTilePos)
                    || i is 6 or 13 && !(next.OpCode == OpCodes.Stfld && next.Operand is FieldReference { Name: "X" or "Y" }))
                    throw new InvalidOperationException("Main.DoDraw: RenderTiles not followed by the sceneTilePos update at " + call);
            if (next?.OpCode == OpCodes.Ldsflda && next.Operand == sceneTile2Pos && next.Next?.OpCode == OpCodes.Ldfld)
                anchors.Add(next);
        }
        if (anchors.Count != 1)
            throw new InvalidOperationException($"Main.DoDraw: {anchors.Count} out-of-range RenderTiles refreshes, expected 1");
        var anchor = anchors[0];
        if (doDraw.Body.ExceptionHandlers.Any(h => h.TryStart == anchor || h.TryEnd == anchor || h.HandlerStart == anchor || h.HandlerEnd == anchor)
            || !doDraw.Body.Instructions.Any(i => i.OpCode.FlowControl == FlowControl.Cond_Branch && i.Operand == anchor)
            || doDraw.Body.Instructions.Any(i => i.Operand is Instruction[] targets && targets.Contains(anchor)))
            throw new InvalidOperationException("Main.DoDraw: unexpected branches to the tile2Target range check");

        // DoDraw_WallsAndBlacks: spriteBatch.Draw(blackTarget, sceneTilePos - screenPosition, Color.White).
        var blackPosReads = wallsAndBlacks.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld && i.Operand == blackTarget).ToList();
        if (blackPosReads.Count != 1 || blackPosReads[0].Next is not { OpCode.Code: Code.Ldsfld } blackPos || blackPos.Operand != sceneTilePos
            || blackPos.Next is not { OpCode.Code: Code.Ldsfld } screenRead || screenRead.Operand != screenPosition
            || screenRead.Next?.Operand is not MethodReference { Name: "op_Subtraction" })
            throw new InvalidOperationException("Main.DoDraw_WallsAndBlacks: unexpected blackTarget draw");

        // RenderTiles: if (drawToScreen) return; RenderBlack(); ...
        var blackCalls = renderTiles.Body.Instructions.Where(i => i.Operand is MethodReference { Name: "RenderBlack" }).ToList();
        if (blackCalls.Count != 1 || blackCalls[0].OpCode != OpCodes.Call || blackCalls[0].Operand != renderBlack
            || blackCalls[0].Previous?.OpCode != OpCodes.Ldarg_0)
            throw new InvalidOperationException("Main.RenderTiles: unexpected RenderBlack call");
        if (main.Methods.Any(m => m.Name.StartsWith("NxRenderBlack")) || main.Fields.Any(f => f.Name is "_nxSceneBlackPos" or "_nxBlackPending"))
            throw new InvalidOperationException("Main: lighting-cycle patch already applied");

        var blackScenePos = new FieldDefinition("_nxSceneBlackPos", FieldAttributes.Private | FieldAttributes.Static, vector2);
        var blackPending = new FieldDefinition("_nxBlackPending", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Boolean);
        main.Fields.Add(blackScenePos);
        main.Fields.Add(blackPending);

        MethodDefinition Helper(string name)
        {
            var method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
            main.Methods.Add(method);
            return method;
        }
        // _nxSceneBlackPos.<axis> = screenPosition.<axis> - offScreenRange;
        void StoreAxis(ILProcessor il, FieldReference axis)
        {
            il.Emit(OpCodes.Ldsflda, blackScenePos);
            il.Emit(OpCodes.Ldsflda, screenPosition);
            il.Emit(OpCodes.Ldfld, axis);
            il.Emit(OpCodes.Ldsfld, offScreenRange);
            il.Emit(OpCodes.Conv_R4);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Stfld, axis);
        }

        // NxRenderBlackNow: RenderBlack(); _nxBlackPending = false; _nxSceneBlackPos = screenPosition - offScreenRange.
        var renderNow = Helper("NxRenderBlackNow");
        {
            var il = renderNow.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, renderBlack);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stsfld, blackPending);
            StoreAxis(il, vectorX);
            StoreAxis(il, vectorY);
            il.Emit(OpCodes.Ret);
        }

        // NxRenderBlackForTiles (RenderTiles' RenderBlack call):
        // if (renderCount == 3 && LegacyLighting.RenderPhases > 4 && !Lighting.UsingNewLighting) _nxBlackPending = true;
        // else NxRenderBlackNow();
        var forTiles = Helper("NxRenderBlackForTiles");
        {
            var il = forTiles.Body.GetILProcessor();
            var now = il.Create(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldsfld, renderCount);
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Bne_Un, now);
            il.Emit(OpCodes.Ldsfld, renderPhases);
            il.Emit(OpCodes.Ldc_I4_4);
            il.Emit(OpCodes.Ble, now);
            il.Emit(OpCodes.Call, usingNewLighting);
            il.Emit(OpCodes.Brtrue, now);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stsfld, blackPending);
            il.Emit(OpCodes.Ret);
            il.Append(now);
            il.Emit(OpCodes.Call, renderNow);
            il.Emit(OpCodes.Ret);
        }

        // NxRenderBlackIfDue (DoDraw, after the tileTarget range check):
        // if (drawToScreen) return;
        // if (_nxBlackPending && renderCount != 3
        //     || Math.Abs(_nxSceneBlackPos.X - (screenPosition.X - offScreenRange)) > offScreenRange
        //     || Math.Abs(_nxSceneBlackPos.Y - (screenPosition.Y - offScreenRange)) > offScreenRange)
        //     NxRenderBlackNow();
        var abs = doDraw.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .First(m => m.Name == "Abs" && m.DeclaringType.FullName == "System.Math" && m.ReturnType.MetadataType == MetadataType.Single
                && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Single);
        var ifDue = Helper("NxRenderBlackIfDue");
        {
            var il = ifDue.Body.GetILProcessor();
            var ret = il.Create(OpCodes.Ret);
            var render = il.Create(OpCodes.Ldarg_0);
            var check = il.Create(OpCodes.Ldsflda, blackScenePos);
            il.Emit(OpCodes.Ldsfld, drawToScreen);
            il.Emit(OpCodes.Brtrue, ret);
            il.Emit(OpCodes.Ldsfld, blackPending);
            il.Emit(OpCodes.Brfalse, check);
            il.Emit(OpCodes.Ldsfld, renderCount);
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Bne_Un, render);
            foreach (var axis in new[] { vectorX, vectorY })
            {
                if (axis == vectorX)
                    il.Append(check);
                else
                    il.Emit(OpCodes.Ldsflda, blackScenePos);
                il.Emit(OpCodes.Ldfld, axis);
                il.Emit(OpCodes.Ldsflda, screenPosition);
                il.Emit(OpCodes.Ldfld, axis);
                il.Emit(OpCodes.Ldsfld, offScreenRange);
                il.Emit(OpCodes.Conv_R4);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Call, abs);
                il.Emit(OpCodes.Ldsfld, offScreenRange);
                il.Emit(OpCodes.Conv_R4);
                il.Emit(axis == vectorX ? OpCodes.Bgt : OpCodes.Ble_Un, axis == vectorX ? render : ret);
            }
            il.Append(render);
            il.Emit(OpCodes.Call, renderNow);
            il.Append(ret);
        }
        foreach (var helper in new[] { renderNow, forTiles, ifDue })
            VerifyPatched(helper, true, "");

        blackCalls[0].Operand = forTiles;
        VerifyPatched(renderTiles, true, "");
        Console.WriteLine("FIX Main.RenderTiles: RenderBlack waits for the next frame on the legacy renderCount 3 frame (RenderPhases > 4)");

        blackPos.Operand = blackScenePos;
        VerifyPatched(wallsAndBlacks, true, "");
        Console.WriteLine("FIX Main.DoDraw_WallsAndBlacks: blackTarget drawn at its own capture position");

        WidenShortBranches(doDraw.Body);
        var dueCall = Instruction.Create(OpCodes.Ldarg_0);
        foreach (var ins in doDraw.Body.Instructions)
            if (ins.Operand == anchor)
                ins.Operand = dueCall;
        var doDrawIl = doDraw.Body.GetILProcessor();
        doDrawIl.InsertBefore(anchor, dueCall);
        doDrawIl.InsertBefore(anchor, Instruction.Create(OpCodes.Call, ifDue));
        VerifyPatched(doDraw, true, "");
        Console.WriteLine("FIX Main.DoDraw: pending or out-of-range blackTarget refreshed after the tileTarget range check");
    }
}
