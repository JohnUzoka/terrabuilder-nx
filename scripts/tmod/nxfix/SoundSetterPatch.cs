using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Active-sound setters. ActiveSound.Update runs every frame for every tracked sound and assigns
// SoundEffectInstance.Pan (positioned sounds), Volume and Pitch each time. FNA's setters always pass
// the value on to FAudio (FAudioVoice_SetOutputMatrix, FAudioVoice_SetVolume,
// FAudioSourceVoice_SetFrequencyRatio): a P/Invoke with a GC transition plus FAudio's voice locks,
// even when nothing changed. In run 44, SetOutputMatrix on the game thread went from no samples in
// minutes 6-12 to 0.18 ms per frame in minutes 24-36, when many sounds were active.
//
// Update now calls ActiveSound.NxSetVolume/NxSetPitch(sound, value), which skip the setter when the
// instance already holds that value: FNA keeps INTERNAL_volume/INTERNAL_pitch in step with the voice
// (Play applies both when it creates the voice), so the skipped calls would have sent FAudio the
// value it already has. Pan differs: Play leaves FAudio's default output matrix when Pan is 0, so
// NxSetPan(sound, value, activeSound) always runs the setter the first time for each ActiveSound
// (field _nxPanApplied) and skips only repeats after that. Comparisons use the getters, so a value
// a mod writes to the SoundEffectInstance directly is still overwritten as before; NaN never
// compares equal and goes through the setter.
static class SoundSetterPatch
{
    public static void Apply(ModuleDefinition module)
    {
        var activeSound = module.GetType("Terraria.Audio.ActiveSound")
            ?? throw new InvalidOperationException("Terraria.Audio.ActiveSound not found");
        var update = activeSound.Methods.Single(m => m.Name == "Update" && m.HasBody && !m.HasParameters);
        var setters = update.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference
            { Name: "set_Pan" or "set_Volume" or "set_Pitch", DeclaringType.FullName: "Microsoft.Xna.Framework.Audio.SoundEffectInstance" }).ToList();
        if (setters.Count != 3 || setters.Select(i => ((MethodReference)i.Operand).Name).Distinct().Count() != 3)
            throw new InvalidOperationException("ActiveSound.Update: expected one set_Pan, set_Volume and set_Pitch call");

        var panApplied = new FieldDefinition("_nxPanApplied", FieldAttributes.Private, module.TypeSystem.Boolean);
        if (activeSound.Fields.Any(f => f.Name == panApplied.Name))
            throw new InvalidOperationException("ActiveSound already has " + panApplied.Name);
        activeSound.Fields.Add(panApplied);

        var il = update.Body.GetILProcessor();
        foreach (var call in setters)
        {
            var setter = (MethodReference)call.Operand;
            var name = setter.Name.Substring("set_".Length);
            if (!setter.HasThis || setter.Parameters.Count != 1 || setter.Parameters[0].ParameterType.MetadataType != MetadataType.Single
                || !IsVoid(setter.ReturnType))
                throw new InvalidOperationException("unexpected SoundEffectInstance setter: " + setter.FullName);
            var getter = new MethodReference("get_" + name, module.TypeSystem.Single, setter.DeclaringType) { HasThis = true };
            if (getter.Resolve() is not { IsStatic: false, HasParameters: false } resolved || resolved.ReturnType.MetadataType != MetadataType.Single)
                throw new InvalidOperationException("SoundEffectInstance.get_" + name + " does not resolve to a float getter");

            bool pan = name == "Pan";
            var helper = new MethodDefinition("NxSet" + name,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
            helper.Parameters.Add(new ParameterDefinition("sound", ParameterAttributes.None, setter.DeclaringType));
            helper.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Single));
            if (pan)
                helper.Parameters.Add(new ParameterDefinition("activeSound", ParameterAttributes.None, activeSound));
            var h = helper.Body.GetILProcessor();
            var set = h.Create(OpCodes.Ldarg_0);
            var skip = h.Create(OpCodes.Ret);
            if (pan)
            {
                h.Append(h.Create(OpCodes.Ldarg_2));
                h.Append(h.Create(OpCodes.Ldfld, panApplied));
                h.Append(h.Create(OpCodes.Brfalse, set));
            }
            h.Append(h.Create(OpCodes.Ldarg_0));
            h.Append(h.Create(OpCodes.Callvirt, getter));
            h.Append(h.Create(OpCodes.Ldarg_1));
            h.Append(h.Create(OpCodes.Beq, skip));
            h.Append(set);
            h.Append(h.Create(OpCodes.Ldarg_1));
            h.Append(h.Create(OpCodes.Callvirt, setter));
            if (pan)
            {
                h.Append(h.Create(OpCodes.Ldarg_2));
                h.Append(h.Create(OpCodes.Ldc_I4_1));
                h.Append(h.Create(OpCodes.Stfld, panApplied));
            }
            h.Append(skip);
            activeSound.Methods.Add(helper);
            VerifyPatched(helper, true, "");

            // Stack at the call: [sound, value]; the Pan helper also takes this ActiveSound.
            call.OpCode = OpCodes.Call;
            call.Operand = helper;
            if (pan)
                il.InsertBefore(call, il.Create(OpCodes.Ldarg_0));
        }
        VerifyPatched(update, true, "");
        Console.WriteLine("FIX ActiveSound.Update: Volume/Pitch/Pan reach FAudio only when they change (Pan always on the first update)");
    }
}
