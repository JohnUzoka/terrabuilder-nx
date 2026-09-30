using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

partial class Program
{
    record HookTargetSpec(
        string TargetTypeName,
        string HookTypeName,
        string MethodName,
        string EventName,
        bool IsStatic,
        bool IsCtor,
        string TargetFullName
    );

    static readonly HookTargetSpec[] LegacyMutantHooks = new[]
    {
        new HookTargetSpec("Terraria.Player", "Terraria.On_Player", "DoCommonDashHandle", "DoCommonDashHandle", false, false, null),
        new HookTargetSpec("Terraria.Player", "Terraria.On_Player", "KeyDoubleTap", "KeyDoubleTap", false, false, null),
        new HookTargetSpec("Terraria.Player", "Terraria.On_Player", "KeyHoldDown", "KeyHoldDown", false, false, null),
        new HookTargetSpec("Terraria.Recipe", "Terraria.On_Recipe", "FindRecipes", "FindRecipes", true, false, null),
        new HookTargetSpec("Terraria.WorldGen", "Terraria.On_WorldGen", "CountTileTypesInArea", "CountTileTypesInArea", true, false, null),
        new HookTargetSpec("Terraria.SceneMetrics", "Terraria.On_SceneMetrics", "ExportTileCountsToMain", "ExportTileCountsToMain", false, false, null),
        new HookTargetSpec("Terraria.Player", "Terraria.On_Player", "HasUnityPotion", "HasUnityPotion", false, false, null),
        new HookTargetSpec("Terraria.Player", "Terraria.On_Player", "TakeUnityPotion", "TakeUnityPotion", false, false, null),
        new HookTargetSpec("Terraria.Player", "Terraria.On_Player", "DropTombstone", "DropTombstone", false, false, null),
        new HookTargetSpec("Terraria.Item", "Terraria.On_Item", "GetShimmered", "GetShimmered", false, false, null),
        new HookTargetSpec("Terraria.Main", "Terraria.On_Main", "DrawInterface_Resources_Buffs", "DrawInterface_Resources_Buffs", false, false, null),
        new HookTargetSpec("Terraria.WorldGen", "Terraria.On_WorldGen", "ShakeTree", "ShakeTree", true, false, null),
    };

    static Instruction LdargHelper(ILProcessor il, int index, ParameterDefinition p)
    {
        return index switch
        {
            0 => il.Create(OpCodes.Ldarg_0),
            1 => il.Create(OpCodes.Ldarg_1),
            2 => il.Create(OpCodes.Ldarg_2),
            3 => il.Create(OpCodes.Ldarg_3),
            _ => il.Create(OpCodes.Ldarg, p)
        };
    }

    static bool TypeMatches(TypeReference a, TypeReference b)
    {
        if (a.FullName == b.FullName) return true;
        if (a.IsByReference != b.IsByReference) return false;
        return a.FullName.TrimEnd('&') == b.FullName.TrimEnd('&');
    }

    static MethodDefinition CloneMethodBody(MethodDefinition src, string newName, TypeDefinition targetType)
    {
        var dst = new MethodDefinition(
            newName,
            MethodAttributes.Private | (src.Attributes & MethodAttributes.Static) | MethodAttributes.HideBySig,
            src.ReturnType
        );

        foreach (var p in src.Parameters)
        {
            var newP = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
            if (p.HasConstant) newP.Constant = p.Constant;
            dst.Parameters.Add(newP);
        }

        var srcBody = src.Body;
        var dstBody = dst.Body;
        dstBody.InitLocals = srcBody.InitLocals;
        dstBody.MaxStackSize = srcBody.MaxStackSize;

        var varMap = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var v in srcBody.Variables)
        {
            var newV = new VariableDefinition(v.VariableType);
            dstBody.Variables.Add(newV);
            varMap[v] = newV;
        }

        var paramMap = new Dictionary<ParameterDefinition, ParameterDefinition>();
        for (int i = 0; i < src.Parameters.Count; i++)
        {
            paramMap[src.Parameters[i]] = dst.Parameters[i];
        }

        var instrMap = new Dictionary<Instruction, Instruction>();
        foreach (var instr in srcBody.Instructions)
        {
            Instruction newInstr;
            if (instr.Operand is VariableDefinition vd)
            {
                newInstr = Instruction.Create(instr.OpCode, varMap[vd]);
            }
            else if (instr.Operand is ParameterDefinition pd)
            {
                newInstr = Instruction.Create(instr.OpCode, paramMap[pd]);
            }
            else if (instr.Operand is Instruction)
            {
                newInstr = Instruction.Create(instr.OpCode, Instruction.Create(OpCodes.Nop));
            }
            else if (instr.Operand is Instruction[])
            {
                newInstr = Instruction.Create(instr.OpCode, new Instruction[0]);
            }
            else if (instr.Operand == null)
            {
                newInstr = Instruction.Create(instr.OpCode);
            }
            else
            {
                dynamic op = instr.Operand;
                newInstr = Instruction.Create(instr.OpCode, op);
            }
            dstBody.Instructions.Add(newInstr);
            instrMap[instr] = newInstr;
        }

        for (int i = 0; i < srcBody.Instructions.Count; i++)
        {
            var srcInstr = srcBody.Instructions[i];
            var dstInstr = dstBody.Instructions[i];

            if (srcInstr.Operand is Instruction target)
            {
                dstInstr.Operand = instrMap[target];
            }
            else if (srcInstr.Operand is Instruction[] targets)
            {
                dstInstr.Operand = targets.Select(t => instrMap[t]).ToArray();
            }
        }

        foreach (var eh in srcBody.ExceptionHandlers)
        {
            var newEh = new ExceptionHandler(eh.HandlerType)
            {
                CatchType = eh.CatchType,
                TryStart = eh.TryStart != null ? instrMap[eh.TryStart] : null,
                TryEnd = eh.TryEnd != null ? instrMap[eh.TryEnd] : null,
                HandlerStart = eh.HandlerStart != null ? instrMap[eh.HandlerStart] : null,
                HandlerEnd = eh.HandlerEnd != null ? instrMap[eh.HandlerEnd] : null,
                FilterStart = eh.FilterStart != null ? instrMap[eh.FilterStart] : null,
            };
            dstBody.ExceptionHandlers.Add(newEh);
        }

