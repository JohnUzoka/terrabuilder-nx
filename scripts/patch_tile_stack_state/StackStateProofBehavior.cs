namespace StackStateProof;

using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static StackStateProofCommon;

internal static class Behavior
{
    internal record Scenario(string Name,ushort Type=0,Action<CostDrawing>? Configure=null,string? Failure=null,string? Mutation=null,string At="CacheSpecialDraws_Part2:after",bool DataMutation=false,bool NullTile=false,bool BadIndex=false);
    internal record Result(string[] Trace,long[] Values,string[] Observations,string[] Methods,string State,string? Error,string? ErrorMessage,bool FailureIdentity,int Mutations,int FreshArrays);
    static readonly string[] Fields={"tileCache","typeCache","tileFrameX","tileFrameY","tileWidth","tileHeight","tileTop","halfBrickHeight","addFrX","addFrY","tileSpriteEffect","glowTexture","glowSourceRect","glowColor","tileLight","drawTexture","colorSlices"};
    static CostCell Cell => CostWorld.tile[2,2]!;
    static List<Scenario> Scenarios()
    {
        var cases=new List<Scenario>{
            new("basic_9slice"),new("null_tile",NullTile:true),new("array_bounds",BadIndex:true),
            new("liquid_early_return",518,_=>Cell.liquid=1),new("liquid_no_liquid",518),
            new("outline",Configure:_=>CostSets.HasOutlines[0]=true),new("minecart",314),new("tree",171),new("cactus",80),new("plant_reload",83),
            new("return_751",751,_=>Cell.frameX=90),new("return_752",752,_=>Cell.frameX=90),new("return_323",323,_=>Cell.frameX=90),
            new("two_GetColor",72,_=>Cell.frameX=36),new("crystal_reload",129,_=>{CostWorld.tileGlowMask[129]=0;Cell.frameX=324;}),
            new("glow_reload",429,_=>CostWorld.tileGlowMask[429]=0),new("filter",725,_=>CostWorld.tileGlowMask[725]=0),
            new("hidden_dark",Configure:_=>{CostFixture.Dark=true;Cell.Hidden=true;}),new("dark",Configure:_=>CostFixture.Dark=true),
            new("glow",Configure:_=>CostFixture.Glow=true),new("over_layer",Configure:_=>CostFixture.TileTop=-2),new("behind_layer",Configure:_=>CostFixture.TileHeight=24),
            new("particles_rng",Configure:d=>{d._isActiveAndNotPaused=true;CostWorld.player[0].dangerSense=CostWorld.player[0].findTreasure=CostWorld.player[0].biomeSight=true;}),
            new("value_rng",Configure:d=>{d._isActiveAndNotPaused=true;CostFixture.UpdateEveryFrame=true;}),
            new("value_rng_skip",Configure:d=>{d._isActiveAndNotPaused=true;CostFixture.UpdateEveryFrame=true;CostFixture.RandomValue=1;}),
            new("data_12_byref_outputs",Configure:_=>CostSets.HasOutlines[0]=true,DataMutation:true),
            new("nonzero_frames",72,_=>{Cell.frameX=90;Cell.frameY=36;}),
            new("chest_actual_writes",21,_=>{CostSets.BasicChest[21]=true;Cell.frameX=48*36+18;Cell.frameY=18;}),
            new("dummy_actual_write",378),new("rope_actual_draw",380,_=>CostUtils.Rope=true),
            new("slice4",Configure:d=>d._highQualityLightingRequirement=new(255,255,255,255)),
            new("slice_low",Configure:d=>{d._highQualityLightingRequirement=new(255,255,255,255);d._mediumQualityLightingRequirement=new(255,255,255,255);}),
            new("slice_replace_local_array",Configure:_=>CostLighting.ReplaceSlices=true),new("fullbright",Configure:_=>Cell.Fullbright=true),
            new("retro",Configure:_=>CostLighting.NotRetro=false),new("halfbrick",Configure:_=>Cell.Half=true),
            new("slope1",Configure:_=>Cell.Slope=1),new("slope2",Configure:_=>Cell.Slope=2),new("slope3",Configure:_=>Cell.Slope=3),new("slope4",Configure:_=>Cell.Slope=4),
            new("platform",Configure:_=>CostSets.Platforms[0]=true),new("cage",Configure:_=>{CostSets.CritterCageLidStyle[0]=2;CostSets.DontDrawTileSliced[0]=true;Cell.frameY=50;}),
            new("flame_rng",4,_=>{CostWorld.tileFlame[4]=true;CostSets.DontDrawTileSliced[4]=true;}),
            new("flame548",548,_=>CostSets.DontDrawTileSliced[548]=true),new("flame613",613,_=>CostSets.DontDrawTileSliced[613]=true),new("flame614",614,_=>CostSets.DontDrawTileSliced[614]=true),new("flame593",593,_=>CostSets.DontDrawTileSliced[593]=true),
            new("tree_frames",171,_=>{Cell.frameX=10;Cell.frameY=7;}),new("track_bumper",314,_=>{Cell.frameX=2;Cell.frameY=3;})
        };
        foreach(var field in Fields)cases.Add(new("callback_"+field,Configure:_=>CostFixture.Glow=true,Mutation:field));
        foreach(var boundary in new[]{"GetColor","GetTileDrawData","GetTileDrawTexture","GetTileOutlineInfo","DrawTiles_GetLightOverride","GetFinalLight","Graphics.Draw","GetColor9Slice","GetFinalLight.Vector3"})cases.Add(new("throw_"+boundary,Configure:_=>CostSets.HasOutlines[0]=true,Failure:boundary));
        cases.Add(new("throw_chest",21,_=>CostSets.BasicChest[21]=true,Failure:"Chest.FindChest"));
        cases.Add(new("throw_track",314,Failure:"TrackColors"));
        cases.Add(new("throw_flame_rng",4,_=>{CostWorld.tileFlame[4]=true;CostSets.DontDrawTileSliced[4]=true;},Failure:"RandomInt"));
        cases.Add(new("throw_rope",380,_=>CostUtils.Rope=true,Failure:"IsRope"));
        return cases;
    }
    static void Reset(ushort type=0)
    {
        GcFixture.Reset();FieldFixture.Reset();CostFixture.Reset(5,5,type);
        foreach(var field in typeof(CostSets).GetFields().Where(f=>f.FieldType==typeof(bool[])))Array.Clear((Array)field.GetValue(null)!);
        Array.Fill(CostSets.CritterCageLidStyle,-1);Array.Fill(CostWorld.tileSolid,true);Array.Clear(CostWorld.tileFrame);
        CostLighting.NotRetro=true;CostLighting.ReplaceSlices=false;CostUtils.Rope=false;
        FieldFixture.ReplacementTexture=new(){Id=17};FieldFixture.ReplacementCell=new(){type=0,frameX=36,frameY=18};
    }
    static Result Execute(Action<CostDrawing,CostVec,CostVec,int,int> action,Scenario scenario)
    {
        Reset(scenario.Type);var owner=new CostDrawing();scenario.Configure?.Invoke(owner);
        if(scenario.NullTile)CostWorld.tile[2,2]=null;
        FieldFixture.Enabled=true;FieldFixture.MutateData=scenario.DataMutation;FieldFixture.MutationField=scenario.Mutation;FieldFixture.MutationBoundary=scenario.At;
        CostFixture.ThrowAt=scenario.Failure;CostFixture.Trace.Clear();CostFixture.Effects=0;FieldFixture.Values.Clear();
        Exception? error=null;try{action(owner,new(10,20),new(3,4),scenario.BadIndex?5:2,2);}catch(Exception e){error=e;}
        string state=string.Join("|",CostFixture.Effects,FieldFixture.FinalState,FieldFixture.Value(CostWorld.tile),FieldFixture.Value(CostFixture.Dust),string.Join(",",owner._chestPositions.Select(p=>$"{p.Key.X}:{p.Key.Y}:{p.Value}")),string.Join(",",owner._trainingDummyTileEntityPositions.Select(p=>$"{p.Key.X}:{p.Key.Y}:{p.Value}")));
        return new(CostFixture.Trace.ToArray(),FieldFixture.Values.ToArray(),FieldFixture.Observations.ToArray(),FieldFixture.Methods.ToArray(),state,error?.GetType().FullName,error?.Message,error==null||ReferenceEquals(error,CostFixture.Failure),FieldFixture.Mutations,FieldFixture.FreshArrays);
    }
    static bool Same(Result a,Result b)=>a.Trace.SequenceEqual(b.Trace)&&a.Values.SequenceEqual(b.Values)&&a.Observations.SequenceEqual(b.Observations)&&a.Methods.SequenceEqual(b.Methods)&&a.State==b.State&&a.Error==b.Error&&a.FailureIdentity==b.FailureIdentity;
    static string Difference(Result a,Result b)=>!a.Trace.SequenceEqual(b.Trace)?"ordered_calls":!a.Values.SequenceEqual(b.Values)?"numeric_arguments":!a.Observations.SequenceEqual(b.Observations)?"scratch_values_or_reference_identity":a.State!=b.State?"side_effects":a.Error!=b.Error?"exception_type":"exception_identity";
    internal static object Run(byte[] bytes,string output)
    {
        var assembly=Assembly.Load(bytes);var hooks=assembly.GetType("Probe.Hooks",true)!;
        var before=hooks.GetMethod("Baseline")!.CreateDelegate<Action<CostDrawing,CostVec,CostVec,int,int>>();var after=hooks.GetMethod("Candidate")!.CreateDelegate<Action<CostDrawing,CostVec,CostVec,int,int>>();
        var cases=Scenarios();var expected=new List<Result>();var results=new List<object>();
        foreach(var scenario in cases)
        {
            var a=Execute(before,scenario);var replay=Execute(before,scenario);var b=Execute(after,scenario);
            Require(Same(a,replay),"nondeterministic baseline "+scenario.Name+":"+Difference(a,replay));
            if(!Same(a,b)){Json(Path.Combine(output,"mismatch.json"),new{scenario.Name,baseline=a,candidate=b});throw new InvalidDataException("candidate mismatch "+scenario.Name+":"+Difference(a,b));}
            if(scenario.Failure!=null)Require(a.FailureIdentity&&a.Error!=null,"exception boundary not reached "+scenario.Name);
            else Require(a.Error==(scenario.NullTile?typeof(NullReferenceException).FullName:scenario.BadIndex?typeof(IndexOutOfRangeException).FullName:null),"unexpected fixture exception "+scenario.Name+":"+a.Error+":"+a.ErrorMessage);
            if(scenario.Mutation!=null)Require(b.Mutations==1,"callback not reached "+scenario.Name);
            expected.Add(a);results.Add(new{scenario.Name,scenario.Type,scenario.Mutation,scenario.Failure,result=b});Console.WriteLine("PASS "+scenario.Name+" calls="+b.Trace.Length+" helpers="+b.Methods.Length);
        }
        Json(Path.Combine(output,"scenarios.json"),results);
        var covered=expected.SelectMany(r=>r.Methods).Distinct().Order().ToArray();Require(covered.Length==14,"not all seven helper bodies completed");
        Require(expected[cases.FindIndex(s=>s.Name=="two_GetColor")].Trace.Count(t=>t=="GetColor")==2,"two GetColor not reached");
        Require(expected[cases.FindIndex(s=>s.Name=="basic_9slice")].Trace.Count(t=>t=="GetColor")==1,"one GetColor not reached");
        Require(expected[cases.FindIndex(s=>s.Name=="null_tile")].Trace.Count(t=>t=="GetColor")==0,"zero GetColor not reached");
        Require(expected[cases.FindIndex(s=>s.Name=="chest_actual_writes")].Observations.Any(x=>x.StartsWith("CacheSpecialDraws_Part2:after:")&&x.Contains("addFrY=76")&&x.Contains("tileHeight=18")),"real chest helper writes not observed");
        Require(expected[cases.FindIndex(s=>s.Name=="dummy_actual_write")].Observations.Any(x=>x.StartsWith("CacheSpecialDraws_Part2:after:")&&x.Contains("addFrY=108")),"real dummy helper write not observed");
        Require(expected[cases.FindIndex(s=>s.Name=="flame_rng")].Trace.Count(t=>t=="RandomInt")>=4,"actual flame RNG loop not reached");
        var rope=expected[cases.FindIndex(s=>s.Name=="rope_actual_draw")].Trace;
        Require(rope.Count(t=>t=="GetTileDrawTexture")==2&&rope.Contains("IsRope"),"actual rope draw path not reached");
        Require(expected[cases.FindIndex(s=>s.Name=="slice4")].Trace.Contains("GetColor4Slice"),"actual four-slice path not reached");
        Require(expected[cases.FindIndex(s=>s.Name=="cage")].Trace.Contains("LookupCageTopDrawTexture"),"actual cage path not reached");
        var publicCtor=new List<object>();
        foreach(var name in new[]{"Baseline","Candidate"})
        {
            var scratch=assembly.GetType("Probe."+name+"PublicScratch",true)!;var a=Activator.CreateInstance(scratch)!;var b=Activator.CreateInstance(scratch)!;
            var aa=(CostVec3[])scratch.GetField("colorSlices")!.GetValue(a)!;var bb=(CostVec3[])scratch.GetField("colorSlices")!.GetValue(b)!;
            Require(scratch.GetFields().Length==19&&!ReferenceEquals(a,b)&&!ReferenceEquals(aa,bb)&&aa.Length==9&&aa.All(v=>v.X==0&&v.Y==0&&v.Z==0),"public ctor invariant failed");publicCtor.Add(new{name,fields=19,arrayLength=9,freshOwner=true,freshArray=true,zero=true});
        }
        var successive=Successive(before,after);var gc=Gc(before,after);var allocations=Allocations(before,after);
        var negatives=Negatives(bytes,cases,expected,output);
        var branchCounts=cases.Select((s,i)=>new{s.Name,getColor=expected[i].Trace.Count(t=>t=="GetColor"),getColor9Slice=expected[i].Trace.Count(t=>t=="GetColor9Slice"),getColor4Slice=expected[i].Trace.Count(t=>t=="GetColor4Slice"),draws=expected[i].Trace.Count(t=>t=="Graphics.Draw"),randomInt=expected[i].Trace.Count(t=>t=="RandomInt")}).ToArray();
        return new{passed=true,scenarioCount=cases.Count,deterministicBaselineReplays=cases.Count,coveredHelpers=covered,branchCounts,publicCtor,successive,gc,allocations,negatives};
    }
    static object Successive(Action<CostDrawing,CostVec,CostVec,int,int> before,Action<CostDrawing,CostVec,CostVec,int,int> after)
    {
        var results=new List<object>();foreach(var (name,action) in new[]{("baseline",before),("candidate",after)})
        {
            Reset();var owner=new CostDrawing();object? previous=null;
            string[]? originalObservations=null;long[]? originalValues=null;
            for(int i=0;i<20;i++)
            {
                FieldFixture.Reset();FieldFixture.Enabled=true;action(owner,default,default,2,2);
                Require(FieldFixture.FreshArrays==1&&FieldFixture.FirstArray!=null&&!ReferenceEquals(previous,FieldFixture.FirstArray),"successive invocation reused array/state");
                originalObservations??=FieldFixture.Observations.ToArray();originalValues??=FieldFixture.Values.ToArray();
                Require(originalObservations.SequenceEqual(FieldFixture.Observations)&&originalValues.SequenceEqual(FieldFixture.Values),"successive invocation retained stale scalar/reference state");
                previous=FieldFixture.FirstArray;((CostVec3[])previous!)[8]=new(){X=999,Y=888,Z=777};
            }
            results.Add(new{name,invocations=20,freshZeroArrayEachCall=true,previousArrayPoisoned=true,allScratchSnapshotsAndNumericArgumentsRepeat=true});
        }return results;
    }
    static object Gc(Action<CostDrawing,CostVec,CostVec,int,int> before,Action<CostDrawing,CostVec,CostVec,int,int> after)
    {
        var results=new List<object>();foreach(var (name,action) in new[]{("baseline",before),("candidate",after)})
        {
            Reset();var owner=new CostDrawing();CostFixture.Record=false;GcFixture.Enabled=true;
            for(int i=0;i<24;i++)action(owner,default,default,2,2);
            Require(GcFixture.Collections==24&&GcFixture.StateChecks==24&&GcFixture.DrawChecks>=24,"GC liveness path not reached");
            Require(GcFixture.TextureMoves>0&&GcFixture.OriginalMoves>0&&GcFixture.ReplacementMoves>0,"GC requested but observed no movement for a live category");
            results.Add(new{name,GcFixture.Collections,GcFixture.TextureMoves,GcFixture.OriginalMoves,GcFixture.ReplacementMoves,GcFixture.StateChecks,GcFixture.DrawChecks,evidence=GcFixture.Evidence.ToArray(),limits="After-helper wrapper observes scratch to extend its liveness; no observer holds strong texture/array references at collection. Original array held in scratch, replacement in real sliced-helper local. Diagnostic addresses are never dereferenced."});
        }GcFixture.Reset();return results;
    }
    static CostVec3[]? AllocationSink;
    static object Allocations(Action<CostDrawing,CostVec,CostVec,int,int> before,Action<CostDrawing,CostVec,CostVec,int,int> after)
    {
        Reset();CostFixture.Record=false;var owner=new CostDrawing();
        for(int i=0;i<2000;i++){before(owner,default,default,2,2);after(owner,default,default,2,2);}
        long Measure(Action<CostDrawing,CostVec,CostVec,int,int> action){long start=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++)action(owner,default,default,2,2);return GC.GetAllocatedBytesForCurrentThread()-start;}
        long arrayStart=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++)AllocationSink=new CostVec3[9];long arrayBytes=GC.GetAllocatedBytesForCurrentThread()-arrayStart;AllocationSink=null;
        var results=new List<object>();for(int i=0;i<3;i++){long a=Measure(before),b=Measure(after);Require(a>b&&a-b==120L*10000,"unexpected owner allocation delta");Require(b==arrayBytes&&arrayBytes>0,"quiet candidate allocation exceeded independent fresh-array control");results.Add(new{round=i,iterations=10000,baselineBytes=a,candidateBytes=b,arrayOnlyControlBytes=arrayBytes,savedBytesPerInvocation=(a-b)/10000});}CostFixture.Record=true;return results;
    }
    static object Negatives(byte[] bytes,List<Scenario> cases,List<Result> expected,string output)
    {
        var results=new List<object>();foreach(var name in new[]{"helper_lost_writeback","nonzero_initial_state","array_reuse","field_height_to_width","remove_GetColor"})
        {
            using var a=AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));a.Name.Name="NegativeStack54_"+name;a.MainModule.Name=a.Name.Name;
            var hooks=Types(a.MainModule).Single(t=>t.Name=="Hooks");var single=hooks.Methods.Single(m=>m.Name=="Candidate");var state=Types(a.MainModule).Single(t=>t.Name=="StackState");
            var cache=hooks.Methods.Single(m=>m.Name=="Candidate_CacheSpecialDraws_Part2");
            if(name=="helper_lost_writeback")
            {
                var p=cache.Parameters.Last();var v=new VariableDefinition(state);cache.Body.Variables.Add(v);cache.Body.InitLocals=true;var il=cache.Body.GetILProcessor();var first=cache.Body.Instructions[0];
                foreach(var ins in new[]{Instruction.Create(OpCodes.Ldarg,p),Instruction.Create(OpCodes.Ldobj,state),Instruction.Create(OpCodes.Stloc,v),Instruction.Create(OpCodes.Ldloca,v),Instruction.Create(OpCodes.Starg,p)})il.InsertBefore(first,ins);
            }
            else if(name=="nonzero_initial_state")
            {
                var store=single.Body.Instructions.First(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name=="colorSlices");var il=single.Body.GetILProcessor();var cursor=store;
                foreach(var ins in new[]{Instruction.Create(OpCodes.Ldloca,single.Body.Variables[0]),Instruction.Create(OpCodes.Ldflda,state.Fields.Single(f=>f.Name=="colorTint")),Instruction.Create(OpCodes.Ldc_I4,17),Instruction.Create(OpCodes.Call,a.MainModule.ImportReference(typeof(CostColor).GetProperty("R")!.SetMethod!))}){il.InsertAfter(cursor,ins);cursor=ins;}
            }
            else if(name=="array_reuse")
            {
                NegativeArray.Reset();var array=single.Body.Instructions.First(i=>i.OpCode==OpCodes.Newarr);array.Previous.OpCode=OpCodes.Nop;array.Previous.Operand=null;array.OpCode=OpCodes.Call;array.Operand=a.MainModule.ImportReference(typeof(NegativeArray).GetMethod("Get")!);
            }
            else if(name=="field_height_to_width")
            {
                var store=cache.Body.Instructions.Single(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name=="tileHeight");store.Operand=state.Fields.Single(f=>f.Name=="tileWidth");
            }
            else
            {
                var call=single.Body.Instructions.First(i=>i.Operand is MethodReference m&&m.Name=="GetColor");var v=new VariableDefinition(((MethodReference)call.Operand).ReturnType);single.Body.Variables.Add(v);call.OpCode=OpCodes.Pop;call.Operand=null;var il=single.Body.GetILProcessor();var cursor=call;
                foreach(var ins in new[]{Instruction.Create(OpCodes.Pop),Instruction.Create(OpCodes.Ldloca,v),Instruction.Create(OpCodes.Initobj,v.VariableType),Instruction.Create(OpCodes.Ldloc,v)}){il.InsertAfter(cursor,ins);cursor=ins;}
            }
            foreach(var m in hooks.Methods.Where(m=>m.HasBody))Widen(m);
            using var stream=new MemoryStream();a.Write(stream,new WriterParameters{Timestamp=0});var mutant=stream.ToArray();File.WriteAllBytes(Path.Combine(output,name+".dll"),mutant);
            var loaded=Assembly.Load(mutant);var action=loaded.GetType("Probe.Hooks",true)!.GetMethod("Candidate")!.CreateDelegate<Action<CostDrawing,CostVec,CostVec,int,int>>();
            string? rejectedBy=null,reason=null,error=null,message=null;
            for(int i=0;i<cases.Count;i++){var actual=Execute(action,cases[i]);if(!Same(expected[i],actual)){rejectedBy=cases[i].Name;reason=Difference(expected[i],actual);error=actual.Error;message=actual.ErrorMessage;break;}}
            Require(rejectedBy!=null,"mutant survived "+name);Require(error!=typeof(InvalidProgramException).FullName&&error!=typeof(MissingMethodException).FullName&&error!=typeof(TypeLoadException).FullName,"mutant did not execute "+name+":"+message);
            results.Add(new{name,sha256=Sha(mutant),rejectedBy,reason,error,message,executed=true});Console.WriteLine("REJECT "+name+" by="+rejectedBy+" reason="+reason);
        }Json(Path.Combine(output,"negative-results.json"),results);return results;
    }
}
public static class NegativeArray
{
    static CostVec3[]? Cached;
    public static void Reset()=>Cached=null;
    public static CostVec3[] Get()=>Cached??=new CostVec3[9];
}
