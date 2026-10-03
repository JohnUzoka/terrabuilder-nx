using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

// Runtime detours (new Hook(MethodBase, Delegate), MonoModHooks.Add, library wrappers) on
// statically resolved targets. The target gets the same lowered multi-handler chain as an
// On_ event: an existing HookGen event when one has the exact signature, otherwise a
// synthesized holder in TerrariaHooks.Lowered. The type that declares each detour method gets
// a nested LoweredDetourFactory with typed adapters (ILoweredDetour); the patched Hook resolves
// it once at construction, matching the target by signature and the detour by method handle.
// Dispatch stays typed: the adapter wraps each chain link's orig in the mod's own orig delegate
// type, cached per link.
partial class Program
{
    const string LoweredNamespace = "TerrariaHooks.Lowered";
    const string FactoryTypeName = "LoweredDetourFactory";
    const string LoweredApiNamespace = "MonoMod.RuntimeDetour";

    record RuntimeDetourTarget(MethodDefinition Target, List<string> DetourMethods);

    static List<RuntimeDetourTarget> LoadRuntimeDetourTargets(string inventoryPath, ModuleDefinition tml)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        var byTarget = new SortedDictionary<string, RuntimeDetourTarget>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("runtime_detours", out var detours))
            return new List<RuntimeDetourTarget>();
        foreach (var entry in detours.EnumerateArray())
        {
            string mechanism = entry.GetProperty("mechanism").GetString();
            if (!entry.TryGetProperty("target", out var target) || target.ValueKind == JsonValueKind.Null)
                throw new NotSupportedException($"Runtime detour without a static target ({mechanism}); rebuild cannot lower it.");
            if (mechanism == "Detour" || mechanism == "NativeDetour")
                throw new NotSupportedException($"{mechanism} (method-to-method or native) cannot be lowered offline: {target.GetProperty("full_name").GetString()}");
            string fullName = target.GetProperty("full_name").GetString();
            var typeDef = tml.GetType(target.GetProperty("type").GetString())
                ?? throw new InvalidOperationException($"Runtime detour target type not found in tModLoader: {fullName}");
            var method = typeDef.Methods.SingleOrDefault(m => m.FullName == fullName)
                ?? throw new InvalidOperationException($"Runtime detour target not found in tModLoader: {fullName}");
            if (method.IsConstructor)
                throw new NotSupportedException($"Runtime detour on a constructor is not lowered offline: {fullName}");
            CheckUnsupportedTarget(method, fullName);
            if (!byTarget.TryGetValue(fullName, out var lowered))
                byTarget[fullName] = lowered = new RuntimeDetourTarget(method, new List<string>());
            foreach (var registration in entry.GetProperty("registrations").EnumerateArray())
            {
                string detourMethod = registration.TryGetProperty("detour_method", out var dm) ? dm.GetString() : null;
                if (string.IsNullOrEmpty(detourMethod))
                    throw new NotSupportedException($"Runtime detour on {fullName} does not bind one static method ({mechanism}).");
                if (!lowered.DetourMethods.Contains(detourMethod))
                    lowered.DetourMethods.Add(detourMethod);
            }
        }
        foreach (var lowered in byTarget.Values)
            lowered.DetourMethods.Sort(StringComparer.Ordinal);
        return byTarget.Values.ToList();
    }

    static List<TypeReference> OrigSignature(MethodDefinition target)
    {
        var types = new List<TypeReference>();
        if (!target.IsStatic)
            types.Add(target.DeclaringType);
        types.AddRange(target.Parameters.Select(p => p.ParameterType));
        return types;
    }

    static bool SameSignature(MethodReference invoke, TypeReference returnType, IReadOnlyList<TypeReference> parameters)
    {
        return invoke.ReturnType.FullName == returnType.FullName
            && invoke.Parameters.Count == parameters.Count
            && invoke.Parameters.Select((p, i) => p.ParameterType.FullName == parameters[i].FullName).All(x => x);
    }

    static string Mangle(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Find the HookGen On_ event for this exact target, or synthesize a holder with
    /// orig_/hook_ delegates and add_/remove_ accessors whose bodies the chain generator writes.</summary>
    static HookTargetSpec ResolveRuntimeHolder(ModuleDefinition hooks, MethodDefinition target)
    {
        var parameters = OrigSignature(target);
        var declaring = target.DeclaringType;
        if (!declaring.IsNested)
        {
            var onType = hooks.GetType(declaring.Namespace, "On_" + declaring.Name);
            if (onType != null)
            {
                var matches = onType.NestedTypes
                    .Where(t => t.Name.StartsWith("orig_") && SameSignature(t.Methods.First(m => m.Name == "Invoke"), target.ReturnType, parameters))
                    .Select(t => t.Name.Substring("orig_".Length))
                    .Where(e => e == target.Name || e.StartsWith(target.Name + "_"))
                    .Where(e => onType.NestedTypes.Any(t => t.Name == "hook_" + e) && onType.Methods.Any(m => m.Name == "add_" + e))
                    .ToList();
                if (matches.Count > 1)
                    throw new InvalidOperationException($"Ambiguous HookGen events for {target.FullName}: {string.Join(", ", matches)}");
                if (matches.Count == 1)
                    return new HookTargetSpec(declaring.FullName, onType.FullName, target.Name, matches[0], target.IsStatic, false, target.FullName);
            }
        }
        string holderName = "On_" + Mangle(declaring.FullName);
        string eventName = target.Name + "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(target.FullName)), 0, 4);
        var holder = hooks.GetType(LoweredNamespace, holderName);
        if (holder == null)
        {
            holder = new TypeDefinition(LoweredNamespace, holderName,
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                hooks.TypeSystem.Object);
            hooks.Types.Add(holder);
        }
        if (holder.NestedTypes.All(t => t.Name != "orig_" + eventName))
        {
            var orig = MakeDelegate(hooks, "orig_" + eventName, hooks.ImportReference(target.ReturnType),
                target.IsStatic ? null : target.DeclaringType, target.Parameters);
            holder.NestedTypes.Add(orig);
            var hook = MakeDelegate(hooks, "hook_" + eventName, hooks.ImportReference(target.ReturnType),
                target.IsStatic ? null : target.DeclaringType, target.Parameters, orig);
            holder.NestedTypes.Add(hook);
            foreach (string accessor in new[] { "add_", "remove_" })
            {
                var method = new MethodDefinition(accessor + eventName,
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                    hooks.TypeSystem.Void);
                method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, hook));
                method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
                holder.Methods.Add(method);
            }
        }
        return new HookTargetSpec(declaring.FullName, holder.FullName, target.Name, eventName, target.IsStatic, false, target.FullName);
    }

    static TypeDefinition MakeDelegate(ModuleDefinition module, string name, TypeReference returnType,
        TypeReference self, IEnumerable<ParameterDefinition> parameters, TypeReference firstParameter = null)
    {
        var multicast = module.ImportReference(typeof(MulticastDelegate));
        var type = new TypeDefinition("", name, TypeAttributes.NestedPublic | TypeAttributes.Sealed, multicast);
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void) { ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed };
        ctor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, module.TypeSystem.Object));
        ctor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, module.TypeSystem.IntPtr));
        type.Methods.Add(ctor);
        var invoke = new MethodDefinition("Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            returnType) { ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed };
        if (firstParameter != null)
            invoke.Parameters.Add(new ParameterDefinition("orig", ParameterAttributes.None, firstParameter));
        if (self != null)
            invoke.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, module.ImportReference(self)));
        foreach (var p in parameters)
            invoke.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes & (ParameterAttributes.Out | ParameterAttributes.In),
                module.ImportReference(p.ParameterType)));
        type.Methods.Add(invoke);
        return type;
    }

    /// <summary>Adds TerrariaHooks.Lowered holders for every runtime detour target to the On
    /// pass. Existing On_ events already present in the list are reused, not duplicated.</summary>
    static void AddRuntimeDetourSpecs(List<HookTargetSpec> activeHooks, string inventoryPath, ModuleDefinition hooks, string tmlPath)
    {
        var tml = ModuleDefinition.ReadModule(tmlPath, new ReaderParameters { ReadWrite = false });
        foreach (var lowered in LoadRuntimeDetourTargets(inventoryPath, tml))
        {
            var spec = ResolveRuntimeHolder(hooks, lowered.Target);
            if (activeHooks.All(s => s.HookTypeName != spec.HookTypeName || s.EventName != spec.EventName))
                activeHooks.Add(spec);
            Console.WriteLine($"Runtime detour target {lowered.Target.FullName} -> {spec.HookTypeName}::{spec.EventName} ({lowered.DetourMethods.Count} detour method(s))");
        }
    }

    /// <summary>ILoweredDetour and LoweredDetours.Find in MonoMod.RuntimeDetour (idempotent).</summary>
    static (TypeDefinition Api, MethodDefinition Find) EnsureLoweredDetourApi(ModuleDefinition rd)
    {
        var api = rd.GetType(LoweredApiNamespace, "ILoweredDetour");
        if (api == null)
        {
            api = new TypeDefinition(LoweredApiNamespace, "ILoweredDetour",
                TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
            foreach (string name in new[] { "Apply", "Undo" })
                api.Methods.Add(new MethodDefinition(name,
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.Virtual,
                    rd.TypeSystem.Void));
            rd.Types.Add(api);
        }
        var holder = rd.GetType(LoweredApiNamespace, "LoweredDetours");
        if (holder != null)
            return (api, holder.Methods.First(m => m.Name == "Find"));
        holder = new TypeDefinition(LoweredApiNamespace, "LoweredDetours",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, rd.TypeSystem.Object);
        rd.Types.Add(holder);
        var find = new MethodDefinition("Find", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, api);
        find.Parameters.Add(new ParameterDefinition("source", ParameterAttributes.None, rd.ImportReference(typeof(MethodBase))));
        find.Parameters.Add(new ParameterDefinition("detour", ParameterAttributes.None, rd.ImportReference(typeof(MethodInfo))));
        find.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, rd.TypeSystem.Object));
        holder.Methods.Add(find);
        // Signature(MethodBase): "Declaring.Type+Nested::Name(Param.Type,Param.Type&)", the same
        // string the build bakes from Cecil. Avoids ldtoken of (possibly internal) tML targets.
        var signature = new MethodDefinition("Signature", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, rd.TypeSystem.String);
        signature.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, rd.ImportReference(typeof(MethodBase))));
        holder.Methods.Add(signature);
        {
            var builder = new VariableDefinition(rd.ImportReference(typeof(System.Text.StringBuilder)));
            var parameters = new VariableDefinition(rd.ImportReference(typeof(ParameterInfo[])));
            var index = new VariableDefinition(rd.TypeSystem.Int32);
            signature.Body.Variables.Add(builder);
            signature.Body.Variables.Add(parameters);
            signature.Body.Variables.Add(index);
            signature.Body.InitLocals = true;
            var sil = signature.Body.GetILProcessor();
            var appendString = rd.ImportReference(typeof(System.Text.StringBuilder).GetMethod("Append", new[] { typeof(string) }));
            var appendChar = rd.ImportReference(typeof(System.Text.StringBuilder).GetMethod("Append", new[] { typeof(char) }));
            var typeFullName = rd.ImportReference(typeof(Type).GetProperty("FullName")!.GetMethod);
            sil.Append(sil.Create(OpCodes.Newobj, rd.ImportReference(typeof(System.Text.StringBuilder).GetConstructor(Type.EmptyTypes))));
            sil.Append(sil.Create(OpCodes.Stloc, builder));
            sil.Append(sil.Create(OpCodes.Ldloc, builder));
            sil.Append(sil.Create(OpCodes.Ldarg_0));
            sil.Append(sil.Create(OpCodes.Callvirt, rd.ImportReference(typeof(MemberInfo).GetProperty("DeclaringType")!.GetMethod)));
            sil.Append(sil.Create(OpCodes.Callvirt, typeFullName));
            sil.Append(sil.Create(OpCodes.Callvirt, appendString));
            sil.Append(sil.Create(OpCodes.Ldstr, "::"));
            sil.Append(sil.Create(OpCodes.Callvirt, appendString));
            sil.Append(sil.Create(OpCodes.Ldarg_0));
            sil.Append(sil.Create(OpCodes.Callvirt, rd.ImportReference(typeof(MemberInfo).GetProperty("Name")!.GetMethod)));
            sil.Append(sil.Create(OpCodes.Callvirt, appendString));
            sil.Append(sil.Create(OpCodes.Ldc_I4, '('));
            sil.Append(sil.Create(OpCodes.Callvirt, appendChar));
            sil.Append(sil.Create(OpCodes.Pop));
            sil.Append(sil.Create(OpCodes.Ldarg_0));
            sil.Append(sil.Create(OpCodes.Callvirt, rd.ImportReference(typeof(MethodBase).GetMethod("GetParameters", Type.EmptyTypes))));
            sil.Append(sil.Create(OpCodes.Stloc, parameters));
            var check = sil.Create(OpCodes.Ldloc, index);
            var body = sil.Create(OpCodes.Ldloc, index);
            sil.Append(sil.Create(OpCodes.Br, check));
            sil.Append(body);
            var noComma = sil.Create(OpCodes.Ldloc, builder);
            sil.Append(sil.Create(OpCodes.Brfalse, noComma));
            sil.Append(sil.Create(OpCodes.Ldloc, builder));
            sil.Append(sil.Create(OpCodes.Ldc_I4, ','));
            sil.Append(sil.Create(OpCodes.Callvirt, appendChar));
            sil.Append(sil.Create(OpCodes.Pop));
            sil.Append(noComma);
            sil.Append(sil.Create(OpCodes.Ldloc, parameters));
            sil.Append(sil.Create(OpCodes.Ldloc, index));
            sil.Append(sil.Create(OpCodes.Ldelem_Ref));
            sil.Append(sil.Create(OpCodes.Callvirt, rd.ImportReference(typeof(ParameterInfo).GetProperty("ParameterType")!.GetMethod)));
            sil.Append(sil.Create(OpCodes.Callvirt, typeFullName));
            sil.Append(sil.Create(OpCodes.Callvirt, appendString));
            sil.Append(sil.Create(OpCodes.Pop));
            sil.Append(sil.Create(OpCodes.Ldloc, index));
            sil.Append(sil.Create(OpCodes.Ldc_I4_1));
            sil.Append(sil.Create(OpCodes.Add));
            sil.Append(sil.Create(OpCodes.Stloc, index));
            sil.Append(check);
            sil.Append(sil.Create(OpCodes.Ldloc, parameters));
            sil.Append(sil.Create(OpCodes.Ldlen));
            sil.Append(sil.Create(OpCodes.Conv_I4));
            sil.Append(sil.Create(OpCodes.Blt, body));
            sil.Append(sil.Create(OpCodes.Ldloc, builder));
            sil.Append(sil.Create(OpCodes.Ldc_I4, ')'));
            sil.Append(sil.Create(OpCodes.Callvirt, appendChar));
            sil.Append(sil.Create(OpCodes.Callvirt, rd.ImportReference(typeof(object).GetMethod("ToString"))));
            sil.Append(sil.Create(OpCodes.Ret));
        }

        // Find: detour.DeclaringType's nested LoweredDetourFactory.Create(Signature(source), detour, target).
        var factory = new VariableDefinition(rd.ImportReference(typeof(Type)));
        var create = new VariableDefinition(rd.ImportReference(typeof(MethodInfo)));
        var arguments = new VariableDefinition(rd.ImportReference(typeof(object[])));
        find.Body.Variables.Add(factory);
        find.Body.Variables.Add(create);
        find.Body.Variables.Add(arguments);
        find.Body.InitLocals = true;
        var il = find.Body.GetILProcessor();
        var none = il.Create(OpCodes.Ldnull);
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Brfalse, none));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Callvirt, rd.ImportReference(typeof(MemberInfo).GetProperty("DeclaringType")!.GetMethod)));
        il.Append(il.Create(OpCodes.Dup));
        var haveType = il.Create(OpCodes.Ldstr, FactoryTypeName);
        il.Append(il.Create(OpCodes.Brtrue, haveType));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Br, none));
        il.Append(haveType);
        il.Append(il.Create(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.NonPublic)));
        il.Append(il.Create(OpCodes.Callvirt, rd.ImportReference(typeof(Type).GetMethod("GetNestedType", new[] { typeof(string), typeof(BindingFlags) }))));
        il.Append(il.Create(OpCodes.Stloc, factory));
        il.Append(il.Create(OpCodes.Ldloc, factory));
        il.Append(il.Create(OpCodes.Brfalse, none));
        il.Append(il.Create(OpCodes.Ldloc, factory));
        il.Append(il.Create(OpCodes.Ldstr, "Create"));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)));
        il.Append(il.Create(OpCodes.Callvirt, rd.ImportReference(typeof(Type).GetMethod("GetMethod", new[] { typeof(string), typeof(BindingFlags) }))));
        il.Append(il.Create(OpCodes.Stloc, create));
        il.Append(il.Create(OpCodes.Ldloc, create));
        il.Append(il.Create(OpCodes.Brfalse, none));
        il.Append(il.Create(OpCodes.Ldc_I4_3));
        il.Append(il.Create(OpCodes.Newarr, rd.TypeSystem.Object));
        il.Append(il.Create(OpCodes.Stloc, arguments));
        il.Append(il.Create(OpCodes.Ldloc, arguments));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, signature));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        for (int i = 1; i < 3; i++)
        {
            il.Append(il.Create(OpCodes.Ldloc, arguments));
            il.Append(il.Create(OpCodes.Ldc_I4, i));
            il.Append(il.Create(OpCodes.Ldarg, find.Parameters[i]));
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }
        il.Append(il.Create(OpCodes.Ldloc, create));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldloc, arguments));
        il.Append(il.Create(OpCodes.Callvirt, rd.ImportReference(typeof(MethodBase).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) }))));
        il.Append(il.Create(OpCodes.Isinst, api));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(none);
        il.Append(il.Create(OpCodes.Ret));
        return (api, find);
    }

    /// <summary>Hook binds to the lowered chain instead of MonoMod.Core: every constructor
    /// resolves the build-time adapter; Apply/Undo/IsApplied/Dispose drive it. A Hook whose
    /// detour was not lowered at build time throws on Apply.</summary>
    static void PatchHookToLoweredChains(ModuleDefinition rd, MethodReference notSupportedCtor, MethodReference getName, MethodReference concat)
    {
        var (api, find) = EnsureLoweredDetourApi(rd);
        var hook = rd.GetType("MonoMod.RuntimeDetour.Hook");
        var lowered = new FieldDefinition("__lowered", FieldAttributes.Private | FieldAttributes.InitOnly, api);
        var applied = new FieldDefinition("__applied", FieldAttributes.Private, rd.TypeSystem.Boolean);
        hook.Fields.Add(lowered);
        hook.Fields.Add(applied);
        var source = hook.Fields.Single(f => f.Name == "<Source>k__BackingField");
        var target = hook.Fields.Single(f => f.Name == "<Target>k__BackingField");
        var config = hook.Fields.Single(f => f.Name == "<Config>k__BackingField");
        var disposed = hook.Fields.Single(f => f.Name == "disposedValue");
        var checkDisposed = hook.Methods.Single(m => m.Name == "CheckDisposed");

        // No MonoMod.Core platform/factory initialization on any constructor path.
        var getDefaultFactory = rd.GetType("MonoMod.RuntimeDetour.DetourContext").Methods.Single(m => m.Name == "GetDefaultFactory");
        foreach (var ctor in hook.Methods.Where(m => m.IsConstructor && !m.IsStatic))
            foreach (var instruction in ctor.Body.Instructions.Where(i => i.Operand is MethodReference m && m.FullName == getDefaultFactory.FullName).ToList())
            {
                instruction.OpCode = OpCodes.Ldnull;
                instruction.Operand = null;
            }

        var core = hook.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 6 && m.Parameters[1].ParameterType.Name == "MethodInfo");
        Rewrite(core, il =>
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, rd.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes))));
            var argumentNull = rd.ImportReference(typeof(ArgumentNullException).GetConstructor(new[] { typeof(string) }));
            foreach (var (index, name) in new[] { (1, "source"), (2, "target") })
            {
                var present = il.Create(OpCodes.Nop);
                il.Append(il.Create(OpCodes.Ldarg, core.Parameters[index - 1]));
                il.Append(il.Create(OpCodes.Brtrue, present));
                il.Append(il.Create(OpCodes.Ldstr, name));
                il.Append(il.Create(OpCodes.Newobj, argumentNull));
                il.Append(il.Create(OpCodes.Throw));
                il.Append(present);
            }
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg, core.Parameters[4]));
            il.Append(il.Create(OpCodes.Stfld, config));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, source));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Stfld, target));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Ldarg_3));
            il.Append(il.Create(OpCodes.Call, find));
            il.Append(il.Create(OpCodes.Stfld, lowered));
            var done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg, core.Parameters[5]));
            il.Append(il.Create(OpCodes.Brfalse, done));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, hook.Methods.Single(m => m.Name == "Apply" && m.Parameters.Count == 0)));
            il.Append(done);
        });

        var apply = hook.Methods.Single(m => m.Name == "Apply" && m.Parameters.Count == 0);
        Rewrite(apply, il =>
        {
            var done = il.Create(OpCodes.Ret);
            var have = il.Create(OpCodes.Ldarg_0);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, checkDisposed));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, applied));
            il.Append(il.Create(OpCodes.Brtrue, done));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, lowered));
            il.Append(il.Create(OpCodes.Brtrue, have));
            il.Append(il.Create(OpCodes.Ldstr, "hook not lowered at build time; rebuild the mod set: "));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, source));
            il.Append(il.Create(OpCodes.Callvirt, getName));
            il.Append(il.Create(OpCodes.Call, concat));
            il.Append(il.Create(OpCodes.Newobj, notSupportedCtor));
            il.Append(il.Create(OpCodes.Throw));
            il.Append(have);
            il.Append(il.Create(OpCodes.Ldfld, lowered));
            il.Append(il.Create(OpCodes.Callvirt, api.Methods.Single(m => m.Name == "Apply")));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Stfld, applied));
            il.Append(done);
        });

        var undo = hook.Methods.Single(m => m.Name == "Undo" && m.Parameters.Count == 0);
        Rewrite(undo, il =>
        {
            var done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, checkDisposed));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, applied));
            il.Append(il.Create(OpCodes.Brfalse, done));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, lowered));
            il.Append(il.Create(OpCodes.Callvirt, api.Methods.Single(m => m.Name == "Undo")));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stfld, applied));
            il.Append(done);
        });

        Rewrite(hook.Methods.Single(m => m.Name == "get_IsApplied"), il =>
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, applied));
            il.Append(il.Create(OpCodes.Ret));
        });

        Rewrite(hook.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 1), il =>
        {
            var done = il.Create(OpCodes.Ret);
            var mark = il.Create(OpCodes.Ldarg_0);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, disposed));
            il.Append(il.Create(OpCodes.Brtrue, done));
            il.Append(il.Create(OpCodes.Call, rd.ImportReference(typeof(Environment).GetProperty("HasShutdownStarted")!.GetMethod)));
            il.Append(il.Create(OpCodes.Brtrue, mark));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, undo));
            il.Append(mark);
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Stfld, disposed));
            il.Append(done);
        });
    }

    static void Rewrite(MethodDefinition method, Action<ILProcessor> emit)
    {
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        emit(method.Body.GetILProcessor());
    }

    /// <summary>--lower-mod-detours: add adapters + factory to each mod assembly that owns a
    /// detour method. Rewrites the DLL in place only when the generated content changes.</summary>
    static int LowerModDetours(string inventoryPath, string hooksIn, string tmlIn, string detourIn, string modsDir, List<string> refDirs)
    {
        var tml = ModuleDefinition.ReadModule(tmlIn, new ReaderParameters { ReadWrite = false });
        var hooks = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(hooksIn)), new ReaderParameters { ReadWrite = false });
        var rd = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(detourIn)), new ReaderParameters { ReadWrite = false });
        var (api, _) = EnsureLoweredDetourApi(rd);
        var targets = LoadRuntimeDetourTargets(inventoryPath, tml);
        var perAssembly = new SortedDictionary<string, List<(MethodDefinition Target, HookTargetSpec Spec, string Detour)>>(StringComparer.Ordinal);
        foreach (var lowered in targets)
        {
            var spec = ResolveRuntimeHolder(hooks, lowered.Target);
            foreach (string detour in lowered.DetourMethods)
            {
                string owner = OwnerAssembly(modsDir, detour);
                if (!perAssembly.TryGetValue(owner, out var list))
                    perAssembly[owner] = list = new();
                list.Add((lowered.Target, spec, detour));
            }
        }
        int changed = 0;
        foreach (var (path, entries) in perAssembly)
        {
            byte[] original = File.ReadAllBytes(path);
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(modsDir);
            resolver.AddSearchDirectory(Path.GetDirectoryName(tmlIn));
            resolver.AddSearchDirectory(Path.GetDirectoryName(hooksIn));
            resolver.AddSearchDirectory(Path.GetDirectoryName(detourIn));
            foreach (string directory in refDirs)
                resolver.AddSearchDirectory(directory);
            var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(original), new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false });
            var module = asm.MainModule;
            foreach (var old in module.Types.SelectMany(AllTypes).Where(t => t.Name == FactoryTypeName).ToList())
                old.DeclaringType.NestedTypes.Remove(old);
            int index = 0;
            // One factory nested in each detour's declaring type: nested types may call and
            // ldtoken that type's private detour methods, which a top-level type may not.
            foreach (var group in entries.GroupBy(e => FindMethod(module, e.Detour).DeclaringType.FullName, StringComparer.Ordinal)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var owner = FindMethod(module, group.First().Detour).DeclaringType;
                if (owner.HasGenericParameters)
                    throw new NotSupportedException("Detour declared in a generic type: " + owner.FullName);
                var factory = new TypeDefinition("", FactoryTypeName,
                    TypeAttributes.NestedPrivate | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
                owner.NestedTypes.Add(factory);
                var create = new MethodDefinition("Create", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.ImportReference(api));
                create.Parameters.Add(new ParameterDefinition("source", ParameterAttributes.None, module.TypeSystem.String));
                create.Parameters.Add(new ParameterDefinition("detour", ParameterAttributes.None, module.ImportReference(typeof(MethodInfo))));
                create.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, module.TypeSystem.Object));
                factory.Methods.Add(create);
                var handleType = module.ImportReference(typeof(RuntimeMethodHandle));
                var getHandle = module.ImportReference(typeof(MethodBase).GetProperty("MethodHandle")!.GetMethod);
                var getValue = module.ImportReference(typeof(RuntimeMethodHandle).GetProperty("Value")!.GetMethod);
                var ptrEquals = module.ImportReference(typeof(IntPtr).GetMethod("op_Equality", new[] { typeof(IntPtr), typeof(IntPtr) }));
                var stringEquals = module.ImportReference(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }));
                var handle = new VariableDefinition(handleType);
                var detourPtr = new VariableDefinition(module.TypeSystem.IntPtr);
                create.Body.Variables.Add(handle);
                create.Body.Variables.Add(detourPtr);
                create.Body.InitLocals = true;
                var il = create.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_1));
                il.Append(il.Create(OpCodes.Callvirt, getHandle));
                il.Append(il.Create(OpCodes.Stloc, handle));
                il.Append(il.Create(OpCodes.Ldloca, handle));
                il.Append(il.Create(OpCodes.Call, getValue));
                il.Append(il.Create(OpCodes.Stloc, detourPtr));
                foreach (var (target, spec, detour) in group.OrderBy(e => e.Target.FullName, StringComparer.Ordinal).ThenBy(e => e.Detour, StringComparer.Ordinal))
                {
                    var detourMethod = FindMethod(module, detour);
                    var adapter = BuildAdapter(module, factory, api, hooks, spec, target, detourMethod, index++);
                    var next = il.Create(OpCodes.Nop);
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldstr, ReflectionSignature(target)));
                    il.Append(il.Create(OpCodes.Call, stringEquals));
                    il.Append(il.Create(OpCodes.Brfalse, next));
                    il.Append(il.Create(OpCodes.Ldtoken, detourMethod));
                    il.Append(il.Create(OpCodes.Stloc, handle));
                    il.Append(il.Create(OpCodes.Ldloca, handle));
                    il.Append(il.Create(OpCodes.Call, getValue));
                    il.Append(il.Create(OpCodes.Ldloc, detourPtr));
                    il.Append(il.Create(OpCodes.Call, ptrEquals));
                    il.Append(il.Create(OpCodes.Brfalse, next));
                    var ctor = adapter.Methods.Single(m => m.IsConstructor);
                    if (!detourMethod.IsStatic)
                    {
                        il.Append(il.Create(OpCodes.Ldarg_2));
                        il.Append(il.Create(OpCodes.Castclass, detourMethod.DeclaringType));
                    }
                    il.Append(il.Create(OpCodes.Newobj, ctor));
                    il.Append(il.Create(OpCodes.Ret));
                    il.Append(next);
                }
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ret));
            }

            using var written = new MemoryStream();
            module.Mvid = Guid.Empty;
            asm.Write(written, new WriterParameters { Timestamp = 0 });
            byte[] hash = System.Security.Cryptography.SHA256.HashData(written.ToArray());
            module.Mvid = new Guid(hash.AsSpan(0, 16));
            using var final = new MemoryStream();
            asm.Write(final, new WriterParameters { Timestamp = 0 });
            byte[] bytes = final.ToArray();
            if (bytes.AsSpan().SequenceEqual(original))
            {
                Console.WriteLine($"Unchanged {Path.GetFileName(path)}: {entries.Count} lowered detour(s)");
                continue;
            }
            File.WriteAllBytes(path, bytes);
            changed++;
            Console.WriteLine($"Lowered {entries.Count} detour(s) in {Path.GetFileName(path)}; MVID {module.Mvid}");
        }
        Console.WriteLine($"LOWERED_MOD_DETOURS changed={changed}");
        return 0;
    }

    static string OwnerAssembly(string modsDir, string detourFullName)
    {
        var owners = Directory.GetFiles(modsDir, "*.dll").OrderBy(p => p, StringComparer.Ordinal).Where(path =>
        {
            using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadWrite = false });
            return module.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).Any(m => m.FullName == detourFullName);
        }).ToList();
        if (owners.Count != 1)
            throw new InvalidOperationException($"Detour method must be defined by exactly one mod assembly ({owners.Count}): {detourFullName}");
        return owners[0];
    }

    static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(AllTypes));

    static MethodDefinition FindMethod(ModuleDefinition module, string fullName) =>
        module.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).Single(m => m.FullName == fullName);

    /// <summary>The string LoweredDetours.Signature(MethodBase) produces for this method.</summary>
    static string ReflectionSignature(MethodDefinition method)
    {
        foreach (var p in method.Parameters)
        {
            var type = p.ParameterType is ByReferenceType byRef ? byRef.ElementType : p.ParameterType;
            if (type.IsGenericInstance || type.ContainsGenericParameter)
                throw new NotSupportedException($"Generic parameter type in runtime detour target: {method.FullName}");
        }
        return method.DeclaringType.FullName.Replace('/', '+') + "::" + method.Name + "("
            + string.Join(",", method.Parameters.Select(p => p.ParameterType.FullName.Replace('/', '+'))) + ")";
    }

    /// <summary>Typed adapter: hook_E.Invoke(orig_E orig, args) calls the mod's detour with the
    /// mod's own orig delegate wrapping orig.Invoke, cached per chain link (immutable pair).</summary>
    static TypeDefinition BuildAdapter(ModuleDefinition module, TypeDefinition factory, TypeDefinition api, ModuleDefinition hooks, HookTargetSpec spec,
        MethodDefinition target, MethodDefinition detour, int index)
    {
        var holder = hooks.GetType(spec.HookTypeName) ?? throw new InvalidOperationException("holder " + spec.HookTypeName);
        var origDef = holder.NestedTypes.Single(t => t.Name == "orig_" + spec.EventName);
        var hookDef = holder.NestedTypes.Single(t => t.Name == "hook_" + spec.EventName);
        var origInvokeDef = origDef.Methods.Single(m => m.Name == "Invoke");
        var parameters = OrigSignature(target);
        if (detour.HasGenericParameters || detour.DeclaringType.HasGenericParameters)
            throw new NotSupportedException("Generic detour method: " + detour.FullName);
        if (detour.Parameters.Count != parameters.Count + 1 || detour.ReturnType.FullName != target.ReturnType.FullName
            || detour.Parameters.Skip(1).Select((p, i) => p.ParameterType.FullName != parameters[i].FullName).Any(x => x))
            throw new NotSupportedException($"Detour signature does not match target {target.FullName}: {detour.FullName}");
        var modOrig = detour.Parameters[0].ParameterType.Resolve();
        if (modOrig == null || modOrig.BaseType?.FullName != "System.MulticastDelegate")
            throw new NotSupportedException("Detour orig parameter is not a delegate: " + detour.FullName);
        var modOrigInvoke = modOrig.Methods.Single(m => m.Name == "Invoke");
        if (!SameSignature(modOrigInvoke, origInvokeDef.ReturnType, origInvokeDef.Parameters.Select(p => p.ParameterType).ToList()))
            throw new NotSupportedException($"Orig delegate {modOrig.FullName} does not match {target.FullName}");

        var origRef = module.ImportReference(origDef);
        var hookRef = module.ImportReference(hookDef);
        var modOrigRef = module.ImportReference(detour.Parameters[0].ParameterType);
        var origInvoke = module.ImportReference(origInvokeDef);
        var hookCtor = module.ImportReference(hookDef.Methods.Single(m => m.IsConstructor));
        var modOrigCtor = module.ImportReference(modOrig.Methods.Single(m => m.IsConstructor));
        var add = module.ImportReference(holder.Methods.Single(m => m.Name == "add_" + spec.EventName));
        var remove = module.ImportReference(holder.Methods.Single(m => m.Name == "remove_" + spec.EventName));
        var objectCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes));

        var pair = new TypeDefinition("", "Link" + index,
            TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
        var pairOrig = new FieldDefinition("Orig", FieldAttributes.Public | FieldAttributes.InitOnly, origRef);
        var pairMod = new FieldDefinition("Mod", FieldAttributes.Public | FieldAttributes.InitOnly, modOrigRef);
        pair.Fields.Add(pairOrig);
        pair.Fields.Add(pairMod);
        var pairCtor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        pairCtor.Parameters.Add(new ParameterDefinition("orig", ParameterAttributes.None, origRef));
        pairCtor.Parameters.Add(new ParameterDefinition("mod", ParameterAttributes.None, modOrigRef));
        var pil = pairCtor.Body.GetILProcessor();
        pil.Append(pil.Create(OpCodes.Ldarg_0));
        pil.Append(pil.Create(OpCodes.Call, objectCtor));
        pil.Append(pil.Create(OpCodes.Ldarg_0));
        pil.Append(pil.Create(OpCodes.Ldarg_1));
        pil.Append(pil.Create(OpCodes.Stfld, pairOrig));
        pil.Append(pil.Create(OpCodes.Ldarg_0));
        pil.Append(pil.Create(OpCodes.Ldarg_2));
        pil.Append(pil.Create(OpCodes.Stfld, pairMod));
        pil.Append(pil.Create(OpCodes.Ret));
        pair.Methods.Add(pairCtor);
        factory.NestedTypes.Add(pair);

        var adapter = new TypeDefinition("", "Detour" + index,
            TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
        adapter.Interfaces.Add(new InterfaceImplementation(module.ImportReference(api)));
        var targetField = detour.IsStatic ? null : new FieldDefinition("target", FieldAttributes.Private | FieldAttributes.InitOnly, detour.DeclaringType);
        var hookField = new FieldDefinition("hook", FieldAttributes.Private, hookRef);
        var linkField = new FieldDefinition("link", FieldAttributes.Private, pair);
        if (targetField != null) adapter.Fields.Add(targetField);
        adapter.Fields.Add(hookField);
        adapter.Fields.Add(linkField);
        factory.NestedTypes.Add(adapter);

        var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        if (targetField != null)
            ctor.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, detour.DeclaringType));
        var cil = ctor.Body.GetILProcessor();
        cil.Append(cil.Create(OpCodes.Ldarg_0));
        cil.Append(cil.Create(OpCodes.Call, objectCtor));
        if (targetField != null)
        {
            cil.Append(cil.Create(OpCodes.Ldarg_0));
            cil.Append(cil.Create(OpCodes.Ldarg_1));
            cil.Append(cil.Create(OpCodes.Stfld, targetField));
        }
        cil.Append(cil.Create(OpCodes.Ret));
        adapter.Methods.Add(ctor);

        var invoke = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig, module.ImportReference(target.ReturnType));
        invoke.Parameters.Add(new ParameterDefinition("orig", ParameterAttributes.None, origRef));
        foreach (var p in origInvokeDef.Parameters)
            invoke.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, module.ImportReference(p.ParameterType)));
        var link = new VariableDefinition(pair);
        invoke.Body.Variables.Add(link);
        invoke.Body.InitLocals = true;
        var iil = invoke.Body.GetILProcessor();
        var build = iil.Create(OpCodes.Ldarg_1);
        var call = iil.Create(OpCodes.Nop);
        iil.Append(iil.Create(OpCodes.Ldarg_0));
        iil.Append(iil.Create(OpCodes.Ldfld, linkField));
        iil.Append(iil.Create(OpCodes.Stloc, link));
        iil.Append(iil.Create(OpCodes.Ldloc, link));
        iil.Append(iil.Create(OpCodes.Brfalse, build));
        iil.Append(iil.Create(OpCodes.Ldloc, link));
        iil.Append(iil.Create(OpCodes.Ldfld, pairOrig));
        iil.Append(iil.Create(OpCodes.Ldarg_1));
        iil.Append(iil.Create(OpCodes.Beq, call));
        iil.Append(build);
        iil.Append(iil.Create(OpCodes.Ldarg_1));
        iil.Append(iil.Create(OpCodes.Dup));
        iil.Append(iil.Create(OpCodes.Ldvirtftn, origInvoke));
        iil.Append(iil.Create(OpCodes.Newobj, modOrigCtor));
        iil.Append(iil.Create(OpCodes.Newobj, pairCtor));
        iil.Append(iil.Create(OpCodes.Stloc, link));
        iil.Append(iil.Create(OpCodes.Ldarg_0));
        iil.Append(iil.Create(OpCodes.Ldloc, link));
        iil.Append(iil.Create(OpCodes.Stfld, linkField));
        iil.Append(call);
        if (targetField != null)
        {
            iil.Append(iil.Create(OpCodes.Ldarg_0));
            iil.Append(iil.Create(OpCodes.Ldfld, targetField));
        }
        iil.Append(iil.Create(OpCodes.Ldloc, link));
        iil.Append(iil.Create(OpCodes.Ldfld, pairMod));
        for (int i = 2; i < invoke.Parameters.Count + 1; i++)
            iil.Append(iil.Create(OpCodes.Ldarg, invoke.Parameters[i - 1]));
        iil.Append(iil.Create(OpCodes.Call, detour));
        iil.Append(iil.Create(OpCodes.Ret));
        adapter.Methods.Add(invoke);

        foreach (var (name, accessor) in new[] { ("Apply", add), ("Undo", remove) })
        {
            var method = new MethodDefinition(name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final,
                module.TypeSystem.Void);
            var mil = method.Body.GetILProcessor();
            var done = mil.Create(OpCodes.Ret);
            if (name == "Apply")
            {
                var have = mil.Create(OpCodes.Ldarg_0);
                mil.Append(mil.Create(OpCodes.Ldarg_0));
                mil.Append(mil.Create(OpCodes.Ldfld, hookField));
                mil.Append(mil.Create(OpCodes.Brtrue, have));
                mil.Append(mil.Create(OpCodes.Ldarg_0));
                mil.Append(mil.Create(OpCodes.Ldarg_0));
                mil.Append(mil.Create(OpCodes.Ldftn, invoke));
                mil.Append(mil.Create(OpCodes.Newobj, hookCtor));
                mil.Append(mil.Create(OpCodes.Stfld, hookField));
                mil.Append(have);
            }
            else
            {
                mil.Append(mil.Create(OpCodes.Ldarg_0));
                mil.Append(mil.Create(OpCodes.Ldfld, hookField));
                mil.Append(mil.Create(OpCodes.Brfalse, done));
                mil.Append(mil.Create(OpCodes.Ldarg_0));
            }
            mil.Append(mil.Create(OpCodes.Ldfld, hookField));
            mil.Append(mil.Create(OpCodes.Call, accessor));
            mil.Append(done);
            method.Overrides.Add(module.ImportReference(api.Methods.Single(m => m.Name == name)));
            adapter.Methods.Add(method);
        }
        return adapter;
    }
}
