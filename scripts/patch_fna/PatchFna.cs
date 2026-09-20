using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: PatchFna <original-game-FNA.dll> <output-FNA.dll> <NxInputDiag.dll>");
    return 2;
}

try
{
    Patch(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("PatchFna: " + error.Message);
    return 1;
}

static void Patch(string input, string output, string helper)
{
    Require(File.Exists(input), "original game FNA input does not exist: " + input);
    Require(File.Exists(helper), "input helper does not exist: " + helper);
    Require(output != input && output != helper, "output must not overwrite either input assembly");
    using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { ReadSymbols = false });
    using var diagAssembly = AssemblyDefinition.ReadAssembly(helper, new ReaderParameters { ReadSymbols = false });
    var module = assembly.MainModule;
    Require(assembly.Name.Name == "FNA", "input is not FNA; use the original game's FNA.dll");
    Require(diagAssembly.Name.Name == "NxInputDiag", "helper assembly must be named NxInputDiag");
    Require(!module.AssemblyReferences.Any(r => r.Name == "NxInputDiag"),
        "FNA already references NxInputDiag; use the original game's unpatched FNA.dll");
    var originalReferences = module.AssemblyReferences.Select(r => r.FullName).ToArray();

    var fna3d = FindType(module, "Microsoft.Xna.Framework.Graphics.FNA3D");
    var linkedVersion = FindMethod(fna3d, "FNA3D_LinkedVersion", "System.UInt32");
    Require(linkedVersion.IsStatic && linkedVersion.IsPInvokeImpl, "unexpected FNA3D_LinkedVersion signature");
    var renderTarget = FindType(module, "Microsoft.Xna.Framework.Graphics.RenderTarget2D");
    var dispose = FindMethod(renderTarget, "Dispose", "System.Void", "System.Boolean");
    RequireBody(dispose);
    var throws = dispose.Body.Instructions.Where(i => i.OpCode == OpCodes.Throw).ToArray();
    Require(throws.Length == 1, "RenderTarget2D.Dispose must have exactly one bound-target throw");
    var throwInstruction = throws[0];
    var newException = throwInstruction.Previous;
    var message = newException?.Previous;
    var continuation = throwInstruction.Next;
    Require(message != null && message.OpCode == OpCodes.Ldstr &&
        (string)message.Operand == "Disposing target that is still bound" &&
        newException != null && newException.OpCode == OpCodes.Newobj &&
        newException.Operand is MethodReference exceptionConstructor &&
        exceptionConstructor.FullName == "System.Void System.InvalidOperationException::.ctor(System.String)" &&
        continuation != null && message.Previous.OpCode == OpCodes.Bne_Un_S &&
        ReferenceEquals(message.Previous.Operand, continuation),
        "unexpected RenderTarget2D.Dispose bound-target cleanup shape");
    RequireNoIncoming(dispose, newException!, throwInstruction);
    var graphicsResource = FindType(module, "Microsoft.Xna.Framework.Graphics.GraphicsResource");
    var graphicsDevice = FindType(module, "Microsoft.Xna.Framework.Graphics.GraphicsDevice");
    var getDevice = FindMethod(graphicsResource, "get_GraphicsDevice", "Microsoft.Xna.Framework.Graphics.GraphicsDevice");
    var setTarget = FindMethod(graphicsDevice, "SetRenderTarget", "System.Void", "Microsoft.Xna.Framework.Graphics.RenderTarget2D");

    var contentManager = FindType(module, "Microsoft.Xna.Framework.Content.ContentManager");
    var root = FindMethod(contentManager, "get_RootDirectory", "System.String");
    var fullRoot = FindMethod(contentManager, "get_RootDirectoryFullPath", "System.String");
    var platform = FindType(module, "Microsoft.Xna.Framework.SDL2_FNAPlatform");
    var baseRoot = FindMethod(platform, "GetBaseDirectory", "System.String");
    foreach (var method in new[] { root, fullRoot, baseRoot })
    {
        RequireBody(method);
        Require(method.Body.ExceptionHandlers.Count == 0 &&
            !method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).StartsWith("romfs:")),
            "unexpected or already patched root accessor: " + method.FullName);
    }
    Require(root.Body.Instructions.Count == 3 && root.Body.Instructions[0].OpCode == OpCodes.Ldarg_0 &&
        root.Body.Instructions[1].OpCode == OpCodes.Ldfld &&
        root.Body.Instructions[1].Operand is FieldReference rootField &&
        rootField.Name == "<RootDirectory>k__BackingField" && root.Body.Instructions[2].OpCode == OpCodes.Ret,
        "unexpected ContentManager.RootDirectory IL");
    Require(fullRoot.Body.Instructions.Count == 12 && Calls(fullRoot, contentManager.FullName, "get_RootDirectory").Length == 3 &&
        Calls(fullRoot, "System.IO.Path", "IsPathRooted").Length == 1 &&
        Calls(fullRoot, "System.IO.Path", "Combine").Length == 1,
        "unexpected ContentManager.RootDirectoryFullPath IL");
    Require(baseRoot.IsStatic && Calls(baseRoot, "SDL2.SDL", "SDL_GetBasePath").Length == 1 &&
        baseRoot.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "FNA_SDL_FORCE_BASE_PATH"),
        "unexpected SDL2_FNAPlatform.GetBaseDirectory IL");

    var programInit = FindMethod(platform, "ProgramInit", "System.String", "Microsoft.Xna.Framework.LaunchParameters");
    RequireBody(programInit);
    var sdlInitCalls = Calls(programInit, "SDL2.SDL", "SDL_Init");
    Require(sdlInitCalls.Length == 1 && sdlInitCalls[0].OpCode == OpCodes.Call &&
        ((MethodReference)sdlInitCalls[0].Operand).FullName == "System.Int32 SDL2.SDL::SDL_Init(System.UInt32)" &&
        sdlInitCalls[0].Next.OpCode == OpCodes.Brfalse_S,
        "ProgramInit must have exactly one SDL_Init call followed by its failure check");

    var getState = FindMethod(platform, "GetGamePadState", "Microsoft.Xna.Framework.Input.GamePadState",
        "System.Int32", "Microsoft.Xna.Framework.Input.GamePadDeadZone");
    RequireBody(getState);
    var buttons = Calls(getState, "SDL2.SDL", "SDL_GameControllerGetButton");
    Require(buttons.Length == 21, "GetGamePadState must have exactly 21 SDL button calls; found " + buttons.Length);
    for (int index = 0; index < buttons.Length; index++)
    {
        var call = buttons[index];
        Require(call.OpCode == OpCodes.Call && ((MethodReference)call.Operand).FullName ==
            "System.Byte SDL2.SDL::SDL_GameControllerGetButton(System.IntPtr,SDL2.SDL/SDL_GameControllerButton)" &&
            call.Previous != null && IntegerConstant(call.Previous) == index &&
            call.Previous.Previous?.OpCode == OpCodes.Ldloc_0,
            "unexpected GetGamePadState button call shape for SDL button " + index);
    }

    var game = FindType(module, "Microsoft.Xna.Framework.Game");
    var tick = FindMethod(game, "Tick", "System.Void");
    RequireBody(tick);
    Require(!tick.IsStatic && tick.Body.ExceptionHandlers.Count == 0, "unexpected Game.Tick exception/instance shape");
    var updates = Calls(tick, game.FullName, "Update");
    var draws = Calls(tick, game.FullName, "Draw");
    var returns = tick.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
    Require(updates.Length == 2 && draws.Length == 1 && returns.Length == 2,
        $"Game.Tick requires 2 Update calls, 1 Draw call, 2 returns; found {updates.Length}, {draws.Length}, {returns.Length}");
    RequireNoIncoming(tick, tick.Body.Instructions[0]);
    foreach (var call in updates.Concat(draws))
    {
        var method = (MethodReference)call.Operand;
        Require(call.OpCode == OpCodes.Callvirt && method.HasThis && method.ReturnType.FullName == "System.Void" &&
            method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "Microsoft.Xna.Framework.GameTime" &&
            call.Previous?.OpCode == OpCodes.Ldfld && call.Previous.Operand is FieldReference timeField &&
            timeField.FullName == "Microsoft.Xna.Framework.GameTime Microsoft.Xna.Framework.Game::gameTime" &&
            call.Previous.Previous?.OpCode == OpCodes.Ldarg_0 &&
            call.Previous.Previous.Previous?.OpCode == OpCodes.Ldarg_0,
            "unexpected Game.Tick argument loads for " + method.Name);
        RequireNoIncoming(tick, call, call.Previous!, call.Previous!.Previous!);
    }

    var diagType = FindType(diagAssembly.MainModule, "Terraria.NxInputDiag.InputDiagnostics");
    Require(diagType.IsPublic && !diagType.Methods.Any(m => m.Name == "LogRaw"),
        "helper must expose InputDiagnostics without legacy raw diagnostics");
    var mapping = FindMethod(diagType, "InstallSwitchMapping", "System.Void");
    Require(mapping.IsPublic && mapping.IsStatic && mapping.HasBody, "invalid InstallSwitchMapping helper");
    var phaseNames = new[] { "BeginTick", "EndTick", "BeginUpdate", "EndUpdate", "BeginDraw", "EndDraw" };
    foreach (var name in phaseNames)
        RequireInternalCall(FindMethod(diagType, name, "System.Void"));
    RequireInternalCall(FindMethod(diagType, "GetButton", "System.Byte", "System.IntPtr", "System.Int32"));

    // Build signatures with FNA's core-library types, not the helper's System.Runtime
    // references. The sole new assembly reference must be NxInputDiag itself.
    var helperType = module.ImportReference(diagType);
    MethodReference HelperMethod(string name, TypeReference result, params TypeReference[] parameters)
    {
        var reference = new MethodReference(name, result, helperType) { HasThis = false };
        foreach (var parameter in parameters)
            reference.Parameters.Add(new ParameterDefinition(parameter));
        return reference;
    }
    var installMapping = HelperMethod("InstallSwitchMapping", module.TypeSystem.Void);
    var getButton = HelperMethod("GetButton", module.TypeSystem.Byte, module.TypeSystem.IntPtr, module.TypeSystem.Int32);
    var phases = phaseNames.ToDictionary(name => name, name => HelperMethod(name, module.TypeSystem.Void));

    fna3d.IsPublic = true;
    linkedVersion.IsPublic = true;
    // Preserve the known-good cleanup: unbind, then continue disposing buffers/base.
    // Reuse original instruction objects so no branch can target removed IL.
    message!.OpCode = OpCodes.Ldarg_0;
    message.Operand = null;
    newException!.OpCode = OpCodes.Call;
    newException.Operand = getDevice;
    throwInstruction.OpCode = OpCodes.Ldnull;
    throwInstruction.Operand = null;
    var disposeIl = dispose.Body.GetILProcessor();
    var unbind = Instruction.Create(OpCodes.Callvirt, setTarget);
    disposeIl.InsertAfter(throwInstruction, unbind);
    disposeIl.InsertAfter(unbind, Instruction.Create(OpCodes.Br, continuation!));
    ReplaceWithRomfsRoot(root, "romfs:/Content");
    ReplaceWithRomfsRoot(fullRoot, "romfs:/Content");
    ReplaceWithRomfsRoot(baseRoot, "romfs:/");
    programInit.Body.GetILProcessor().InsertAfter(sdlInitCalls[0], Instruction.Create(OpCodes.Call, installMapping));
    foreach (var call in buttons)
        call.Operand = getButton;

    var tickIl = tick.Body.GetILProcessor();
    tickIl.InsertBefore(tick.Body.Instructions[0], Instruction.Create(OpCodes.Call, phases["BeginTick"]));
    foreach (var call in updates)
        SurroundCall(tickIl, call, phases["BeginUpdate"], phases["EndUpdate"]);
    foreach (var call in draws)
        SurroundCall(tickIl, call, phases["BeginDraw"], phases["EndDraw"]);
    foreach (var ret in returns)
    {
        // The BeginDraw-false branch targets the final ret directly. Mutating the
        // target (rather than inserting before it) makes that path run EndTick too.
        ret.OpCode = OpCodes.Call;
        ret.Operand = phases["EndTick"];
        tickIl.InsertAfter(ret, Instruction.Create(OpCodes.Ret));
    }
    foreach (var method in new[] { dispose, programInit, tick })
        WidenOverflowingBranches(method);

    Require(module.AssemblyReferences.Count == originalReferences.Length + 1 &&
        module.AssemblyReferences.Take(originalReferences.Length).Select(r => r.FullName).SequenceEqual(originalReferences) &&
        module.AssemblyReferences.Last().FullName == diagAssembly.Name.FullName,
        "patch unexpectedly changed assembly references beyond adding NxInputDiag");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    assembly.Write(output);
    Console.WriteLine("patched FNA3D/version public access and bound-render-target disposal cleanup");
    Console.WriteLine("patched ContentManager roots=romfs:/Content, SDL2 title root=romfs:/");
    Console.WriteLine("installed Switch mapping hooks=1; replaced GetGamePadState SDL button calls=21");
    Console.WriteLine("Game.Tick hooks: BeginTick=1 EndTick=2 BeginUpdate=2 EndUpdate=2 BeginDraw=1 EndDraw=1");
    Console.WriteLine("preserved gameplay scheduling and existing assembly references; added NxInputDiag only");
    Console.WriteLine("wrote " + output);
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}

