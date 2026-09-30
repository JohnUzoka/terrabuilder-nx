using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace HookScan;

public static class MethodBaseResolver
{
    public static Dictionary<Instruction, (int Entry, int Exit)> ComputeStackHeights(MethodDefinition method)
    {
        var dict = new Dictionary<Instruction, (int Entry, int Exit)>();
        if (!method.HasBody) return dict;

        int current = 0;
        foreach (var inst in method.Body.Instructions)
        {
            int entry = current;
            int pop = GetPopCount(inst);
            int push = GetPushCount(inst);
            current = Math.Max(0, current - pop + push);
            dict[inst] = (entry, current);
        }
        return dict;
    }

    public static Instruction? FindArgumentProducer(
        MethodDefinition method, 
        Instruction consumerInst, 
        int argIndex, 
        int totalArgs, 
        Dictionary<Instruction, (int Entry, int Exit)> heights)
    {
        if (!heights.TryGetValue(consumerInst, out var h)) return null;
        int targetSlot = h.Entry - totalArgs + argIndex;
        int targetExit = targetSlot + 1;

        var inst = consumerInst.Previous;
        while (inst != null)
        {
            if (heights.TryGetValue(inst, out var ih) && ih.Exit == targetExit)
            {
                return inst;
            }
            inst = inst.Previous;
        }
        return null;
    }

