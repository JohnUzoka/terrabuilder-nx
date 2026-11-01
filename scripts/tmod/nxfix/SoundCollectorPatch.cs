using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Sound instance collection. SoundInstanceGarbageCollector.Update runs every update and asks FAudio for
// the state of every SoundEffectInstance it tracks (a P/Invoke with a GC transition and the voice's
// lock each) so it can dispose the stopped ones. Every sound ActiveSound.Play or the legacy player
// starts is tracked until it stops, so in a fight the list holds every recent hit and death sound: in
// run 47's stress test those queries were 0.86 ms of each 51 ms frame (78% of
// FAudioSourceVoice_GetState). Update now runs its loop on every eighth call, so a stopped instance is
// disposed up to seven updates (about 0.12 s) later. Nothing else reads the list.
static class SoundCollectorPatch
{
    const int Period = 8;

    public static void Apply(ModuleDefinition module)
    {
        var collector = module.GetType("Terraria.Audio.SoundInstanceGarbageCollector")
            ?? throw new InvalidOperationException("Terraria.Audio.SoundInstanceGarbageCollector not found");
        var update = collector.Methods.Single(m => m.Name == "Update" && m.HasBody);
        if (!update.IsStatic || update.HasParameters || !IsVoid(update.ReturnType))
            throw new InvalidOperationException("unexpected SoundInstanceGarbageCollector.Update signature: " + update.FullName);
        var body = update.Body;
        var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        var first = body.Instructions[0];
        if (rets.Count != 1 || rets[0] != body.Instructions[^1]
            || body.Instructions.Any(i => i.Operand == first || i.Operand is Instruction[] targets && targets.Contains(first))
            || body.ExceptionHandlers.Count != 0)
            throw new InvalidOperationException("unexpected SoundInstanceGarbageCollector.Update shape");
        if (collector.Fields.Any(f => f.Name == "_nxCalls"))
            throw new InvalidOperationException("SoundInstanceGarbageCollector already has _nxCalls");
        var calls = new FieldDefinition("_nxCalls", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Int32);
        collector.Fields.Add(calls);

        WidenShortBranches(body);
        var il = body.GetILProcessor();
        foreach (var instruction in new[]
        {
            il.Create(OpCodes.Ldsfld, calls), il.Create(OpCodes.Ldc_I4_1), il.Create(OpCodes.Add),
            il.Create(OpCodes.Dup), il.Create(OpCodes.Stsfld, calls),
            il.Create(OpCodes.Ldc_I4, Period - 1), il.Create(OpCodes.And),
            il.Create(OpCodes.Brtrue, rets[0]), // run on every Period-th call only
        })
            il.InsertBefore(first, instruction);
        VerifyPatched(update, true, "");
        Console.WriteLine($"FIX SoundInstanceGarbageCollector.Update: state checks and disposal every {Period} updates instead of every update");
    }
}
