using Mono.Cecil;
using Mono.Cecil.Cil;

// IL helpers shared by NxFix's patches.
static class NxIl
{
internal static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types)
    {
        yield return type;
        foreach (var nested in AllTypes(type.NestedTypes))
            yield return nested;
    }
}

internal static bool IsVoid(TypeReference type)
{
    while (type is IModifierType modified)
        type = modified.ElementType;
    return type.MetadataType == MetadataType.Void;
}

// Patches insert instructions into large methods; long-form branches keep every offset encodable.
internal static void WidenShortBranches(MethodBody body)
{
    foreach (var ins in body.Instructions)
    {
        if (ins.OpCode.OperandType != OperandType.ShortInlineBrTarget)
            continue;
        ins.OpCode = ins.OpCode.Code switch
        {
            Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
            Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bgt_S => OpCodes.Bgt,
            Code.Ble_S => OpCodes.Ble, Code.Blt_S => OpCodes.Blt, Code.Bne_Un_S => OpCodes.Bne_Un,
            Code.Bge_Un_S => OpCodes.Bge_Un, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_Un_S => OpCodes.Ble_Un,
            Code.Blt_Un_S => OpCodes.Blt_Un, Code.Leave_S => OpCodes.Leave,
            _ => throw new InvalidOperationException("unknown short branch " + ins),
        };
    }
}

internal static void VerifyPatched(MethodDefinition method, bool expected, string unexpected)
{
    var problems = new List<string>();
    if (!expected || UnderflowingPops(method, problems).Count > 0 || problems.Count > 0)
        throw new InvalidOperationException($"{method.FullName}: {unexpected} {string.Join("; ", problems)}");
}

// Forward dataflow over the evaluation-stack depth. Returns the `pop` instructions whose
// entry depth is 0; any other underflow or inconsistent merge is reported as unrepairable.
internal static List<Instruction> UnderflowingPops(MethodDefinition method, List<string> problems)
{
    var body = method.Body;
    var depth = new Dictionary<Instruction, int>();
    var work = new Stack<Instruction>();
    var bad = new List<Instruction>();
    void Enqueue(Instruction target, int d)
    {
        if (target == null)
            return;
        if (depth.TryGetValue(target, out var known))
        {
            if (known != d)
                problems.Add($"{method.FullName}: inconsistent stack depth at {target} ({known} vs {d})");
            return;
        }
        depth[target] = d;
        work.Push(target);
    }
    Enqueue(body.Instructions[0], 0);
    foreach (var handler in body.ExceptionHandlers)
    {
        Enqueue(handler.TryStart, 0);
        Enqueue(handler.HandlerStart, handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ? 1 : 0);
        if (handler.HandlerType == ExceptionHandlerType.Filter)
            Enqueue(handler.FilterStart, 1);
    }
    while (work.Count > 0)
    {
        var instruction = work.Pop();
        int d = depth[instruction];
        int pops = Pops(instruction, method, d);
        if (Environment.GetEnvironmentVariable("NXFIX_TRACE") is string trace && method.FullName.Contains(trace))
            Console.Error.WriteLine($"TRACE {instruction} depth={d} pops={pops} push={Pushes(instruction)}");
        if (pops > d)
        {
            if (instruction.OpCode.Code == Code.Pop && d == 0)
            {
                bad.Add(instruction);
                pops = 0;
            }
            else
            {
                problems.Add($"{method.FullName}: stack underflow at {instruction} (depth {d}, pops {pops})");
                continue;
            }
        }
        int after = d - pops + Pushes(instruction);
        switch (instruction.OpCode.FlowControl)
        {
            case FlowControl.Branch:
                Enqueue((Instruction)instruction.Operand, instruction.OpCode.Code is Code.Leave or Code.Leave_S ? 0 : after);
                break;
            case FlowControl.Cond_Branch:
                if (instruction.Operand is Instruction[] targets)
                    foreach (var target in targets)
                        Enqueue(target, after);
                else
                    Enqueue((Instruction)instruction.Operand, after);
                Enqueue(instruction.Next, after);
                break;
            case FlowControl.Return:
            case FlowControl.Throw:
                break;
            default:
                Enqueue(instruction.Next, after);
                break;
        }
    }
    return bad;
}

static int Pops(Instruction instruction, MethodDefinition method, int depth)
{
    var op = instruction.OpCode;
    switch (op.StackBehaviourPop)
    {
        case StackBehaviour.Pop0: return 0;
        case StackBehaviour.Pop1:
        case StackBehaviour.Popi:
        case StackBehaviour.Popref:
            return 1;
        case StackBehaviour.Pop1_pop1:
        case StackBehaviour.Popi_pop1:
        case StackBehaviour.Popi_popi:
        case StackBehaviour.Popi_popi8:
        case StackBehaviour.Popi_popr4:
        case StackBehaviour.Popi_popr8:
        case StackBehaviour.Popref_pop1:
        case StackBehaviour.Popref_popi:
            return 2;
        case StackBehaviour.Popi_popi_popi:
        case StackBehaviour.Popref_popi_popi:
        case StackBehaviour.Popref_popi_popi8:
        case StackBehaviour.Popref_popi_popr4:
        case StackBehaviour.Popref_popi_popr8:
        case StackBehaviour.Popref_popi_popref:
            return 3;
        case StackBehaviour.PopAll:
            return depth;
        case StackBehaviour.Varpop:
            switch (op.Code)
            {
                case Code.Ret:
                    return IsVoid(method.ReturnType) ? 0 : 1;
                case Code.Newobj:
                    return ((MethodReference)instruction.Operand).Parameters.Count;
                case Code.Calli:
                {
                    var site = (CallSite)instruction.Operand;
                    return site.Parameters.Count + (site.HasThis && !site.ExplicitThis ? 1 : 0) + 1;
                }
                default:
                {
                    var callee = (MethodReference)instruction.Operand;
                    return callee.Parameters.Count + (callee.HasThis && !callee.ExplicitThis ? 1 : 0);
                }
            }
        default:
            throw new NotSupportedException($"pop behaviour {op.StackBehaviourPop} at {instruction}");
    }
}

static int Pushes(Instruction instruction)
{
    var op = instruction.OpCode;
    switch (op.StackBehaviourPush)
    {
        case StackBehaviour.Push0: return 0;
        case StackBehaviour.Push1_push1: return 2;
        case StackBehaviour.Push1:
        case StackBehaviour.Pushi:
        case StackBehaviour.Pushi8:
        case StackBehaviour.Pushr4:
        case StackBehaviour.Pushr8:
        case StackBehaviour.Pushref:
            return 1;
        case StackBehaviour.Varpush:
            if (op.Code == Code.Newobj)
                return 1;
            var returnType = instruction.Operand is CallSite site ? site.ReturnType : ((MethodReference)instruction.Operand).ReturnType;
            return IsVoid(returnType) ? 0 : 1;
        default:
            throw new NotSupportedException($"push behaviour {op.StackBehaviourPush} at {instruction}");
    }
}
}
