using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Dust slots. Dust.NewDust takes the lowest inactive slot of Main.dust, walking the array from 0 and
// reading every Dust object's active flag on the way (6,000 slots, one pointer chase each). With
// thousands of dusts alive that walk is most of the call: in run 47's stress test (crowds of enemies and
// their projectiles) NewDust took 1.3 ms of each 51 ms frame, 88% of it called from Projectile.VanillaAI.
//
// Dust._nxFirstFree is a lower bound on the lowest inactive slot: every slot below it is active.
// NewDust starts its walk there and stores the slot where it stops, so it picks the same slot as the
// full walk and draws Main.rand the same way. Every other write of Dust.active in tModLoader
// (Dust.UpdateDust, Dust.CloneDust, Main.DrawDust, Main.DrawCapture) goes through Dust.NxSetActive,
// which lowers the bound to the dust's dustIndex when it clears the flag. Mod code can clear it too
// (AlchemistNPC Lite's AlchFlask.Update, a ModDust hook that runs inside UpdateDust), so UpdateDust
// resets the bound to 0 whenever it returns: the first NewDust of each update walks from 0 as before,
// and the rest start where the previous one stopped.
static class DustSlotPatch
{
    public static void Apply(ModuleDefinition module)
    {
        var dust = module.GetType("Terraria.Dust") ?? throw new InvalidOperationException("Terraria.Dust not found");
        var main = module.GetType("Terraria.Main") ?? throw new InvalidOperationException("Terraria.Main not found");
        var active = dust.Fields.Single(f => f.Name == "active" && !f.IsStatic && f.FieldType.MetadataType == MetadataType.Boolean);
        var dustIndex = dust.Fields.Single(f => f.Name == "dustIndex" && !f.IsStatic && f.FieldType.MetadataType == MetadataType.Int32);
        if (dust.Fields.Any(f => f.Name == "_nxFirstFree"))
            throw new InvalidOperationException("Terraria.Dust already has _nxFirstFree");
        var firstFree = new FieldDefinition("_nxFirstFree", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Int32);
        dust.Fields.Add(firstFree);

        // internal static void NxSetActive(Dust dust, bool value)
        // { dust.active = value; if (!value && (uint)dust.dustIndex < (uint)_nxFirstFree) _nxFirstFree = dust.dustIndex; }
        // Internal, not private: Main.DrawDust and Main.DrawCapture call it (Mono throws MethodAccessException otherwise).
        var setActive = new MethodDefinition("NxSetActive",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        setActive.Parameters.Add(new ParameterDefinition("dust", ParameterAttributes.None, dust));
        setActive.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Boolean));
        var s = setActive.Body.GetILProcessor();
        var done = s.Create(OpCodes.Ret);
        foreach (var instruction in new[]
        {
            s.Create(OpCodes.Ldarg_0), s.Create(OpCodes.Ldarg_1), s.Create(OpCodes.Stfld, active),
            s.Create(OpCodes.Ldarg_1), s.Create(OpCodes.Brtrue, done),
            s.Create(OpCodes.Ldarg_0), s.Create(OpCodes.Ldfld, dustIndex),
            s.Create(OpCodes.Ldsfld, firstFree), s.Create(OpCodes.Bge_Un, done),
            s.Create(OpCodes.Ldarg_0), s.Create(OpCodes.Ldfld, dustIndex), s.Create(OpCodes.Stsfld, firstFree),
            done,
        })
            s.Append(instruction);
        dust.Methods.Add(setActive);
        VerifyPatched(setActive, true, "");

        PatchNewDust(dust, main, active, firstFree);

