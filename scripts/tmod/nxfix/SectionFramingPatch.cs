using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Section pre-framing. Tiles are framed per 200x150-tile world section on first sight:
// TileDrawing.Update -> EnsureWindGridSize -> GetScreenDrawArea -> WorldGen.SectionTileFrameWithCheck
// reframes every loaded, unframed section the drawn area touches (WorldGen.SectionTileFrame: 30,704
// TileFrame + WallFrame calls, mods' hooks included) inside one update. In run 41 that was 5 of the 7
// slow in-world windows, 88-156 ms of framing each, all on that path.
// WorldSections.NxPreframe() now runs in TileDrawing.Update just before EnsureWindGridSize. It takes
// the loaded, unframed section nearest the drawn area (the area GetScreenDrawArea computes there),
// within MarginX/MarginY tiles of it, and reframes it exactly as SectionTileFrame does (Reframe(x, y,
// true) over the same bounds in the same order, map updates off) for up to 1.5 ms per drawn frame,
// resuming where it stopped. After the last tile it bumps Main.mapTime and calls SetSectionFramed like
// SectionTileFrame, so the lazy path skips the section. A section the drawn area already touches is
// finished at once (the lazy path would frame all of it right after). The section in progress restarts
// when tile data arrives for any section (WorldSections.SetSectionLoaded: multiplayer tile blocks,
// PurgeUnloaded) or invisible-block visibility changes, which TileFrame reads
// (SetAllFramedSectionsAsNeedingRefresh), and is dropped when the camera moves twice the margins away
// (teleports). As on the lazy path, only sections whose tiles are loaded are framed (multiplayer).
static class SectionFramingPatch
{
    const int MarginX = 100, MarginY = 75; // tiles beyond the drawn area: half a section
    const int BudgetDivisor = 667;          // Stopwatch.Frequency / 667 = 1.5 ms per drawn frame
    const int ClockEvery = 8;               // tiles between clock reads (a tile takes about 3-5 us)

