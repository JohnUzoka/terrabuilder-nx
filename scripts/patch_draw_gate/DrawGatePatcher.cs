using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class DrawGatePatcher
{
    internal const int PreparationOffset = 0x0713;
    internal const int GateLoadOffset = 0x07b3;
    internal const int GateBranchOffset = 0x07b5;
    internal const int FinalReturnOffset = 0x1ac1;
    internal static readonly int[] IncomingOffsets = { 0x0676, 0x0683 };

    internal static Instruction At(MethodDefinition method, int offset) => method.Body.Instructions.Single(i => i.Offset == offset);
    internal static IEnumerable<Instruction> Targets(Instruction instruction) => instruction.Operand switch
    {
        Instruction target => new[] { target },
        Instruction[] targets => targets,
        _ => Array.Empty<Instruction>()
    };

    internal static void CheckOriginal(MethodDefinition method)
    {
        Require(method.FullName == Program.SingleName && method.HasBody, "unexpected gate target");
        var body = method.Body;
        Require(body.InitLocals && body.MaxStackSize == 17 && body.CodeSize == 0x1ac2 && body.Instructions.Count == 2312 && body.ExceptionHandlers.Count == 0, "unsupported original body shape");
        Require(body.Variables.Count > 8 && body.Variables[4].VariableType.FullName == "System.Boolean" && body.Variables[6].VariableType.FullName == "Microsoft.Xna.Framework.Rectangle" && body.Variables[7].VariableType.FullName == "System.Single" && body.Variables[8].VariableType.FullName == "Microsoft.Xna.Framework.Vector2", "original local types changed");
        var preparation = At(method, PreparationOffset);
        var load = At(method, GateLoadOffset);
        var branch = At(method, GateBranchOffset);
        var finalReturn = At(method, FinalReturnOffset);
        Require(preparation.OpCode == OpCodes.Ldloca_S && ReferenceEquals(preparation.Operand, body.Variables[6]), "rectangle setup entry changed");
        Require(load.OpCode == OpCodes.Ldloc_S && ReferenceEquals(load.Operand, body.Variables[4]) && ReferenceEquals(load.Next, branch), "gate no longer reads original V4");
        Require(branch.OpCode == OpCodes.Brfalse && ReferenceEquals(branch.Operand, finalReturn) && finalReturn.OpCode == OpCodes.Ret && ReferenceEquals(body.Instructions.Last(), finalReturn), "gate false destination changed");
        Require(load.Previous.OpCode == OpCodes.Stloc_S && ReferenceEquals(load.Previous.Operand, body.Variables[8]) && branch.Next.Offset == 0x07ba, "preparation/gate boundary changed");
        int start = body.Instructions.IndexOf(preparation), stop = body.Instructions.IndexOf(load);
        Require(stop - start == 66, "preparation instruction count changed");
        var region = body.Instructions.Skip(start).Take(stop - start).ToHashSet();
        var incoming = body.Instructions.Where(i => Targets(i).Any(t => ReferenceEquals(t, preparation))).ToArray();
        Require(incoming.Select(i => i.Offset).SequenceEqual(IncomingOffsets) && incoming[0].OpCode == OpCodes.Bne_Un && incoming[1].OpCode == OpCodes.Blt, "all original preparation entry branches must be known");
        Require(body.Instructions.Where(i => !region.Contains(i)).SelectMany(Targets).Where(region.Contains).All(t => ReferenceEquals(t, preparation)), "external branch enters preparation interior");
        Require(!body.Instructions.SelectMany(Targets).Any(t => ReferenceEquals(t, load) || ReferenceEquals(t, branch)), "unexpected direct entry to old gate");
        Require(region.SelectMany(Targets).All(region.Contains), "preparation has an external control-flow edge");
        Require(preparation.Previous.Offset == 0x070e && preparation.Previous.OpCode == OpCodes.Callvirt && preparation.Previous.Operand is MethodReference draw && draw.DeclaringType.FullName == "Terraria.Graphics.TileBatch" && draw.Name == "Draw", "optional shroom draw must precede convergence");
        var calls = region.Where(i => i.OpCode.FlowControl == FlowControl.Call).Select(i => (i.Offset, Op: i.OpCode.Code, Name: ((MethodReference)i.Operand).FullName)).ToArray();
        Require(calls.Length == 3 && calls[0] == (0x0742, Code.Call, "System.Void Microsoft.Xna.Framework.Rectangle::.ctor(System.Int32,System.Int32,System.Int32,System.Int32)") && calls[1] == (0x07a6, Code.Newobj, "System.Void Microsoft.Xna.Framework.Vector2::.ctor(System.Single,System.Single)") && calls[2] == (0x07ac, Code.Call, "Microsoft.Xna.Framework.Vector2 Microsoft.Xna.Framework.Vector2::op_Addition(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2)"), "preparation primitive calls changed");
        Require(region.All(i => i.OpCode.Code is not (Code.Stfld or Code.Stsfld or Code.Stobj or Code.Stind_Ref or Code.Stelem_Any or Code.Throw or Code.Rethrow)), "preparation acquired a nonlocal write or throw");
    }

    internal static object Relocate(MethodDefinition method)
    {
        CheckOriginal(method);
        string beforeHash = Fingerprint(method);
        var body = method.Body;
        var preparation = At(method, PreparationOffset);
        var load = At(method, GateLoadOffset);
        var branch = At(method, GateBranchOffset);
        var incoming = IncomingOffsets.Select(offset => At(method, offset)).ToArray();
        int preparationIndex = body.Instructions.IndexOf(preparation), originalLoadIndex = body.Instructions.IndexOf(load);
        var il = body.GetILProcessor();
        il.Remove(load);
        il.Remove(branch);
        il.InsertBefore(preparation, load);
        il.InsertBefore(preparation, branch);
        foreach (var entry in incoming) entry.Operand = load;
        Require(body.Instructions.Count == 2312 && ReferenceEquals(load.Next, branch) && ReferenceEquals(branch.Next, preparation), "relocation altered body shape");
        return new
        {
            version = 50, method = method.FullName, originalFingerprint = beforeHash, candidateFingerprint = Fingerprint(method),
            originalPreparationOffset = PreparationOffset, originalLoadOffset = GateLoadOffset, originalBranchOffset = GateBranchOffset,
            originalReturnOffset = FinalReturnOffset, incomingOriginalOffsets = IncomingOffsets, preparationIndex, originalLoadIndex,
            preparationInstructions = 66, movedInstructions = 2, insertedInstructions = 0, removedInstructions = 0,
            originalLocals = body.Variables.Count, originalExceptionHandlers = body.ExceptionHandlers.Count,
            statement = "Move the original V4 false gate only; both shroom skip branches and Draw fallthrough enter it after all prior side effects."
        };
    }
}
