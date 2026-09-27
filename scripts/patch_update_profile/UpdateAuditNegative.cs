using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static partial class UpdateAudit
{
    internal static object NegativeProof(byte[] originalBytes, byte[] candidateBytes, UpdatePatcher.Receipt[] receipts, string inputPath, string output)
    {
        Directory.CreateDirectory(output);
        const string inWorld = "System.Void Terraria.Main::DoUpdateInWorld()";
        const string worldTiles = "System.Void Terraria.Main::UpdateWorld_WorldGenAndInvasion()";
        const string housing = "System.Void Terraria.WorldGen::UpdatePrioritizedTownNPC()";
        MethodDefinition Helper(ModuleDefinition m, string name) => m.Types.Single(t => t.FullName == UpdatePatcher.HelperName).Methods.Single(x => x.Name == name);
        Instruction Call(ModuleDefinition m, string method, string name) => Program.Method(m, method).Body.Instructions.First(i => i.Operand is MethodReference r && r.Name == name);
        var cases = new (string Name, string Reason, Action<ModuleDefinition> Mutate)[] {
            ("omit-worldgen-call", "raw signature use", m => { var call = Call(m, worldTiles, "UpdateWorld"); call.OpCode = OpCodes.Nop; call.Operand = null; }),
            ("reorder-worldgen-and-invasion", "qualified candidate differs at method:" + worldTiles + ":body", m => { var first = Call(m, worldTiles, "UpdateWorld"); var second = Call(m, worldTiles, "UpdateInvasion"); (first.Operand, second.Operand) = (second.Operand, first.Operand); }),
            ("omit-rng-swap", "raw signature use", m => { var call = Call(m, inWorld, "SwapRandom"); call.OpCode = OpCodes.Pop; call.Operand = null; }),
            ("wrong-rng-domain", "qualified candidate differs at method:" + inWorld + ":body", m => Call(m, inWorld, "SwapRandom").Previous.Operand = "wrong-update-domain"),
            ("callvirt-null-semantics", "qualified candidate differs at method:" + Program.MainUpdateName + ":body", m => Call(m, Program.MainUpdateName, "Invoke").OpCode = OpCodes.Call),
            ("missing-update-end", "raw signature use", m => { var hook = Call(m, Program.MainUpdateName, "FrameEnd"); hook.OpCode = OpCodes.Nop; hook.Operand = null; }),
            ("wrong-body-scope", "qualified candidate differs at method:" + Program.BodyName + ":body", m => Call(m, Program.BodyName, "Enter").Previous.Operand = 18),
            ("wrong-housing-scope", "qualified candidate differs at method:" + housing + ":body", m => Call(m, housing, "Enter").Previous.Operand = 8),
            ("swallow-original-failure", "raw signature use", m => {
                var method = Program.Method(m, inWorld); var handler = method.Body.ExceptionHandlers.Last();
                var pop = Instruction.Create(OpCodes.Pop); method.Body.GetILProcessor().InsertBefore(handler.HandlerStart, pop);
                handler.HandlerType = ExceptionHandlerType.Catch; handler.CatchType = m.ImportReference(m.GetTypeReferences().First(t => t.FullName == "System.Exception"));
                handler.TryEnd = handler.HandlerStart = pop;
                var end = handler.HandlerEnd.Previous; end.OpCode = OpCodes.Leave; end.Operand = handler.HandlerEnd;
            }),
            ("retarget-original-branch", "qualified candidate differs at method:" + Program.BodyName + ":body", m => { var method = Program.Method(m, Program.BodyName); method.Body.Instructions.First(i => i.Operand is Instruction).Operand = method.Body.Instructions.Last(); }),
            ("observer-byref-alias", "qualified candidate differs at method:", m => {
                var site = Helper(m, "EmitScene").Body.Instructions.First(i => i.OpCode == OpCodes.Ldflda && i.Operand is FieldReference f && f.Name == "PlayerX");
                site.Operand = ((FieldReference)site.Operand).DeclaringType.Resolve().Fields.Single(f => f.Name == "PlayerY");
            }),
            ("transaction-array-alias", "qualified candidate differs at method:", m => {
                var helper = m.Types.Single(t => t.FullName == UpdatePatcher.HelperName);
                var site = helper.Methods.Where(x => x.HasBody).SelectMany(x => x.Body.Instructions).First(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.Name == "FrameMetrics");
                site.Operand = helper.Fields.Single(f => f.Name == "WindowMetrics");
            }),
            ("raw-netmode-context-alias", "qualified candidate differs at method:", m => {
                var helper = m.Types.Single(t => t.FullName == UpdatePatcher.HelperName);
                var site = helper.Methods.Where(x => x.HasBody).SelectMany(x => x.Body.Instructions).First(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.DeclaringType.FullName == "Terraria.Main" && f.Name == "netMode");
                site.Operand = m.Types.Single(t => t.FullName == "Terraria.Main").Fields.Single(f => f.Name == "maxTilesX");
            }),
            ("omit-rng-finally-cleanup", "raw signature use", m => { var call = Call(m, inWorld, "Dispose"); call.OpCode = OpCodes.Pop; call.Operand = null; }),
            ("omit-original-finally-cleanup", "raw signature use", m => { var call = Call(m, Program.RunGameName, "Dispose"); call.OpCode = OpCodes.Pop; call.Operand = null; }),
            ("wrong-vector-scope", "qualified TypeRef scope changed", m => m.GetTypeReferences().Single(t => t.FullName == "Microsoft.Xna.Framework.Vector2").Scope = m.AssemblyReferences.Single(r => r.Name == "mscorlib")),
            ("changed-original-access", "original method metadata/token changed:", m => { var method = Program.Method(m, Program.MainUpdateName); method.Attributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public; }),
            ("primitive-named-alias", "raw signature use differs:", m => m.Types.Single(t => t.FullName == UpdatePatcher.HelperName).Fields.Single(f => f.Name == "Version").FieldType = new TypeReference("System", "Int32", m, m.TypeSystem.CoreLibrary, true))
        };
        var rows = new List<object>();
        void Reject(string name, byte[] bytes, string reason) {
            string path = Path.Combine(output, name + ".exe");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            string? rejection = null;
            try { Verify(originalBytes, bytes, receipts, inputPath); }
            catch (InvalidDataException error) { rejection = error.Message; }
            Require(rejection != null && rejection.StartsWith(reason, StringComparison.Ordinal), "static negative rejected for wrong reason: " + name + "; wanted=" + reason + "; actual=" + rejection);
            rows.Add(new { name, sha256 = Sha(bytes), rejection, beforeFixtureRemapping = true });
        }
        foreach (var test in cases) {
            using var resolver = UpdatePatcher.Resolver(inputPath);
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
            test.Mutate(changed.MainModule);
            using var stream = new MemoryStream(); changed.Write(stream, new WriterParameters { Timestamp = 0 });
            Reject(test.Name, stream.ToArray(), test.Reason);
        }
        using var image = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes));
        var state = image.MainModule.Types.Single(t => t.FullName == UpdatePatcher.HelperName).Fields.Single(f => f.Name == "WindowState");
        byte[] wrongKind = RenderRawSignatures.FlipKindForProof(candidateBytes, state.MetadataToken.ToInt32(), state.FieldType.MetadataToken.ToInt32(), false);
        Reject("raw-state-class-kind", wrongKind, "raw signature use differs:");
        string? namedKindRejection = null;
        try { Signatures(wrongKind, receipts.Select(r => r.Method).ToHashSet(), inputPath); }
        catch (InvalidDataException error) { namedKindRejection = error.Message; }
        Require(namedKindRejection != null && namedKindRejection.StartsWith("invalid raw named type kind:", StringComparison.Ordinal), "raw CLASS/VALUETYPE mismatch was not rejected against defining SDK type");
        var malformedPrimitives = Invoke("RawSignatures", "VerifyMalformedRejection", output)!;
        var result = new { passed = true, candidateSha256 = Sha(candidateBytes), rejectedCandidates = rows, namedKindRejection, malformedPrimitives };
        Json(Path.Combine(output, "static-negative-proof.json"), result); return result;
    }
}
