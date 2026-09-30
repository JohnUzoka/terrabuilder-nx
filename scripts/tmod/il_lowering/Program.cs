using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlLowering;

public class ModLoadContext : AssemblyLoadContext
{
    private readonly string[] _searchPaths;

    public ModLoadContext(string[] searchPaths) : base(isCollectible: true)
    {
        _searchPaths = searchPaths;
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name.StartsWith("MonoMod.") ||
            assemblyName.Name.StartsWith("Mono.Cecil") ||
            assemblyName.Name.StartsWith("System.") ||
            assemblyName.Name == "System" ||
            assemblyName.Name == "mscorlib" ||
            assemblyName.Name == "netstandard")
        {
            return null; // Delegate to default ALC
        }

        foreach (var dir in _searchPaths)
        {
            string candidate = Path.Combine(dir, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                return LoadFromAssemblyPath(candidate);
            }
        }
        return null;
    }
}

public class SlotBindingRule
{
    public int SlotId { get; set; }
    public string SlotName { get; set; }
    public string Kind { get; set; } // "StaticMethod"
    public string AssemblyName { get; set; }
    public string DeclaringType { get; set; }
    public string MethodName { get; set; }
    public string Signature { get; set; }
    public string DelegateTypeName { get; set; }
}

public class LoweredEditSpec
{
    public string TargetAssembly { get; set; }
    public string TargetType { get; set; }
    public string TargetMethod { get; set; }
    public string TargetFullName { get; set; }
    public string ManipulatorDeclaringType { get; set; }
    public string ManipulatorMethodName { get; set; }
    public string ManipulatorFullName { get; set; }
    public string ModName { get; set; }
    public string ModMvid { get; set; }
    public string Mechanism { get; set; }
    public List<SlotBindingRule> SlotRules { get; set; } = new();
}