        targetType.Methods.Add(dst);
        return dst;
    }

    static void SetContentDerivedMvid(AssemblyDefinition asm, string outPath)
    {
        asm.MainModule.Mvid = Guid.Empty;
        var writerParams = new WriterParameters { Timestamp = 0 };

        using (var ms = new MemoryStream())
        {
            asm.Write(ms, writerParams);
            byte[] bytes = ms.ToArray();
            byte[] hash = SHA256.HashData(bytes);
            byte[] guidBytes = new byte[16];
            Array.Copy(hash, 0, guidBytes, 0, 16);
            asm.MainModule.Mvid = new Guid(guidBytes);
        }

        asm.Write(outPath, writerParams);
        Console.WriteLine($"Saved {outPath} with deterministic content-derived MVID: {asm.MainModule.Mvid}");
    }

    static void CheckUnsupportedTarget(MethodDefinition targetMethod, string targetName)
    {
        if (targetMethod.HasGenericParameters)
            throw new NotSupportedException($"Generic method '{targetName}' is unsupported in offline hook lowering.");

        if (targetMethod.CallingConvention == MethodCallingConvention.VarArg)
            throw new NotSupportedException($"Vararg method '{targetName}' is unsupported in offline hook lowering.");

        if (targetMethod.ReturnType.IsByReference)
            throw new NotSupportedException($"Ref return in method '{targetName}' is unsupported in offline hook lowering.");

        if (targetMethod.DeclaringType.IsValueType)
            throw new NotSupportedException($"Value type target method '{targetName}' is unsupported in offline hook lowering.");

        foreach (var p in targetMethod.Parameters)
        {
            if (p.ParameterType.IsGenericParameter)
                throw new NotSupportedException($"Generic parameter in '{targetName}' is unsupported in offline hook lowering.");
        }
    }

    static List<HookTargetSpec> LoadInventorySpecs(string inventoryPath)
    {
        var specs = new List<HookTargetSpec>();
        using var doc = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("on_hooks", out var onHooks))
            return specs;

        var seen = new HashSet<string>();
        foreach (var h in onHooks.EnumerateArray())
        {
            string hookType = h.GetProperty("hook_type").GetString()!;
            string eventName = h.GetProperty("event").GetString()!;
            var target = h.GetProperty("target");
            string targetType = target.GetProperty("type").GetString()!;
            string methodName = target.GetProperty("method").GetString()!;
            bool isStatic = target.GetProperty("is_static").GetBoolean();
            bool isCtor = target.GetProperty("is_ctor").GetBoolean();
            string fullName = target.TryGetProperty("full_name", out var fnProp) ? fnProp.GetString() : null;

            bool hasAdd = false;
            if (h.TryGetProperty("registrations", out var regs))
            {
                foreach (var r in regs.EnumerateArray())
                {
                    if (r.GetProperty("op").GetString() == "add")
                    {
                        hasAdd = true;
                        break;
                    }
                }
            }

            if (!hasAdd)
                continue;

            string key = $"{hookType}::{eventName}";
            if (!seen.Add(key))
                continue;

            specs.Add(new HookTargetSpec(targetType, hookType, methodName, eventName, isStatic, isCtor, fullName));
        }


        return specs;
    }

    static void PatchMonoModRuntimeDetour(string runtimeDetourIn, string runtimeDetourOut)
    {
        Console.WriteLine($"=== Patching MonoMod.RuntimeDetour Global Guards ===");
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(runtimeDetourIn));
        byte[] inBytes = File.ReadAllBytes(runtimeDetourIn);
        var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(inBytes), new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false });

        var notSupportedCtor = asm.MainModule.ImportReference(
            typeof(NotSupportedException).GetConstructor(new[] { typeof(string) })
        );

        var detManager = asm.MainModule.GetType("MonoMod.RuntimeDetour.DetourManager");
        var mds = detManager.NestedTypes.First(t => t.Name == "ManagedDetourState");
        var sourceField = mds.Fields.First(f => f.Name == "Source");
        var getName = asm.MainModule.ImportReference(typeof(System.Reflection.MemberInfo).GetMethod("get_Name"));
        var concat = asm.MainModule.ImportReference(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) }));

        // 1. ManagedDetourState.AddDetour -> fail closed with target method name
        var addDetour = mds.Methods.First(m => m.Name == "AddDetour");
        addDetour.Body.Instructions.Clear();
        addDetour.Body.Variables.Clear();
        addDetour.Body.ExceptionHandlers.Clear();
        addDetour.Body.InitLocals = true;

        var ilAdd = addDetour.Body.GetILProcessor();
        ilAdd.Append(ilAdd.Create(OpCodes.Ldstr, "hook not lowered at build time; rebuild the mod set: "));
        ilAdd.Append(ilAdd.Create(OpCodes.Ldarg_0));
        ilAdd.Append(ilAdd.Create(OpCodes.Ldfld, sourceField));
        ilAdd.Append(ilAdd.Create(OpCodes.Callvirt, getName));
        ilAdd.Append(ilAdd.Create(OpCodes.Call, concat));
        ilAdd.Append(ilAdd.Create(OpCodes.Newobj, notSupportedCtor));
        ilAdd.Append(ilAdd.Create(OpCodes.Throw));

        // 2. ManagedDetourState.RemoveDetour -> no-op
        var remDetour = mds.Methods.First(m => m.Name == "RemoveDetour");
        remDetour.Body.Instructions.Clear();
        remDetour.Body.Variables.Clear();
        remDetour.Body.ExceptionHandlers.Clear();
        remDetour.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));

        // 3. NativeDetourState.AddDetour -> fail closed
        var nds = detManager.NestedTypes.First(t => t.Name == "NativeDetourState");
        var addNat = nds.Methods.First(m => m.Name == "AddDetour");
        addNat.Body.Instructions.Clear();
        addNat.Body.Variables.Clear();
        addNat.Body.ExceptionHandlers.Clear();
        var ilNat = addNat.Body.GetILProcessor();
        ilNat.Append(ilNat.Create(OpCodes.Ldstr, "Native detour not supported in offline lowered mode; rebuild the mod set."));
        ilNat.Append(ilNat.Create(OpCodes.Newobj, notSupportedCtor));
        ilNat.Append(ilNat.Create(OpCodes.Throw));

        // 4. NativeDetourState.RemoveDetour -> no-op
        var remNat = nds.Methods.First(m => m.Name == "RemoveDetour");
        remNat.Body.Instructions.Clear();
        remNat.Body.Variables.Clear();
        remNat.Body.ExceptionHandlers.Clear();
        remNat.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));

        // 5. Hook: every constructor binds to the build-time lowered chain (see RuntimeDetourLowering.cs).
        PatchHookToLoweredChains(asm.MainModule, notSupportedCtor, getName, concat);

        // 6. NativeHook.Apply -> fail closed
        var natHook = asm.MainModule.GetType("MonoMod.RuntimeDetour.NativeHook");
        if (natHook != null)
        {
            var applyNat = natHook.Methods.FirstOrDefault(m => m.Name == "Apply");
            if (applyNat != null)
            {
                applyNat.Body.Instructions.Clear();
                applyNat.Body.Variables.Clear();
                applyNat.Body.ExceptionHandlers.Clear();
                var ilAh = applyNat.Body.GetILProcessor();
                ilAh.Append(ilAh.Create(OpCodes.Ldstr, "Native hook not supported in offline lowered mode; rebuild the mod set."));
                ilAh.Append(ilAh.Create(OpCodes.Newobj, notSupportedCtor));
                ilAh.Append(ilAh.Create(OpCodes.Throw));
            }
            var undoNat = natHook.Methods.FirstOrDefault(m => m.Name == "Undo");
            if (undoNat != null)
            {
                undoNat.Body.Instructions.Clear();
                undoNat.Body.Variables.Clear();
                undoNat.Body.ExceptionHandlers.Clear();
                undoNat.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(runtimeDetourOut)!);
        SetContentDerivedMvid(asm, runtimeDetourOut);
        Console.WriteLine($"Patched MonoMod.RuntimeDetour with global guards saved to {runtimeDetourOut}");
    }

    static void Main(string[] args)
    {
        bool legacyMode = false;
        string inventoryPath = null;
        string detourInPath = null;
        var positionalArgs = new List<string>();

        string lowerModDetoursInventory = null, hooksInArg = null, tmlInArg = null, modsDirArg = null;
        var refDirs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--legacy")
            {
                legacyMode = true;
            }
            else if (args[i] == "--inventory" && i + 1 < args.Length)
            {
                inventoryPath = args[++i];
            }
            else if (args[i] == "--detour-in" && i + 1 < args.Length)
            {
                detourInPath = args[++i];
            }
            else if (args[i] == "--lower-mod-detours" && i + 1 < args.Length)
            {
                lowerModDetoursInventory = args[++i];
            }
            else if (args[i] == "--hooks-in" && i + 1 < args.Length)
            {
                hooksInArg = args[++i];
            }
            else if (args[i] == "--tml-in" && i + 1 < args.Length)
            {
                tmlInArg = args[++i];
            }
            else if (args[i] == "--mods-dir" && i + 1 < args.Length)
            {
                modsDirArg = args[++i];
            }
            else if (args[i] == "--ref-dir" && i + 1 < args.Length)
            {
                refDirs.Add(args[++i]);
            }
            else
            {
                positionalArgs.Add(args[i]);
            }
        }

        if (lowerModDetoursInventory != null)
        {
            if (hooksInArg == null || tmlInArg == null || detourInPath == null || modsDirArg == null)
                throw new ArgumentException("--lower-mod-detours needs --hooks-in, --tml-in, --detour-in and --mods-dir");
            Environment.Exit(LowerModDetours(lowerModDetoursInventory, hooksInArg, tmlInArg, detourInPath, modsDirArg, refDirs));
        }

        string terrariaHooksIn = positionalArgs.Count > 0 ? positionalArgs[0] : CacheRoot.Path + "/tmod/tmod09/romfs/TerrariaHooks.dll";
        string terrariaHooksOut = positionalArgs.Count > 1 ? positionalArgs[1] : CacheRoot.Path + "/tmod/mod-trials/fargo/patched/TerrariaHooks.dll";
        string tmlIn = positionalArgs.Count > 2 ? positionalArgs[2] : CacheRoot.Path + "/tmod/tmod09/romfs/tModLoader.dll";
        string tmlOut = positionalArgs.Count > 3 ? positionalArgs[3] : CacheRoot.Path + "/tmod/mod-trials/fargo/patched/tModLoader.dll";

        Directory.CreateDirectory(Path.GetDirectoryName(terrariaHooksOut)!);
        Directory.CreateDirectory(Path.GetDirectoryName(tmlOut)!);

        List<HookTargetSpec> activeHooks;
        if (legacyMode)
        {
            Console.WriteLine("Running in LEGACY mode (exact 12 HookSpec Mutant targets, single handler enforcement)");
            activeHooks = LegacyMutantHooks.ToList();
        }
        else if (!string.IsNullOrEmpty(inventoryPath))
        {
            Console.WriteLine($"Running in GENERIC inventory mode with inventory: {inventoryPath}");
            activeHooks = LoadInventorySpecs(inventoryPath);
            Console.WriteLine($"Loaded {activeHooks.Count} distinct On_ hook targets with active additions.");
        }
        else
        {
            Console.WriteLine("No --inventory specified, running in GENERIC mode with default 12 Mutant hooks.");
            activeHooks = LegacyMutantHooks.ToList();
        }

        Console.WriteLine($"=== Patching TerrariaHooks ===");
        var resolverHooks = new DefaultAssemblyResolver();
        resolverHooks.AddSearchDirectory(Path.GetDirectoryName(terrariaHooksIn));
        var hooksAsm = AssemblyDefinition.ReadAssembly(terrariaHooksIn, new ReaderParameters { AssemblyResolver = resolverHooks, ReadWrite = false });

        var sysRuntime = hooksAsm.MainModule.AssemblyReferences.First(a => a.Name == "System.Runtime");
        var notSupportedCtor = hooksAsm.MainModule.ImportReference(
            typeof(NotSupportedException).GetConstructor(new[] { typeof(string) })
        );
        if (!legacyMode && !string.IsNullOrEmpty(inventoryPath))
            AddRuntimeDetourSpecs(activeHooks, inventoryPath, hooksAsm.MainModule, tmlIn);


        if (legacyMode)
        {
            foreach (var spec in activeHooks)
            {
                var hookType = hooksAsm.MainModule.GetType(spec.HookTypeName)
                    ?? throw new InvalidOperationException($"Type {spec.HookTypeName} not found in TerrariaHooks!");

                var hookDelegateType = hookType.NestedTypes.FirstOrDefault(t => t.Name == "hook_" + spec.MethodName)
                    ?? throw new InvalidOperationException($"Delegate hook_{spec.MethodName} not found in {spec.HookTypeName}!");

                string fieldName = "Hook_" + spec.MethodName;
                var hookField = hookType.Fields.FirstOrDefault(f => f.Name == fieldName);
                if (hookField == null)
                {
                    hookField = new FieldDefinition(fieldName, FieldAttributes.Public | FieldAttributes.Static, hookDelegateType);
                    hookType.Fields.Add(hookField);
                }

                var addMethod = hookType.Methods.FirstOrDefault(m => m.Name == "add_" + spec.MethodName)
                    ?? throw new InvalidOperationException($"Method add_{spec.MethodName} not found!");
                addMethod.Body.Instructions.Clear();
                var ilAdd = addMethod.Body.GetILProcessor();
                var okLabel = ilAdd.Create(OpCodes.Nop);

                ilAdd.Append(ilAdd.Create(OpCodes.Ldsfld, hookField));
                ilAdd.Append(ilAdd.Create(OpCodes.Brfalse_S, okLabel));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldstr, "Multiple hook registrations not supported in offline lowered mode for " + spec.MethodName));
                ilAdd.Append(ilAdd.Create(OpCodes.Newobj, notSupportedCtor));
                ilAdd.Append(ilAdd.Create(OpCodes.Throw));

                ilAdd.Append(okLabel);
                ilAdd.Append(ilAdd.Create(OpCodes.Ldarg_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Stsfld, hookField));
                ilAdd.Append(ilAdd.Create(OpCodes.Ret));

                var removeMethod = hookType.Methods.FirstOrDefault(m => m.Name == "remove_" + spec.MethodName)
                    ?? throw new InvalidOperationException($"Method remove_{spec.MethodName} not found!");
                removeMethod.Body.Instructions.Clear();
                var ilRem = removeMethod.Body.GetILProcessor();
                var retLabel = ilRem.Create(OpCodes.Ret);

                ilRem.Append(ilRem.Create(OpCodes.Ldsfld, hookField));
                ilRem.Append(ilRem.Create(OpCodes.Ldarg_0));
                ilRem.Append(ilRem.Create(OpCodes.Bne_Un_S, retLabel));
                ilRem.Append(ilRem.Create(OpCodes.Ldnull));
                ilRem.Append(ilRem.Create(OpCodes.Stsfld, hookField));
                ilRem.Append(retLabel);

                Console.WriteLine($"Patched TerrariaHooks hook field and event methods for {spec.HookTypeName}::{spec.MethodName}");
            }

            var registryType = new TypeDefinition("TerrariaHooks", "LoweredHookRegistry",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                hooksAsm.MainModule.TypeSystem.Object);
            hooksAsm.MainModule.Types.Add(registryType);

            var assemblyTypeRef = new TypeReference("System.Reflection", "Assembly", hooksAsm.MainModule, sysRuntime);
            var delegateTypeRef = new TypeReference("System", "Delegate", hooksAsm.MainModule, sysRuntime);
            var methodInfoTypeRef = new TypeReference("System.Reflection", "MethodInfo", hooksAsm.MainModule, sysRuntime);
            var memberInfoTypeRef = new TypeReference("System.Reflection", "MemberInfo", hooksAsm.MainModule, sysRuntime);
            var typeTypeRef = new TypeReference("System", "Type", hooksAsm.MainModule, sysRuntime);

            var delegateGetMethod = new MethodReference("get_Method", methodInfoTypeRef, delegateTypeRef) { HasThis = true };
            var memberGetDeclaringType = new MethodReference("get_DeclaringType", typeTypeRef, memberInfoTypeRef) { HasThis = true };
            var typeGetAssembly = new MethodReference("get_Assembly", assemblyTypeRef, typeTypeRef) { HasThis = true };

            var clearModHooksMethod = new MethodDefinition("ClearModHooks",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                hooksAsm.MainModule.TypeSystem.Void);
            clearModHooksMethod.Parameters.Add(new ParameterDefinition("modAssembly", ParameterAttributes.None, assemblyTypeRef));
            registryType.Methods.Add(clearModHooksMethod);

            var varDelegate = new VariableDefinition(delegateTypeRef);
            var varMethod = new VariableDefinition(methodInfoTypeRef);
            var varDeclaringType = new VariableDefinition(typeTypeRef);
            clearModHooksMethod.Body.Variables.Add(varDelegate);
            clearModHooksMethod.Body.Variables.Add(varMethod);
            clearModHooksMethod.Body.Variables.Add(varDeclaringType);
            clearModHooksMethod.Body.InitLocals = true;

            var ilClear = clearModHooksMethod.Body.GetILProcessor();
            foreach (var spec in activeHooks)
            {
                var hookType = hooksAsm.MainModule.GetType(spec.HookTypeName)!;
                var hookField = hookType.Fields.First(f => f.Name == "Hook_" + spec.MethodName);

                var nextLabel = ilClear.Create(OpCodes.Nop);
                var doClearLabel = ilClear.Create(OpCodes.Nop);

                ilClear.Append(ilClear.Create(OpCodes.Ldsfld, hookField));
                ilClear.Append(ilClear.Create(OpCodes.Stloc_0));

                ilClear.Append(ilClear.Create(OpCodes.Ldloc_0));
                ilClear.Append(ilClear.Create(OpCodes.Brfalse_S, nextLabel));

                ilClear.Append(ilClear.Create(OpCodes.Ldarg_0));
                ilClear.Append(ilClear.Create(OpCodes.Brfalse_S, doClearLabel));

                ilClear.Append(ilClear.Create(OpCodes.Ldloc_0));
                ilClear.Append(ilClear.Create(OpCodes.Callvirt, delegateGetMethod));
                ilClear.Append(ilClear.Create(OpCodes.Stloc_1));

                ilClear.Append(ilClear.Create(OpCodes.Ldloc_1));
                ilClear.Append(ilClear.Create(OpCodes.Brfalse_S, nextLabel));

                ilClear.Append(ilClear.Create(OpCodes.Ldloc_1));
                ilClear.Append(ilClear.Create(OpCodes.Callvirt, memberGetDeclaringType));
                ilClear.Append(ilClear.Create(OpCodes.Stloc_2));

                ilClear.Append(ilClear.Create(OpCodes.Ldloc_2));
                ilClear.Append(ilClear.Create(OpCodes.Brfalse_S, nextLabel));

                ilClear.Append(ilClear.Create(OpCodes.Ldloc_2));
                ilClear.Append(ilClear.Create(OpCodes.Callvirt, typeGetAssembly));
                ilClear.Append(ilClear.Create(OpCodes.Ldarg_0));
                ilClear.Append(ilClear.Create(OpCodes.Bne_Un_S, nextLabel));

                ilClear.Append(doClearLabel);
                ilClear.Append(ilClear.Create(OpCodes.Ldnull));
                ilClear.Append(ilClear.Create(OpCodes.Stsfld, hookField));

                ilClear.Append(nextLabel);
            }
            ilClear.Append(ilClear.Create(OpCodes.Ret));

            var clearAllMethod = new MethodDefinition("ClearAll",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                hooksAsm.MainModule.TypeSystem.Void);
            registryType.Methods.Add(clearAllMethod);
            var ilClearAll = clearAllMethod.Body.GetILProcessor();
            ilClearAll.Append(ilClearAll.Create(OpCodes.Ldnull));
            ilClearAll.Append(ilClearAll.Create(OpCodes.Call, clearModHooksMethod));
            ilClearAll.Append(ilClearAll.Create(OpCodes.Ret));
            Console.WriteLine("Added TerrariaHooks.LoweredHookRegistry with owner-aware ClearModHooks and ClearAll");
        }
        else
        {
            var intTypeRef = hooksAsm.MainModule.TypeSystem.Int32;
            var assemblyTypeRef = new TypeReference("System.Reflection", "Assembly", hooksAsm.MainModule, sysRuntime);
            var delegateTypeRef = new TypeReference("System", "Delegate", hooksAsm.MainModule, sysRuntime);
            var methodInfoTypeRef = new TypeReference("System.Reflection", "MethodInfo", hooksAsm.MainModule, sysRuntime);
            var memberInfoTypeRef = new TypeReference("System.Reflection", "MemberInfo", hooksAsm.MainModule, sysRuntime);
            var typeTypeRef = new TypeReference("System", "Type", hooksAsm.MainModule, sysRuntime);

            var interlockedCompareExchange = hooksAsm.MainModule.ImportReference(
                typeof(System.Threading.Interlocked).GetMethods()
                    .First(m => m.Name == "CompareExchange" && m.IsGenericMethod && m.GetParameters().Length == 3)
            );

            var delegateGetMethod = new MethodReference("get_Method", methodInfoTypeRef, delegateTypeRef) { HasThis = true };
            var memberGetDeclaringType = new MethodReference("get_DeclaringType", typeTypeRef, memberInfoTypeRef) { HasThis = true };
            var typeGetAssembly = new MethodReference("get_Assembly", assemblyTypeRef, typeTypeRef) { HasThis = true };

            var registryType = new TypeDefinition("TerrariaHooks", "LoweredHookRegistry",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                hooksAsm.MainModule.TypeSystem.Object);
            hooksAsm.MainModule.Types.Add(registryType);

            var clearModHooksMethod = new MethodDefinition("ClearModHooks",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                hooksAsm.MainModule.TypeSystem.Void);
            clearModHooksMethod.Parameters.Add(new ParameterDefinition("modAssembly", ParameterAttributes.None, assemblyTypeRef));
            registryType.Methods.Add(clearModHooksMethod);

            var varDelegate = new VariableDefinition(delegateTypeRef);
            var varMethod = new VariableDefinition(methodInfoTypeRef);
            var varDeclaringType = new VariableDefinition(typeTypeRef);
            clearModHooksMethod.Body.Variables.Add(varDelegate);
            clearModHooksMethod.Body.Variables.Add(varMethod);
            clearModHooksMethod.Body.Variables.Add(varDeclaringType);
            clearModHooksMethod.Body.InitLocals = true;

            var ilClear = clearModHooksMethod.Body.GetILProcessor();

            foreach (var spec in activeHooks)
            {
                var hookType = hooksAsm.MainModule.GetType(spec.HookTypeName)
                    ?? throw new InvalidOperationException($"Type {spec.HookTypeName} not found in TerrariaHooks!");

                string delegateName = "hook_" + spec.EventName;
                var hookDelegateType = hookType.NestedTypes.FirstOrDefault(t => t.Name == delegateName)
                    ?? throw new InvalidOperationException($"Delegate {delegateName} not found in {spec.HookTypeName}!");

                var arrayOfDelegateType = new ArrayType(hookDelegateType);

                string arrayFieldName = "Hooks_" + spec.EventName;
                var arrayField = hookType.Fields.FirstOrDefault(f => f.Name == arrayFieldName);
                if (arrayField == null)
                {
                    arrayField = new FieldDefinition(arrayFieldName, FieldAttributes.Public | FieldAttributes.Static, arrayOfDelegateType);
                    hookType.Fields.Add(arrayField);
                }

                string legacyFieldName = "Hook_" + spec.EventName;
                var legacyField = hookType.Fields.FirstOrDefault(f => f.Name == legacyFieldName);
                if (legacyField == null)
                {
                    legacyField = new FieldDefinition(legacyFieldName, FieldAttributes.Public | FieldAttributes.Static, hookDelegateType);
                    hookType.Fields.Add(legacyField);
                }

                var genCompareExchange = new GenericInstanceMethod(interlockedCompareExchange);
                genCompareExchange.GenericArguments.Add(arrayOfDelegateType);

                var addMethod = hookType.Methods.FirstOrDefault(m => m.Name == "add_" + spec.EventName)
                    ?? throw new InvalidOperationException($"Method add_{spec.EventName} not found in {spec.HookTypeName}!");
                addMethod.Body.Instructions.Clear();
                addMethod.Body.Variables.Clear();
                addMethod.Body.InitLocals = true;

                var vOldAdd = new VariableDefinition(arrayOfDelegateType);
                var vNewAdd = new VariableDefinition(arrayOfDelegateType);
                addMethod.Body.Variables.Add(vOldAdd);
                addMethod.Body.Variables.Add(vNewAdd);

                var ilAdd = addMethod.Body.GetILProcessor();
                var loopStartAdd = ilAdd.Create(OpCodes.Nop);
                var isNullAdd = ilAdd.Create(OpCodes.Nop);
                var afterAllocAdd = ilAdd.Create(OpCodes.Nop);

                ilAdd.Append(loopStartAdd);
                ilAdd.Append(ilAdd.Create(OpCodes.Ldsfld, arrayField));
                ilAdd.Append(ilAdd.Create(OpCodes.Stloc_0));

                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Brfalse_S, isNullAdd));

                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldlen));
                ilAdd.Append(ilAdd.Create(OpCodes.Conv_I4));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldc_I4_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Add));
                ilAdd.Append(ilAdd.Create(OpCodes.Newarr, hookDelegateType));
                ilAdd.Append(ilAdd.Create(OpCodes.Stloc_1));

                var arrayCopyMethod = hooksAsm.MainModule.ImportReference(
                    typeof(Array).GetMethod("Copy", new[] { typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int) })
                );
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldc_I4_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldc_I4_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldlen));
                ilAdd.Append(ilAdd.Create(OpCodes.Conv_I4));
                ilAdd.Append(ilAdd.Create(OpCodes.Call, arrayCopyMethod));

                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldlen));
                ilAdd.Append(ilAdd.Create(OpCodes.Conv_I4));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldarg_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Stelem_Ref));
                ilAdd.Append(ilAdd.Create(OpCodes.Br_S, afterAllocAdd));

                ilAdd.Append(isNullAdd);
                ilAdd.Append(ilAdd.Create(OpCodes.Ldc_I4_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Newarr, hookDelegateType));
                ilAdd.Append(ilAdd.Create(OpCodes.Stloc_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldc_I4_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldarg_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Stelem_Ref));

                ilAdd.Append(afterAllocAdd);
                ilAdd.Append(ilAdd.Create(OpCodes.Ldsflda, arrayField));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Call, genCompareExchange));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_0));
                ilAdd.Append(ilAdd.Create(OpCodes.Bne_Un_S, loopStartAdd));

                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldloc_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldlen));
                ilAdd.Append(ilAdd.Create(OpCodes.Conv_I4));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldc_I4_1));
                ilAdd.Append(ilAdd.Create(OpCodes.Sub));
                ilAdd.Append(ilAdd.Create(OpCodes.Ldelem_Ref));
                ilAdd.Append(ilAdd.Create(OpCodes.Stsfld, legacyField));
                ilAdd.Append(ilAdd.Create(OpCodes.Ret));

                var removeMethod = hookType.Methods.FirstOrDefault(m => m.Name == "remove_" + spec.EventName)
                    ?? throw new InvalidOperationException($"Method remove_{spec.EventName} not found in {spec.HookTypeName}!");
                removeMethod.Body.Instructions.Clear();
                removeMethod.Body.Variables.Clear();
                removeMethod.Body.InitLocals = true;

                var vOldRem = new VariableDefinition(arrayOfDelegateType);
                var vNewRem = new VariableDefinition(arrayOfDelegateType);
                var vIdxRem = new VariableDefinition(intTypeRef);
                var vLenRem = new VariableDefinition(intTypeRef);
                removeMethod.Body.Variables.Add(vOldRem);
                removeMethod.Body.Variables.Add(vNewRem);
                removeMethod.Body.Variables.Add(vIdxRem);
                removeMethod.Body.Variables.Add(vLenRem);

                var ilRem = removeMethod.Body.GetILProcessor();
                var loopStartRem = ilRem.Create(OpCodes.Nop);
                var retLabelRem = ilRem.Create(OpCodes.Ret);
                var singleToNullRem = ilRem.Create(OpCodes.Nop);
                var afterAllocRem = ilRem.Create(OpCodes.Nop);

                ilRem.Append(loopStartRem);
                ilRem.Append(ilRem.Create(OpCodes.Ldsfld, arrayField));
                ilRem.Append(ilRem.Create(OpCodes.Stloc_0));

                ilRem.Append(ilRem.Create(OpCodes.Ldloc_0));
                ilRem.Append(ilRem.Create(OpCodes.Brfalse, retLabelRem));

                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_M1));
                ilRem.Append(ilRem.Create(OpCodes.Stloc_2));

                ilRem.Append(ilRem.Create(OpCodes.Ldloc_0));
                ilRem.Append(ilRem.Create(OpCodes.Ldlen));
                ilRem.Append(ilRem.Create(OpCodes.Conv_I4));
                ilRem.Append(ilRem.Create(OpCodes.Stloc_3));

                var searchLoopStart = ilRem.Create(OpCodes.Nop);
                var searchLoopCheck = ilRem.Create(OpCodes.Nop);
                var vSearchI = new VariableDefinition(intTypeRef);
                removeMethod.Body.Variables.Add(vSearchI);
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_0));
                ilRem.Append(ilRem.Create(OpCodes.Stloc, vSearchI));
                ilRem.Append(ilRem.Create(OpCodes.Br_S, searchLoopCheck));

                ilRem.Append(searchLoopStart);
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_0));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc, vSearchI));
                ilRem.Append(ilRem.Create(OpCodes.Ldelem_Ref));
                ilRem.Append(ilRem.Create(OpCodes.Ldarg_0));
                var notMatch = ilRem.Create(OpCodes.Nop);
                ilRem.Append(ilRem.Create(OpCodes.Bne_Un_S, notMatch));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc, vSearchI));
                ilRem.Append(ilRem.Create(OpCodes.Stloc_2));
                var breakSearch = ilRem.Create(OpCodes.Nop);
                ilRem.Append(ilRem.Create(OpCodes.Br_S, breakSearch));

                ilRem.Append(notMatch);
                ilRem.Append(ilRem.Create(OpCodes.Ldloc, vSearchI));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_1));
                ilRem.Append(ilRem.Create(OpCodes.Add));
                ilRem.Append(ilRem.Create(OpCodes.Stloc, vSearchI));

                ilRem.Append(searchLoopCheck);
                ilRem.Append(ilRem.Create(OpCodes.Ldloc, vSearchI));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_3));
                ilRem.Append(ilRem.Create(OpCodes.Blt_S, searchLoopStart));

                ilRem.Append(breakSearch);
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_2));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_0));
                ilRem.Append(ilRem.Create(OpCodes.Blt, retLabelRem));

                ilRem.Append(ilRem.Create(OpCodes.Ldloc_3));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_1));
                ilRem.Append(ilRem.Create(OpCodes.Beq_S, singleToNullRem));

                ilRem.Append(ilRem.Create(OpCodes.Ldloc_3));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_1));
                ilRem.Append(ilRem.Create(OpCodes.Sub));
                ilRem.Append(ilRem.Create(OpCodes.Newarr, hookDelegateType));
                ilRem.Append(ilRem.Create(OpCodes.Stloc_1));

                var noPreCopy = ilRem.Create(OpCodes.Nop);
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_2));
                ilRem.Append(ilRem.Create(OpCodes.Brfalse_S, noPreCopy));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_0));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_0));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_1));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_0));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_2));
                ilRem.Append(ilRem.Create(OpCodes.Call, arrayCopyMethod));
                ilRem.Append(noPreCopy);

                var noPostCopy = ilRem.Create(OpCodes.Nop);
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_3));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_2));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_1));
                ilRem.Append(ilRem.Create(OpCodes.Add));
                ilRem.Append(ilRem.Create(OpCodes.Sub));
                var vPostCount = new VariableDefinition(intTypeRef);
                removeMethod.Body.Variables.Add(vPostCount);
                ilRem.Append(ilRem.Create(OpCodes.Stloc, vPostCount));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc, vPostCount));
                ilRem.Append(ilRem.Create(OpCodes.Brfalse_S, noPostCopy));

                ilRem.Append(ilRem.Create(OpCodes.Ldloc_0));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_2));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_1));
                ilRem.Append(ilRem.Create(OpCodes.Add));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_1));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_2));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc, vPostCount));
                ilRem.Append(ilRem.Create(OpCodes.Call, arrayCopyMethod));
                ilRem.Append(noPostCopy);
                ilRem.Append(ilRem.Create(OpCodes.Br_S, afterAllocRem));

                ilRem.Append(singleToNullRem);
                ilRem.Append(ilRem.Create(OpCodes.Ldnull));
                ilRem.Append(ilRem.Create(OpCodes.Stloc_1));

                ilRem.Append(afterAllocRem);
                ilRem.Append(ilRem.Create(OpCodes.Ldsflda, arrayField));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_1));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_0));
                ilRem.Append(ilRem.Create(OpCodes.Call, genCompareExchange));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_0));
                ilRem.Append(ilRem.Create(OpCodes.Bne_Un, loopStartRem));

                var legacyNullRem = ilRem.Create(OpCodes.Nop);
                var legacyDoneRem = ilRem.Create(OpCodes.Nop);
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_1));
                ilRem.Append(ilRem.Create(OpCodes.Brfalse_S, legacyNullRem));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_1));
                ilRem.Append(ilRem.Create(OpCodes.Ldloc_1));
                ilRem.Append(ilRem.Create(OpCodes.Ldlen));
                ilRem.Append(ilRem.Create(OpCodes.Conv_I4));
                ilRem.Append(ilRem.Create(OpCodes.Ldc_I4_1));
                ilRem.Append(ilRem.Create(OpCodes.Sub));
                ilRem.Append(ilRem.Create(OpCodes.Ldelem_Ref));
                ilRem.Append(ilRem.Create(OpCodes.Stsfld, legacyField));
                ilRem.Append(ilRem.Create(OpCodes.Br_S, legacyDoneRem));

                ilRem.Append(legacyNullRem);
                ilRem.Append(ilRem.Create(OpCodes.Ldnull));
                ilRem.Append(ilRem.Create(OpCodes.Stsfld, legacyField));

                ilRem.Append(legacyDoneRem);
                ilRem.Append(ilRem.Create(OpCodes.Ret));

                ilRem.Append(retLabelRem);

                var clearHelper = new MethodDefinition("ClearHooks_" + spec.EventName,
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                    hooksAsm.MainModule.TypeSystem.Void);
                clearHelper.Parameters.Add(new ParameterDefinition("modAssembly", ParameterAttributes.None, assemblyTypeRef));
                hookType.Methods.Add(clearHelper);

                var ilHlp = clearHelper.Body.GetILProcessor();
                clearHelper.Body.Variables.Add(new VariableDefinition(arrayOfDelegateType));
                clearHelper.Body.Variables.Add(new VariableDefinition(intTypeRef));
                clearHelper.Body.InitLocals = true;

                var hlpRet = ilHlp.Create(OpCodes.Ret);
                ilHlp.Append(ilHlp.Create(OpCodes.Ldsfld, arrayField));
                ilHlp.Append(ilHlp.Create(OpCodes.Stloc_0));
                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc_0));
                ilHlp.Append(ilHlp.Create(OpCodes.Brfalse, hlpRet));

                var hlpLoop = ilHlp.Create(OpCodes.Nop);
                var hlpCheck = ilHlp.Create(OpCodes.Nop);
                ilHlp.Append(ilHlp.Create(OpCodes.Ldc_I4_0));
                ilHlp.Append(ilHlp.Create(OpCodes.Stloc_1));
                ilHlp.Append(ilHlp.Create(OpCodes.Br_S, hlpCheck));

                ilHlp.Append(hlpLoop);
                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc_0));
                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc_1));
                ilHlp.Append(ilHlp.Create(OpCodes.Ldelem_Ref));
                var vCurDel = new VariableDefinition(delegateTypeRef);
                clearHelper.Body.Variables.Add(vCurDel);
                ilHlp.Append(ilHlp.Create(OpCodes.Stloc, vCurDel));

                var hlpNextElem = ilHlp.Create(OpCodes.Nop);
                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc, vCurDel));
                ilHlp.Append(ilHlp.Create(OpCodes.Brfalse_S, hlpNextElem));

                var doRemLabel = ilHlp.Create(OpCodes.Nop);
                ilHlp.Append(ilHlp.Create(OpCodes.Ldarg_0));
                ilHlp.Append(ilHlp.Create(OpCodes.Brfalse_S, doRemLabel));

                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc, vCurDel));
                ilHlp.Append(ilHlp.Create(OpCodes.Callvirt, delegateGetMethod));
                ilHlp.Append(ilHlp.Create(OpCodes.Dup));
                ilHlp.Append(ilHlp.Create(OpCodes.Brfalse_S, hlpNextElem));
                ilHlp.Append(ilHlp.Create(OpCodes.Callvirt, memberGetDeclaringType));
                ilHlp.Append(ilHlp.Create(OpCodes.Dup));
                ilHlp.Append(ilHlp.Create(OpCodes.Brfalse_S, hlpNextElem));
                ilHlp.Append(ilHlp.Create(OpCodes.Callvirt, typeGetAssembly));
                ilHlp.Append(ilHlp.Create(OpCodes.Ldarg_0));
                ilHlp.Append(ilHlp.Create(OpCodes.Bne_Un_S, hlpNextElem));

                ilHlp.Append(doRemLabel);
                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc, vCurDel));
                ilHlp.Append(ilHlp.Create(OpCodes.Call, removeMethod));

                ilHlp.Append(hlpNextElem);
                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc_1));
                ilHlp.Append(ilHlp.Create(OpCodes.Ldc_I4_1));
                ilHlp.Append(ilHlp.Create(OpCodes.Add));
                ilHlp.Append(ilHlp.Create(OpCodes.Stloc_1));

                ilHlp.Append(hlpCheck);
                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc_1));
                ilHlp.Append(ilHlp.Create(OpCodes.Ldloc_0));
                ilHlp.Append(ilHlp.Create(OpCodes.Ldlen));
                ilHlp.Append(ilHlp.Create(OpCodes.Conv_I4));
                ilHlp.Append(ilHlp.Create(OpCodes.Blt_S, hlpLoop));

                ilHlp.Append(hlpRet);

                ilClear.Append(ilClear.Create(OpCodes.Ldarg_0));
                ilClear.Append(ilClear.Create(OpCodes.Call, clearHelper));

                Console.WriteLine($"Patched TerrariaHooks atomic multi-handler chain for {spec.HookTypeName}::{spec.EventName}");
            }

            ilClear.Append(ilClear.Create(OpCodes.Ret));

            var clearAllMethod = new MethodDefinition("ClearAll",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                hooksAsm.MainModule.TypeSystem.Void);
            registryType.Methods.Add(clearAllMethod);
            var ilClearAll = clearAllMethod.Body.GetILProcessor();
            ilClearAll.Append(ilClearAll.Create(OpCodes.Ldnull));
            ilClearAll.Append(ilClearAll.Create(OpCodes.Call, clearModHooksMethod));
            ilClearAll.Append(ilClearAll.Create(OpCodes.Ret));
            Console.WriteLine("Added TerrariaHooks.LoweredHookRegistry with multi-handler ClearModHooks and ClearAll");
        }

        SetContentDerivedMvid(hooksAsm, terrariaHooksOut);

        Console.WriteLine($"\n=== Patching tModLoader ===");
        var resolverTml = new DefaultAssemblyResolver();
        resolverTml.AddSearchDirectory(Path.GetDirectoryName(tmlIn));
        resolverTml.AddSearchDirectory(Path.GetDirectoryName(terrariaHooksOut));
        resolverTml.AddSearchDirectory(CacheRoot.Path + "/tmod/release");
        resolverTml.AddSearchDirectory(CacheRoot.Path + "/tmod/release/Libraries");
        resolverTml.AddSearchDirectory(CacheRoot.Path + "/tmod/flat_libs");
        resolverTml.AddSearchDirectory(CacheRoot.Path + "/tmod/mod-trials/fargo/romfs");
        var tmlAsm = AssemblyDefinition.ReadAssembly(tmlIn, new ReaderParameters { AssemblyResolver = resolverTml, ReadWrite = false });

        var patchedHooksAsm = AssemblyDefinition.ReadAssembly(terrariaHooksOut);

        foreach (var spec in activeHooks)
        {
            var targetType = tmlAsm.MainModule.GetType(spec.TargetTypeName)
                ?? throw new InvalidOperationException($"Type {spec.TargetTypeName} not found in tModLoader!");

            var hookTypeDef = patchedHooksAsm.MainModule.GetType(spec.HookTypeName)!;
            string origName = "orig_" + spec.EventName;
            var origDelegateTypeDef = hookTypeDef.NestedTypes.FirstOrDefault(t => t.Name == origName)
                ?? throw new InvalidOperationException($"Delegate {origName} not found in {spec.HookTypeName}!");
            var origInvokeMethod = origDelegateTypeDef.Methods.First(m => m.Name == "Invoke");

            int expectedParamOffset = spec.IsStatic ? 0 : 1;
            int expectedParamCount = origInvokeMethod.Parameters.Count - expectedParamOffset;

            var candidates = targetType.Methods.Where(m =>
                (spec.IsCtor ? m.IsConstructor : m.Name == spec.MethodName) &&
                m.IsStatic == spec.IsStatic &&
                TypeMatches(m.ReturnType, origInvokeMethod.ReturnType) &&
                m.Parameters.Count == expectedParamCount
            ).ToList();

            MethodDefinition matchedMethod = null;
            foreach (var cand in candidates)
            {
                bool match = true;
                for (int i = 0; i < expectedParamCount; i++)
                {
                    var origP = origInvokeMethod.Parameters[i + expectedParamOffset];
                    var candP = cand.Parameters[i];
                    if (!TypeMatches(origP.ParameterType, candP.ParameterType))
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    if (matchedMethod != null)
                    {
                        if (spec.TargetFullName != null)
                        {
                            if (cand.FullName == spec.TargetFullName)
                            {
                                matchedMethod = cand;
                                break;
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"Ambiguous target methods matching signature for {spec.TargetTypeName}::{spec.MethodName}!");
                        }
                    }
                    else
                    {
                        matchedMethod = cand;
                    }
                }
            }

            if (matchedMethod == null)
                throw new InvalidOperationException($"Zero target methods matching full signature for {spec.TargetTypeName}::{spec.MethodName}!");

            Console.WriteLine($"Matched unique target: {matchedMethod.FullName} for event {spec.EventName}");

            CheckUnsupportedTarget(matchedMethod, matchedMethod.FullName);

            MethodReference baseCtorCallRef = null;
            if (spec.IsCtor)
            {
                var baseCtorInst = matchedMethod.Body.Instructions.FirstOrDefault(i =>
                    i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == ".ctor"
                );
                if (baseCtorInst != null)
                {
                    baseCtorCallRef = (MethodReference)baseCtorInst.Operand;
                }
            }

            string origCloneName = "__orig_" + spec.EventName;
            var clonedOrig = CloneMethodBody(matchedMethod, origCloneName, targetType);

            MethodReference origCallableTarget;
            if (!spec.IsStatic)
            {
                var staticBridge = new MethodDefinition(
                    origCloneName + "_Static",
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                    matchedMethod.ReturnType
                );

                staticBridge.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, targetType));
                foreach (var p in matchedMethod.Parameters)
                {
                    var newP = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
                    if (p.HasConstant) newP.Constant = p.Constant;
                    staticBridge.Parameters.Add(newP);
                }

                var ilBridge = staticBridge.Body.GetILProcessor();
                ilBridge.Append(ilBridge.Create(OpCodes.Ldarg_0));
                for (int i = 0; i < matchedMethod.Parameters.Count; i++)
                {
                    ilBridge.Append(LdargHelper(ilBridge, i + 1, staticBridge.Parameters[i + 1]));
                }
                ilBridge.Append(ilBridge.Create(OpCodes.Call, clonedOrig));
                ilBridge.Append(ilBridge.Create(OpCodes.Ret));

                targetType.Methods.Add(staticBridge);
                origCallableTarget = staticBridge;
            }
            else
            {
                origCallableTarget = clonedOrig;
            }

            var origDelegateTypeRef = tmlAsm.MainModule.ImportReference(origDelegateTypeDef);
            string cachedFieldName = "_orig_" + spec.EventName + "_Delegate";
            var cachedOrigField = new FieldDefinition(
                cachedFieldName,
                FieldAttributes.Private | FieldAttributes.Static,
                origDelegateTypeRef
            );
            targetType.Fields.Add(cachedOrigField);

            var origCtorDef = origDelegateTypeDef.Methods.First(m => m.Name == ".ctor");
            var origCtorRef = tmlAsm.MainModule.ImportReference(origCtorDef);

            var hookDelegateTypeDef = hookTypeDef.NestedTypes.First(t => t.Name == "hook_" + spec.EventName);
            var hookInvokeDef = hookDelegateTypeDef.Methods.First(m => m.Name == "Invoke");
            var hookInvokeRef = tmlAsm.MainModule.ImportReference(hookInvokeDef);

            if (legacyMode)
            {
                var hookFieldDef = hookTypeDef.Fields.First(f => f.Name == "Hook_" + spec.EventName);
                var hookFieldRef = tmlAsm.MainModule.ImportReference(hookFieldDef);

                matchedMethod.IsPrivate = false;
                matchedMethod.IsPublic = true;
                matchedMethod.Body.Instructions.Clear();
                matchedMethod.Body.Variables.Clear();
                matchedMethod.Body.ExceptionHandlers.Clear();
                matchedMethod.Body.InitLocals = true;

                var il = matchedMethod.Body.GetILProcessor();
                var noHookLabel = il.Create(OpCodes.Nop);
                var hasCachedOrigLabel = il.Create(OpCodes.Nop);

                il.Append(il.Create(OpCodes.Ldsfld, hookFieldRef));
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Brfalse_S, noHookLabel));

                il.Append(il.Create(OpCodes.Ldsfld, cachedOrigField));
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Brtrue_S, hasCachedOrigLabel));

                il.Append(il.Create(OpCodes.Pop));
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ldftn, origCallableTarget));
                il.Append(il.Create(OpCodes.Newobj, origCtorRef));
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Stsfld, cachedOrigField));

                il.Append(hasCachedOrigLabel);

                if (!spec.IsStatic)
                {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                }

                for (int i = 0; i < matchedMethod.Parameters.Count; i++)
                {
                    il.Append(LdargHelper(il, i + (spec.IsStatic ? 0 : 1), matchedMethod.Parameters[i]));
                }

                il.Append(il.Create(OpCodes.Callvirt, hookInvokeRef));
                il.Append(il.Create(OpCodes.Ret));

                il.Append(noHookLabel);
                il.Append(il.Create(OpCodes.Pop));

                if (!spec.IsStatic)
                {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                }

                for (int i = 0; i < matchedMethod.Parameters.Count; i++)
                {
                    il.Append(LdargHelper(il, i + (spec.IsStatic ? 0 : 1), matchedMethod.Parameters[i]));
                }

                il.Append(il.Create(OpCodes.Call, clonedOrig));
                il.Append(il.Create(OpCodes.Ret));
            }
            else
            {
                var arrayOfOrigType = new ArrayType(origDelegateTypeRef);
                string chainLinksFieldName = "_chain_links_" + spec.EventName;
                var chainLinksField = new FieldDefinition(
                    chainLinksFieldName,
                    FieldAttributes.Private | FieldAttributes.Static,
                    arrayOfOrigType
                );
                targetType.Fields.Add(chainLinksField);

                var linkMethod = new MethodDefinition(
                    "__link_" + spec.EventName,
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                    matchedMethod.ReturnType
                );

                linkMethod.Parameters.Add(new ParameterDefinition("boxedIndex", ParameterAttributes.None, tmlAsm.MainModule.TypeSystem.Object));
                if (!spec.IsStatic)
                {
                    linkMethod.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, targetType));
                }
                foreach (var p in matchedMethod.Parameters)
                {
                    var newP = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
                    if (p.HasConstant) newP.Constant = p.Constant;
                    linkMethod.Parameters.Add(newP);
                }

                var ilLnk = linkMethod.Body.GetILProcessor();
                linkMethod.Body.InitLocals = true;

                var hookArrayFieldDef = hookTypeDef.Fields.First(f => f.Name == "Hooks_" + spec.EventName);
                var hookArrayFieldRef = tmlAsm.MainModule.ImportReference(hookArrayFieldDef);

                var lnkBaseOrigLabel = ilLnk.Create(OpCodes.Nop);
                var lnkInvokeLabel = ilLnk.Create(OpCodes.Nop);
                var vLnkIdx = new VariableDefinition(tmlAsm.MainModule.TypeSystem.Int32);
                linkMethod.Body.Variables.Add(vLnkIdx);
                ilLnk.Append(ilLnk.Create(OpCodes.Ldarg_0));
                ilLnk.Append(ilLnk.Create(OpCodes.Unbox_Any, tmlAsm.MainModule.TypeSystem.Int32));
                ilLnk.Append(ilLnk.Create(OpCodes.Stloc, vLnkIdx));

                ilLnk.Append(ilLnk.Create(OpCodes.Ldsfld, hookArrayFieldRef));
                ilLnk.Append(ilLnk.Create(OpCodes.Ldloc, vLnkIdx));
                ilLnk.Append(ilLnk.Create(OpCodes.Ldelem_Ref));

                ilLnk.Append(ilLnk.Create(OpCodes.Ldloc, vLnkIdx));
                ilLnk.Append(ilLnk.Create(OpCodes.Brfalse_S, lnkBaseOrigLabel));

                ilLnk.Append(ilLnk.Create(OpCodes.Ldsfld, chainLinksField));
                ilLnk.Append(ilLnk.Create(OpCodes.Ldloc, vLnkIdx));
                ilLnk.Append(ilLnk.Create(OpCodes.Ldc_I4_1));
                ilLnk.Append(ilLnk.Create(OpCodes.Sub));
                ilLnk.Append(ilLnk.Create(OpCodes.Ldelem_Ref));
                ilLnk.Append(ilLnk.Create(OpCodes.Br_S, lnkInvokeLabel));

                ilLnk.Append(lnkBaseOrigLabel);
                ilLnk.Append(ilLnk.Create(OpCodes.Ldsfld, cachedOrigField));

                ilLnk.Append(lnkInvokeLabel);
                int lnkParamOffset = 1;
                if (!spec.IsStatic)
                {
                    ilLnk.Append(ilLnk.Create(OpCodes.Ldarg_1));
                    lnkParamOffset = 2;
                }

                for (int i = 0; i < matchedMethod.Parameters.Count; i++)
                {
                    ilLnk.Append(LdargHelper(ilLnk, i + lnkParamOffset, linkMethod.Parameters[i + lnkParamOffset]));
                }

                ilLnk.Append(ilLnk.Create(OpCodes.Callvirt, hookInvokeRef));
                ilLnk.Append(ilLnk.Create(OpCodes.Ret));

                targetType.Methods.Add(linkMethod);

                var ensureChainMethod = new MethodDefinition(
                    "_ensure_chain_" + spec.EventName,
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                    origDelegateTypeRef
                );
                ensureChainMethod.Parameters.Add(new ParameterDefinition("outerIndex", ParameterAttributes.None, tmlAsm.MainModule.TypeSystem.Int32));
                targetType.Methods.Add(ensureChainMethod);

                var ilEns = ensureChainMethod.Body.GetILProcessor();
                ensureChainMethod.Body.InitLocals = true;

                var ensRetCached = ilEns.Create(OpCodes.Nop);
                var ensDoneLabel = ilEns.Create(OpCodes.Nop);
                ilEns.Append(ilEns.Create(OpCodes.Ldarg_0));
                ilEns.Append(ilEns.Create(OpCodes.Ldc_I4_0));
                ilEns.Append(ilEns.Create(OpCodes.Blt, ensRetCached));

                var ensAllocLabel = ilEns.Create(OpCodes.Nop);
                ilEns.Append(ilEns.Create(OpCodes.Ldsfld, chainLinksField));
                ilEns.Append(ilEns.Create(OpCodes.Dup));
                ilEns.Append(ilEns.Create(OpCodes.Brfalse_S, ensAllocLabel));
                ilEns.Append(ilEns.Create(OpCodes.Dup));
                ilEns.Append(ilEns.Create(OpCodes.Ldlen));
                ilEns.Append(ilEns.Create(OpCodes.Conv_I4));
                ilEns.Append(ilEns.Create(OpCodes.Ldarg_0));
                ilEns.Append(ilEns.Create(OpCodes.Ble_S, ensAllocLabel));
                ilEns.Append(ilEns.Create(OpCodes.Ldarg_0));
                ilEns.Append(ilEns.Create(OpCodes.Ldelem_Ref));
                ilEns.Append(ilEns.Create(OpCodes.Dup));
                ilEns.Append(ilEns.Create(OpCodes.Brtrue, ensDoneLabel));
                ilEns.Append(ilEns.Create(OpCodes.Pop));

                ilEns.Append(ensAllocLabel);
                ilEns.Append(ilEns.Create(OpCodes.Pop));

                ilEns.Append(ilEns.Create(OpCodes.Ldarg_0));
                ilEns.Append(ilEns.Create(OpCodes.Ldc_I4_1));
                ilEns.Append(ilEns.Create(OpCodes.Add));
                ilEns.Append(ilEns.Create(OpCodes.Newarr, origDelegateTypeRef));
                var vNewArrEns = new VariableDefinition(arrayOfOrigType);
                ensureChainMethod.Body.Variables.Add(vNewArrEns);
                ilEns.Append(ilEns.Create(OpCodes.Stloc, vNewArrEns));

                var ensNoOldCopy = ilEns.Create(OpCodes.Nop);
                ilEns.Append(ilEns.Create(OpCodes.Ldsfld, chainLinksField));
                ilEns.Append(ilEns.Create(OpCodes.Dup));
                ilEns.Append(ilEns.Create(OpCodes.Brfalse_S, ensNoOldCopy));
                ilEns.Append(ilEns.Create(OpCodes.Ldc_I4_0));
                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vNewArrEns));
                ilEns.Append(ilEns.Create(OpCodes.Ldc_I4_0));
                ilEns.Append(ilEns.Create(OpCodes.Ldsfld, chainLinksField));
                ilEns.Append(ilEns.Create(OpCodes.Ldlen));
                ilEns.Append(ilEns.Create(OpCodes.Conv_I4));
                var arrayCopyMethod = tmlAsm.MainModule.ImportReference(
                    typeof(Array).GetMethod("Copy", new[] { typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int) })
                );
                ilEns.Append(ilEns.Create(OpCodes.Call, arrayCopyMethod));
                var ensOldCopyDone = ilEns.Create(OpCodes.Nop);
                ilEns.Append(ilEns.Create(OpCodes.Br_S, ensOldCopyDone));

                ilEns.Append(ensNoOldCopy);
                ilEns.Append(ilEns.Create(OpCodes.Pop));
                ilEns.Append(ensOldCopyDone);

                var vIEns = new VariableDefinition(tmlAsm.MainModule.TypeSystem.Int32);
                ensureChainMethod.Body.Variables.Add(vIEns);
                ilEns.Append(ilEns.Create(OpCodes.Ldc_I4_0));
                ilEns.Append(ilEns.Create(OpCodes.Stloc, vIEns));

                var ensLoopStart = ilEns.Create(OpCodes.Nop);
                var ensLoopCheck = ilEns.Create(OpCodes.Nop);
                ilEns.Append(ilEns.Create(OpCodes.Br_S, ensLoopCheck));

                ilEns.Append(ensLoopStart);
                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vNewArrEns));
                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vIEns));
                ilEns.Append(ilEns.Create(OpCodes.Ldelem_Ref));
                var ensNextI = ilEns.Create(OpCodes.Nop);
                ilEns.Append(ilEns.Create(OpCodes.Brtrue_S, ensNextI));

                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vNewArrEns));
                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vIEns));
                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vIEns));
                ilEns.Append(ilEns.Create(OpCodes.Box, tmlAsm.MainModule.TypeSystem.Int32));
                ilEns.Append(ilEns.Create(OpCodes.Ldftn, linkMethod));
                ilEns.Append(ilEns.Create(OpCodes.Newobj, origCtorRef));
                ilEns.Append(ilEns.Create(OpCodes.Stelem_Ref));

                ilEns.Append(ensNextI);
                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vIEns));
                ilEns.Append(ilEns.Create(OpCodes.Ldc_I4_1));
                ilEns.Append(ilEns.Create(OpCodes.Add));
                ilEns.Append(ilEns.Create(OpCodes.Stloc, vIEns));

                ilEns.Append(ensLoopCheck);
                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vIEns));
                ilEns.Append(ilEns.Create(OpCodes.Ldarg_0));
                ilEns.Append(ilEns.Create(OpCodes.Ldc_I4_1));
                ilEns.Append(ilEns.Create(OpCodes.Add));
                ilEns.Append(ilEns.Create(OpCodes.Blt_S, ensLoopStart));

                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vNewArrEns));
                ilEns.Append(ilEns.Create(OpCodes.Stsfld, chainLinksField));

                ilEns.Append(ilEns.Create(OpCodes.Ldloc, vNewArrEns));
                ilEns.Append(ilEns.Create(OpCodes.Ldarg_0));
                ilEns.Append(ilEns.Create(OpCodes.Ldelem_Ref));
                ilEns.Append(ilEns.Create(OpCodes.Ret));

                ilEns.Append(ensRetCached);
                ilEns.Append(ilEns.Create(OpCodes.Ldsfld, cachedOrigField));
                ilEns.Append(ilEns.Create(OpCodes.Ret));

                ilEns.Append(ensDoneLabel);
                ilEns.Append(ilEns.Create(OpCodes.Ret));

                // The canonical method keeps its declared accessibility: mods reflect on it with
                // exact BindingFlags (e.g. Souls: Player.PickAmmo is internal -> NonPublic).
                matchedMethod.Body.Instructions.Clear();
                matchedMethod.Body.Variables.Clear();
                matchedMethod.Body.ExceptionHandlers.Clear();
                matchedMethod.Body.InitLocals = true;

                var il = matchedMethod.Body.GetILProcessor();
                if (spec.IsCtor && baseCtorCallRef != null)
                {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Call, baseCtorCallRef));
                }

                var vSnap = new VariableDefinition(arrayOfOrigType);
                matchedMethod.Body.Variables.Add(vSnap);

                var noHooksLabel = il.Create(OpCodes.Nop);
                var hasCachedOrigLabel = il.Create(OpCodes.Nop);

                il.Append(il.Create(OpCodes.Ldsfld, cachedOrigField));
                il.Append(il.Create(OpCodes.Brtrue_S, hasCachedOrigLabel));
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ldftn, origCallableTarget));
                il.Append(il.Create(OpCodes.Newobj, origCtorRef));
                il.Append(il.Create(OpCodes.Stsfld, cachedOrigField));

                il.Append(hasCachedOrigLabel);
                il.Append(il.Create(OpCodes.Ldsfld, hookArrayFieldRef));
                il.Append(il.Create(OpCodes.Stloc, vSnap));

                il.Append(il.Create(OpCodes.Ldloc, vSnap));
                il.Append(il.Create(OpCodes.Brfalse, noHooksLabel));
                il.Append(il.Create(OpCodes.Ldloc, vSnap));
                il.Append(il.Create(OpCodes.Ldlen));
                il.Append(il.Create(OpCodes.Conv_I4));
                il.Append(il.Create(OpCodes.Brfalse, noHooksLabel));

                il.Append(il.Create(OpCodes.Ldloc, vSnap));
                il.Append(il.Create(OpCodes.Ldloc, vSnap));
                il.Append(il.Create(OpCodes.Ldlen));
                il.Append(il.Create(OpCodes.Conv_I4));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Sub));
                il.Append(il.Create(OpCodes.Ldelem_Ref));

                il.Append(il.Create(OpCodes.Ldloc, vSnap));
                il.Append(il.Create(OpCodes.Ldlen));
                il.Append(il.Create(OpCodes.Conv_I4));
                il.Append(il.Create(OpCodes.Ldc_I4_2));
                il.Append(il.Create(OpCodes.Sub));
                il.Append(il.Create(OpCodes.Call, ensureChainMethod));

                if (!spec.IsStatic)
                {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                }

                for (int i = 0; i < matchedMethod.Parameters.Count; i++)
                {
                    il.Append(LdargHelper(il, i + (spec.IsStatic ? 0 : 1), matchedMethod.Parameters[i]));
                }

                il.Append(il.Create(OpCodes.Callvirt, hookInvokeRef));
                il.Append(il.Create(OpCodes.Ret));

                il.Append(noHooksLabel);
                if (!spec.IsStatic)
                {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                }

                for (int i = 0; i < matchedMethod.Parameters.Count; i++)
                {
                    il.Append(LdargHelper(il, i + (spec.IsStatic ? 0 : 1), matchedMethod.Parameters[i]));
                }

                il.Append(il.Create(OpCodes.Call, clonedOrig));
                il.Append(il.Create(OpCodes.Ret));
            }

            Console.WriteLine($"Rewrote canonical wrapper for {spec.TargetTypeName}::{spec.EventName}");
        }
        // MonoModHooks.Add keeps its stock body: new Hook(...) binds to the lowered chain.
        var mmhType = tmlAsm.MainModule.GetType("Terraria.ModLoader.MonoModHooks");
        if (mmhType != null)
        {
            var registryTypeDef = patchedHooksAsm.MainModule.GetType("TerrariaHooks.LoweredHookRegistry")!;
            var clearModHooksDef = registryTypeDef.Methods.First(m => m.Name == "ClearModHooks");
            var clearModHooksRef = tmlAsm.MainModule.ImportReference(clearModHooksDef);

            var clearAllDef = registryTypeDef.Methods.First(m => m.Name == "ClearAll");
            var clearAllRef = tmlAsm.MainModule.ImportReference(clearAllDef);

            var removeAllMethod = mmhType.Methods.FirstOrDefault(m => m.Name == "RemoveAll");
            if (removeAllMethod != null && removeAllMethod.HasBody)
            {
                for (int i = 0; i < removeAllMethod.Body.Instructions.Count; i++)
                {
                    var instr = removeAllMethod.Body.Instructions[i];
                    if (instr.OpCode == OpCodes.Stloc_1)
                    {
                        var ilRem = removeAllMethod.Body.GetILProcessor();
                        ilRem.InsertAfter(instr, ilRem.Create(OpCodes.Call, clearModHooksRef));
                        ilRem.InsertAfter(instr, ilRem.Create(OpCodes.Ldloc_1));
                        Console.WriteLine("Injected LoweredHookRegistry.ClearModHooks into MonoModHooks.RemoveAll");
                        break;
                    }
                }
            }

            var clearMethod = mmhType.Methods.FirstOrDefault(m => m.Name == "Clear");
            if (clearMethod != null && clearMethod.HasBody)
            {
                var ilClr = clearMethod.Body.GetILProcessor();
                ilClr.InsertBefore(clearMethod.Body.Instructions[0], ilClr.Create(OpCodes.Call, clearAllRef));
                Console.WriteLine("Injected LoweredHookRegistry.ClearAll into MonoModHooks.Clear");
            }
        }
        SetContentDerivedMvid(tmlAsm, tmlOut);

        // Also patch MonoMod.RuntimeDetour.dll global guard if target directory specified
        string targetDir = Path.GetDirectoryName(tmlOut)!;
        string defaultMmIn = CacheRoot.Path + "/tmod/release/Libraries/monomod.runtimedetour/25.3.2/lib/net8.0/MonoMod.RuntimeDetour.dll";
        string targetMmOut = Path.Combine(targetDir, "MonoMod.RuntimeDetour.dll");
        string mmIn = detourInPath ?? (File.Exists(targetMmOut) && new FileInfo(targetMmOut).Length > 0 ? targetMmOut : defaultMmIn);
        if (!legacyMode)
        {
            PatchMonoModRuntimeDetour(mmIn, targetMmOut);
        }
    }
}