        // Every other write of Dust.active in tModLoader goes through NxSetActive.
        var writers = new[]
        {
            (dust, "UpdateDust", 8), (dust, "CloneDust", 1), (main, "DrawDust", 3), (main, "DrawCapture", 1),
        };
        foreach (var (type, name, expected) in writers)
        {
            var methods = type.Methods.Where(m => m.Name == name && m.HasBody).ToList();
            var stores = methods.SelectMany(m => m.Body.Instructions)
                .Where(i => i.OpCode == OpCodes.Stfld && IsField(i.Operand, active)).ToList();
            if (stores.Count != expected)
                throw new InvalidOperationException($"{type.Name}.{name}: {stores.Count} Dust.active writes, expected {expected}");
            foreach (var store in stores)
            {
                store.OpCode = OpCodes.Call;
                store.Operand = setActive;
            }
            foreach (var method in methods)
                VerifyPatched(method, true, "");
        }
        var unexpected = AllTypes(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody
            && m != setActive && m.Name != "NewDust"
            && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && IsField(i.Operand, active))).Select(m => m.FullName).ToList();
        if (unexpected.Count > 0)
            throw new InvalidOperationException("unpatched Dust.active writes: " + string.Join(", ", unexpected));

        // UpdateDust resets the bound to 0 on every return (mod hooks inside it may clear active).
        var update = dust.Methods.Single(m => m.Name == "UpdateDust" && m.HasBody);
        var body = update.Body;
        if (body.ExceptionHandlers.Any(h => h.TryEnd == null || h.HandlerEnd == null))
            throw new InvalidOperationException("Dust.UpdateDust: an exception handler runs to the end of the method");
        WidenShortBranches(body);
        var il = body.GetILProcessor();
        var reset = il.Create(OpCodes.Ldc_I4_0);
        var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (rets.Count == 0)
            throw new InvalidOperationException("Dust.UpdateDust has no ret");
        foreach (var ret in rets)
        {
            ret.OpCode = OpCodes.Br;
            ret.Operand = reset;
        }
        il.Append(reset);
        il.Append(il.Create(OpCodes.Stsfld, firstFree));
        il.Append(il.Create(OpCodes.Ret));
        VerifyPatched(update, true, "");
        Console.WriteLine($"FIX Dust.NewDust: slot walk starts at the lowest possibly free slot (Dust._nxFirstFree); 13 Dust.active writes through NxSetActive; UpdateDust resets the bound at its {rets.Count} returns");
    }

    static void PatchNewDust(TypeDefinition dust, TypeDefinition main, FieldDefinition active, FieldDefinition firstFree)
    {
        var newDust = dust.Methods.Single(m => m.Name == "NewDust" && m.HasBody && m.Parameters.Count == 9);
        var body = newDust.Body;
        var reads = body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld && IsField(i.Operand, active)).ToList();
        if (reads.Count != 1)
            throw new InvalidOperationException($"Dust.NewDust: {reads.Count} Dust.active reads, expected 1");
        // Loop body: ldsfld Main.dust; ldloc i; ldelem.ref; stloc d; ldloc d; ldfld active; brtrue next
        var check = reads[0];
        var skip = check.Next;
        var loadDust = check.Previous;
        var storeDust = loadDust?.Previous;
        var element = storeDust?.Previous;
        var loadIndex = element?.Previous;
        var loadArray = loadIndex?.Previous;
        if (skip?.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S) || element?.OpCode != OpCodes.Ldelem_Ref
            || loadArray?.Operand is not FieldReference { Name: "dust" } arrayField || arrayField.DeclaringType.FullName != main.FullName
            || !IsLocalLoad(loadIndex, out var index) || !IsLocalStore(storeDust, out var dustLocal) || !IsLocalLoad(loadDust, out var dustLoaded)
            || dustLocal != dustLoaded)
            throw new InvalidOperationException("Dust.NewDust: unexpected slot loop shape at " + check);
        var inits = body.Instructions.Where(i => IsLocalStore(i, out var v) && v == index && i.Previous?.OpCode == OpCodes.Ldc_I4_0).ToList();
        var writes = body.Instructions.Count(i => IsLocalStore(i, out var v) && v == index);
        if (inits.Count != 1 || writes != 2)
            throw new InvalidOperationException($"Dust.NewDust: slot index has {inits.Count} zero inits and {writes} writes, expected 1 and 2");
        var free = skip.Next;
        if (body.Instructions.Any(i => i.Operand == free || i.Operand is Instruction[] targets && targets.Contains(free))
            || body.ExceptionHandlers.Any(h => h.TryStart == free || h.HandlerStart == free))
            throw new InvalidOperationException("Dust.NewDust: the free-slot path is a branch target");

        WidenShortBranches(body);
        var init = inits[0].Previous;
        init.OpCode = OpCodes.Ldsfld; // i = _nxFirstFree instead of 0
        init.Operand = firstFree;
        var il = body.GetILProcessor();
        il.InsertBefore(free, il.Create(OpCodes.Ldloc, index));
        il.InsertBefore(free, il.Create(OpCodes.Stsfld, firstFree)); // every slot below i is active
        VerifyPatched(newDust, true, "");
    }

    static bool IsField(object operand, FieldDefinition field) =>
        operand is FieldReference f && f.Name == field.Name && f.DeclaringType.FullName == field.DeclaringType.FullName;

    static bool IsLocalLoad(Instruction i, out VariableDefinition variable)
    {
        variable = i?.OpCode.Code is Code.Ldloc or Code.Ldloc_S ? (VariableDefinition)i.Operand : null;
        return variable != null;
    }

    static bool IsLocalStore(Instruction i, out VariableDefinition variable)
    {
        variable = i?.OpCode.Code is Code.Stloc or Code.Stloc_S ? (VariableDefinition)i.Operand : null;
        return variable != null;
    }
}
