using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace HookScan;

public class ModScanner
{
    private readonly TargetResolver _resolver;
    private readonly string _hooksPath;
    private readonly string _tmlPath;
    private readonly List<string> _modPaths;

    public ModScanner(TargetResolver resolver, string hooksPath, string tmlPath, List<string> modPaths)
    {
        _resolver = resolver;
        _hooksPath = hooksPath;
        _tmlPath = tmlPath;
        _modPaths = modPaths;
    }

    public HookInventory Scan()
    {
        var inventory = new HookInventory();

        // Game assemblies
        inventory.Game.TerrariaHooks = CreateAssemblyInfo(_hooksPath);
        inventory.Game.TModLoader = CreateAssemblyInfo(_tmlPath);

        // Load all mod assemblies
        var modAssemblies = new List<(string Path, AssemblyDefinition Asm)>();
        foreach (var path in _modPaths)
        {
            var asm = AssemblyDefinition.ReadAssembly(path);
            modAssemblies.Add((path, asm));
            inventory.Mods.Add(CreateModEntry(path, asm));
        }

        // Dictionaries for grouping
        var onHooksDict = new Dictionary<(string HookType, string Event), OnHookEntry>();
        var ilHooksDict = new Dictionary<string, ILHookEntry>();
        var detoursDict = new Dictionary<string, RuntimeDetourEntry>();

        // Set to track processed ILHook instructions from ManagedILEdit closures so we don't duplicate
        var handledILHooks = new HashSet<Instruction>();

        // Main scan
        foreach (var (modPath, asm) in modAssemblies)
        {
            string modName = asm.Name.Name;

            // Check assembly-level Harmony references
            foreach (var ar in asm.MainModule.AssemblyReferences)
            {
                if (ar.Name.Contains("Harmony", StringComparison.OrdinalIgnoreCase))
                {
                    inventory.Unsupported.Add(new UnsupportedEntry
                    {
                        Mod = modName,
                        Caller = $"{asm.Name.Name}::AssemblyReference",
                        IlOffset = 0,
                        ReasonCode = "HARMONY",
                        Detail = $"Assembly references {ar.Name}"
                    });
                }
            }

            foreach (var type in asm.MainModule.Types)
            {
                ScanType(type, modName, onHooksDict, ilHooksDict, detoursDict, inventory.Unsupported, handledILHooks);
            }
        }

        inventory.OnHooks = onHooksDict.Values.OrderBy(h => h.HookType).ThenBy(h => h.Event).ToList();
        inventory.ILHooks = ilHooksDict.Values.OrderBy(h => h.Target?.FullName ?? "").ToList();
        inventory.RuntimeDetours = detoursDict.Values.OrderBy(d => d.Target?.FullName ?? "").ToList();
        // Regression check: verify all direct calls to TerrariaHooks accessors are accounted for
        int totalDirectCalls = 0;
        foreach (var (modPath, asm) in modAssemblies)
        {
            foreach (var t in asm.MainModule.Types)
            {
                CountCallsInType(t, ref totalDirectCalls);
            }
        }

        // An IL-event `add_` whose manipulator cannot be resolved is recorded as UNRESOLVED_MANIPULATOR
        // instead of a registration. It is still one accessor call, so the inventory (with that
        // unsupported entry) is returned instead of failing this check.
        int totalAccountedCalls = inventory.OnHooks.Sum(h => h.Registrations.Count) +
            inventory.ILHooks.Where(h => h.Mechanism == "IL_event").Sum(h => h.Registrations.Count) +
            inventory.Unsupported.Count(u => u.ReasonCode == "UNRESOLVED_MANIPULATOR");

        if (totalDirectCalls != totalAccountedCalls)
        {
            throw new InvalidOperationException(
                $"Regression check failed: Scanned assemblies make {totalDirectCalls} calls into TerrariaHooks accessors, " +
                $"but scanner only accounted for {totalAccountedCalls} registrations!");
        }

        foreach (var hook in inventory.OnHooks)
            AddLoweringLimit(inventory.Unsupported, hook.Target, hook.Registrations, allowValueType: true);
        foreach (var detour in inventory.RuntimeDetours)
            AddLoweringLimit(inventory.Unsupported, detour.Target, detour.Registrations, allowValueType: false);
        inventory.MissingMembers = FindMissingMembers();
        return inventory;
    }

