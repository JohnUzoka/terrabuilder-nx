using System.Globalization;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class DrawGateAudit
{
    internal static object Verify(byte[] originalBytes, byte[] candidateBytes)
    {
        Require(Sha(originalBytes) == Program.InputHash, "audit requires pinned original");
        using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes));
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes));
        DrawGatePatcher.CheckOriginal(Program.Single(original.MainModule));
        var bindings = QualifiedBindings.Compare(original.MainModule, candidate.MainModule);
        // Reuse the established single-target metadata/signature audit without modifying its historical source.
        var preservation = LightLookupAudit.Preservation(original, candidate);
        var signatures = LightLookupAudit.Signatures(originalBytes, candidateBytes);
        var resources = PeResources.Fingerprints(originalBytes);
        Require(resources.SequenceEqual(PeResources.Fingerprints(candidateBytes)), "native PE resources changed");
        var target = Program.Single(candidate.MainModule);
        string candidateFingerprint = Fingerprint(target);
        var relocation = Relocation(Program.Single(original.MainModule), target);
        return new { passed = true, candidateFingerprint, preservation, signatures, bindings, resources, relocation };
    }

    internal static object Relocation(MethodDefinition original, MethodDefinition candidate)
    {
        DrawGatePatcher.CheckOriginal(original);
        var before = original.Body;
        var after = candidate.Body;
        Require(candidate.FullName == original.FullName && after.Instructions.Count == before.Instructions.Count && after.CodeSize == before.CodeSize && after.MaxStackSize == before.MaxStackSize && after.InitLocals == before.InitLocals && after.ExceptionHandlers.Count == 0, "candidate changed method shape");
        Require(after.Variables.Select(v => v.VariableType.FullName).SequenceEqual(before.Variables.Select(v => v.VariableType.FullName)), "candidate changed locals");
        var sourcePreparation = DrawGatePatcher.At(original, DrawGatePatcher.PreparationOffset);
        var sourceLoad = DrawGatePatcher.At(original, DrawGatePatcher.GateLoadOffset);
        var sourceBranch = DrawGatePatcher.At(original, DrawGatePatcher.GateBranchOffset);
        var sourceIncoming = DrawGatePatcher.IncomingOffsets.Select(offset => DrawGatePatcher.At(original, offset)).ToHashSet();
        int preparationIndex = before.Instructions.IndexOf(sourcePreparation);
        int originalLoadIndex = before.Instructions.IndexOf(sourceLoad);
        var expected = before.Instructions.Where(i => !ReferenceEquals(i, sourceLoad) && !ReferenceEquals(i, sourceBranch)).ToList();
        expected.InsertRange(preparationIndex, new[] { sourceLoad, sourceBranch });
        var expectedIndices = expected.Select((instruction, index) => (instruction, index)).ToDictionary(p => p.instruction, p => p.index);
        var actualIndices = after.Instructions.Select((instruction, index) => (instruction, index)).ToDictionary(p => p.instruction, p => p.index);
        string Operand(object? value, IReadOnlyDictionary<Instruction, int> indices) => value switch
        {
            null => "",
            Instruction instruction => "@" + indices[instruction],
            Instruction[] instructions => string.Join(",", instructions.Select(i => "@" + indices[i])),
            VariableDefinition variable => "local:" + variable.Index,
            ParameterDefinition parameter => "arg:" + parameter.Index,
            MemberReference member => member.FullName,
            float number => BitConverter.SingleToInt32Bits(number).ToString("x8", CultureInfo.InvariantCulture),
            double number => BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
        };
        for (int index = 0; index < expected.Count; index++)
        {
            var source = expected[index];
            var actual = after.Instructions[index];
            object? operand = sourceIncoming.Contains(source) ? sourceLoad : source.Operand;
            Require(source.OpCode.Code == actual.OpCode.Code && Operand(operand, expectedIndices) == Operand(actual.Operand, actualIndices), "unexpected candidate instruction/operand at index " + index);
        }
        var movedLoad = after.Instructions[preparationIndex];
        var movedBranch = after.Instructions[preparationIndex + 1];
        var movedPreparation = after.Instructions[preparationIndex + 2];
        Require(movedLoad.Offset == DrawGatePatcher.PreparationOffset && ReferenceEquals(movedBranch.Operand, after.Instructions.Last()), "serialized gate boundary differs");
        var incoming = sourceIncoming.Select(i => after.Instructions[before.Instructions.IndexOf(i)]).ToArray();
        Require(incoming.All(i => ReferenceEquals(i.Operand, movedLoad)), "both original branches must enter the moved gate");
        Require(movedLoad.Previous.Operand is MethodReference shroomDraw && shroomDraw.DeclaringType.FullName == "Terraria.Graphics.TileBatch" && shroomDraw.Name == "Draw", "moved gate bypasses optional shroom drawing");
        var calledBefore = before.Instructions.Where(i => i.OpCode.FlowControl == FlowControl.Call).Select(i => ((MethodReference)i.Operand).FullName).ToArray();
        var calledAfter = after.Instructions.Where(i => i.OpCode.FlowControl == FlowControl.Call).Select(i => ((MethodReference)i.Operand).FullName).ToArray();
        Require(calledBefore.SequenceEqual(calledAfter), "original call sequence changed");
        string changedFingerprint = Fingerprint(candidate);

        // Invert the serialized change independently: restore both entries, then the old gate location.
        foreach (var entry in incoming) entry.Operand = movedPreparation;
        var il = after.GetILProcessor();
        il.Remove(movedLoad);
        il.Remove(movedBranch);
        var restoreBefore = after.Instructions[originalLoadIndex];
        il.InsertBefore(restoreBefore, movedLoad);
        il.InsertBefore(restoreBefore, movedBranch);
        Require(Body(original) == Body(candidate), "inverse relocation does not reconstruct every original instruction/local/handler");
        return new
        {
            passed = true, originalFingerprint = Fingerprint(original), candidateFingerprint = changedFingerprint,
            originalInstructions = before.Instructions.Count, originalCodeBytes = before.CodeSize, originalMaxStack = before.MaxStackSize,
            movedInstructions = 2, redirectedBranches = 2, preparationInstructionsSkippedOnlyOnFalseGate = 66,
            originalLocals = before.Variables.Count, originalCallsites = calledBefore.Length,
            exactAllInstructionAndOperandComparison = true, inverseRelocationExact = true,
            allThreeEntriesRetained = true, unchangedOriginalCallOrder = true,
            limits = "Static full-method preservation plus separately executed behavioral proof; no hardware time or arbitrary corrupt-state/custom-hook claim."
        };
    }
}