    public static bool TryResolveMethodBase(
        MethodDefinition method, 
        Instruction? inst, 
        Dictionary<Instruction, (int Entry, int Exit)> heights, 
        out string targetType, 
        out string targetMethod)
    {
        targetType = "";
        targetMethod = "";
        if (inst == null) return false;

        // If inst is ldloc, find preceding stloc
        if (IsLdloc(inst, out int locIndex))
        {
            var stloc = FindPrecedingStloc(inst, locIndex);
            if (stloc != null)
            {
                var producer = FindArgumentProducer(method, stloc, 0, 1, heights);
                return TryResolveMethodBase(method, producer, heights, out targetType, out targetMethod);
            }
        }

        // Handle stsfld
        if (inst.OpCode == OpCodes.Stsfld)
        {
            return TryResolveMethodBase(method, inst.Previous, heights, out targetType, out targetMethod);
        }

        // Handle pop from delegate caching branch
        if (inst.OpCode == OpCodes.Pop)
        {
            var prev = inst.Previous;
            while (prev != null && (prev.OpCode == OpCodes.Pop || prev.OpCode == OpCodes.Nop || 
                   prev.OpCode.FlowControl == FlowControl.Branch || prev.OpCode.FlowControl == FlowControl.Cond_Branch ||
                   prev.OpCode == OpCodes.Dup || (prev.OpCode == OpCodes.Ldsfld && prev.Operand?.ToString()?.Contains("Method") != true)))
            {
                prev = prev.Previous;
            }
            if (prev != null)
            {
                return TryResolveMethodBase(method, prev, heights, out targetType, out targetMethod);
            }
        }

        // Handle dup
        if (inst.OpCode == OpCodes.Dup)
        {
            return TryResolveMethodBase(method, inst.Previous, heights, out targetType, out targetMethod);
        }

        // Handle ldsfld
        if (inst.OpCode == OpCodes.Ldsfld && inst.Operand is FieldReference sfr)
        {
            var fieldDef = sfr.Resolve();
            if (fieldDef != null)
            {
                foreach (var m in fieldDef.DeclaringType.Methods)
                {
                    if (!m.HasBody) continue;
                    var mHeights = ComputeStackHeights(m);
                    foreach (var i in m.Body.Instructions)
                    {
                        if (i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference sst && sst.FullName == sfr.FullName)
                        {
                            var producer = FindArgumentProducer(m, i, 0, 1, mHeights);
                            if (TryResolveMethodBase(m, producer, mHeights, out targetType, out targetMethod))
                                return true;
                        }
                    }
                }
            }
        }

        // If inst is ldfld, check if it's a compiler-generated closure field
        if (inst.OpCode == OpCodes.Ldfld && inst.Operand is FieldReference fr)
        {
            // Look for stfld to this field in the declaring type's methods or enclosing methods
            var fieldDef = fr.Resolve();
            if (fieldDef != null)
            {
                var searchTypes = new List<TypeDefinition> { fieldDef.DeclaringType };
                if (fieldDef.DeclaringType.DeclaringType != null) searchTypes.Add(fieldDef.DeclaringType.DeclaringType);
                foreach (var st in searchTypes)
                {
                    foreach (var m in st.Methods)
                    {
                        if (!m.HasBody) continue;
                        var mHeights = ComputeStackHeights(m);
                        foreach (var i in m.Body.Instructions)
                        {
                            if (i.OpCode == OpCodes.Stfld && i.Operand is FieldReference ssfr && ssfr.FullName == fr.FullName)
                            {
                                var producer = FindArgumentProducer(m, i, 1, 2, mHeights);
                                if (TryResolveMethodBase(m, producer, mHeights, out targetType, out targetMethod))
                                    return true;
                            }
                        }
                    }
                }
            }
        }
        // Case 1: call GetMethod
        if ((inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt) && inst.Operand is MethodReference mr && mr.Name == "GetMethod")
        {
            int numArgs = mr.Parameters.Count + (mr.HasThis ? 1 : 0);
            var typeProducer = mr.HasThis ? FindArgumentProducer(method, inst, 0, numArgs, heights) : null;
            var nameProducer = FindArgumentProducer(method, inst, mr.HasThis ? 1 : 0, numArgs, heights);

            if (typeProducer != null)
            {
                // Trace type producer
                if ((typeProducer.OpCode == OpCodes.Call || typeProducer.OpCode == OpCodes.Callvirt) &&
                    typeProducer.Operand is MethodReference typeMr && typeMr.Name == "GetTypeFromHandle")
                {
                    var ldtoken = FindArgumentProducer(method, typeProducer, 0, 1, heights);
                    if (ldtoken != null && ldtoken.OpCode == OpCodes.Ldtoken && ldtoken.Operand is TypeReference tr)
                    {
                        targetType = tr.FullName;
                    }
                }
            }

            if (nameProducer != null && nameProducer.OpCode == OpCodes.Ldstr && nameProducer.Operand is string s)
            {
                targetMethod = s;
            }

            return !string.IsNullOrEmpty(targetType) && !string.IsNullOrEmpty(targetMethod);
        }

        // Case 2: ldtoken MethodReference -> GetMethodFromHandle
        if ((inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt) && inst.Operand is MethodReference gmfh && gmfh.Name == "GetMethodFromHandle")
        {
            var ldtoken = FindArgumentProducer(method, inst, 0, gmfh.Parameters.Count, heights);
            if (ldtoken != null && ldtoken.OpCode == OpCodes.Ldtoken && ldtoken.Operand is MethodReference targetMr)
            {
                targetType = targetMr.DeclaringType.FullName;
                targetMethod = targetMr.Name;
                return true;
            }
        }

        // Case 3: ldtoken MethodReference directly
        if (inst.OpCode == OpCodes.Ldtoken && inst.Operand is MethodReference directMr)
        {
            targetType = directMr.DeclaringType.FullName;
            targetMethod = directMr.Name;
            return true;
        }

        return false;
    }