    // offline_hooks' CheckUnsupportedTarget rejects these targets when it lowers On_ hooks and runtime
    // detours; reporting them here refuses the mod before its build starts instead of in the middle.
    private void AddLoweringLimit(List<UnsupportedEntry> unsupported, HookTarget? target,
                                  List<HookRegistration> registrations, bool allowValueType)
    {
        var method = _resolver.FindTmlMethod(target);
        if (method == null)
            return;
        string? reason = method.HasGenericParameters ? "a generic method"
            : method.CallingConvention == MethodCallingConvention.VarArg ? "a vararg method"
            : method.ReturnType.IsByReference ? "a ref return"
            : method.DeclaringType.IsValueType && !allowValueType ? "a method of a value type"
            : method.Parameters.Any(p => p.ParameterType.IsGenericParameter) ? "a generic parameter"
            : null;
        if (reason == null)
            return;
        foreach (var registration in registrations.Where(r => r.Op != "remove").GroupBy(r => r.Mod).Select(g => g.First()))
        {
            unsupported.Add(new UnsupportedEntry
            {
                Mod = registration.Mod,
                Caller = registration.Caller,
                IlOffset = registration.IlOffset,
                ReasonCode = "UNSUPPORTED_TARGET",
                Detail = $"{target!.FullName}: offline hook lowering does not support {reason}"
            });
        }
    }

    // The game-facing assemblies a mod is compiled against. A mod built for an older tModLoader can
    // reference members this build's assemblies no longer have; those references fail only when the
    // method holding them runs, so they are reported here instead of being found on the console.
    private static readonly string[] ApiAssemblies = { "tModLoader", "FNA", "ReLogic", "TerrariaHooks" };

    private sealed class ScanAssemblyResolver : DefaultAssemblyResolver
    {
        public void Register(AssemblyDefinition assembly) => RegisterAssembly(assembly);
    }

    // A separate read with its own resolver, so the hook scan above keeps its resolution behaviour.
    private List<MissingMemberEntry> FindMissingMembers()
    {
        var resolver = new ScanAssemblyResolver();
        foreach (var directory in new[] { _hooksPath, _tmlPath }.Concat(_modPaths)
                     .Select(p => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(p))!).Distinct())
            resolver.AddSearchDirectory(directory);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        // NxFix output is not named tModLoader.dll, so the game assemblies are registered by identity.
        resolver.Register(AssemblyDefinition.ReadAssembly(_tmlPath, parameters));
        resolver.Register(AssemblyDefinition.ReadAssembly(_hooksPath, parameters));
        var mods = _modPaths.Select(p => AssemblyDefinition.ReadAssembly(p, parameters)).ToList();
        foreach (var mod in mods)
            resolver.Register(mod);
        var api = new HashSet<string>(ApiAssemblies, StringComparer.OrdinalIgnoreCase);
        api.UnionWith(mods.Select(m => m.Name.Name));