    public static void Apply(ModuleDefinition module)
    {
        var main = module.GetType("Terraria.Main");
        var worldGen = module.GetType("Terraria.WorldGen");
        var sections = module.GetType("Terraria.WorldSections");
        var tileDrawing = module.GetType("Terraria.GameContent.Drawing.TileDrawing");
        var netplay = module.GetType("Terraria.Netplay");
        var int32 = module.TypeSystem.Int32;

        // What the helper reproduces: SectionTileFrame's per-tile work and bounds, and the lazy check.
        var sectionTileFrame = Method(worldGen, "SectionTileFrame", int32, int32, int32, int32);
        Expect(sectionTileFrame,
            "ldsfld System.Int32 Terraria.Main::mapTimeMax", "ldc.i4.s 10", "add", "stsfld System.Int32 Terraria.Main::mapTime",
            "ldc.i4.1", "stsfld System.Boolean Terraria.WorldGen::noMapUpdate",
            "ldarg.0", "ldarg.1", "ldarg.2", "ldarg.3", "ldloca.s V_0", "ldloca.s V_1", "ldloca.s V_2", "ldloca.s V_3",
            "call System.Void Terraria.WorldGen::GetSectionBounds(System.Int32,System.Int32,System.Int32,System.Int32,System.Int32&,System.Int32&,System.Int32&,System.Int32&)",
            "ldloc.0", "ldc.i4.1", "sub", "stloc.s V_4", "br");
        Expect(sectionTileFrame, "ldloc.2", "ldc.i4.1", "sub", "stloc.s V_5", "br");
        Expect(sectionTileFrame,
            "ldloc.s V_4", "ldloc.s V_5", "ldc.i4.1",
            "call System.Void Terraria.WorldGen::Reframe(System.Int32,System.Int32,System.Boolean)",
            "ldloc.s V_5", "ldc.i4.1", "add", "stloc.s V_5", "ldloc.s V_5", "ldloc.3", "ldc.i4.1", "add", "blt",
            "ldloc.s V_4", "ldc.i4.1", "add", "stloc.s V_4", "ldloc.s V_4", "ldloc.1", "ldc.i4.1", "add", "blt");
        Expect(sectionTileFrame, "callvirt System.Void Terraria.WorldSections::SetSectionFramed(System.Int32,System.Int32)");
        Expect(sectionTileFrame, "ldc.i4.0", "stsfld System.Boolean Terraria.WorldGen::noMapUpdate", "ret");
        var calls = sectionTileFrame.Body.Instructions.Where(i => i.OpCode.FlowControl == FlowControl.Call)
            .Select(i => ((MethodReference)i.Operand).Name).ToList();
        if (!calls.SequenceEqual(new[] { "GetSectionBounds", "get_Item", "op_Equality", "set_Item", "Reframe", "SetSectionFramed" }))
            throw new InvalidOperationException("unexpected WorldGen.SectionTileFrame calls: " + string.Join(", ", calls));
        var getSectionBounds = Callee(sectionTileFrame, "GetSectionBounds");
        var reframe = Callee(sectionTileFrame, "Reframe");
        var setSectionFramed = Callee(sectionTileFrame, "SetSectionFramed");
        // The skipped `if (Main.tile[x, y] == null)` branch: Tile is a struct and this operator is constant false.
        var tileIsNull = Method(module.GetType("Terraria.Tile"), "op_Equality", module.GetType("Terraria.Tile"),
            new TypeReference("System", "ArgumentException", module, module.TypeSystem.CoreLibrary));
        if (!Text(tileIsNull).SequenceEqual(new[] { "ldc.i4.0", "ret" }))
            throw new InvalidOperationException("Tile == null is no longer constant false");
        var boundsParameters = getSectionBounds.Parameters.Skip(4).Select(p => p.Name);
        if (!boundsParameters.SequenceEqual(new[] { "tileStartX", "tileEndX", "tileStartY", "tileEndY" }))
            throw new InvalidOperationException("unexpected WorldGen.GetSectionBounds parameters");

        var withCheck = Method(worldGen, "SectionTileFrameWithCheck", int32, int32, int32, int32);
        Expect(withCheck, "callvirt System.Boolean Terraria.WorldSections::SectionLoaded(System.Int32,System.Int32)", "brfalse");
        Expect(withCheck, "callvirt System.Boolean Terraria.WorldSections::SectionFramed(System.Int32,System.Int32)", "brtrue");
        var sectionLoaded = Callee(withCheck, "SectionLoaded");
        var sectionFramed = Callee(withCheck, "SectionFramed");
        Expect(withCheck, "call System.Void Terraria.WorldGen::SectionTileFrame(System.Int32,System.Int32,System.Int32,System.Int32)");

        // The drawn area the helper computes: GetScreenDrawArea's, from EnsureWindGridSize's arguments.
        var ensureWindGrid = Method(tileDrawing, "EnsureWindGridSize");
        var vector2 = ensureWindGrid.Body.Variables[0].VariableType;
        var cameraRead = Expect(ensureWindGrid,
            "ldsfld Terraria.Graphics.Camera Terraria.Main::Camera",
            "callvirt Microsoft.Xna.Framework.Vector2 Terraria.Graphics.Camera::get_UnscaledPosition()", "stloc.0",
            "ldloca.s V_1", "ldsfld System.Int32 Terraria.Main::offScreenRange", "conv.r4",
            "ldsfld System.Int32 Terraria.Main::offScreenRange", "conv.r4",
            "call System.Void Microsoft.Xna.Framework.Vector2::.ctor(System.Single,System.Single)",
            "ldsfld System.Boolean Terraria.Main::drawToScreen", "brfalse",
            "call Microsoft.Xna.Framework.Vector2 Microsoft.Xna.Framework.Vector2::get_Zero()", "stloc.1",
            "ldarg.0", "ldloc.0", "ldloc.1", "ldloca.s V_2", "ldloca.s V_3", "ldloca.s V_4", "ldloca.s V_5",
            "call System.Void Terraria.GameContent.Drawing.TileDrawing::GetScreenDrawArea(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2,System.Int32&,System.Int32&,System.Int32&,System.Int32&)");
        var camera = (FieldReference)cameraRead.Operand;
        var unscaledPosition = (MethodReference)cameraRead.Next.Operand;
        var screenDrawArea = Method(tileDrawing, "GetScreenDrawArea", vector2, vector2,
            new ByReferenceType(int32), new ByReferenceType(int32), new ByReferenceType(int32), new ByReferenceType(int32));
        var vectorX = (FieldReference)Expect(screenDrawArea,
            "ldarg.3", "ldarg.1", "ldfld System.Single Microsoft.Xna.Framework.Vector2::X", "ldarg.2",
            "ldfld System.Single Microsoft.Xna.Framework.Vector2::X", "sub", "ldc.r4 16", "div", "ldc.r4 1", "sub", "conv.i4", "stind.i4",
            "ldarg.s lastTileX", "ldarg.1", "ldfld System.Single Microsoft.Xna.Framework.Vector2::X",
            "ldsfld System.Int32 Terraria.Main::screenWidth", "conv.r4", "add", "ldarg.2",
            "ldfld System.Single Microsoft.Xna.Framework.Vector2::X", "add", "ldc.r4 16", "div", "conv.i4", "ldc.i4.2", "add", "stind.i4",
            "ldarg.s firstTileY", "ldarg.1", "ldfld System.Single Microsoft.Xna.Framework.Vector2::Y", "ldarg.2",
            "ldfld System.Single Microsoft.Xna.Framework.Vector2::Y", "sub", "ldc.r4 16", "div", "ldc.r4 1", "sub", "conv.i4", "stind.i4",
            "ldarg.s lastTileY", "ldarg.1", "ldfld System.Single Microsoft.Xna.Framework.Vector2::Y",
            "ldsfld System.Int32 Terraria.Main::screenHeight", "conv.r4", "add", "ldarg.2",
            "ldfld System.Single Microsoft.Xna.Framework.Vector2::Y", "add", "ldc.r4 16", "div", "conv.i4", "ldc.i4.5", "add", "stind.i4")
            .Next.Next.Operand;
        var vectorY = (FieldReference)screenDrawArea.Body.Instructions.First(i => i.Operand is FieldReference { Name: "Y" }).Operand;
        var anyUnfinished = (MethodReference)Expect(screenDrawArea,
            "ldsfld Terraria.WorldSections Terraria.Main::sectionManager",
            "callvirt System.Boolean Terraria.WorldSections::get_AnyUnfinishedSections()", "brfalse",
            "call System.Void Terraria.TimeLogger::DetailedDrawReset()",
            "ldarg.3", "ldind.i4", "ldarg.s firstTileY", "ldind.i4", "ldarg.s lastTileX", "ldind.i4", "ldarg.s lastTileY", "ldind.i4",
            "call System.Void Terraria.WorldGen::SectionTileFrameWithCheck(System.Int32,System.Int32,System.Int32,System.Int32)")
            .Next.Operand;

        // Tile (re)loads and visibility changes restart the section in progress.
        var loadedRef = Method(sections, "SetSectionLoaded", new ByReferenceType(module.GetType("Terraria.BitsByte")));
        var needRefresh = Method(sections, "SetAllFramedSectionsAsNeedingRefresh");
        // The call site: right before TileDrawing.Update's `this.EnsureWindGridSize()`.
        var update = Method(tileDrawing, "Update");
        var windCall = update.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand == ensureWindGrid).ToList();
        if (windCall.Count != 1 || windCall[0].Previous.OpCode != OpCodes.Ldarg_0
            || update.Body.Instructions.Any(i => i.Operand == windCall[0].Previous || i.Operand is Instruction[] targets && targets.Contains(windCall[0].Previous)))
            throw new InvalidOperationException("unexpected TileDrawing.Update shape around EnsureWindGridSize");