static TypeDefinition FindType(ModuleDefinition module, string name) =>
    module.GetType(name) ?? throw new InvalidDataException("required type not found: " + name);

static MethodDefinition FindMethod(TypeDefinition type, string name, string result, params string[] parameters)
{
    var matches = type.Methods.Where(m => m.Name == name && m.ReturnType.FullName == result &&
        m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)).ToArray();
    Require(matches.Length == 1, $"expected one {type.FullName}::{name}({string.Join(",", parameters)}) returning {result}; found {matches.Length}");
    return matches[0];
}

static void RequireBody(MethodDefinition method) =>
    Require(method.HasBody && method.Body.Instructions.Count != 0, "missing IL body: " + method.FullName);

static void RequireInternalCall(MethodDefinition method) =>
    Require(method.IsPublic && method.IsStatic && method.IsInternalCall && !method.HasBody,
        "helper method must be public static extern InternalCall: " + method.FullName);

static Instruction[] Calls(MethodDefinition method, string type, string name) =>
    method.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
        i.Operand is MethodReference m && m.DeclaringType.FullName == type && m.Name == name).ToArray();

static int IntegerConstant(Instruction instruction) => instruction.OpCode.Code switch
{
    Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3,
    Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7,
    Code.Ldc_I4_8 => 8, Code.Ldc_I4_S => (sbyte)instruction.Operand,
    Code.Ldc_I4 => (int)instruction.Operand, _ => int.MinValue
};

