using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Graveyard wind. While the player is in a graveyard, AmbientWindSystem.Update calls TrySpawningWind
// for each of the 3,600 tiles in a 120 x 30 area around the player every update, and each call starts
// with TestAirCloud, whose first step is _random.Next(120000) == 0: a 1 in 120,000 chance to look for
// room for an airborne cloud. In run 47's stress test TestAirCloud took 0.62 ms of each 51 ms frame.
//
// The trial now goes through AmbientWindSystem.NxAirCloudTrial: once per success it draws, from the
// same _random, how many calls until the next success (geometric with p = 1/120000) and counts down.
// Each call still succeeds independently with probability 1/120000, so airborne clouds appear with the
// same distribution; which tiles get them differs, because _random is drawn less often.
static class AmbientWindPatch
{
    const int Odds = 120000;

    public static void Apply(ModuleDefinition module)
    {
        var wind = module.GetType("Terraria.GameContent.AmbientWindSystem")
            ?? throw new InvalidOperationException("Terraria.GameContent.AmbientWindSystem not found");
        var test = wind.Methods.Single(m => m.Name == "TestAirCloud" && m.HasBody);
        var random = wind.Fields.Single(f => f.Name == "_random" && !f.IsStatic);
        var code = test.Body.Instructions;
        if (test.IsStatic || code.Count < 6 || code[0].OpCode != OpCodes.Ldarg_0
            || code[1].OpCode != OpCodes.Ldfld || code[1].Operand != random
            || code[2].OpCode != OpCodes.Ldc_I4 || (int)code[2].Operand != Odds
            || code[3].OpCode != OpCodes.Callvirt || code[3].Operand is not MethodReference { Name: "Next", Parameters.Count: 1 } next
            || code[4].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S) || code[5].OpCode != OpCodes.Ret)
            throw new InvalidOperationException("unexpected AmbientWindSystem.TestAirCloud prologue");
        var unifiedRandom = next.DeclaringType.Resolve()
            ?? throw new InvalidOperationException("UnifiedRandom does not resolve");
        var nextDouble = unifiedRandom.Methods.Single(m => m.Name == "NextDouble" && !m.HasParameters && !m.IsStatic);
        // Reuse tModLoader's own System.Math::Log(double) reference.
        var log = module.Types.SelectMany(t => AllTypes(new[] { t })).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
            .FirstOrDefault(r => r.Name == "Log" && r.DeclaringType.FullName == "System.Math" && r.Parameters.Count == 1
                && r.Parameters[0].ParameterType.MetadataType == MetadataType.Double)
            ?? throw new InvalidOperationException("no System.Math::Log(double) reference to reuse");

        if (wind.Fields.Any(f => f.Name == "_nxAirCloudCountdown"))
            throw new InvalidOperationException("AmbientWindSystem already has _nxAirCloudCountdown");
        var countdown = new FieldDefinition("_nxAirCloudCountdown", FieldAttributes.Private, module.TypeSystem.Int32);
        wind.Fields.Add(countdown);

        // bool NxAirCloudTrial()
        // {
        //     if (_nxAirCloudCountdown <= 0)  // calls up to and including the next success, >= 1
        //         _nxAirCloudCountdown = (int)(Math.Log(1.0 - _random.NextDouble()) / Math.Log(1.0 - 1.0 / Odds)) + 1;
        //     return --_nxAirCloudCountdown == 0;
        // }
        var trial = new MethodDefinition("NxAirCloudTrial", MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
        var il = trial.Body.GetILProcessor();
        var counting = il.Create(OpCodes.Ldarg_0);
        foreach (var instruction in new[]
        {
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, countdown), il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Bgt, counting),
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldc_R8, 1.0),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, random), il.Create(OpCodes.Callvirt, nextDouble),
            il.Create(OpCodes.Sub),
            il.Create(OpCodes.Call, log),
            il.Create(OpCodes.Ldc_R8, Math.Log(1.0 - 1.0 / Odds)),
            il.Create(OpCodes.Div),
            il.Create(OpCodes.Conv_I4),
            il.Create(OpCodes.Ldc_I4_1), il.Create(OpCodes.Add),
            il.Create(OpCodes.Stfld, countdown),
            counting,
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, countdown), il.Create(OpCodes.Ldc_I4_1), il.Create(OpCodes.Sub),
            il.Create(OpCodes.Stfld, countdown),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, countdown), il.Create(OpCodes.Ldc_I4_0), il.Create(OpCodes.Ceq),
            il.Create(OpCodes.Ret),
        })
            il.Append(instruction);
        wind.Methods.Add(trial);
        VerifyPatched(trial, true, "");

        // TestAirCloud: "if (_random.Next(120000) != 0) return;" becomes "if (!NxAirCloudTrial()) return;".
        var body = test.Body;
        var proceed = (Instruction)code[4].Operand;
        code[1].OpCode = OpCodes.Call;
        code[1].Operand = trial;
        body.GetILProcessor().Remove(code[3]);
        body.GetILProcessor().Remove(code[2]);
        code[2].OpCode = OpCodes.Brtrue;
        code[2].Operand = proceed;
        if (code[3].OpCode != OpCodes.Ret)
            throw new InvalidOperationException("AmbientWindSystem.TestAirCloud: prologue rewrite misplaced");
        VerifyPatched(test, true, "");
        Console.WriteLine($"FIX AmbientWindSystem.TestAirCloud: 1-in-{Odds} trial by geometric countdown (NxAirCloudTrial) instead of a random draw per tile");
    }
}