    public static MethodReference? ResolveDelegateMethod(
        MethodDefinition method, 
        Instruction? inst, 
        Dictionary<Instruction, (int Entry, int Exit)> heights)
    {
        if (inst == null) return null;

        if (inst.OpCode == OpCodes.Stsfld || inst.OpCode == OpCodes.Dup)
            return ResolveDelegateMethod(method, inst.Previous, heights);

        if (inst.OpCode == OpCodes.Ldftn && inst.Operand is MethodReference mr)
            return mr;

        if (inst.OpCode == OpCodes.Ldsfld && inst.Operand is FieldReference sfr)
        {
            var fieldDef = sfr.Resolve();
            if (fieldDef != null)
            {
                foreach (var m in fieldDef.DeclaringType.Methods)
                {
                    if (!m.HasBody) continue;
                    var mHeights = ComputeStackHeights(m);
                    foreach (var i in m.Body.Instructions)
                    {
                        if (i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference sst && sst.FullName == sfr.FullName)
                        {
                            var producer = FindArgumentProducer(m, i, 0, 1, mHeights);
                            var res = ResolveDelegateMethod(m, producer, mHeights);
                            if (res != null) return res;
                        }
                    }
                }
            }
        }
        if (inst.OpCode == OpCodes.Newobj && inst.Operand is MethodReference ctorMr && 
            ctorMr.Parameters.Count == 2 && ctorMr.Parameters[1].ParameterType.FullName == "System.IntPtr")
        {
            // delegate ctor takes (object this, IntPtr method)
            var ftnProducer = FindArgumentProducer(method, inst, 1, 2, heights);
            if (ftnProducer != null && ftnProducer.OpCode == OpCodes.Ldftn && ftnProducer.Operand is MethodReference targetM)
                return targetM;
        }

        if (IsLdloc(inst, out int locIndex))
        {
            var stloc = FindPrecedingStloc(inst, locIndex);
            if (stloc != null)
            {
                var producer = FindArgumentProducer(method, stloc, 0, 1, heights);
                return ResolveDelegateMethod(method, producer, heights);
            }
        }

        return null;
    }

    public static bool UsesEmitDelegate(MethodDefinition? method, HashSet<string>? visited = null)
    {
        if (method == null || !method.HasBody) return false;
        visited ??= new HashSet<string>();
        if (!visited.Add(method.FullName)) return false;

        foreach (var inst in method.Body.Instructions)
        {
            if ((inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt) && inst.Operand is MethodReference mr)
            {
                if (mr.DeclaringType.FullName.Contains("ILCursor") && mr.Name == "EmitDelegate")
                    return true;

                // Check called helper methods within same assembly
                if (mr.DeclaringType.Scope == method.DeclaringType.Scope)
                {
                    var calledDef = mr.Resolve();
                    if (calledDef != null && UsesEmitDelegate(calledDef, visited))
                        return true;
                }
            }
        }
        return false;
    }

    private static bool IsLdloc(Instruction inst, out int index)
    {
        index = -1;
        var op = inst.OpCode;
        if (op == OpCodes.Ldloc_0) { index = 0; return true; }
        if (op == OpCodes.Ldloc_1) { index = 1; return true; }
        if (op == OpCodes.Ldloc_2) { index = 2; return true; }
        if (op == OpCodes.Ldloc_3) { index = 3; return true; }
        if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S)
        {
            if (inst.Operand is VariableDefinition vd) { index = vd.Index; return true; }
        }
        return false;
    }

    private static Instruction? FindPrecedingStloc(Instruction consumer, int locIndex)
    {
        var inst = consumer.Previous;
        while (inst != null)
        {
            var op = inst.OpCode;
            if (op == OpCodes.Stloc_0 && locIndex == 0) return inst;
            if (op == OpCodes.Stloc_1 && locIndex == 1) return inst;
            if (op == OpCodes.Stloc_2 && locIndex == 2) return inst;
            if (op == OpCodes.Stloc_3 && locIndex == 3) return inst;
            if ((op == OpCodes.Stloc || op == OpCodes.Stloc_S) && inst.Operand is VariableDefinition vd && vd.Index == locIndex) return inst;
            inst = inst.Previous;
        }
        return null;
    }

    private static int GetPopCount(Instruction inst)
    {
        var op = inst.OpCode;
        if (op == OpCodes.Call || op == OpCodes.Callvirt)
        {
            var mr = (MethodReference)inst.Operand;
            int count = mr.Parameters.Count;
            if (mr.HasThis) count++;
            return count;
        }
        if (op == OpCodes.Newobj)
        {
            var mr = (MethodReference)inst.Operand;
            return mr.Parameters.Count;
        }
        if (op == OpCodes.Ret) return 0;
        return op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            _ => 0
        };
    }

    private static int GetPushCount(Instruction inst)
    {
        var op = inst.OpCode;
        if (op == OpCodes.Call || op == OpCodes.Callvirt)
        {
            var mr = (MethodReference)inst.Operand;
            return mr.ReturnType.FullName == "System.Void" ? 0 : 1;
        }
        if (op == OpCodes.Newobj) return 1;
        return op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => 0
        };
    }
}
