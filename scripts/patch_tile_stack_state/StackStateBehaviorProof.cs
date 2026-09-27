using System.Text.Json;
using Mono.Cecil;
using StackStateProof;
using static StackStateProof.StackStateProofCommon;

internal static class StackStateBehaviorProof
{
    internal const string BaselineSha256="90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22";
    internal const string CandidateSha256="9782543073b4f66d629126eec2387b9c8e99fdca03bee143750b756107ea409d";

    internal static object Run(string baselinePath,string candidatePath,string fnaPath,string proofDirectory)
    {
        Directory.CreateDirectory(proofDirectory);
        string resultPath=Path.Combine(proofDirectory,"proof.json");
        // A failed rerun must never leave a previous passed report authoritative.
        Json(resultPath,new{passed=false,status="started",candidateSha256=(string?)null,baselinePath,candidatePath,fnaPath});
        byte[] baselineBytes=File.ReadAllBytes(baselinePath),candidateBytes=File.ReadAllBytes(candidatePath),fnaBytes=File.ReadAllBytes(fnaPath);
        Json(resultPath,new{passed=false,status="inputs-read",baselineSha256=Sha(baselineBytes),candidateSha256=Sha(candidateBytes),fnaSha256=Sha(fnaBytes)});
        Require(Sha(baselineBytes)==BaselineSha256,"stack-state proof requires exact frozen baseline52");
        Require(Sha(candidateBytes)==CandidateSha256,"stack-state proof requires exact final candidate03");
        using var baseline=AssemblyDefinition.ReadAssembly(new MemoryStream(baselineBytes));
        using var candidate=AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes));
        using var fna=AssemblyDefinition.ReadAssembly(new MemoryStream(fnaBytes));
        Require(fna.Name.Name=="FNA","FNA witness is not an FNA assembly");
        foreach(var game in new[]{baseline,candidate})
            Require(game.MainModule.AssemblyReferences.Single(r=>r.Name=="FNA").FullName==fna.Name.FullName,"FNA witness identity differs from serialized game reference");
        var methods=Probe.Tokens.Select(t=>(MethodDefinition)baseline.MainModule.LookupToken(t)).ToArray();
        Json(Path.Combine(proofDirectory,"inventory.json"),new{methods=methods.Select(m=>new{method=Probe.Qualified(m),instructions=m.Body.Instructions.Count,calls=m.Body.Instructions.Where(i=>i.Operand is MethodReference).Select(i=>Probe.Qualified((MethodReference)i.Operand)).Distinct(),fields=m.Body.Instructions.Where(i=>i.Operand is FieldReference).Select(i=>Probe.Qualified((FieldReference)i.Operand)).Distinct(),locals=m.Body.Variables.Select(v=>Probe.Qualified(v.VariableType))})});
        var receipts=new List<object>();var bytes=Probe.Build(baseline,candidate,receipts);
        File.WriteAllBytes(Path.Combine(proofDirectory,"ExactStackState54.dll"),bytes);Json(Path.Combine(proofDirectory,"bindings.json"),receipts);
        var behavior=Behavior.Run(bytes,proofDirectory);
        var sourceAudit=JsonSerializer.SerializeToElement(receipts).EnumerateArray().Single(e=>e.TryGetProperty("sourceInstructionAuditPassed",out _));
        string runnerPath=typeof(StackStateBehaviorProof).Assembly.Location;
        Require(!string.IsNullOrEmpty(runnerPath)&&File.Exists(runnerPath),"cannot hash executing proof runner");
        var report=new{
            passed=true,baselineSha256=Sha(baselineBytes),candidateSha256=Sha(candidateBytes),fnaSha256=Sha(fnaBytes),fnaIdentity=fna.Name.FullName,
            probeSha256=Sha(bytes),runnerSha256=Sha(File.ReadAllBytes(runnerPath)),sourceCloneAudit=sourceAudit,behavior,
            hostRuntime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            scope="All eight serialized scratch methods and both original public constructors cloned and instruction-audited against source under explicit qualified member/macro remapping. Every changed helper call goes through before/after observers into the actual cloned body, preserving candidate managed-byref signatures. External dependencies are deterministic fixtures, not original game or FNA implementations.",
            limits=new[]{
                "All seven helper methods complete for at least one scenario; not every instruction, branch, tile or neighboring-world configuration. Flame548 defaults do not establish its nontrivial glow branch. Many flame variants, complex platforms and cages remain untested.",
                "GetTileDrawData, GetTileOutlineInfo, GetColor and slice lighting, GetFinalLight, graphics, assets, RNG, chest/entity queries and other external dependencies are deterministic host fixtures. No rendered-pixel/GPU or full-game equivalence.",
                "Callback mutation is an explicit host wrapper observer, not an original game callback or private reflection/IL-hook compatibility claim. Candidate owner-class identity is intentionally not preserved.",
                "The after-helper observer extends scratch liveness. At collection, observers retain only weak texture/array references and diagnostic integer addresses. Original array is in scratch; replacement is in the real sliced-helper local. Only forced compacting host gen0 collections are covered.",
                "Allocation evidence is quiet warmed host per-thread accounting with independent array-only control, not native/Switch profiling or FPS. OOM/allocation/GC behavior equivalence is intentionally not claimed.",
                "No Switch/native-GC, old-generation/concurrent-GC, multithreaded, hardware or game-wide validation; this report alone makes no publication/adoption decision."
            }
        };
        Json(resultPath,report);
        return report;
    }
}