        var missing = new Dictionary<(string Mod, string Member), MissingMemberEntry>();
        foreach (var mod in mods)
        {
            string modName = mod.Name.Name;
            void Check(MemberReference? reference, string caller)
            {
                // Get/Set/Address/.ctor of a multi-dimensional array are runtime methods with no definition.
                if (reference is MethodReference { DeclaringType: ArrayType })
                    return;
                var type = reference switch
                {
                    TypeReference t => t,
                    MethodReference m => m.DeclaringType,
                    FieldReference f => f.DeclaringType,
                    _ => null,
                };
                type = type?.GetElementType();
                if (type == null || type is GenericParameter)
                    return;
                while (type.DeclaringType != null)
                    type = type.DeclaringType;
                // TerrariaHooks.Lowered is generated by this build (offline_hooks), after the first scan.
                if (type.Scope is not AssemblyNameReference scope || !api.Contains(scope.Name) || scope.Name == modName
                    || type.Namespace == "TerrariaHooks.Lowered")
                    return;
                bool resolved;
                try
                {
                    resolved = reference switch
                    {
                        TypeReference t => t.GetElementType().Resolve() != null,
                        MethodReference m => m.Resolve() != null,
                        FieldReference f => f.Resolve() != null,
                        _ => true,
                    };
                }
                catch (AssemblyResolutionException)
                {
                    resolved = false;
                }
                if (resolved)
                    return;
                string member = reference is TypeReference typeReference ? typeReference.GetElementType().FullName : reference!.FullName;
                if (!missing.TryGetValue((modName, member), out var entry))
                {
                    entry = new MissingMemberEntry { Mod = modName, Member = member, Assembly = scope.Name, FirstCaller = caller };
                    missing[(modName, member)] = entry;
                }
                entry.Sites++;
            }

            foreach (var type in AllTypes(mod.MainModule.Types))
            {
                Check(type.BaseType, type.FullName);
                foreach (var implemented in type.Interfaces)
                    Check(implemented.InterfaceType, type.FullName);
                foreach (var field in type.Fields)
                    Check(field.FieldType, field.FullName);
                foreach (var method in type.Methods)
                {
                    Check(method.ReturnType, method.FullName);
                    foreach (var parameter in method.Parameters)
                        Check(parameter.ParameterType, method.FullName);
                    if (!method.HasBody)
                        continue;
                    foreach (var instruction in method.Body.Instructions)
                        Check(instruction.Operand as MemberReference, method.FullName);
                }
            }
        }
        return missing.Values.OrderBy(e => e.Mod).ThenBy(e => e.Member).ToList();
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private void CountCallsInType(TypeDefinition td, ref int count)
    {
        if (td.FullName == "Luminance.Core.Hooking.HookHelper" || IsBuildGenerated(td)) return;

        foreach (var m in td.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var inst in m.Body.Instructions)
            {
                if ((inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt) && 
                    inst.Operand is MethodReference mr && mr.DeclaringType != null)
                {
                    bool isTerrariaHooks = (mr.DeclaringType.Scope?.Name?.Contains("TerrariaHooks") == true) ||
                                          mr.DeclaringType.FullName.Contains(".On_") ||
                                          mr.DeclaringType.FullName.Contains(".IL_") ||
                                          mr.DeclaringType.FullName.StartsWith("On_") ||
                                          mr.DeclaringType.FullName.StartsWith("IL_");
                    if (isTerrariaHooks && (mr.Name.StartsWith("add_") || mr.Name.StartsWith("remove_")))
                    {
                        count++;
                    }
                }
            }
        }
        foreach (var nt in td.NestedTypes)
        {
            CountCallsInType(nt, ref count);
        }
    }

    private void ScanType(
        TypeDefinition type, 
        string modName, 
        Dictionary<(string HookType, string Event), OnHookEntry> onHooksDict,
        Dictionary<string, ILHookEntry> ilHooksDict,
        Dictionary<string, RuntimeDetourEntry> detoursDict,
        List<UnsupportedEntry> unsupported,
        HashSet<Instruction> handledILHooks)
    {
        // Adapters emitted by offline_hooks --lower-mod-detours implement registrations that are
        // already inventoried as runtime_detours; they are not new mod registrations.
        if (IsBuildGenerated(type)) return;
        foreach (var m in type.Methods)
        {
            if (!m.HasBody) continue;
            ScanMethod(m, modName, onHooksDict, ilHooksDict, detoursDict, unsupported, handledILHooks);
        }

        foreach (var nt in type.NestedTypes)
        {
            ScanType(nt, modName, onHooksDict, ilHooksDict, detoursDict, unsupported, handledILHooks);
        }
    }

    // offline_hooks --lower-mod-detours nests LoweredDetourFactory (and its adapters) in the type
    // that declares each detour method.
    private static bool IsBuildGenerated(TypeDefinition type) => type.Name == "LoweredDetourFactory";

    private void ScanMethod(
        MethodDefinition method, 
        string modName, 
        Dictionary<(string HookType, string Event), OnHookEntry> onHooksDict,
        Dictionary<string, ILHookEntry> ilHooksDict,
        Dictionary<string, RuntimeDetourEntry> detoursDict,
        List<UnsupportedEntry> unsupported,
        HashSet<Instruction> handledILHooks)
    {
        // Skip scanning the internals of HookHelper in Luminance since it defines the wrapper APIs
        if (method.DeclaringType.FullName == "Luminance.Core.Hooking.HookHelper") return;

        var heights = MethodBaseResolver.ComputeStackHeights(method);
        foreach (var inst in method.Body.Instructions)
        {
            // 1. Harmony check
            if (IsHarmonyReference(inst, out string harmonyDetail))
            {
                unsupported.Add(new UnsupportedEntry
                {
                    Mod = modName,
                    Caller = method.FullName,
                    IlOffset = inst.Offset,
                    ReasonCode = "HARMONY",
                    Detail = harmonyDetail
                });
            }

            // 2. Reflection.Emit check
            if (IsReflectionEmit(inst, out string emitDetail))
            {
                unsupported.Add(new UnsupportedEntry
                {
                    Mod = modName,
                    Caller = method.FullName,
                    IlOffset = inst.Offset,
                    ReasonCode = "REFLECTION_EMIT",
                    Detail = emitDetail
                });
            }

            // 3. NativeDetour check
            if (inst.OpCode == OpCodes.Newobj && inst.Operand is MethodReference ctorNative && 
                ctorNative.DeclaringType.FullName.Contains("NativeDetour"))
            {
                unsupported.Add(new UnsupportedEntry
                {
                    Mod = modName,
                    Caller = method.FullName,
                    IlOffset = inst.Offset,
                    ReasonCode = "NATIVE_DETOUR",
                    Detail = $"Direct instantiation of NativeDetour: {ctorNative.FullName}"
                });

                string key = $"NativeDetour_{method.FullName}_{inst.Offset}";
                detoursDict[key] = new RuntimeDetourEntry
                {
                    Mechanism = "NativeDetour",
                    Target = null,
                    TargetResolution = "unresolved",
                    Registrations = new List<HookRegistration>
                    {
                        new HookRegistration { Mod = modName, Caller = method.FullName, IlOffset = inst.Offset }
                    }
                };
            }

            // 4. Calls: Event accessors, MonoModHooks, Library wrappers
            if (inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt)
            {
                if (inst.Operand is MethodReference mr)
                {
                    string decl = mr.DeclaringType.FullName;

                    bool isTerrariaHooks = (mr.DeclaringType.Scope?.Name?.Contains("TerrariaHooks") == true) ||
                                          mr.DeclaringType.FullName.Contains(".On_") ||
                                          mr.DeclaringType.FullName.Contains(".IL_") ||
                                          mr.DeclaringType.FullName.StartsWith("On_") ||
                                          mr.DeclaringType.FullName.StartsWith("IL_");

                    bool isOn = mr.DeclaringType.Name.StartsWith("On_") || mr.DeclaringType.FullName.Contains(".On_") || mr.DeclaringType.FullName.Contains("/On_");
                    bool isIL = mr.DeclaringType.Name.StartsWith("IL_") || mr.DeclaringType.FullName.Contains(".IL_") || mr.DeclaringType.FullName.Contains("/IL_");

                    // A. On_* event accessors
                    if (isTerrariaHooks && isOn && (mr.Name.StartsWith("add_") || mr.Name.StartsWith("remove_")))
                    {
                        string op = mr.Name.StartsWith("add_") ? "add" : "remove";
                        string evName = mr.Name.Substring(op == "add" ? 4 : 7);
                        var target = _resolver.ResolveEvent(decl, evName);

                        var key = (decl, evName);
                        if (!onHooksDict.TryGetValue(key, out var entry))
                        {
                            entry = new OnHookEntry
                            {
                                HookType = decl,
                                Event = evName,
                                Target = target,
                                Registrations = new List<HookRegistration>()
                            };
                            onHooksDict[key] = entry;
                        }

                        entry.Registrations.Add(new HookRegistration
                        {
                            Mod = modName,
                            Caller = method.FullName,
                            IlOffset = inst.Offset,
                            Op = op
                        });
                    }
                    // B. IL_* event accessors
                    else if (isTerrariaHooks && isIL && (mr.Name.StartsWith("add_") || mr.Name.StartsWith("remove_")))
                    {
                        string op = mr.Name.StartsWith("add_") ? "add" : "remove";
                            string evName = mr.Name.Substring(op == "add" ? 4 : 7);
                            var target = _resolver.ResolveEvent(decl, evName);

                            var manipProducer = MethodBaseResolver.FindArgumentProducer(method, inst, 0, 1, heights);
                            var manipMethod = MethodBaseResolver.ResolveDelegateMethod(method, manipProducer, heights);

                            // Resolve through wrapper if it's SubscriptionWrapper
                            if (manipMethod != null && manipMethod.DeclaringType.FullName == "Luminance.Core.Hooking.ManagedILEdit" && manipMethod.Name == "SubscriptionWrapper")
                            {
                                var performEdit = method.DeclaringType.Methods.FirstOrDefault(m => m.Name == "PerformEdit");
                                if (performEdit != null)
                                {
                                    manipMethod = performEdit;
                                }
                            }

                            string? manip = manipMethod?.FullName;
                            bool usesEmitDelegate = MethodBaseResolver.UsesEmitDelegate(manipMethod?.Resolve());
                            bool? isPure = manipMethod != null ? true : null;

                            string key = $"IL_event_{target.FullName}";
                            if (!ilHooksDict.TryGetValue(key, out var entry))
                            {
                                if (op == "add" && manip == null)
                                {
                                    unsupported.Add(new UnsupportedEntry
                                    {
                                        Mod = modName,
                                        Caller = method.FullName,
                                        IlOffset = inst.Offset,
                                        ReasonCode = "UNRESOLVED_MANIPULATOR",
                                        Detail = $"Could not statically resolve manipulator for IL hook on {target.FullName}"
                                    });
                                    continue;
                                }

                                entry = new ILHookEntry
                                {
                                    Mechanism = "IL_event",
                                    Target = target,
                                    Manipulator = manip,
                                    UsesEmitDelegate = usesEmitDelegate,
                                    IsPure = isPure,
                                    Registrations = new List<HookRegistration>()
                                };
                                ilHooksDict[key] = entry;
                            }
                            else
                            {
                                if (entry.Manipulator == null && manip != null)
                                {
                                    entry.Manipulator = manip;
                                    entry.UsesEmitDelegate = usesEmitDelegate;
                                    entry.IsPure = isPure;
                                }
                            }

                            entry.Registrations.Add(new HookRegistration
                            {
                                Mod = modName,
                                Caller = method.FullName,
                                IlOffset = inst.Offset,
                                Op = op
                            });
                    }

                    // C. MonoModHooks.Add
                    else if (decl == "Terraria.ModLoader.MonoModHooks" && mr.Name == "Add")
                    {
                        var arg0 = MethodBaseResolver.FindArgumentProducer(method, inst, 0, 2, heights);
                        var detourMethod = MethodBaseResolver.ResolveDelegateMethod(method,
                            MethodBaseResolver.FindArgumentProducer(method, inst, 1, 2, heights), heights);
                        bool resolved = MethodBaseResolver.TryResolveMethodBase(method, arg0, heights, out string tType, out string tMethod);
                        HookTarget? target = resolved ? _resolver.ResolveDirectTarget(tType, tMethod) : null;

                        // One entry per registration site: offsets alone collide across methods/mods.
                        string key = $"MonoModHooks.Add_{modName}_{method.FullName}_{tType}_{tMethod}_{inst.Offset}";
                        var entry = new RuntimeDetourEntry
                        {
                            Mechanism = "MonoModHooks.Add",
                            Target = target,
                            TargetResolution = target != null ? "static" : "unresolved",
                            Registrations = new List<HookRegistration>
                            {
                                new HookRegistration { Mod = modName, Caller = method.FullName, IlOffset = inst.Offset, DetourMethod = detourMethod?.FullName }
                            }
                        };
                        detoursDict[key] = entry;

                        if (target == null || detourMethod == null)
                        {
                            unsupported.Add(new UnsupportedEntry
                            {
                                Mod = modName,
                                Caller = method.FullName,
                                IlOffset = inst.Offset,
                                ReasonCode = target == null ? "UNRESOLVED_TARGET" : "UNRESOLVED_DETOUR_METHOD",
                                Detail = target == null ? "Unresolved MethodBase argument in MonoModHooks.Add"
                                    : "Detour delegate does not statically bind one method in MonoModHooks.Add"
                            });
                        }
                    }

                    // D. MonoModHooks.Modify
                    else if (decl == "Terraria.ModLoader.MonoModHooks" && mr.Name == "Modify")
                    {
                        var arg0 = MethodBaseResolver.FindArgumentProducer(method, inst, 0, 2, heights);
                        bool resolved = MethodBaseResolver.TryResolveMethodBase(method, arg0, heights, out string tType, out string tMethod);
                        HookTarget? target = resolved ? _resolver.ResolveDirectTarget(tType, tMethod) : null;

                        var arg1 = MethodBaseResolver.FindArgumentProducer(method, inst, 1, 2, heights);
                        var manipMethod = MethodBaseResolver.ResolveDelegateMethod(method, arg1, heights);
                        bool usesEmitDelegate = MethodBaseResolver.UsesEmitDelegate(manipMethod?.Resolve());

                        string key = $"MonoModHooks.Modify_{target?.FullName ?? inst.Offset.ToString()}";
                        var entry = new ILHookEntry
                        {
                            Mechanism = "MonoModHooks.Modify",
                            Target = target,
                            Manipulator = manipMethod?.FullName,
                            UsesEmitDelegate = usesEmitDelegate,
                            Registrations = new List<HookRegistration>
                            {
                                new HookRegistration { Mod = modName, Caller = method.FullName, IlOffset = inst.Offset, Op = "add" }
                            }
                        };
                        ilHooksDict[key] = entry;

                        if (target == null)
                        {
                            unsupported.Add(new UnsupportedEntry
                            {
                                Mod = modName,
                                Caller = method.FullName,
                                IlOffset = inst.Offset,
                                ReasonCode = "UNRESOLVED_TARGET",
                                Detail = $"Unresolved MethodBase argument in MonoModHooks.Modify"
                            });
                        }
                    }

                    // E. Library wrappers: HookHelper.ModifyMethodWithDetour / ModifyMethodWithIL
                    else if (decl.Contains("HookHelper") && mr.Name == "ModifyMethodWithDetour")
                    {
                        var arg1 = MethodBaseResolver.FindArgumentProducer(method, inst, 1, 2, heights);
                        var detourMethod = MethodBaseResolver.ResolveDelegateMethod(method, arg1, heights);

                        var arg0 = MethodBaseResolver.FindArgumentProducer(method, inst, 0, 2, heights);
                        bool resolved = MethodBaseResolver.TryResolveMethodBase(method, arg0, heights, out string tType, out string tMethod);
                        HookTarget? target = resolved ? _resolver.ResolveDirectTarget(tType, tMethod, detourMethod) : null;

                        string scopeName = mr.DeclaringType.Scope?.Name ?? "Luminance";
                        if (scopeName.EndsWith(".dll")) scopeName = scopeName.Substring(0, scopeName.Length - 4);
                        string mech = $"library:{scopeName}.{mr.DeclaringType.Name}.{mr.Name}";
                        string key = $"{mech}_{tType}_{tMethod}";
                        if (!detoursDict.TryGetValue(key, out var entry))
                        {
                            entry = new RuntimeDetourEntry
                            {
                                Mechanism = mech,
                                Target = target,
                                TargetResolution = target != null ? "static" : "unresolved",
                                Registrations = new List<HookRegistration>()
                            };
                            detoursDict[key] = entry;
                        }

                        entry.Registrations.Add(new HookRegistration
                        {
                            Mod = modName,
                            Caller = method.FullName,
                            IlOffset = inst.Offset,
                            DetourMethod = detourMethod?.FullName
                        });

                        if (target == null || detourMethod == null)
                        {
                            unsupported.Add(new UnsupportedEntry
                            {
                                Mod = modName,
                                Caller = method.FullName,
                                IlOffset = inst.Offset,
                                ReasonCode = target == null ? "UNRESOLVED_TARGET" : "UNRESOLVED_DETOUR_METHOD",
                                Detail = target == null ? $"Unresolved MethodBase in {mech}"
                                    : $"Detour delegate does not statically bind one method in {mech}"
                            });
                        }
                    }
                    else if (decl.Contains("HookHelper") && mr.Name == "ModifyMethodWithIL")
                    {
                        var arg0 = MethodBaseResolver.FindArgumentProducer(method, inst, 0, 2, heights);
                        bool resolved = MethodBaseResolver.TryResolveMethodBase(method, arg0, heights, out string tType, out string tMethod);
                        HookTarget? target = resolved ? _resolver.ResolveDirectTarget(tType, tMethod) : null;

                        var arg1 = MethodBaseResolver.FindArgumentProducer(method, inst, 1, 2, heights);
                        var manipMethod = MethodBaseResolver.ResolveDelegateMethod(method, arg1, heights);
                        bool usesEmitDelegate = MethodBaseResolver.UsesEmitDelegate(manipMethod?.Resolve());

                        string scopeName = mr.DeclaringType.Scope?.Name ?? "Luminance";
                        if (scopeName.EndsWith(".dll")) scopeName = scopeName.Substring(0, scopeName.Length - 4);
                        string mech = $"library:{scopeName}.{mr.DeclaringType.Name}.{mr.Name}";
                        string key = $"{mech}_{target?.FullName ?? inst.Offset.ToString()}";
                        if (!ilHooksDict.TryGetValue(key, out var entry))
                        {
                            entry = new ILHookEntry
                            {
                                Mechanism = mech,
                                Target = target,
                                Manipulator = manipMethod?.FullName,
                                UsesEmitDelegate = usesEmitDelegate,
                                Registrations = new List<HookRegistration>()
                            };
                            ilHooksDict[key] = entry;
                        }

                        entry.Registrations.Add(new HookRegistration
                        {
                            Mod = modName,
                            Caller = method.FullName,
                            IlOffset = inst.Offset,
                            Op = "add"
                        });

                        if (target == null)
                        {
                            unsupported.Add(new UnsupportedEntry
                            {
                                Mod = modName,
                                Caller = method.FullName,
                                IlOffset = inst.Offset,
                                ReasonCode = "UNRESOLVED_TARGET",
                                Detail = $"Unresolved MethodBase in {mech}"
                            });
                        }
                    }
                }
            }

            // 5. Direct new Hook / new Detour / new ILHook
            if (inst.OpCode == OpCodes.Newobj && inst.Operand is MethodReference newobjMr)
            {
                string decl = newobjMr.DeclaringType.FullName;
                if (decl == "MonoMod.RuntimeDetour.Hook" || decl == "MonoMod.RuntimeDetour.Detour")
                {
                    string mech = decl.EndsWith("Hook") ? "new Hook" : "Detour";
                    var arg1 = MethodBaseResolver.FindArgumentProducer(method, inst, 1, newobjMr.Parameters.Count, heights);
                    var detourMethod = MethodBaseResolver.ResolveDelegateMethod(method, arg1, heights);

                    var arg0 = MethodBaseResolver.FindArgumentProducer(method, inst, 0, newobjMr.Parameters.Count, heights);
                    bool resolved = MethodBaseResolver.TryResolveMethodBase(method, arg0, heights, out string tType, out string tMethod);
                    HookTarget? target = resolved ? _resolver.ResolveDirectTarget(tType, tMethod, detourMethod) : null;

                    // One entry per registration site: offsets alone collide across methods/mods
                    // (SerousCommonLib and MagicStorage both register `new Hook` at IL_0012).
                    string key = $"{mech}_{modName}_{method.FullName}_{tType}_{tMethod}_{inst.Offset}";
                    detoursDict[key] = new RuntimeDetourEntry
                    {
                        Mechanism = mech,
                        Target = target,
                        TargetResolution = target != null ? "static" : "unresolved",
                        Registrations = new List<HookRegistration>
                        {
                            new HookRegistration { Mod = modName, Caller = method.FullName, IlOffset = inst.Offset, DetourMethod = detourMethod?.FullName }
                        }
                    };

                    if (target == null || (mech == "new Hook" && detourMethod == null))
                    {
                        unsupported.Add(new UnsupportedEntry
                        {
                            Mod = modName,
                            Caller = method.FullName,
                            IlOffset = inst.Offset,
                            ReasonCode = target == null ? "UNRESOLVED_TARGET" : "UNRESOLVED_DETOUR_METHOD",
                            Detail = target == null ? $"Unresolved MethodBase in {mech}"
                                : $"Detour delegate does not statically bind one method in {mech}"
                        });
                    }
                }
                else if (decl == "MonoMod.RuntimeDetour.ILHook")
                {
                    var arg0 = MethodBaseResolver.FindArgumentProducer(method, inst, 0, newobjMr.Parameters.Count, heights);
                    bool resolved = MethodBaseResolver.TryResolveMethodBase(method, arg0, heights, out string tType, out string tMethod);
                    HookTarget? target = resolved ? _resolver.ResolveDirectTarget(tType, tMethod) : null;

                    var arg1 = MethodBaseResolver.FindArgumentProducer(method, inst, 1, newobjMr.Parameters.Count, heights);
                    var manipMethod = MethodBaseResolver.ResolveDelegateMethod(method, arg1, heights);
                    bool usesEmitDelegate = MethodBaseResolver.UsesEmitDelegate(manipMethod?.Resolve());

                    string mechanism = "ILHook";
                    string caller = method.FullName;
                    int ilOffset = inst.Offset;
                    List<DelegateSlotEntry>? slots = null;
                    bool? isPure = null;

                    var enclosingType = method.DeclaringType.DeclaringType;
                    if (enclosingType != null && method.Parameters.Any(p => p.ParameterType.FullName.Contains("ManagedILEdit")))
                    {
                        mechanism = "library:Luminance.ManagedILEdit";
                        foreach (var em in enclosingType.Methods)
                        {
                            if (!em.HasBody) continue;
                            var emHeights = MethodBaseResolver.ComputeStackHeights(em);
                            foreach (var ei in em.Body.Instructions)
                            {
                                if (ei.OpCode == OpCodes.Newobj && ei.Operand is MethodReference emr && 
                                    emr.DeclaringType.FullName == "Luminance.Core.Hooking.ManagedILEdit" && emr.Name == ".ctor" && emr.Parameters.Count == 5)
                                {
                                    caller = em.FullName;
                                    ilOffset = ei.Offset;
                                    var arg4 = MethodBaseResolver.FindArgumentProducer(em, ei, 4, 5, emHeights);
                                    var manipRef = MethodBaseResolver.ResolveDelegateMethod(em, arg4, emHeights);
                                    if (manipRef != null)
                                    {
                                        manipMethod = manipRef;
                                        usesEmitDelegate = MethodBaseResolver.UsesEmitDelegate(manipMethod.Resolve());
                                    }
                                }
                            }
                        }

                        if (usesEmitDelegate && target != null)
                        {
                            slots = new List<DelegateSlotEntry>
                            {
                                new DelegateSlotEntry
                                {
                                    SlotId = 0,
                                    SlotName = $"Slot_{target.Type.Split('.').Last()}_{target.Method}_0",
                                    DelegateType = "System.Func`2[Terraria.Player,System.Boolean]"
                                }
                            };
                            isPure = true;
                        }
                    }

                    string key = $"{mechanism}_{target?.FullName ?? inst.Offset.ToString()}";
                    ilHooksDict[key] = new ILHookEntry
                    {
                        Mechanism = mechanism,
                        Target = target,
                        Manipulator = manipMethod?.FullName,
                        UsesEmitDelegate = usesEmitDelegate,
                        DelegateSlots = slots,
                        IsPure = isPure,
                        Registrations = new List<HookRegistration>
                        {
                            new HookRegistration { Mod = modName, Caller = caller, IlOffset = ilOffset, Op = "add" }
                        }
                    };

                    if (target == null)
                    {
                        unsupported.Add(new UnsupportedEntry
                        {
                            Mod = modName,
                            Caller = method.FullName,
                            IlOffset = inst.Offset,
                            ReasonCode = "UNRESOLVED_TARGET",
                            Detail = $"Unresolved MethodBase in ILHook"
                        });
                    }
                }
            }
        }
    }



    private bool IsHarmonyReference(Instruction inst, out string detail)
    {
        detail = "";
        object operand = inst.Operand;
        if (operand == null) return false;

        string name = operand.ToString() ?? "";
        if (name.Contains("HarmonyLib") || name.Contains("0Harmony"))
        {
            detail = $"Instruction references Harmony: {name}";
            return true;
        }

        if (operand is TypeReference tr && (tr.FullName?.Contains("HarmonyLib") == true || tr.FullName?.Contains("0Harmony") == true))
        {
            detail = $"Reference to Harmony type: {tr.FullName}";
            return true;
        }
        if (operand is MemberReference mr && mr.DeclaringType?.FullName != null && 
            (mr.DeclaringType.FullName.Contains("HarmonyLib") || mr.DeclaringType.FullName.Contains("0Harmony")))
        {
            detail = $"Reference to Harmony member: {mr.FullName}";
            return true;
        }
        return false;
    }

    private bool IsReflectionEmit(Instruction inst, out string detail)
    {
        detail = "";
        if (inst.Operand is MethodReference mr && mr.DeclaringType?.FullName != null)
        {
            string decl = mr.DeclaringType.FullName;
            if (decl.StartsWith("System.Reflection.Emit.DynamicMethod") ||
                decl.StartsWith("System.Reflection.Emit.TypeBuilder") ||
                decl.StartsWith("System.Reflection.Emit.MethodBuilder") ||
                decl.StartsWith("System.Reflection.Emit.ILGenerator"))
            {
                detail = $"Call to Reflection.Emit: {mr.FullName}";
                return true;
            }
        }
        return false;
    }

    private AssemblyInfo CreateAssemblyInfo(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var asm = AssemblyDefinition.ReadAssembly(fullPath);
        string mvid = asm.MainModule.Mvid.ToString();

        return new AssemblyInfo
        {
            Path = fullPath,
            Sha256 = sha256,
            Mvid = mvid
        };
    }

    private ModEntry CreateModEntry(string dllPath, AssemblyDefinition asm)
    {
        string fullPath = Path.GetFullPath(dllPath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string mvid = asm.MainModule.Mvid.ToString();

        string modName = asm.Name.Name;
        var (version, modRefs, dllRefs) = FindModMetadata(modName, Path.GetDirectoryName(fullPath) ?? "");

        if (string.IsNullOrEmpty(version) || version == "0.0.0.0")
        {
            version = asm.Name.Version.ToString();
        }

        return new ModEntry
        {
            Name = modName,
            Version = version,
            Dll = fullPath,
            DllSha256 = sha256,
            Mvid = mvid,
            ModReferences = modRefs,
            DllReferences = dllRefs
        };
    }

    private (string Version, List<string> ModRefs, List<string> DllRefs) FindModMetadata(string modName, string baseDir)
    {
        string[] searchPaths = new[]
        {
            Path.Combine(baseDir, "build.txt"),
            Path.Combine(baseDir, "..", "build.txt"),
        };

        foreach (var p in searchPaths)
        {
            if (File.Exists(p))
            {
                string ver = "";
                var modRefs = new List<string>();
                var dllRefs = new List<string>();

                foreach (var line in File.ReadAllLines(p))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    if (key.Equals("version", StringComparison.OrdinalIgnoreCase))
                        ver = val;
                    else if (key.Equals("modReferences", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var part in val.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string name = part.Split('@')[0].Trim();
                            if (!string.IsNullOrEmpty(name)) modRefs.Add(name);
                        }
                    }
                    else if (key.Equals("dllReferences", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var part in val.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string name = part.Trim();
                            if (!string.IsNullOrEmpty(name)) dllRefs.Add(name);
                        }
                    }
                }
                return (ver, modRefs, dllRefs);
            }
        }

        return ("0.0.0.0", new List<string>(), new List<string>());
    }
}