static void RequireNoIncoming(MethodDefinition method, params Instruction[] targets)
{
    var targetSet = targets.ToHashSet();
    Require(!method.Body.Instructions.Any(i =>
        i.Operand is Instruction target && targetSet.Contains(target) ||
        i.Operand is Instruction[] branches && branches.Any(targetSet.Contains)) &&
        !method.Body.ExceptionHandlers.Any(h => targetSet.Contains(h.TryStart) || targetSet.Contains(h.TryEnd) ||
            targetSet.Contains(h.HandlerStart) || targetSet.Contains(h.HandlerEnd) || targetSet.Contains(h.FilterStart)),
        "unexpected branch/exception target inside patch site: " + method.FullName);
}

static void ReplaceWithRomfsRoot(MethodDefinition method, string root)
{
    method.Body.Variables.Clear();
    method.Body.ExceptionHandlers.Clear();
    method.Body.Instructions.Clear();
    method.Body.InitLocals = false;
    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, root));
    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
}

static void SurroundCall(ILProcessor il, Instruction call, MethodReference begin, MethodReference end)
{
    // The two arguments already on the evaluation stack survive a void() call.
    // Keep every original argument load, virtual dispatch and continuation intact.
    il.InsertBefore(call, Instruction.Create(OpCodes.Call, begin));
    il.InsertAfter(call, Instruction.Create(OpCodes.Call, end));
}

static void WidenOverflowingBranches(MethodDefinition method)
{
    bool changed;
    do
    {
        changed = false;
        var offsets = new Dictionary<Instruction, int>();
        int offset = 0;
        foreach (var instruction in method.Body.Instructions)
        {
            offsets.Add(instruction, offset);
            offset += instruction.GetSize();
        }
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.OperandType != OperandType.ShortInlineBrTarget)
                continue;
            int delta = offsets[(Instruction)instruction.Operand] - offsets[instruction] - instruction.GetSize();
            if (delta >= sbyte.MinValue && delta <= sbyte.MaxValue)
                continue;
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bgt_S => OpCodes.Bgt,
                Code.Ble_S => OpCodes.Ble, Code.Blt_S => OpCodes.Blt, Code.Bne_Un_S => OpCodes.Bne_Un,
                Code.Bge_Un_S => OpCodes.Bge_Un, Code.Bgt_Un_S => OpCodes.Bgt_Un,
                Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_Un_S => OpCodes.Blt_Un, Code.Leave_S => OpCodes.Leave,
                _ => throw new InvalidDataException("unsupported short branch in " + method.FullName)
            };
            changed = true;
        }
    } while (changed);
}
