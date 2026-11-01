using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Face-button labels. devkitPro SDL's built-in "Switch Controller" mapping is positional (Xbox
// layout: a:b1,b:b0,x:b3,y:b2), so in tModLoader the button printed A acts as Xbox B, B as A,
// X as Y and Y as X: B confirms and picks up, A closes the inventory, X opens it, and every
// on-screen glyph names the opposite button. The vanilla port installs a label-correct mapping
// from its patched FNA (NxInputDiag.InstallSwitchMapping); tModLoader's FNA never got that patch,
// and a gamecontrollerdb.txt without a "platform:" field is ignored by SDL's file loader.
//
// PlayerInput.Initialize (called once from Main.Initialize, after FNA has initialized SDL and
// opened the controllers) now first registers the same mapping as the vanilla port. SDL replaces
// the built-in entry (same GUID, higher priority) and rebinds the already-open controllers.
// The result is logged as vanilla does: "NX Switch mapping install result=0" (0 = existing
// mapping replaced, 1 = added, -1 = error). Saved input profiles are not touched; they store
// Xbox button names, which now mean the same physical buttons as in the vanilla port.
static class SwitchPadMappingPatch
{
    // Identical to managed/nx_input_diag/NxInputDiag.cs InstallSwitchMapping (label-correct).
    const string Mapping = "000038f853776974636820436f6e7400,Switch Controller,a:b0,b:b1,back:b11,dpdown:b15,dpleft:b12,dpright:b14,dpup:b13,leftshoulder:b6,leftstick:b4,lefttrigger:b8,leftx:a0,lefty:a1,rightshoulder:b7,rightstick:b5,righttrigger:b9,rightx:a2,righty:a3,start:b10,x:b2,y:b3,";

    public static void Apply(ModuleDefinition module)
    {
        var playerInput = module.GetType("Terraria.GameInput.PlayerInput")
            ?? throw new InvalidOperationException("Terraria.GameInput.PlayerInput not found");
        var initialize = playerInput.Methods.Single(m => m.Name == "Initialize" && m.HasBody);
        if (!initialize.IsStatic || initialize.HasParameters || !IsVoid(initialize.ReturnType))
            throw new InvalidOperationException("unexpected PlayerInput.Initialize signature: " + initialize.FullName);
        var body = initialize.Body;
        var first = body.Instructions[0];
        if (body.Instructions.Any(i => i.Operand is string s && s.StartsWith("000038f853776974636820436f6e7400,")))
            throw new InvalidOperationException("PlayerInput.Initialize already installs a Switch mapping");
        if (body.Instructions.Any(i => i.Operand == first || i.Operand is Instruction[] targets && targets.Contains(first))
            || body.ExceptionHandlers.Any(h => h.TryStart == first || h.HandlerStart == first))
            throw new InvalidOperationException("PlayerInput.Initialize: unexpected branch or handler at its first instruction");

        var fnaReference = module.AssemblyReferences.Single(r => r.Name == "FNA");
        var fna = module.AssemblyResolver.Resolve(fnaReference)
            ?? throw new InvalidOperationException("FNA could not be resolved");
        var sdl = fna.MainModule.GetType("SDL2.SDL") ?? throw new InvalidOperationException("SDL2.SDL not found in FNA");
        var addMapping = sdl.Methods.Single(m => m.Name == "SDL_GameControllerAddMapping" && m.IsStatic && m.IsPublic
            && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        if (addMapping.ReturnType.MetadataType != MetadataType.Int32)
            throw new InvalidOperationException("unexpected SDL_GameControllerAddMapping signature: " + addMapping.FullName);
        var logger = fna.MainModule.GetType("Microsoft.Xna.Framework.FNALoggerEXT")
            ?? throw new InvalidOperationException("FNALoggerEXT not found in FNA");
        var logInfo = logger.Fields.Single(f => f.Name == "LogInfo");
        if (!logInfo.IsStatic || !logInfo.IsPublic || logInfo.FieldType is not GenericInstanceType action
            || action.ElementType.FullName != "System.Action`1" || action.GenericArguments.Single().MetadataType != MetadataType.String)
            throw new InvalidOperationException("unexpected FNALoggerEXT.LogInfo: " + logInfo.FullName);

        // FNA itself logs through LogInfo.Invoke; reuse its Action<string>.Invoke reference.
        var invoke = fna.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "Invoke"
                && r.DeclaringType.FullName == action.FullName)
            .Select(i => (MethodReference)i.Operand).FirstOrDefault()
            ?? throw new InvalidOperationException("FNA has no Action<string>.Invoke call to reuse");
        var stringType = module.TypeSystem.String;
        var int32ToString = new MethodReference("ToString", stringType, module.TypeSystem.Int32) { HasThis = true };
        var concat = new MethodReference("Concat", stringType, stringType);
        concat.Parameters.Add(new ParameterDefinition(stringType));
        concat.Parameters.Add(new ParameterDefinition(stringType));

        var result = new VariableDefinition(module.TypeSystem.Int32);
        body.Variables.Add(result);
        body.InitLocals = true;
        var il = body.GetILProcessor();
        foreach (var instruction in new[]
        {
            il.Create(OpCodes.Ldstr, Mapping),
            il.Create(OpCodes.Call, module.ImportReference(addMapping)),
            il.Create(OpCodes.Stloc, result),
            il.Create(OpCodes.Ldsfld, module.ImportReference(logInfo)),
            il.Create(OpCodes.Ldstr, "NX Switch mapping install result="),
            il.Create(OpCodes.Ldloca, result),
            il.Create(OpCodes.Call, int32ToString),
            il.Create(OpCodes.Call, concat),
            il.Create(OpCodes.Callvirt, module.ImportReference(invoke)),
        })
            il.InsertBefore(first, instruction);
        VerifyPatched(initialize, body.Instructions[0].OpCode == OpCodes.Ldstr && body.Instructions[9] == first,
            "Switch mapping prologue not in place");
        Console.WriteLine("FIX PlayerInput.Initialize: install the label-correct Switch Controller mapping (a:b0,b:b1,x:b2,y:b3) and log its result");
    }
}