        FieldDefinition AddField(string name, TypeReference type, bool isStatic = false)
        {
            var field = new FieldDefinition(name, FieldAttributes.Private | (isStatic ? FieldAttributes.Static : 0), type);
            sections.Fields.Add(field);
            return field;
        }
        var loads = AddField("_nxLoads", int32);
        var active = AddField("_nxPreframing", module.TypeSystem.Boolean);
        var sectionX = AddField("_nxPreframeSectionX", int32);
        var sectionY = AddField("_nxPreframeSectionY", int32);
        var loadsAtStart = AddField("_nxPreframeLoads", int32);
        var nextX = AddField("_nxPreframeX", int32);
        var nextY = AddField("_nxPreframeY", int32);
        var endX = AddField("_nxPreframeEndX", int32);
        var startY = AddField("_nxPreframeStartY", int32);
        var endY = AddField("_nxPreframeEndY", int32);
        var lastDrawFrame = AddField("_nxPreframeDrawFrame", int32, isStatic: true);

        foreach (var (method, cause) in new[] { (loadedRef, "tile data (re)loaded"), (needRefresh, "invisible-block visibility changed") })
        {
            WidenShortBranches(method.Body);
            var il = method.Body.GetILProcessor();
            var first = method.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, loads));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(first, il.Create(OpCodes.Add));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, loads));
            VerifyPatched(method, true, "");
            Console.WriteLine($"FIX WorldSections.{method.Name}: bumps _nxLoads ({cause}), restarting a part-framed section");
        }

        var stopwatch = module.GetTypeReferences().Single(t => t.FullName == "System.Diagnostics.Stopwatch");
        var getTimestamp = new MethodReference("GetTimestamp", module.TypeSystem.Int64, stopwatch);
        var frequency = new FieldReference("Frequency", module.TypeSystem.Int64, stopwatch);
        var max = module.GetMemberReferences().OfType<MethodReference>().Single(r => r.DeclaringType.FullName == "System.Math"
            && r.FullName == "System.Int32 System.Math::Max(System.Int32,System.Int32)");
        var sectionManager = main.Fields.Single(f => f.Name == "sectionManager");
        var noMapUpdate = worldGen.Fields.Single(f => f.Name == "noMapUpdate");
        FieldDefinition MainField(string name) => main.Fields.Single(f => f.Name == name && f.IsStatic);
        var getSectionX = Method(netplay, "GetSectionX", int32);
        var getSectionY = Method(netplay, "GetSectionY", int32);

        var preframe = new MethodDefinition("NxPreframe", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        sections.Methods.Add(preframe);
        var body = preframe.Body;
        body.InitLocals = true;
        VariableDefinition Local(TypeReference type) { var v = new VariableDefinition(type); body.Variables.Add(v); return v; }
        var sm = Local(sections);
        var pos = Local(vector2);
        var off = Local(module.TypeSystem.Single);
        var firstX = Local(int32); var lastX = Local(int32); var firstY = Local(int32); var lastY = Local(int32);
        var best = Local(int32); var sx = Local(int32); var sy = Local(int32); var sxEnd = Local(int32);
        var syStart = Local(int32); var syEnd = Local(int32); var distance = Local(int32);
        var x0 = Local(int32); var x1 = Local(int32); var y0 = Local(int32); var y1 = Local(int32);
        var now = Local(module.TypeSystem.Boolean);
        var deadline = Local(module.TypeSystem.Int64);
        var count = Local(int32);
        var e = body.GetILProcessor();
        Instruction Label() => Instruction.Create(OpCodes.Nop);
        var ret = Label(); var area = Label(); var select = Label(); var schedule = Label(); var xBody = Label();
        var xCond = Label(); var yBody = Label(); var yNext = Label(); var yCond = Label(); var budget = Label();
        var work = Label(); var loop = Label(); var check = Label(); var done = Label();
        // Branch to `target` unless the section in progress lies within `slackX`/`slackY` tiles of the drawn area.
        void Outside(int slackX, int slackY, Instruction target)
        {
            foreach (var (field, first, last, size, slack) in new[] { (sectionX, firstX, lastX, 200, slackX), (sectionY, firstY, lastY, 150, slackY) })
            {
                e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, field); e.Emit(OpCodes.Ldc_I4, size); e.Emit(OpCodes.Mul);
                e.Emit(OpCodes.Ldloc, last); e.Emit(OpCodes.Ldc_I4, slack); e.Emit(OpCodes.Add); e.Emit(OpCodes.Bgt, target);
                e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, field); e.Emit(OpCodes.Ldc_I4, size); e.Emit(OpCodes.Mul);
                e.Emit(OpCodes.Ldc_I4, size - 1); e.Emit(OpCodes.Add);
                e.Emit(OpCodes.Ldloc, first); e.Emit(OpCodes.Ldc_I4, slack); e.Emit(OpCodes.Sub); e.Emit(OpCodes.Blt, target);
            }
        }

        // sm = Main.sectionManager; if (sm == null || !sm.AnyUnfinishedSections) return;
        e.Emit(OpCodes.Ldsfld, sectionManager); e.Emit(OpCodes.Stloc, sm);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Brfalse, ret);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Call, anyUnfinished); e.Emit(OpCodes.Brfalse, ret);
        // The area GetScreenDrawArea gets from EnsureWindGridSize (unclamped; clamping only drops tiles outside the world).
        e.Emit(OpCodes.Ldsfld, camera); e.Emit(OpCodes.Callvirt, unscaledPosition); e.Emit(OpCodes.Stloc, pos);
        e.Emit(OpCodes.Ldc_R4, 0f); e.Emit(OpCodes.Stloc, off);
        e.Emit(OpCodes.Ldsfld, MainField("drawToScreen")); e.Emit(OpCodes.Brtrue, area);
        e.Emit(OpCodes.Ldsfld, MainField("offScreenRange")); e.Emit(OpCodes.Conv_R4); e.Emit(OpCodes.Stloc, off);
        e.Append(area);
        foreach (var (axis, first, last, size, extra) in new[] { (vectorX, firstX, lastX, "screenWidth", 2), (vectorY, firstY, lastY, "screenHeight", 5) })
        {
            // first = (int)((pos - off) / 16f - 1f); last = (int)((pos + size + off) / 16f) + extra
            e.Emit(OpCodes.Ldloca, pos); e.Emit(OpCodes.Ldfld, axis); e.Emit(OpCodes.Ldloc, off); e.Emit(OpCodes.Sub);
            e.Emit(OpCodes.Ldc_R4, 16f); e.Emit(OpCodes.Div); e.Emit(OpCodes.Ldc_R4, 1f); e.Emit(OpCodes.Sub);
            e.Emit(OpCodes.Conv_I4); e.Emit(OpCodes.Stloc, first);
            e.Emit(OpCodes.Ldloca, pos); e.Emit(OpCodes.Ldfld, axis); e.Emit(OpCodes.Ldsfld, MainField(size)); e.Emit(OpCodes.Conv_R4);
            e.Emit(OpCodes.Add); e.Emit(OpCodes.Ldloc, off); e.Emit(OpCodes.Add); e.Emit(OpCodes.Ldc_R4, 16f); e.Emit(OpCodes.Div);
            e.Emit(OpCodes.Conv_I4); e.Emit(OpCodes.Ldc_I4, extra); e.Emit(OpCodes.Add); e.Emit(OpCodes.Stloc, last);
        }
        // Keep the section in progress unless it was framed elsewhere, unloaded, reloaded since it started, or the
        // camera left it (beyond twice the margins: a teleport).
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, active); e.Emit(OpCodes.Brfalse, select);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, loadsAtStart); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, loads);
        e.Emit(OpCodes.Bne_Un, select);
        Outside(2 * MarginX, 2 * MarginY, select);
        EmitSectionCall(e, sm, sectionLoaded, sectionX, sectionY); e.Emit(OpCodes.Brfalse, select);
        EmitSectionCall(e, sm, sectionFramed, sectionX, sectionY); e.Emit(OpCodes.Brfalse, schedule);
        // Otherwise pick the loaded, unframed section nearest the drawn area (tile gap, x plus y) within the margins.
        e.Append(select);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldc_I4_0); e.Emit(OpCodes.Stfld, active);
        e.Emit(OpCodes.Ldc_I4, int.MaxValue); e.Emit(OpCodes.Stloc, best);
        e.Emit(OpCodes.Ldloc, lastX); e.Emit(OpCodes.Ldc_I4, MarginX); e.Emit(OpCodes.Add); e.Emit(OpCodes.Call, getSectionX); e.Emit(OpCodes.Stloc, sxEnd);
        e.Emit(OpCodes.Ldloc, firstY); e.Emit(OpCodes.Ldc_I4, MarginY); e.Emit(OpCodes.Sub); e.Emit(OpCodes.Call, getSectionY); e.Emit(OpCodes.Stloc, syStart);
        e.Emit(OpCodes.Ldloc, lastY); e.Emit(OpCodes.Ldc_I4, MarginY); e.Emit(OpCodes.Add); e.Emit(OpCodes.Call, getSectionY); e.Emit(OpCodes.Stloc, syEnd);
        e.Emit(OpCodes.Ldloc, firstX); e.Emit(OpCodes.Ldc_I4, MarginX); e.Emit(OpCodes.Sub); e.Emit(OpCodes.Call, getSectionX); e.Emit(OpCodes.Stloc, sx);
        e.Emit(OpCodes.Br, xCond);
        e.Append(xBody);
        e.Emit(OpCodes.Ldloc, syStart); e.Emit(OpCodes.Stloc, sy);
        e.Emit(OpCodes.Br, yCond);
        e.Append(yBody);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sx); e.Emit(OpCodes.Ldloc, sy); e.Emit(OpCodes.Call, sectionLoaded); e.Emit(OpCodes.Brfalse, yNext);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sx); e.Emit(OpCodes.Ldloc, sy); e.Emit(OpCodes.Call, sectionFramed); e.Emit(OpCodes.Brtrue, yNext);
        foreach (var (s, first, last, size) in new[] { (sx, firstX, lastX, 200), (sy, firstY, lastY, 150) })
        {
            // Math.Max(0, Math.Max(s * size - last, first - (s * size + size - 1)))
            e.Emit(OpCodes.Ldc_I4_0);
            e.Emit(OpCodes.Ldloc, s); e.Emit(OpCodes.Ldc_I4, size); e.Emit(OpCodes.Mul); e.Emit(OpCodes.Ldloc, last); e.Emit(OpCodes.Sub);
            e.Emit(OpCodes.Ldloc, first); e.Emit(OpCodes.Ldloc, s); e.Emit(OpCodes.Ldc_I4, size); e.Emit(OpCodes.Mul); e.Emit(OpCodes.Sub);
            e.Emit(OpCodes.Ldc_I4, size - 1); e.Emit(OpCodes.Sub);
            e.Emit(OpCodes.Call, max); e.Emit(OpCodes.Call, max);
        }
        e.Emit(OpCodes.Add); e.Emit(OpCodes.Stloc, distance);
        e.Emit(OpCodes.Ldloc, distance); e.Emit(OpCodes.Ldloc, best); e.Emit(OpCodes.Bge, yNext);
        e.Emit(OpCodes.Ldloc, distance); e.Emit(OpCodes.Stloc, best);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sx); e.Emit(OpCodes.Stfld, sectionX);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sy); e.Emit(OpCodes.Stfld, sectionY);
        e.Append(yNext);
        e.Emit(OpCodes.Ldloc, sy); e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Add); e.Emit(OpCodes.Stloc, sy);
        e.Append(yCond);
        e.Emit(OpCodes.Ldloc, sy); e.Emit(OpCodes.Ldloc, syEnd); e.Emit(OpCodes.Ble, yBody);
        e.Emit(OpCodes.Ldloc, sx); e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Add); e.Emit(OpCodes.Stloc, sx);
        e.Append(xCond);
        e.Emit(OpCodes.Ldloc, sx); e.Emit(OpCodes.Ldloc, sxEnd); e.Emit(OpCodes.Ble, xBody);
        e.Emit(OpCodes.Ldloc, best); e.Emit(OpCodes.Ldc_I4, int.MaxValue); e.Emit(OpCodes.Beq, ret);
        // Start it with SectionTileFrame(sx, sy, sx, sy)'s loops: x in [tileStartX - 1, tileEndX], y in [tileStartY - 1, tileEndY].
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, sectionX); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, sectionY);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, sectionX); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, sectionY);
        e.Emit(OpCodes.Ldloca, x0); e.Emit(OpCodes.Ldloca, x1); e.Emit(OpCodes.Ldloca, y0); e.Emit(OpCodes.Ldloca, y1);
        e.Emit(OpCodes.Call, getSectionBounds);
        foreach (var (field, source, delta) in new[] { (nextX, x0, -1), (endX, x1, 1), (startY, y0, -1), (nextY, y0, -1), (endY, y1, 1) })
        {
            e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, source); e.Emit(OpCodes.Ldc_I4_1); e.Emit(delta < 0 ? OpCodes.Sub : OpCodes.Add);
            e.Emit(OpCodes.Stfld, field);
        }
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, loads); e.Emit(OpCodes.Stfld, loadsAtStart);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Stfld, active);
        // now = the section touches the drawn area, so GetScreenDrawArea would frame all of it next.
        e.Append(schedule);
        e.Emit(OpCodes.Ldc_I4_0); e.Emit(OpCodes.Stloc, now);
        Outside(0, 0, budget);
        e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Stloc, now);
        e.Emit(OpCodes.Br, work);
        // Otherwise one budgeted step per drawn frame (Main.fpsCount counts draws), however many updates run.
        e.Append(budget);
        e.Emit(OpCodes.Ldsfld, MainField("fpsCount")); e.Emit(OpCodes.Ldsfld, lastDrawFrame); e.Emit(OpCodes.Beq, ret);
        e.Emit(OpCodes.Ldsfld, MainField("fpsCount")); e.Emit(OpCodes.Stsfld, lastDrawFrame);
        e.Append(work);
        e.Emit(OpCodes.Call, getTimestamp); e.Emit(OpCodes.Ldsfld, frequency); e.Emit(OpCodes.Ldc_I4, BudgetDivisor); e.Emit(OpCodes.Conv_I8);
        e.Emit(OpCodes.Div); e.Emit(OpCodes.Add); e.Emit(OpCodes.Stloc, deadline);
        e.Emit(OpCodes.Ldc_I4_0); e.Emit(OpCodes.Stloc, count);
        e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Stsfld, noMapUpdate);
        // WorldGen.Reframe(x, y, true); y++; at the column end y = start, x++; after the last column, mark it framed.
        e.Append(loop);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, nextX); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, nextY);
        e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Call, reframe);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, nextY); e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Add); e.Emit(OpCodes.Stfld, nextY);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, nextY); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, endY); e.Emit(OpCodes.Blt, check);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, startY); e.Emit(OpCodes.Stfld, nextY);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, nextX); e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Add); e.Emit(OpCodes.Stfld, nextX);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, nextX); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, endX); e.Emit(OpCodes.Blt, check);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldc_I4_0); e.Emit(OpCodes.Stfld, active);
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, loadsAtStart); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, loads); e.Emit(OpCodes.Bne_Un, done);
        e.Emit(OpCodes.Ldsfld, MainField("mapTimeMax")); e.Emit(OpCodes.Ldc_I4, 10); e.Emit(OpCodes.Add); e.Emit(OpCodes.Stsfld, MainField("mapTime"));
        EmitSectionCall(e, sm, setSectionFramed, sectionX, sectionY);
        e.Emit(OpCodes.Br, done);
        e.Append(check);
        e.Emit(OpCodes.Ldloc, now); e.Emit(OpCodes.Brtrue, loop);
        e.Emit(OpCodes.Ldloc, count); e.Emit(OpCodes.Ldc_I4_1); e.Emit(OpCodes.Add); e.Emit(OpCodes.Dup); e.Emit(OpCodes.Stloc, count);
        e.Emit(OpCodes.Ldc_I4, ClockEvery - 1); e.Emit(OpCodes.And); e.Emit(OpCodes.Brtrue, loop);
        e.Emit(OpCodes.Call, getTimestamp); e.Emit(OpCodes.Ldloc, deadline); e.Emit(OpCodes.Blt, loop);
        e.Append(done);
        e.Emit(OpCodes.Ldc_I4_0); e.Emit(OpCodes.Stsfld, noMapUpdate);
        e.Append(ret);
        e.Emit(OpCodes.Ret);
        VerifyPatched(preframe, true, "");
        Console.WriteLine($"FIX WorldSections.NxPreframe: frames the loaded, unframed section nearest the drawn area (within {MarginX}x{MarginY} tiles) for up to 1.5 ms per drawn frame");

        WidenShortBranches(update.Body);
        update.Body.GetILProcessor().InsertBefore(windCall[0].Previous, Instruction.Create(OpCodes.Call, preframe));
        VerifyPatched(update, true, "");
        Console.WriteLine("FIX TileDrawing.Update: WorldSections.NxPreframe before EnsureWindGridSize's on-demand section framing");
    }

    static void EmitSectionCall(ILProcessor e, VariableDefinition sm, MethodReference method, FieldReference x, FieldReference y)
    {
        e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, x); e.Emit(OpCodes.Ldloc, sm); e.Emit(OpCodes.Ldfld, y);
        e.Emit(OpCodes.Call, method);
    }

    static MethodDefinition Method(TypeDefinition type, string name, params TypeReference[] parameters) =>
        type.Methods.Single(m => m.Name == name && m.HasBody
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters.Select(p => p.FullName)));

    // The method `method` calls by that name (once); every callee used here is defined in tModLoader.dll.
    static MethodDefinition Callee(MethodDefinition method, string name) => (MethodDefinition)method.Body.Instructions
        .Single(i => i.OpCode.FlowControl == FlowControl.Call && ((MethodReference)i.Operand).Name == name).Operand;

    // Instruction text without offsets; branches print only their opcode, long form.
    static List<string> Text(MethodDefinition method) => method.Body.Instructions.Select(i => i.Operand is Instruction
        ? i.OpCode.Name.Replace(".s", "") : i.ToString().Substring(i.ToString().IndexOf(':') + 2)).ToList();

    // The first instruction of the only occurrence of `run` in `method`; throws unless there is exactly one.
    static Instruction Expect(MethodDefinition method, params string[] run)
    {
        var text = Text(method);
        var starts = Enumerable.Range(0, Math.Max(0, text.Count - run.Length + 1))
            .Where(i => text.Skip(i).Take(run.Length).SequenceEqual(run)).ToList();
        if (starts.Count != 1)
            throw new InvalidOperationException($"{method.FullName}: expected one '{string.Join("; ", run)}', found {starts.Count}");
        return method.Body.Instructions[starts[0]];
    }
}