public class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("  Generic Data-Driven Build-Time IL Hook Lowering Pass");
        Console.WriteLine("=================================================================");

        string inventoryPath = GetArg(args, "--inventory", "/home/juzoka/.cache/terraria-switch-build/tmod/auto-hooks/scan/souls-set/hook-inventory.json");
        string tmlIn = GetArg(args, "--tml-in", "/home/juzoka/.cache/terraria-switch-build/tmod/save-exit/crypto/tModLoader_patched_nxfix.dll");
        string hooksIn = GetArg(args, "--hooks-in", "/home/juzoka/.cache/terraria-switch-build/tmod/tmod09/romfs/TerrariaHooks.dll");
        string detourIn = GetArg(args, "--detour-in", "/home/juzoka/.cache/terraria-switch-build/tmod/release/Libraries/monomod.runtimedetour/25.3.2/lib/net8.0/MonoMod.RuntimeDetour.dll");
        string outDir = GetArg(args, "--out-dir", "/home/juzoka/.cache/terraria-switch-build/tmod/auto-hooks/il-lowering2");

        var modsDirs = new List<string>
        {
            "/home/juzoka/.cache/terraria-switch-build/tmod/mod-trials/souls/extracted",
            "/home/juzoka/.cache/terraria-switch-build/tmod/mod-trials/fargo/extracted"
        };
        string extraModsDir = GetArg(args, "--mods-dir", null);
        if (!string.IsNullOrEmpty(extraModsDir) && !modsDirs.Contains(extraModsDir))
        {
            modsDirs.Insert(0, extraModsDir);
        }

        Directory.CreateDirectory(outDir);
        string tmlOut = Path.Combine(outDir, "tModLoader.dll");
        string hooksOut = Path.Combine(outDir, "TerrariaHooks.dll");
        string detourOut = Path.Combine(outDir, "MonoMod.RuntimeDetour.dll");

        Console.WriteLine($"Inventory: {inventoryPath}");
        Console.WriteLine($"Input tML: {tmlIn}");
        Console.WriteLine($"Input Hooks: {hooksIn}");
        Console.WriteLine($"Input Detour: {detourIn}");
        Console.WriteLine($"Output dir: {outDir}");

        // 1. Read inventory
        using var jsonDoc = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        var root = jsonDoc.RootElement;
        var ilHooksEl = root.GetProperty("il_hooks");
        Console.WriteLine($"Loaded inventory: {ilHooksEl.GetArrayLength()} IL hook target entries.");

        // Map mod names to their DLL paths and MVIDs from inventory
        var modInfoMap = new Dictionary<string, (string DllPath, string Mvid)>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("mods", out var modsArray))
        {
            foreach (var modEl in modsArray.EnumerateArray())
            {
                string name = modEl.GetProperty("name").GetString();
                string dll = modEl.GetProperty("dll").GetString();
                string mvid = modEl.GetProperty("mvid").GetString();
                modInfoMap[name] = (dll, mvid);
            }
        }

        // Build search directories for ALC and Cecil
        var searchPaths = new List<string>(modsDirs)
        {
            Path.GetDirectoryName(tmlIn),
            Path.GetDirectoryName(hooksIn),
            Path.GetDirectoryName(detourIn),
            "/home/juzoka/.cache/terraria-switch-build/tmod/release",
            "/home/juzoka/.cache/terraria-switch-build/tmod/release/Libraries/monomod.runtimedetour/25.3.2/lib/net8.0",
            "/home/juzoka/.cache/terraria-switch-build/tmod/release/Libraries/monomod.utils/25.0.10/lib/net8.0",
            "/home/juzoka/.cache/terraria-switch-build/tmod/release/Libraries/monomod.core/1.3.2/lib/net8.0",
            "/home/juzoka/.cache/terraria-switch-build/tmod/release/Libraries/mono.cecil/0.11.6/lib/netstandard2.0"
        };

        // 2. Setup ALC for mod loading
        var alc = new ModLoadContext(searchPaths.ToArray());

        // Setup Cecil resolver
        var resolver = new DefaultAssemblyResolver();
        foreach (var p in searchPaths)
        {
            if (Directory.Exists(p)) resolver.AddSearchDirectory(p);
        }

        var readerParams = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
        var tmlAsm = AssemblyDefinition.ReadAssembly(tmlIn, readerParams);
        var hooksAsm = AssemblyDefinition.ReadAssembly(hooksIn, readerParams);
        var detourAsm = AssemblyDefinition.ReadAssembly(detourIn, readerParams);

        // 3. Process each IL hook generically
        var loweredSpecs = new List<LoweredEditSpec>();
        int totalSlotCounter = 0;

        foreach (var hookEl in ilHooksEl.EnumerateArray())
        {
            var targetEl = hookEl.GetProperty("target");
            string targetAsmName = targetEl.GetProperty("assembly").GetString();
            string targetTypeName = targetEl.GetProperty("type").GetString();
            string targetMethodName = targetEl.GetProperty("method").GetString();
            string targetFullName = targetEl.GetProperty("full_name").GetString();
            bool targetIsStatic = targetEl.GetProperty("is_static").GetBoolean();

            string manipFullName = hookEl.GetProperty("manipulator").GetString();
            string mechanism = hookEl.GetProperty("mechanism").GetString();

            // Find mod name from registrations
            string modName = null;
            if (hookEl.TryGetProperty("registrations", out var regsEl))
            {
                foreach (var reg in regsEl.EnumerateArray())
                {
                    if (reg.TryGetProperty("mod", out var mProp))
                    {
                        modName = mProp.GetString();
                        break;
                    }
                }
            }

            Console.WriteLine($"\n[IL Hook Entry] Target: {targetFullName}, Manipulator: {manipFullName}, Mod: {modName}");

            // Resolve target method in game assembly
            var targetType = tmlAsm.MainModule.GetType(targetTypeName)
                ?? throw new InvalidOperationException($"Target type {targetTypeName} not found in {tmlIn}!");

            var targetMethod = targetType.Methods.FirstOrDefault(m => m.FullName == targetFullName ||
                (m.Name == targetMethodName && m.IsStatic == targetIsStatic))
                ?? throw new InvalidOperationException($"Target method {targetFullName} not found in {targetTypeName}!");

            Console.WriteLine($"Found target MethodDefinition: {targetMethod.FullName} ({targetMethod.Body.Instructions.Count} instrs)");

            // Clone vanilla body: __il_vanilla_<MethodName>
            string vanillaCloneName = "__il_vanilla_" + targetMethodName;
            var vanillaClone = targetType.Methods.FirstOrDefault(m => m.Name == vanillaCloneName);
            if (vanillaClone == null)
            {
                vanillaClone = CloneMethodBody(targetMethod, vanillaCloneName, targetType);
                Console.WriteLine($"Created vanilla clone: {vanillaClone.FullName}");
            }

            // Clone edited body: __il_edited_<MethodName>
            string editedCloneName = "__il_edited_" + targetMethodName;
            var editedClone = targetType.Methods.FirstOrDefault(m => m.Name == editedCloneName);
            if (editedClone == null)
            {
                editedClone = CloneMethodBody(targetMethod, editedCloneName, targetType);
                Console.WriteLine($"Created edited clone: {editedClone.FullName}");
            }

            // Parse manipulator method declaration
            // e.g. "System.Void Luminance.Core.Graphics.ScreenShaderFixer::PerformEdit(MonoMod.Cil.ILContext,Luminance.Core.Hooking.ManagedILEdit)"
            ParseManipulatorName(manipFullName, out string manipDeclTypeName, out string manipMethodName);

            // Load mod assembly containing the manipulator
            Assembly modRuntimeAsm = null;
            if (!string.IsNullOrEmpty(modName) && modInfoMap.TryGetValue(modName, out var modInfo) && File.Exists(modInfo.DllPath))
            {
                modRuntimeAsm = alc.LoadFromAssemblyPath(modInfo.DllPath);
            }
            else
            {
                // Try resolving by mod name or scanning search paths
                foreach (var dir in modsDirs)
                {
                    if (!string.IsNullOrEmpty(modName))
                    {
                        string p = Path.Combine(dir, modName + ".dll");
                        if (File.Exists(p)) { modRuntimeAsm = alc.LoadFromAssemblyPath(p); break; }
                    }
                }
            }

            if (modRuntimeAsm == null)
            {
                // Search in loaded ALC assemblies
                foreach (var a in alc.Assemblies)
                {
                    if (a.GetType(manipDeclTypeName) != null) { modRuntimeAsm = a; break; }
                }
            }

            if (modRuntimeAsm == null)
            {
                // Try loading from search paths by type's namespace root
                string possibleModName = manipDeclTypeName.Split('.')[0];
                foreach (var dir in modsDirs)
                {
                    string p = Path.Combine(dir, possibleModName + ".dll");
                    if (File.Exists(p)) { modRuntimeAsm = alc.LoadFromAssemblyPath(p); break; }
                }
            }

            if (modRuntimeAsm == null)
            {
                throw new InvalidOperationException($"Could not load mod assembly for manipulator {manipFullName}!");
            }

            string modMvid = modRuntimeAsm.ManifestModule.ModuleVersionId.ToString();
            Console.WriteLine($"Loaded mod runtime assembly: {modRuntimeAsm.FullName} (MVID: {modMvid})");

            var manipType = modRuntimeAsm.GetType(manipDeclTypeName)
                ?? throw new InvalidOperationException($"Type {manipDeclTypeName} not found in {modRuntimeAsm.FullName}!");

            // Prepare spec
            var spec = new LoweredEditSpec
            {
                TargetAssembly = targetAsmName,
                TargetType = targetTypeName,
                TargetMethod = targetMethodName,
                TargetFullName = targetFullName,
                ManipulatorDeclaringType = manipDeclTypeName,
                ManipulatorMethodName = manipMethodName,
                ManipulatorFullName = manipFullName,
                ModName = modName ?? modRuntimeAsm.GetName().Name,
                ModMvid = modMvid,
                Mechanism = mechanism
            };

            // Invoke mod's manipulator on MonoMod ILContext over editedClone
            var ilContext = new ILContext(editedClone);
            var manipMethods = manipType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == manipMethodName).ToList();

            if (manipMethods.Count == 0)
                throw new InvalidOperationException($"Method {manipMethodName} not found in {manipType.FullName}!");

            MethodInfo selectedManipMethod = null;
            object targetInstance = null;
            object[] invokeParams = null;

            foreach (var m in manipMethods)
            {
                var parameters = m.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType.Name == "ILContext")
                {
                    selectedManipMethod = m;
                    targetInstance = m.IsStatic ? null : Activator.CreateInstance(manipType);
                    invokeParams = new object[] { ilContext };
                    break;
                }
                else if (parameters.Length == 2 && parameters[0].ParameterType.Name == "ILContext")
                {
                    selectedManipMethod = m;
                    targetInstance = m.IsStatic ? null : Activator.CreateInstance(manipType);

                    // For Luminance ManagedILEdit: construct real ManagedILEdit
                    var secondParamType = parameters[1].ParameterType;
                    if (secondParamType.FullName == "Luminance.Core.Hooking.ManagedILEdit")
                    {
                        var dummyEdit = Activator.CreateInstance(secondParamType, "LoweredEdit_" + targetMethodName, null, null, null, null);
                        invokeParams = new object[] { ilContext, dummyEdit };
                        break;
                    }
                    else
                    {
                        throw new NotSupportedException($"Unsupported 2-argument manipulator parameter type: {secondParamType.FullName}");
                    }
                }
            }

            if (selectedManipMethod == null)
                throw new InvalidOperationException($"No compatible manipulator signature found for {manipFullName}!");

            Console.WriteLine($"Invoking real manipulator: {selectedManipMethod.DeclaringType.FullName}::{selectedManipMethod.Name}");
            selectedManipMethod.Invoke(targetInstance, invokeParams);
            Console.WriteLine($"Manipulator invoked successfully. Edited body instructions: {editedClone.Body.Instructions.Count}");

            // Post-process the edited clone:
            // Every reference into a mod assembly or DynamicReferenceManager cell becomes a typed static delegate slot in TerrariaHooks.
            PostProcessEditedClone(editedClone, hooksAsm, spec, ref totalSlotCounter);

            loweredSpecs.Add(spec);

            // Build Two-Body Canonical Selector for targetMethod
            BuildTwoBodySelector(targetMethod, vanillaClone, editedClone, hooksAsm);
        }

        // Generate generic LoweredILRegistry in TerrariaHooks.dll
        Console.WriteLine("\n--- Generating generic LoweredILRegistry in TerrariaHooks ---");
        GenerateLoweredILRegistry(hooksAsm, loweredSpecs);

        // Set content-derived MVID and save TerrariaHooks.dll
        SetContentDerivedMvid(hooksAsm, hooksOut);

        // Set content-derived MVID and save tModLoader.dll
        SetContentDerivedMvid(tmlAsm, tmlOut);

        // Patch MonoMod.RuntimeDetour.dll: intercept ILHook
        Console.WriteLine("\n--- Patching MonoMod.RuntimeDetour.dll (ILHook interception) ---");
        var patchedHooksAsm = AssemblyDefinition.ReadAssembly(hooksOut, readerParams);
        PatchRuntimeDetour(detourAsm, patchedHooksAsm);
        SetContentDerivedMvid(detourAsm, detourOut);

        Console.WriteLine("\n=================================================================");
        Console.WriteLine("  IL Lowering Pass Finished Successfully!");
        Console.WriteLine($"  tModLoader: {tmlOut}");
        Console.WriteLine($"  TerrariaHooks: {hooksOut}");
        Console.WriteLine($"  MonoMod.RuntimeDetour: {detourOut}");
        Console.WriteLine("=================================================================");
        return 0;
    }

    private static string GetArg(string[] args, string name, string defaultVal)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return defaultVal;
    }

    private static void ParseManipulatorName(string fullName, out string typeName, out string methodName)
    {
        int openParen = fullName.IndexOf('(');
        string withoutSig = openParen > 0 ? fullName.Substring(0, openParen) : fullName;
        string clean = withoutSig.Trim();
        if (clean.StartsWith("System.Void ")) clean = clean.Substring("System.Void ".Length).Trim();
        else
        {
            int firstSpace = clean.IndexOf(' ');
            if (firstSpace > 0) clean = clean.Substring(firstSpace + 1).Trim();
        }

        int colonColon = clean.IndexOf("::");
        if (colonColon < 0) throw new ArgumentException($"Cannot parse manipulator name: {fullName}");
        typeName = clean.Substring(0, colonColon);
        methodName = clean.Substring(colonColon + 2);
    }

    private static bool IsModAssembly(IMetadataScope scope)
    {
        if (scope == null) return false;
        string name = scope.Name;
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);

        return string.Equals(name, "FargowiltasSouls", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Luminance", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "StructureHelper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Fargowiltas", StringComparison.OrdinalIgnoreCase);
    }

    private static void PostProcessEditedClone(
        MethodDefinition editedClone,
        AssemblyDefinition hooksAsm,
        LoweredEditSpec spec,
        ref int totalSlotCounter)
    {
        var ilProc = editedClone.Body.GetILProcessor();
        var instructions = editedClone.Body.Instructions;
        var tmlModule = editedClone.Module;

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];

            // Case 1: Call to a static method in a mod assembly (e.g. from EmitDelegate(CanFallthrough))
            if (instr.Operand is MethodReference mr && IsModAssembly(mr.DeclaringType.Scope))
            {
                if (instr.OpCode == OpCodes.Call)
                {
                    Console.WriteLine($"Found mod static method call: {mr.FullName} (Scope: {mr.DeclaringType.Scope.Name})");

                    string slotName = $"Slot_{spec.TargetMethod}_{totalSlotCounter++}";
                    var rule = new SlotBindingRule
                    {
                        SlotId = ruleSlotId(slotName),
                        SlotName = slotName,
                        Kind = "StaticMethod",
                        AssemblyName = mr.DeclaringType.Scope.Name.Replace(".dll", ""),
                        DeclaringType = mr.DeclaringType.FullName,
                        MethodName = mr.Name,
                        Signature = mr.FullName
                    };

                    string delTypeName = $"Delegate_{spec.TargetMethod}_{slotName}";
                    var delegateTypeDef = CreateDelegateTypeDefinition(hooksAsm.MainModule, mr, delTypeName);
                    rule.DelegateTypeName = delegateTypeDef.FullName;
                    spec.SlotRules.Add(rule);
                    TypeReference delegateTypeRef = delegateTypeDef;

                    // Add static field and invoker to TerrariaHooks.LoweredILSlots
                    var slotsType = GetOrCreateLoweredILSlotsType(hooksAsm);
                    var slotField = new FieldDefinition(slotName, FieldAttributes.Public | FieldAttributes.Static, delegateTypeRef);
                    slotsType.Fields.Add(slotField);

                    // Create invoker method on LoweredILSlots:
                    // public static <ReturnType> Invoke_<slotName>(<params...>) { ldsfld slotField; ldarg...; callvirt Invoke; ret; }
                    var invokerMethod = CreateSlotInvokerMethod(hooksAsm, slotField, mr);
                    slotsType.Methods.Add(invokerMethod);

                    // In editedClone, replace call mr with call LoweredILSlots::Invoke_<slotName>
                    var invokerRef = tmlModule.ImportReference(invokerMethod);
                    instr.Operand = invokerRef;
                    Console.WriteLine($"Redirected {mr.Name} to static invoker {invokerMethod.FullName} via {slotName}");
                }
                else
                {
                    throw new NotSupportedException($"Mod assembly reference in non-call opcode {instr.OpCode}: {mr.FullName}. Only static calls supported.");
                }
            }
            // Case 2: Call to MonoMod DynamicReferenceManager.GetValueTUnsafe (closure emission)
            else if (instr.Operand is MethodReference dmr &&
                     dmr.DeclaringType.FullName.Contains("DynamicReferenceManager"))
            {
                throw new NotSupportedException(
                    $"IL edit on {spec.TargetFullName} used a closure / dynamic reference cell ({dmr.FullName}). " +
                    "Closures are refused per fail-closed policy (only static method delegates supported).");
            }
            // Case 3: Other mod references (FieldReference, TypeReference, etc.)
            else if (instr.Operand is FieldReference fr && IsModAssembly(fr.DeclaringType.Scope))
            {
                throw new NotSupportedException(
                    $"IL edit on {spec.TargetFullName} references mod field {fr.FullName}. Fail closed.");
            }
            else if (instr.Operand is TypeReference tr && IsModAssembly(tr.Scope))
            {
                throw new NotSupportedException(
                    $"IL edit on {spec.TargetFullName} references mod type {tr.FullName}. Fail closed.");
            }
        }
    }

    private static int ruleSlotId(string slotName)
    {
        int lastUnder = slotName.LastIndexOf('_');
        return int.Parse(slotName.Substring(lastUnder + 1));
    }

    private static TypeDefinition GetOrCreateLoweredILSlotsType(AssemblyDefinition hooksAsm)
    {
        var slotsType = hooksAsm.MainModule.GetType("TerrariaHooks.LoweredILSlots");
        if (slotsType == null)
        {
            slotsType = new TypeDefinition("TerrariaHooks", "LoweredILSlots",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                hooksAsm.MainModule.TypeSystem.Object);
            hooksAsm.MainModule.Types.Add(slotsType);
        }
        return slotsType;
    }

    private static TypeDefinition CreateDelegateTypeDefinition(ModuleDefinition hooksModule, MethodReference mr, string delegateName)
    {
        var sysRuntime = hooksModule.AssemblyReferences.First(a => a.Name == "System.Runtime");
        var delType = new TypeDefinition("TerrariaHooks", delegateName,
            TypeAttributes.Public | TypeAttributes.Sealed,
            new TypeReference("System", "MulticastDelegate", hooksModule, sysRuntime));

        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            hooksModule.TypeSystem.Void)
        {
            ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime
        };
        ctor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, hooksModule.TypeSystem.Object));
        ctor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, new TypeReference("System", "IntPtr", hooksModule, sysRuntime)));
        delType.Methods.Add(ctor);

        var returnType = ImportTypeToHooks(hooksModule, mr.ReturnType);
        var invoke = new MethodDefinition("Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            returnType)
        {
            ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime
        };
        for (int p = 0; p < mr.Parameters.Count; p++)
        {
            var pDef = mr.Parameters[p];
            var pType = ImportTypeToHooks(hooksModule, pDef.ParameterType);
            invoke.Parameters.Add(new ParameterDefinition(pDef.Name, pDef.Attributes, pType));
        }
        delType.Methods.Add(invoke);

        hooksModule.Types.Add(delType);
        return delType;
    }

    private static TypeReference ImportTypeToHooks(ModuleDefinition hooksModule, TypeReference type)
    {
        if (type.IsPrimitive || type.FullName == "System.Void" || type.FullName == "System.String" || type.FullName == "System.Object")
        {
            return hooksModule.ImportReference(type);
        }

        // If it's a type in tModLoader (e.g. Terraria.Player)
        var tmlRef = hooksModule.AssemblyReferences.FirstOrDefault(a => a.Name == "tModLoader");
        if (tmlRef != null)
        {
            return new TypeReference(type.Namespace, type.Name, hooksModule, tmlRef);
        }
        return hooksModule.ImportReference(type);
    }

    private static MethodDefinition CreateSlotInvokerMethod(
        AssemblyDefinition hooksAsm,
        FieldDefinition slotField,
        MethodReference targetMethodRef)
    {
        var hooksModule = hooksAsm.MainModule;
        var returnType = ImportTypeToHooks(hooksModule, targetMethodRef.ReturnType);

        var invoker = new MethodDefinition("Invoke_" + slotField.Name,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            returnType);

        for (int p = 0; p < targetMethodRef.Parameters.Count; p++)
        {
            var pDef = targetMethodRef.Parameters[p];
            var pType = ImportTypeToHooks(hooksModule, pDef.ParameterType);
            invoker.Parameters.Add(new ParameterDefinition(pDef.Name, pDef.Attributes, pType));
        }

        var il = invoker.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldsfld, slotField));
        for (int p = 0; p < invoker.Parameters.Count; p++)
        {
            il.Append(LdargHelper(il, p, invoker.Parameters[p]));
        }

        // Callvirt on custom delegate's Invoke method
        var delType = (TypeDefinition)slotField.FieldType;
        var invokeDef = delType.Methods.First(m => m.Name == "Invoke");
        il.Append(il.Create(OpCodes.Callvirt, invokeDef));
        il.Append(il.Create(OpCodes.Ret));

        return invoker;
    }

    private static void BuildTwoBodySelector(
        MethodDefinition canonicalMethod,
        MethodDefinition vanillaClone,
        MethodDefinition editedClone,
        AssemblyDefinition hooksAsm)
    {
        // Add public static bool IsActive_<MethodName> to TerrariaHooks.LoweredILSlots
        var slotsType = GetOrCreateLoweredILSlotsType(hooksAsm);
        string activeFieldName = "IsActive_" + canonicalMethod.Name;
        var activeField = slotsType.Fields.FirstOrDefault(f => f.Name == activeFieldName);
        if (activeField == null)
        {
            activeField = new FieldDefinition(activeFieldName, FieldAttributes.Public | FieldAttributes.Static, hooksAsm.MainModule.TypeSystem.Boolean);
            slotsType.Fields.Add(activeField);
            Console.WriteLine($"Added {slotsType.FullName}::{activeFieldName}");
        }

        // Canonical target becomes selector: { all baked edits registered ? __il_edited : __il_vanilla }
        canonicalMethod.Body.Instructions.Clear();
        canonicalMethod.Body.Variables.Clear();
        canonicalMethod.Body.ExceptionHandlers.Clear();
        canonicalMethod.Body.InitLocals = true;

        var il = canonicalMethod.Body.GetILProcessor();
        var vanillaLabel = il.Create(OpCodes.Nop);

        var activeFieldRef = canonicalMethod.Module.ImportReference(activeField);
        il.Append(il.Create(OpCodes.Ldsfld, activeFieldRef));
        il.Append(il.Create(OpCodes.Brfalse_S, vanillaLabel));

        // Call __il_edited
        if (!canonicalMethod.IsStatic)
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
        }
        for (int p = 0; p < canonicalMethod.Parameters.Count; p++)
        {
            il.Append(LdargHelper(il, canonicalMethod.IsStatic ? p : p + 1, canonicalMethod.Parameters[p]));
        }
        il.Append(il.Create(OpCodes.Call, editedClone));
        il.Append(il.Create(OpCodes.Ret));

        // Call __il_vanilla
        il.Append(vanillaLabel);
        if (!canonicalMethod.IsStatic)
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
        }
        for (int p = 0; p < canonicalMethod.Parameters.Count; p++)
        {
            il.Append(LdargHelper(il, canonicalMethod.IsStatic ? p : p + 1, canonicalMethod.Parameters[p]));
        }
        il.Append(il.Create(OpCodes.Call, vanillaClone));
        il.Append(il.Create(OpCodes.Ret));

        Console.WriteLine($"Turned canonical method {canonicalMethod.FullName} into Two-Body Selector.");
    }

    private static void GenerateLoweredILRegistry(AssemblyDefinition hooksAsm, List<LoweredEditSpec> specs)
    {
        var hooksModule = hooksAsm.MainModule;
        var sysRuntimeAsm = hooksModule.AssemblyReferences.First(a => a.Name == "System.Runtime");
        var sysRuntimeMod = sysRuntimeAsm;

        var regType = new TypeDefinition("TerrariaHooks", "LoweredILRegistry",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            hooksModule.TypeSystem.Object);
        hooksModule.Types.Add(regType);

        var methodBaseRef = new TypeReference("System.Reflection", "MethodBase", hooksModule, sysRuntimeMod);
        var delegateRef = new TypeReference("System", "Delegate", hooksModule, sysRuntimeMod);
        var assemblyRef = new TypeReference("System.Reflection", "Assembly", hooksModule, sysRuntimeMod);
        var stringRef = hooksModule.TypeSystem.String;
        var boolRef = hooksModule.TypeSystem.Boolean;
        var voidRef = hooksModule.TypeSystem.Void;
        var intRef = hooksModule.TypeSystem.Int32;

        // Group specs by target method
        var targets = specs.GroupBy(s => s.TargetMethod).ToList();

        // Add TargetState nested helper or fields
        // For each target method, define static int RegisteredCount_<targetMethod> and const ExpectedCount_<targetMethod>
        var slotsType = GetOrCreateLoweredILSlotsType(hooksAsm);

        foreach (var group in targets)
        {
            string targetMethod = group.Key;
            int count = group.Count();
            var countField = new FieldDefinition("RegisteredCount_" + targetMethod, FieldAttributes.Public | FieldAttributes.Static, intRef);
            slotsType.Fields.Add(countField);
        }

        // Add EditRegistration class:
        // class EditRegistration { public string TargetMethod; public string DeclaringType; public string MethodName; public string ModMvid; public bool IsRegistered; }
        // For maximum AOT simplicity and zero dynamic reflection issues on Mono interpreter, we generate direct CIL checks in:
        // bool IsLoweredTarget(MethodBase method)
        // bool Register(MethodBase target, Delegate manipulator)
        // bool Unregister(MethodBase target, Delegate manipulator)
        // void ClearModHooks(Assembly modAssembly)
        // void ClearAll()

        // 1. bool IsLoweredTarget(MethodBase method)
        var isLoweredMethod = new MethodDefinition("IsLoweredTarget",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            boolRef);
        isLoweredMethod.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, methodBaseRef));
        regType.Methods.Add(isLoweredMethod);

        var ilIsLow = isLoweredMethod.Body.GetILProcessor();
        var trueLabel = ilIsLow.Create(OpCodes.Ldc_I4_1);
        var falseLabel = ilIsLow.Create(OpCodes.Ldc_I4_0);

        var getMethodNameRef = new MethodReference("get_Name", stringRef, methodBaseRef) { HasThis = true };
        var stringEqRef = hooksModule.ImportReference(
            new MethodReference("op_Equality", boolRef, stringRef)
            {
                Parameters = { new ParameterDefinition(stringRef), new ParameterDefinition(stringRef) }
            });

        var nullLabel = ilIsLow.Create(OpCodes.Ldc_I4_0);
        var startCheck = ilIsLow.Create(OpCodes.Ldarg_0);

        ilIsLow.Append(ilIsLow.Create(OpCodes.Ldarg_0));
        ilIsLow.Append(ilIsLow.Create(OpCodes.Brtrue_S, startCheck));
        ilIsLow.Append(nullLabel);
        ilIsLow.Append(ilIsLow.Create(OpCodes.Ret));

        ilIsLow.Append(startCheck);
        ilIsLow.Append(ilIsLow.Create(OpCodes.Callvirt, getMethodNameRef));

        var distinctTargetNames = targets.Select(t => t.Key).Distinct().ToList();
        for (int i = 0; i < distinctTargetNames.Count; i++)
        {
            string tName = distinctTargetNames[i];
            ilIsLow.Append(ilIsLow.Create(OpCodes.Dup));
            ilIsLow.Append(ilIsLow.Create(OpCodes.Ldstr, tName));
            ilIsLow.Append(ilIsLow.Create(OpCodes.Call, stringEqRef));
            var nextCheck = ilIsLow.Create(OpCodes.Nop);
            ilIsLow.Append(ilIsLow.Create(OpCodes.Brfalse_S, nextCheck));
            ilIsLow.Append(ilIsLow.Create(OpCodes.Pop)); // pop duped name
            ilIsLow.Append(ilIsLow.Create(OpCodes.Ldc_I4_1));
            ilIsLow.Append(ilIsLow.Create(OpCodes.Ret));
            ilIsLow.Append(nextCheck);
        }
        ilIsLow.Append(ilIsLow.Create(OpCodes.Pop)); // pop name
        ilIsLow.Append(ilIsLow.Create(OpCodes.Ldc_I4_0));
        ilIsLow.Append(ilIsLow.Create(OpCodes.Ret));

        // Helper: MethodInfo UnpackManipulator(Delegate manipulator)
        var unpackMethod = new MethodDefinition("UnpackManipulator",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            hooksModule.ImportReference(typeof(MethodInfo)));
        unpackMethod.Parameters.Add(new ParameterDefinition("manipulator", ParameterAttributes.None, hooksModule.ImportReference(typeof(Delegate))));
        regType.Methods.Add(unpackMethod);
        BuildUnpackManipulatorBody(unpackMethod, hooksAsm);

        // 2. void Register(MethodBase target, Delegate manipulator)
        var registerMethod = new MethodDefinition("Register",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            voidRef);
        registerMethod.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, methodBaseRef));
        registerMethod.Parameters.Add(new ParameterDefinition("manipulator", ParameterAttributes.None, delegateRef));
        regType.Methods.Add(registerMethod);

        BuildRegisterBody(registerMethod, hooksAsm, specs, targets, unpackMethod);

        // 3. void Unregister(MethodBase target, Delegate manipulator)
        var unregisterMethod = new MethodDefinition("Unregister",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            voidRef);
        unregisterMethod.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, methodBaseRef));
        unregisterMethod.Parameters.Add(new ParameterDefinition("manipulator", ParameterAttributes.None, delegateRef));
        regType.Methods.Add(unregisterMethod);

        BuildUnregisterBody(unregisterMethod, hooksAsm, specs, targets, unpackMethod);

        // 4. void ClearModHooks(Assembly modAssembly)
        var clearModMethod = new MethodDefinition("ClearModHooks",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            voidRef);
        clearModMethod.Parameters.Add(new ParameterDefinition("modAssembly", ParameterAttributes.None, assemblyRef));
        regType.Methods.Add(clearModMethod);

        BuildClearModBody(clearModMethod, hooksAsm, specs, targets);

        // 5. void ClearAll()
        var clearAllMethod = new MethodDefinition("ClearAll",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            voidRef);
        regType.Methods.Add(clearAllMethod);

        BuildClearAllBody(clearAllMethod, hooksAsm, specs, targets);
    }

    private static void BuildUnpackManipulatorBody(MethodDefinition unpackMethod, AssemblyDefinition hooksAsm)
    {
        var hooksModule = hooksAsm.MainModule;
        var sysRuntimeAsm = hooksModule.AssemblyReferences.First(a => a.Name == "System.Runtime");
        var il = unpackMethod.Body.GetILProcessor();

        var delegateRef = hooksModule.ImportReference(typeof(Delegate));
        var objectRef = hooksModule.TypeSystem.Object;
        var methodInfoRef = hooksModule.ImportReference(typeof(MethodInfo));
        var typeRef = hooksModule.ImportReference(typeof(Type));
        var fieldInfoRef = hooksModule.ImportReference(typeof(FieldInfo));
        var getMethodRef = hooksModule.ImportReference(typeof(Delegate).GetProperty("Method").GetGetMethod());
        var getTargetRef = hooksModule.ImportReference(typeof(Delegate).GetProperty("Target").GetGetMethod());
        var getTypeRef = hooksModule.ImportReference(typeof(object).GetMethod("GetType", Type.EmptyTypes));
        var getFieldsRef = hooksModule.ImportReference(typeof(Type).GetMethod("GetFields", new[] { typeof(BindingFlags) }));
        var getValueRef = hooksModule.ImportReference(typeof(FieldInfo).GetMethod("GetValue", new[] { typeof(object) }));

        unpackMethod.Body.Variables.Add(new VariableDefinition(methodInfoRef)); // 0: resultMethod
        unpackMethod.Body.Variables.Add(new VariableDefinition(objectRef));     // 1: target
        unpackMethod.Body.Variables.Add(new VariableDefinition(typeRef));       // 2: targetType
        unpackMethod.Body.Variables.Add(new VariableDefinition(new ArrayType(fieldInfoRef))); // 3: fields
        unpackMethod.Body.Variables.Add(new VariableDefinition(hooksModule.TypeSystem.Int32)); // 4: i
        unpackMethod.Body.Variables.Add(new VariableDefinition(fieldInfoRef));  // 5: field
        unpackMethod.Body.Variables.Add(new VariableDefinition(objectRef));     // 6: val
        unpackMethod.Body.InitLocals = true;
        unpackMethod.Body.MaxStackSize = 8;
        // resultMethod = manipulator.Method;
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, getMethodRef));
        il.Append(il.Create(OpCodes.Stloc_0));

        // target = manipulator.Target; if (target == null) return resultMethod;
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, getTargetRef));
        il.Append(il.Create(OpCodes.Stloc_1));
        var retDefault = il.Create(OpCodes.Ldloc_0);
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Brfalse, retDefault));

        // Check if target is itself a ManagedILEdit
        var targetIsEdit = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Callvirt, getTypeRef));
        il.Append(il.Create(OpCodes.Callvirt, hooksModule.ImportReference(typeof(Type).GetProperty("FullName").GetGetMethod())));
        il.Append(il.Create(OpCodes.Ldstr, "Luminance.Core.Hooking.ManagedILEdit"));
        il.Append(il.Create(OpCodes.Call, hooksModule.ImportReference(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }))));
        var checkFields = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Brfalse_S, checkFields));

        // Target is ManagedILEdit: get EditingFunction.Method
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Callvirt, getTypeRef));
        il.Append(il.Create(OpCodes.Ldstr, "EditingFunction"));
        il.Append(il.Create(OpCodes.Callvirt, hooksModule.ImportReference(typeof(Type).GetMethod("GetProperty", new[] { typeof(string) }))));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, hooksModule.ImportReference(typeof(PropertyInfo).GetMethod("GetValue", new[] { typeof(object), typeof(object[]) }))));
        il.Append(il.Create(OpCodes.Castclass, delegateRef));
        il.Append(il.Create(OpCodes.Callvirt, getMethodRef));
        il.Append(il.Create(OpCodes.Ret));

        // Otherwise inspect closure fields:
        il.Append(checkFields);
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Callvirt, getTypeRef));
        il.Append(il.Create(OpCodes.Ldc_I4, 60)); // BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        il.Append(il.Create(OpCodes.Callvirt, getFieldsRef));
        il.Append(il.Create(OpCodes.Stloc_3));

        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, unpackMethod.Body.Variables[4]));

        var loopCheck = il.Create(OpCodes.Nop);
        var loopStart = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Br, loopCheck));

        il.Append(loopStart);
        il.Append(il.Create(OpCodes.Ldloc_3));
        il.Append(il.Create(OpCodes.Ldloc, unpackMethod.Body.Variables[4]));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Stloc, unpackMethod.Body.Variables[5]));

        il.Append(il.Create(OpCodes.Ldloc, unpackMethod.Body.Variables[5]));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Callvirt, getValueRef));
        il.Append(il.Create(OpCodes.Stloc, unpackMethod.Body.Variables[6]));

        var nextIter = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Ldloc, unpackMethod.Body.Variables[6]));
        il.Append(il.Create(OpCodes.Brfalse_S, nextIter));

        // Check if val.GetType().FullName == "Luminance.Core.Hooking.ManagedILEdit"
        il.Append(il.Create(OpCodes.Ldloc, unpackMethod.Body.Variables[6]));
        il.Append(il.Create(OpCodes.Callvirt, getTypeRef));
        il.Append(il.Create(OpCodes.Callvirt, hooksModule.ImportReference(typeof(Type).GetProperty("FullName").GetGetMethod())));
        il.Append(il.Create(OpCodes.Ldstr, "Luminance.Core.Hooking.ManagedILEdit"));
        il.Append(il.Create(OpCodes.Call, hooksModule.ImportReference(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }))));
        il.Append(il.Create(OpCodes.Brfalse_S, nextIter));

        // Extract EditingFunction.Method
        il.Append(il.Create(OpCodes.Ldloc, unpackMethod.Body.Variables[6]));
        il.Append(il.Create(OpCodes.Callvirt, getTypeRef));
        il.Append(il.Create(OpCodes.Ldstr, "EditingFunction"));
        il.Append(il.Create(OpCodes.Callvirt, hooksModule.ImportReference(typeof(Type).GetMethod("GetProperty", new[] { typeof(string) }))));
        il.Append(il.Create(OpCodes.Ldloc, unpackMethod.Body.Variables[6]));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, hooksModule.ImportReference(typeof(PropertyInfo).GetMethod("GetValue", new[] { typeof(object), typeof(object[]) }))));
        il.Append(il.Create(OpCodes.Castclass, delegateRef));
        il.Append(il.Create(OpCodes.Callvirt, getMethodRef));
        il.Append(il.Create(OpCodes.Ret));

        il.Append(nextIter);
        il.Append(il.Create(OpCodes.Ldloc, unpackMethod.Body.Variables[4]));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, unpackMethod.Body.Variables[4]));

        il.Append(loopCheck);
        il.Append(il.Create(OpCodes.Ldloc, unpackMethod.Body.Variables[4]));
        il.Append(il.Create(OpCodes.Ldloc_3));
        il.Append(il.Create(OpCodes.Ldlen));
        il.Append(il.Create(OpCodes.Conv_I4));
        il.Append(il.Create(OpCodes.Blt, loopStart));

        il.Append(retDefault);
        il.Append(il.Create(OpCodes.Ret));
    }

    private static void BuildRegisterBody(
        MethodDefinition regMethod,
        AssemblyDefinition hooksAsm,
        List<LoweredEditSpec> specs,
        List<IGrouping<string, LoweredEditSpec>> targets,
        MethodDefinition unpackMethod)
    {
        var hooksModule = hooksAsm.MainModule;
        var sysRuntimeAsm = hooksModule.AssemblyReferences.First(a => a.Name == "System.Runtime");
        var il = regMethod.Body.GetILProcessor();

        var methodBaseRef = new TypeReference("System.Reflection", "MethodBase", hooksModule, sysRuntimeAsm);
        var delegateRef = new TypeReference("System", "Delegate", hooksModule, sysRuntimeAsm);
        var typeRef = new TypeReference("System", "Type", hooksModule, sysRuntimeAsm);
        var moduleRef = new TypeReference("System.Reflection", "Module", hooksModule, sysRuntimeAsm);
        var guidRef = hooksModule.ImportReference(typeof(Guid));
        var stringRef = hooksModule.TypeSystem.String;
        var boolRef = hooksModule.TypeSystem.Boolean;

        var getMethodNameRef = new MethodReference("get_Name", stringRef, methodBaseRef) { HasThis = true };
        var getDelegateMethodRef = new MethodReference("get_Method", new TypeReference("System.Reflection", "MethodInfo", hooksModule, sysRuntimeAsm), delegateRef) { HasThis = true };
        var getDeclaringTypeRef = new MethodReference("get_DeclaringType", typeRef, methodBaseRef) { HasThis = true };
        var getTypeFullNameRef = new MethodReference("get_FullName", stringRef, typeRef) { HasThis = true };
        var getModuleRef = new MethodReference("get_Module", moduleRef, typeRef) { HasThis = true };
        var getMvidRef = new MethodReference("get_ModuleVersionId", guidRef, moduleRef) { HasThis = true };
        var guidToStringRef = hooksModule.ImportReference(typeof(Guid).GetMethod("ToString", Type.EmptyTypes));
        var getAssemblyRef = new MethodReference("get_Assembly", new TypeReference("System.Reflection", "Assembly", hooksModule, sysRuntimeAsm), typeRef) { HasThis = true };
        var stringEqRef = hooksModule.ImportReference(
            new MethodReference("op_Equality", boolRef, stringRef)
            {
                Parameters = { new ParameterDefinition(stringRef), new ParameterDefinition(stringRef) }
            });

        var notSupportedCtor = hooksModule.ImportReference(
            new MethodReference(".ctor", hooksModule.TypeSystem.Void,
                new TypeReference("System", "NotSupportedException", hooksModule, sysRuntimeAsm))
            {
                HasThis = true,
                Parameters = { new ParameterDefinition(stringRef) }
            });

        var stringConcatRef = hooksModule.ImportReference(
            typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) }));

        // Locals:
        // 0: string targetName
        // 1: string manipDeclTypeName
        // 2: string manipMethodName
        // 3: string manipMvid
        // 4: Assembly manipAssembly
        regMethod.Body.Variables.Add(new VariableDefinition(stringRef));
        regMethod.Body.Variables.Add(new VariableDefinition(stringRef));
        regMethod.Body.Variables.Add(new VariableDefinition(stringRef));
        regMethod.Body.Variables.Add(new VariableDefinition(stringRef));
        regMethod.Body.Variables.Add(new VariableDefinition(new TypeReference("System.Reflection", "Assembly", hooksModule, sysRuntimeAsm)));
        regMethod.Body.InitLocals = true;

        var checkTargetNull = il.Create(OpCodes.Ldarg_0);
        il.Append(checkTargetNull);
        var notLoweredLabel = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Brfalse, notLoweredLabel));

        var checkManipNull = il.Create(OpCodes.Ldarg_1);
        il.Append(checkManipNull);
        il.Append(il.Create(OpCodes.Brfalse, notLoweredLabel));

        // targetName = target.Name
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, getMethodNameRef));
        il.Append(il.Create(OpCodes.Stloc_0));

        // MethodInfo dMethod = UnpackManipulator(manipulator)
        // manipDeclTypeName = dMethod.DeclaringType.FullName
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Call, unpackMethod));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Callvirt, getDeclaringTypeRef));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Callvirt, getTypeFullNameRef));
        il.Append(il.Create(OpCodes.Stloc_1));

        // manipAssembly = dMethod.DeclaringType.Assembly
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Callvirt, getAssemblyRef));
        il.Append(il.Create(OpCodes.Stloc, regMethod.Body.Variables[4]));
        // manipMvid = dMethod.DeclaringType.Module.ModuleVersionId.ToString()
        il.Append(il.Create(OpCodes.Callvirt, getModuleRef));
        var mvidBox = new VariableDefinition(guidRef);
        regMethod.Body.Variables.Add(mvidBox);
        il.Append(il.Create(OpCodes.Callvirt, getMvidRef));
        il.Append(il.Create(OpCodes.Stloc, mvidBox));
        il.Append(il.Create(OpCodes.Ldloca, mvidBox));
        il.Append(il.Create(OpCodes.Call, guidToStringRef));
        il.Append(il.Create(OpCodes.Stloc_3));

        // manipMethodName = dMethod.Name
        il.Append(il.Create(OpCodes.Callvirt, getMethodNameRef));
        il.Append(il.Create(OpCodes.Stloc_2));

        var endLabel = il.Create(OpCodes.Ret);
        var slotsType = GetOrCreateLoweredILSlotsType(hooksAsm);

        // Generate dispatch per spec
        foreach (var spec in specs)
        {
            var nextSpecLabel = il.Create(OpCodes.Nop);

            // Check targetName == spec.TargetMethod
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldstr, spec.TargetMethod));
            il.Append(il.Create(OpCodes.Call, stringEqRef));
            il.Append(il.Create(OpCodes.Brfalse, nextSpecLabel));

            // Check manipDeclTypeName == spec.ManipulatorDeclaringType
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldstr, spec.ManipulatorDeclaringType));
            il.Append(il.Create(OpCodes.Call, stringEqRef));
            il.Append(il.Create(OpCodes.Brfalse, nextSpecLabel));

            // Check manipMethodName == spec.ManipulatorMethodName
            il.Append(il.Create(OpCodes.Ldloc_2));
            il.Append(il.Create(OpCodes.Ldstr, spec.ManipulatorMethodName));
            il.Append(il.Create(OpCodes.Call, stringEqRef));
            il.Append(il.Create(OpCodes.Brfalse, nextSpecLabel));

            // Check manipMvid == spec.ModMvid
            il.Append(il.Create(OpCodes.Ldloc_3));
            il.Append(il.Create(OpCodes.Ldstr, spec.ModMvid));
            il.Append(il.Create(OpCodes.Call, stringEqRef));
            il.Append(il.Create(OpCodes.Brfalse, nextSpecLabel));

            // Matched! Bind slots per their rules
            foreach (var rule in spec.SlotRules)
            {
                var slotField = slotsType.Fields.First(f => f.Name == rule.SlotName);

                if (rule.Kind == "StaticMethod")
                {
                    // Slot = (DelegateType)Delegate.CreateDelegate(typeof(DelegateType), manipAssembly.GetType(DeclaringType).GetMethod(MethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    // Emit reflection lookup and bind
                    EmitBindStaticDelegateSlot(il, hooksAsm, slotField, rule, regMethod.Body.Variables[4]);
                }
            }

            // Increment RegisteredCount_<TargetMethod>
            var countField = slotsType.Fields.First(f => f.Name == "RegisteredCount_" + spec.TargetMethod);
            il.Append(il.Create(OpCodes.Ldsfld, countField));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Stsfld, countField));

            // Check if all expected edits for TargetMethod are registered
            int expectedCount = targets.First(t => t.Key == spec.TargetMethod).Count();
            var activeField = slotsType.Fields.First(f => f.Name == "IsActive_" + spec.TargetMethod);

            il.Append(il.Create(OpCodes.Ldsfld, countField));
            il.Append(il.Create(OpCodes.Ldc_I4, expectedCount));
            var notAllLabel = il.Create(OpCodes.Nop);
            il.Append(il.Create(OpCodes.Blt_S, notAllLabel));

            // IsActive = true
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Stsfld, activeField));

            il.Append(notAllLabel);
            il.Append(il.Create(OpCodes.Ret));

            il.Append(nextSpecLabel);
        }

        // If no spec matched: throw NotSupportedException
        il.Append(notLoweredLabel);
        il.Append(il.Create(OpCodes.Ldstr, "Runtime ILHook registration rejected: target method or manipulator identity mismatch. Target: "));
        il.Append(il.Create(OpCodes.Ldarg_0));
        var methodToStringRef = new MethodReference("ToString", stringRef, methodBaseRef) { HasThis = true };
        il.Append(il.Create(OpCodes.Callvirt, methodToStringRef));
        il.Append(il.Create(OpCodes.Call, stringConcatRef));
        il.Append(il.Create(OpCodes.Ldstr, ", Manipulator: "));
        il.Append(il.Create(OpCodes.Call, stringConcatRef));
        il.Append(il.Create(OpCodes.Ldarg_1));
        var objToStringRef = new MethodReference("ToString", stringRef, hooksModule.TypeSystem.Object) { HasThis = true };
        il.Append(il.Create(OpCodes.Callvirt, objToStringRef));
        il.Append(il.Create(OpCodes.Call, stringConcatRef));
        il.Append(il.Create(OpCodes.Newobj, notSupportedCtor));
        il.Append(il.Create(OpCodes.Throw));
    }

    private static void EmitBindStaticDelegateSlot(
        ILProcessor il,
        AssemblyDefinition hooksAsm,
        FieldDefinition slotField,
        SlotBindingRule rule,
        VariableDefinition manipAsmVar)
    {
        var hooksModule = hooksAsm.MainModule;
        var sysRuntimeAsm = hooksModule.AssemblyReferences.First(a => a.Name == "System.Runtime");

        var assemblyRef = new TypeReference("System.Reflection", "Assembly", hooksModule, sysRuntimeAsm);
        var typeRef = new TypeReference("System", "Type", hooksModule, sysRuntimeAsm);
        var methodInfoRef = new TypeReference("System.Reflection", "MethodInfo", hooksModule, sysRuntimeAsm);
        var delegateRef = new TypeReference("System", "Delegate", hooksModule, sysRuntimeAsm);
        var intRef = hooksModule.TypeSystem.Int32;
        var stringRef = hooksModule.TypeSystem.String;

        var getTypeRef = hooksModule.ImportReference(typeof(Assembly).GetMethod("GetType", new[] { typeof(string) }));
        var getMethodRef = hooksModule.ImportReference(typeof(Type).GetMethod("GetMethod", new[] { typeof(string), typeof(BindingFlags) }));
        var createDelegateRef = hooksModule.ImportReference(typeof(Delegate).GetMethod("CreateDelegate", new[] { typeof(Type), typeof(MethodInfo) }));

        var getTypeFromHandleRef = hooksModule.ImportReference(
            typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }));

        // typeof(DelegateType)
        il.Append(il.Create(OpCodes.Ldtoken, slotField.FieldType));
        il.Append(il.Create(OpCodes.Call, getTypeFromHandleRef));

        // manipAsm.GetType(DeclaringType)
        il.Append(il.Create(OpCodes.Ldloc, manipAsmVar));
        il.Append(il.Create(OpCodes.Ldstr, rule.DeclaringType));
        il.Append(il.Create(OpCodes.Callvirt, getTypeRef));

        // .GetMethod(MethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) -> 4 | 8 | 32 = 44 (0x2c) or 60 (0x3c)
        il.Append(il.Create(OpCodes.Ldstr, rule.MethodName));
        il.Append(il.Create(OpCodes.Ldc_I4, 60)); // Public | NonPublic | Static | Instance
        il.Append(il.Create(OpCodes.Callvirt, getMethodRef));

        // Delegate.CreateDelegate(typeof(DelegateType), methodInfo)
        il.Append(il.Create(OpCodes.Call, createDelegateRef));
        il.Append(il.Create(OpCodes.Castclass, slotField.FieldType));

        // stsfld slotField
        il.Append(il.Create(OpCodes.Stsfld, slotField));
    }

    private static void BuildUnregisterBody(
        MethodDefinition unregMethod,
        AssemblyDefinition hooksAsm,
        List<LoweredEditSpec> specs,
        List<IGrouping<string, LoweredEditSpec>> targets,
        MethodDefinition unpackMethod)
    {
        var hooksModule = hooksAsm.MainModule;
        var sysRuntimeAsm = hooksModule.AssemblyReferences.First(a => a.Name == "System.Runtime");
        var il = unregMethod.Body.GetILProcessor();

        var methodBaseRef = new TypeReference("System.Reflection", "MethodBase", hooksModule, sysRuntimeAsm);
        var delegateRef = new TypeReference("System", "Delegate", hooksModule, sysRuntimeAsm);
        var typeRef = new TypeReference("System", "Type", hooksModule, sysRuntimeAsm);
        var stringRef = hooksModule.TypeSystem.String;
        var boolRef = hooksModule.TypeSystem.Boolean;

        var getMethodNameRef = new MethodReference("get_Name", stringRef, methodBaseRef) { HasThis = true };
        var getDelegateMethodRef = new MethodReference("get_Method", new TypeReference("System.Reflection", "MethodInfo", hooksModule, sysRuntimeAsm), delegateRef) { HasThis = true };
        var getDeclaringTypeRef = new MethodReference("get_DeclaringType", typeRef, methodBaseRef) { HasThis = true };
        var getTypeFullNameRef = new MethodReference("get_FullName", stringRef, typeRef) { HasThis = true };
        var stringEqRef = hooksModule.ImportReference(
            new MethodReference("op_Equality", boolRef, stringRef)
            {
                Parameters = { new ParameterDefinition(stringRef), new ParameterDefinition(stringRef) }
            });

        unregMethod.Body.Variables.Add(new VariableDefinition(stringRef)); // 0: targetName
        unregMethod.Body.Variables.Add(new VariableDefinition(stringRef)); // 1: manipDeclTypeName
        unregMethod.Body.Variables.Add(new VariableDefinition(stringRef)); // 2: manipMethodName
        unregMethod.Body.InitLocals = true;

        var retLabel = il.Create(OpCodes.Ret);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Brfalse, retLabel));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Brfalse, retLabel));

        // targetName = target.Name
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, getMethodNameRef));
        il.Append(il.Create(OpCodes.Stloc_0));

        // MethodInfo dMethod = UnpackManipulator(manipulator)
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Call, unpackMethod));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Callvirt, getDeclaringTypeRef));
        il.Append(il.Create(OpCodes.Callvirt, getTypeFullNameRef));
        il.Append(il.Create(OpCodes.Stloc_1));

        il.Append(il.Create(OpCodes.Callvirt, getMethodNameRef));
        il.Append(il.Create(OpCodes.Stloc_2));

        var slotsType = GetOrCreateLoweredILSlotsType(hooksAsm);

        foreach (var spec in specs)
        {
            var nextLabel = il.Create(OpCodes.Nop);

            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldstr, spec.TargetMethod));
            il.Append(il.Create(OpCodes.Call, stringEqRef));
            il.Append(il.Create(OpCodes.Brfalse, nextLabel));

            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldstr, spec.ManipulatorDeclaringType));
            il.Append(il.Create(OpCodes.Call, stringEqRef));
            il.Append(il.Create(OpCodes.Brfalse, nextLabel));

            il.Append(il.Create(OpCodes.Ldloc_2));
            il.Append(il.Create(OpCodes.Ldstr, spec.ManipulatorMethodName));
            il.Append(il.Create(OpCodes.Call, stringEqRef));
            il.Append(il.Create(OpCodes.Brfalse, nextLabel));

            // Deactivate selector and clear slots
            var activeField = slotsType.Fields.First(f => f.Name == "IsActive_" + spec.TargetMethod);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, activeField));

            var countField = slotsType.Fields.First(f => f.Name == "RegisteredCount_" + spec.TargetMethod);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, countField));

            foreach (var rule in spec.SlotRules)
            {
                var slotField = slotsType.Fields.First(f => f.Name == rule.SlotName);
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Stsfld, slotField));
            }

            il.Append(il.Create(OpCodes.Ret));
            il.Append(nextLabel);
        }

        il.Append(retLabel);
    }

    private static void BuildClearModBody(
        MethodDefinition clearModMethod,
        AssemblyDefinition hooksAsm,
        List<LoweredEditSpec> specs,
        List<IGrouping<string, LoweredEditSpec>> targets)
    {
        var hooksModule = hooksAsm.MainModule;
        var sysRuntimeAsm = hooksModule.AssemblyReferences.First(a => a.Name == "System.Runtime");
        var il = clearModMethod.Body.GetILProcessor();

        var assemblyRef = new TypeReference("System.Reflection", "Assembly", hooksModule, sysRuntimeAsm);
        var moduleRef = new TypeReference("System.Reflection", "Module", hooksModule, sysRuntimeAsm);
        var guidRef = hooksModule.ImportReference(typeof(Guid));
        var stringRef = hooksModule.TypeSystem.String;
        var boolRef = hooksModule.TypeSystem.Boolean;

        var getManifestModuleRef = new MethodReference("get_ManifestModule", moduleRef, assemblyRef) { HasThis = true };
        var getMvidRef = new MethodReference("get_ModuleVersionId", guidRef, moduleRef) { HasThis = true };
        var guidToStringRef = new MethodReference("ToString", stringRef, guidRef) { HasThis = true };
        var stringEqRef = hooksModule.ImportReference(
            new MethodReference("op_Equality", boolRef, stringRef)
            {
                Parameters = { new ParameterDefinition(stringRef), new ParameterDefinition(stringRef) }
            });

        clearModMethod.Body.Variables.Add(new VariableDefinition(stringRef)); // 0: modMvid
        clearModMethod.Body.Variables.Add(new VariableDefinition(guidRef));   // 1: guidBox
        clearModMethod.Body.InitLocals = true;

        var retLabel = il.Create(OpCodes.Ret);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Brfalse, retLabel));

        // modMvid = modAssembly.ManifestModule.ModuleVersionId.ToString()
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, getManifestModuleRef));
        il.Append(il.Create(OpCodes.Callvirt, getMvidRef));
        il.Append(il.Create(OpCodes.Stloc_1));
        il.Append(il.Create(OpCodes.Ldloca, clearModMethod.Body.Variables[1]));
        il.Append(il.Create(OpCodes.Call, guidToStringRef));
        il.Append(il.Create(OpCodes.Stloc_0));

        var slotsType = GetOrCreateLoweredILSlotsType(hooksAsm);

        foreach (var spec in specs)
        {
            var nextLabel = il.Create(OpCodes.Nop);

            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldstr, spec.ModMvid));
            il.Append(il.Create(OpCodes.Call, stringEqRef));
            il.Append(il.Create(OpCodes.Brfalse, nextLabel));

            // Deactivate and clear
            var activeField = slotsType.Fields.First(f => f.Name == "IsActive_" + spec.TargetMethod);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, activeField));

            var countField = slotsType.Fields.First(f => f.Name == "RegisteredCount_" + spec.TargetMethod);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, countField));

            foreach (var rule in spec.SlotRules)
            {
                var slotField = slotsType.Fields.First(f => f.Name == rule.SlotName);
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Stsfld, slotField));
            }

            il.Append(nextLabel);
        }

        il.Append(retLabel);
    }

    private static void BuildClearAllBody(
        MethodDefinition clearAllMethod,
        AssemblyDefinition hooksAsm,
        List<LoweredEditSpec> specs,
        List<IGrouping<string, LoweredEditSpec>> targets)
    {
        var il = clearAllMethod.Body.GetILProcessor();
        var slotsType = GetOrCreateLoweredILSlotsType(hooksAsm);

        foreach (var target in targets)
        {
            var activeField = slotsType.Fields.First(f => f.Name == "IsActive_" + target.Key);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, activeField));

            var countField = slotsType.Fields.First(f => f.Name == "RegisteredCount_" + target.Key);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, countField));
        }

        foreach (var spec in specs)
        {
            foreach (var rule in spec.SlotRules)
            {
                var slotField = slotsType.Fields.First(f => f.Name == rule.SlotName);
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Stsfld, slotField));
            }
        }

        il.Append(il.Create(OpCodes.Ret));
    }

    private static void PatchRuntimeDetour(AssemblyDefinition detourAsm, AssemblyDefinition patchedHooksAsm)
    {
        var regTypeDef = patchedHooksAsm.MainModule.GetType("TerrariaHooks.LoweredILRegistry")
            ?? throw new InvalidOperationException("LoweredILRegistry not found in patched TerrariaHooks!");

        var isLoweredDef = regTypeDef.Methods.First(m => m.Name == "IsLoweredTarget");
        var regDef = regTypeDef.Methods.First(m => m.Name == "Register");
        var unregDef = regTypeDef.Methods.First(m => m.Name == "Unregister");

        var isLoweredRef = detourAsm.MainModule.ImportReference(isLoweredDef);
        var regRef = detourAsm.MainModule.ImportReference(regDef);
        var unregRef = detourAsm.MainModule.ImportReference(unregDef);

        var sysRuntimeAsm = detourAsm.MainModule.AssemblyReferences.First(a => a.Name == "System.Runtime");
        var notSupportedCtor = detourAsm.MainModule.ImportReference(
            new MethodReference(".ctor", detourAsm.MainModule.TypeSystem.Void,
                new TypeReference("System", "NotSupportedException", detourAsm.MainModule, sysRuntimeAsm))
            {
                HasThis = true,
                Parameters = { new ParameterDefinition(detourAsm.MainModule.TypeSystem.String) }
            });

        // 1. Patch ILHook.Apply()
        var ilHookType = detourAsm.MainModule.GetType("MonoMod.RuntimeDetour.ILHook");
        var applyMethod = ilHookType.Methods.First(m => m.Name == "Apply");
        applyMethod.Body.Instructions.Clear();
        applyMethod.Body.Variables.Clear();
        applyMethod.Body.ExceptionHandlers.Clear();
        applyMethod.Body.InitLocals = true;

        var ilApply = applyMethod.Body.GetILProcessor();
        var notLoweredApply = ilApply.Create(OpCodes.Nop);

        // if (LoweredILRegistry.IsLoweredTarget(this.Method)) { LoweredILRegistry.Register(this.Method, this.Manipulator); return; }
        ilApply.Append(ilApply.Create(OpCodes.Ldarg_0));
        ilApply.Append(ilApply.Create(OpCodes.Call, ilHookType.Methods.First(m => m.Name == "get_Method")));
        ilApply.Append(ilApply.Create(OpCodes.Dup));
        ilApply.Append(ilApply.Create(OpCodes.Call, isLoweredRef));
        ilApply.Append(ilApply.Create(OpCodes.Brfalse, notLoweredApply));

        // Lowered target branch
        ilApply.Append(ilApply.Create(OpCodes.Ldarg_0));
        ilApply.Append(ilApply.Create(OpCodes.Call, ilHookType.Methods.First(m => m.Name == "get_Manipulator")));
        ilApply.Append(ilApply.Create(OpCodes.Call, regRef));
        ilApply.Append(ilApply.Create(OpCodes.Ret));

        // Not lowered target branch -> throw NotSupportedException
        ilApply.Append(notLoweredApply);
        ilApply.Append(ilApply.Create(OpCodes.Pop)); // pop method
        ilApply.Append(ilApply.Create(OpCodes.Ldstr, "Runtime ILHook on target method is unsupported on Nintendo Switch (no JIT). Target method must be lowered at build time."));
        ilApply.Append(ilApply.Create(OpCodes.Newobj, notSupportedCtor));
        ilApply.Append(ilApply.Create(OpCodes.Throw));

        // 2. Patch ILHook.Undo()
        var undoMethod = ilHookType.Methods.First(m => m.Name == "Undo");
        undoMethod.Body.Instructions.Clear();
        undoMethod.Body.Variables.Clear();
        undoMethod.Body.ExceptionHandlers.Clear();
        undoMethod.Body.InitLocals = true;

        var ilUndo = undoMethod.Body.GetILProcessor();
        var notLoweredUndo = ilUndo.Create(OpCodes.Nop);

        // if (LoweredILRegistry.IsLoweredTarget(this.Method)) { LoweredILRegistry.Unregister(this.Method, this.Manipulator); return; }
        ilUndo.Append(ilUndo.Create(OpCodes.Ldarg_0));
        ilUndo.Append(ilUndo.Create(OpCodes.Call, ilHookType.Methods.First(m => m.Name == "get_Method")));
        ilUndo.Append(ilUndo.Create(OpCodes.Dup));
        ilUndo.Append(ilUndo.Create(OpCodes.Call, isLoweredRef));
        ilUndo.Append(ilUndo.Create(OpCodes.Brfalse, notLoweredUndo));

        ilUndo.Append(ilUndo.Create(OpCodes.Ldarg_0));
        ilUndo.Append(ilUndo.Create(OpCodes.Call, ilHookType.Methods.First(m => m.Name == "get_Manipulator")));
        ilUndo.Append(ilUndo.Create(OpCodes.Call, unregRef));
        ilUndo.Append(ilUndo.Create(OpCodes.Ret));

        ilUndo.Append(notLoweredUndo);
        ilUndo.Append(ilUndo.Create(OpCodes.Pop));
        ilUndo.Append(ilUndo.Create(OpCodes.Ret));

        // 3. Patch ILHookInfo.ApplyCore and UndoCore
        var hookInfoType = detourAsm.MainModule.GetType("MonoMod.RuntimeDetour.ILHookInfo");
        var detourBaseType = detourAsm.MainModule.GetType("MonoMod.RuntimeDetour.DetourBase");
        var getMethodMethod = detourBaseType.Methods.First(m => m.Name == "get_Method");
        var methodDetourInfoType = detourAsm.MainModule.GetType("MonoMod.RuntimeDetour.MethodDetourInfo");
        var getMethodBaseMethod = methodDetourInfoType.Methods.First(m => m.Name == "get_Method");

        var getManipMethod = hookInfoType.Methods.FirstOrDefault(m => m.Name.Contains("Manip"))
            ?? hookInfoType.Methods.First();

        var applyCore = hookInfoType.Methods.First(m => m.Name == "ApplyCore");
        applyCore.Body.Instructions.Clear();
        applyCore.Body.Variables.Clear();
        applyCore.Body.ExceptionHandlers.Clear();
        applyCore.Body.InitLocals = true;

        var ilApplyCore = applyCore.Body.GetILProcessor();
        var notLoweredCore = ilApplyCore.Create(OpCodes.Nop);

        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Ldarg_0));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Call, getMethodMethod));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Callvirt, getMethodBaseMethod));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Dup));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Call, isLoweredRef));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Brfalse, notLoweredCore));

        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Ldarg_0));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Call, getManipMethod));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Call, regRef));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Ret));

        ilApplyCore.Append(notLoweredCore);
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Pop));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Ldstr, "Runtime ILHook on target method is unsupported on Nintendo Switch (no JIT). Target method must be lowered at build time."));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Newobj, notSupportedCtor));
        ilApplyCore.Append(ilApplyCore.Create(OpCodes.Throw));

        var undoCore = hookInfoType.Methods.First(m => m.Name == "UndoCore");
        undoCore.Body.Instructions.Clear();
        undoCore.Body.Variables.Clear();
        undoCore.Body.ExceptionHandlers.Clear();
        undoCore.Body.InitLocals = true;

        var ilUndoCore = undoCore.Body.GetILProcessor();
        var retUndoCore = ilUndoCore.Create(OpCodes.Ret);

        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Ldarg_0));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Call, getMethodMethod));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Callvirt, getMethodBaseMethod));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Dup));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Call, isLoweredRef));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Brfalse, retUndoCore));

        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Ldarg_0));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Call, getManipMethod));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Call, unregRef));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Ret));

        ilUndoCore.Append(retUndoCore);
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Pop));
        ilUndoCore.Append(ilUndoCore.Create(OpCodes.Ret));

        // 4. Patch ILHook.Dispose(bool) and Finalize()
        var dispMethod = ilHookType.Methods.FirstOrDefault(m => m.Name == "Dispose" && m.Parameters.Count == 1);
        if (dispMethod != null)
        {
            dispMethod.Body.Instructions.Clear();
            dispMethod.Body.Variables.Clear();
            dispMethod.Body.ExceptionHandlers.Clear();
            dispMethod.Body.InitLocals = true;
            var ilDisp = dispMethod.Body.GetILProcessor();
            ilDisp.Append(ilDisp.Create(OpCodes.Ldarg_0));
            ilDisp.Append(ilDisp.Create(OpCodes.Call, undoMethod));
            ilDisp.Append(ilDisp.Create(OpCodes.Ret));
        }

        var finalizeMethod = ilHookType.Methods.FirstOrDefault(m => m.Name == "Finalize");
        if (finalizeMethod != null)
        {
            finalizeMethod.Body.Instructions.Clear();
            finalizeMethod.Body.Variables.Clear();
            finalizeMethod.Body.ExceptionHandlers.Clear();
            finalizeMethod.Body.InitLocals = true;
            var ilFin = finalizeMethod.Body.GetILProcessor();
            ilFin.Append(ilFin.Create(OpCodes.Ret));
        }

        // 5. Patch DetourManager.ManagedDetourState AddILHook and RemoveILHook
        var detManager = detourAsm.MainModule.GetType("MonoMod.RuntimeDetour.DetourManager");
        var mds = detManager.NestedTypes.FirstOrDefault(t => t.Name == "ManagedDetourState");
        if (mds != null)
        {
            var addILHook = mds.Methods.FirstOrDefault(m => m.Name == "AddILHook");
            if (addILHook != null)
            {
                addILHook.Body.Instructions.Clear();
                addILHook.Body.Variables.Clear();
                addILHook.Body.ExceptionHandlers.Clear();
                addILHook.Body.InitLocals = true;
                var ilAddIL = addILHook.Body.GetILProcessor();
                ilAddIL.Append(ilAddIL.Create(OpCodes.Ret));
            }

            var removeILHook = mds.Methods.FirstOrDefault(m => m.Name == "RemoveILHook");
            if (removeILHook != null)
            {
                removeILHook.Body.Instructions.Clear();
                removeILHook.Body.Variables.Clear();
                removeILHook.Body.ExceptionHandlers.Clear();
                removeILHook.Body.InitLocals = true;
                var ilRemIL = removeILHook.Body.GetILProcessor();
                ilRemIL.Append(ilRemIL.Create(OpCodes.Ret));
            }
        }

        Console.WriteLine("Patched MonoMod.RuntimeDetour ILHook, ILHookInfo, Dispose, Finalize, and DetourManager ILHook methods.");
    }

    private static Instruction LdargHelper(ILProcessor il, int index, ParameterDefinition p)
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

    private static MethodDefinition CloneMethodBody(MethodDefinition src, string newName, TypeDefinition targetType)
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
            else if (instr.Operand is byte b) newInstr = Instruction.Create(instr.OpCode, b);
            else if (instr.Operand is sbyte sb) newInstr = Instruction.Create(instr.OpCode, sb);
            else if (instr.Operand is int intVal) newInstr = Instruction.Create(instr.OpCode, intVal);
            else if (instr.Operand is long longVal) newInstr = Instruction.Create(instr.OpCode, longVal);
            else if (instr.Operand is float f) newInstr = Instruction.Create(instr.OpCode, f);
            else if (instr.Operand is double d) newInstr = Instruction.Create(instr.OpCode, d);
            else if (instr.Operand is string s) newInstr = Instruction.Create(instr.OpCode, s);
            else if (instr.Operand is FieldReference fr) newInstr = Instruction.Create(instr.OpCode, fr);
            else if (instr.Operand is MethodReference mr) newInstr = Instruction.Create(instr.OpCode, mr);
            else if (instr.Operand is TypeReference tr) newInstr = Instruction.Create(instr.OpCode, tr);
            else if (instr.Operand is CallSite cs) newInstr = Instruction.Create(instr.OpCode, cs);
            else throw new NotSupportedException($"Unsupported operand type: {instr.Operand.GetType()}");

            dstBody.Instructions.Add(newInstr);
            instrMap[instr] = newInstr;
        }

        for (int i = 0; i < srcBody.Instructions.Count; i++)
        {
            var srcInstr = srcBody.Instructions[i];
            var dstInstr = dstBody.Instructions[i];

            if (srcInstr.Operand is Instruction targetInstr)
            {
                dstInstr.Operand = instrMap[targetInstr];
            }
            else if (srcInstr.Operand is Instruction[] targetInstrs)
            {
                dstInstr.Operand = targetInstrs.Select(ti => instrMap[ti]).ToArray();
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
                FilterStart = eh.FilterStart != null ? instrMap[eh.FilterStart] : null
            };
            dstBody.ExceptionHandlers.Add(newEh);
        }

        targetType.Methods.Add(dst);
        return dst;
    }

    private static void SetContentDerivedMvid(AssemblyDefinition asm, string outPath)
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
}
