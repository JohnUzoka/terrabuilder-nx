namespace StackStateProof;

using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static StackStateProofCommon;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class Probe
{
    internal const string Target = "System.Void Terraria.GameContent.Drawing.TileDrawing::DrawSingleTile(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2,System.Int32,System.Int32)";
    internal static readonly int[] Tokens = { 0x0600453a,0x06004541,0x0600454d,0x0600454f,0x06004550,0x06004551,0x06004552,0x06004555 };
    internal static string Qualified(MemberReference m) => m.FullName + " @ " + (m is TypeReference t ? t : m.DeclaringType).Scope;
    internal static MethodDefinition Single(AssemblyDefinition a) => (MethodDefinition)a.MainModule.LookupToken(Tokens[0]);
    internal static byte[] Build(AssemblyDefinition baseline, AssemblyDefinition candidate, List<object> receipts)
    {
        using var probe = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("ExactStackState54", new Version(54,0)),"ExactStackState54",ModuleKind.Dll);
        var module=probe.MainModule;
        var hooks=new TypeDefinition("Probe","Hooks",TA.Public|TA.Abstract|TA.Sealed,module.TypeSystem.Object); module.Types.Add(hooks);
        var hosts = new Dictionary<string,Type> {
            ["Terraria.GameContent.Drawing.TileDrawing"]=typeof(CostDrawing), ["Terraria.GameContent.Drawing.TileDrawingBase"]=typeof(CostDrawing),
            ["Terraria.Main"]=typeof(CostWorld), ["Terraria.Tile"]=typeof(CostCell), ["Terraria.Player"]=typeof(CostPlayer), ["Terraria.HitTile"]=typeof(object),
            ["Microsoft.Xna.Framework.Vector2"]=typeof(CostVec), ["Microsoft.Xna.Framework.Vector3"]=typeof(CostVec3), ["Microsoft.Xna.Framework.Vector4"]=typeof(CostVec4), ["Microsoft.Xna.Framework.Point"]=typeof(CostPoint), ["Microsoft.Xna.Framework.Rectangle"]=typeof(CostRect), ["Microsoft.Xna.Framework.Color"]=typeof(CostColor), ["Microsoft.Xna.Framework.Graphics.SpriteEffects"]=typeof(int),
            ["Terraria.Graphics.TileBatch"]=typeof(CostBatch), ["Terraria.Graphics.VertexColors"]=typeof(CostVertices), ["Terraria.GameContent.Drawing.DrawBlackHelper"]=typeof(CostBlack),
            ["Terraria.FocusHelper"]=typeof(CostFocus), ["Terraria.GameContent.TextureAssets"]=typeof(CostTextures),
            ["ReLogic.Content.Asset`1<Microsoft.Xna.Framework.Graphics.Texture2D>"]=typeof(CostAsset), ["Microsoft.Xna.Framework.Graphics.Texture2D"]=typeof(CostTexture),
            ["Terraria.GameContent.Drawing.TileDrawing/TileCounterType"]=typeof(int), ["Terraria.ID.TileID/Sets"]=typeof(CostSets), ["Terraria.Lighting"]=typeof(CostLighting), ["Terraria.Dust"]=typeof(CostDust), ["Terraria.Utilities.UnifiedRandom"]=typeof(CostRandom), ["Terraria.Utilities.FastRandom"]=typeof(CostFastRandom),
            ["Terraria.Utils"]=typeof(CostUtils), ["Terraria.WorldGen"]=typeof(CostUtils), ["Terraria.GameContent.Liquid.LiquidRenderer"]=typeof(CostUtils), ["Terraria.GameContent.PortalHelper"]=typeof(CostUtils),
            ["Terraria.Graphics.Effects.Filters"]=typeof(CostFilters), ["Terraria.Graphics.Effects.FilterManager"]=typeof(CostFilterManager), ["Terraria.Graphics.Effects.EffectManager`1<Terraria.Graphics.Effects.Filter>"]=typeof(CostFilterManager), ["Terraria.Graphics.Effects.Filter"]=typeof(CostFilter), ["Terraria.Graphics.Effects.GameEffect"]=typeof(CostFilter),
            ["Terraria.GameContent.TilePaintSystemV2/CageTopVariationkey"]=typeof(CostCage), ["Terraria.GameContent.Drawing.TileDrawing/TileFlameData"]=typeof(CostFlame),
            ["Terraria.Chest"]=typeof(CostChest), ["Terraria.DataStructures.TileEntityType`1<Terraria.GameContent.Tile_Entities.TETrainingDummy>"]=typeof(CostEntity), ["Terraria.DataStructures.TileEntity"]=typeof(CostEntity), ["Terraria.GameContent.Tile_Entities.TETrainingDummy"]=typeof(CostDummy), ["Terraria.NPC"]=typeof(CostNpc), ["Terraria.Minecart"]=typeof(CostMinecart)
        };
        var allCopies=new List<(MethodDefinition source,MethodDefinition target)>();
        var qualifiedTypes=new Dictionary<string,string>();
        foreach(var (game,prefix) in new[]{(baseline,"Baseline"),(candidate,"Candidate")})
        {
            var publicSource=Types(game.MainModule).Single(t=>t.FullName=="Terraria.DataStructures.TileDrawInfo");
            var publicScratch=new TypeDefinition("Probe",prefix+"PublicScratch",TA.Public,module.TypeSystem.Object);module.Types.Add(publicScratch);
            var sourceState=prefix=="Candidate"?Types(game.MainModule).Single(t=>t.FullName=="Terraria.GameContent.Drawing.TileDrawState54"):publicSource;
            var state=prefix=="Candidate"?new TypeDefinition("Probe","StackState",TA.Public|TA.SequentialLayout|TA.Sealed,module.ImportReference(typeof(ValueType))):publicScratch;
            if(prefix=="Candidate")module.Types.Add(state);
            void VerifyScope(TypeReference t)
            {
                if(t is TypeSpecification ts){VerifyScope(ts.ElementType);if(t is GenericInstanceType gi)foreach(var arg in gi.GenericArguments)VerifyScope(arg);return;}
                if(t is GenericParameter)return;
                string scope=t.Scope is AssemblyNameReference a?a.Name:t.Scope is ModuleDefinition md?md.Assembly.Name.Name:t.Scope.Name;
                string expected=t.Namespace.StartsWith("System")?"mscorlib":t.FullName.StartsWith("Microsoft.Xna.")?"FNA":t.FullName.StartsWith("ReLogic.")?"ReLogic":"Terraria";
                Require(scope==expected || expected=="mscorlib"&&scope is "System" or "System.Core", "source scope mismatch "+Qualified(t));
            }
            TypeReference TypeMap(TypeReference t)
            {
                VerifyScope(t);
                if(t.FullName==publicSource.FullName)return publicScratch;
                if(t.FullName==sourceState.FullName)return state;
                if(hosts.TryGetValue(t.FullName,out var host)) {var mapped=host.IsPrimitive||host==typeof(object)||host==typeof(string)?Primitive(module,host.FullName!):module.ImportReference(host);qualifiedTypes[Qualified(t)]=mapped.FullName;return mapped;}
                if(t is ArrayType a){var n=new ArrayType(TypeMap(a.ElementType),a.Rank);for(int i=0;i<a.Rank;i++)n.Dimensions[i]=new ArrayDimension(a.Dimensions[i].LowerBound,a.Dimensions[i].UpperBound);return n;}
                if(t is ByReferenceType r)return new ByReferenceType(TypeMap(r.ElementType));
                if(t is GenericInstanceType g){var n=new GenericInstanceType(TypeMap(g.ElementType));foreach(var arg in g.GenericArguments)n.GenericArguments.Add(TypeMap(arg));return n;}
                if(t is GenericParameter)return t;
                Require(t.Namespace.StartsWith("System"),"unmapped fixture type "+Qualified(t));
                if(t.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr)return Primitive(module,t.FullName);
                return module.ImportReference(t);
            }
            foreach(var src in prefix=="Candidate"?new[]{publicSource,sourceState}:new[]{publicSource})
            {
                var dst=src==publicSource?publicScratch:state;
                foreach(var f in src.Fields)dst.Fields.Add(new FieldDefinition(f.Name,(f.Attributes&~FA.FieldAccessMask)|FA.Public,TypeMap(f.FieldType)));
                receipts.Add(new{fieldLayoutSource=Qualified(src),fieldLayoutDestination=Qualified(dst),fields=src.Fields.Select((f,i)=>new{source=Qualified(f),sourceType=Qualified(f.FieldType),destination=Qualified(dst.Fields[i]),destinationType=Qualified(dst.Fields[i].FieldType)})});
            }
            var ctor=publicSource.Methods.Single(m=>m.IsConstructor&&!m.IsStatic);Define(ctor,publicScratch,TypeMap);
            var copies=new List<(MethodDefinition source,MethodDefinition target)>{(ctor,publicScratch.Methods.Single())};
            var sourceMap=new Dictionary<string,MethodDefinition>();
            var wrappers=new List<(MethodDefinition source,MethodDefinition target,MethodDefinition wrapper)>();
            foreach(int token in Tokens)
            {
                var source=(MethodDefinition)game.MainModule.LookupToken(token);
                Require(source.DeclaringType.FullName=="Terraria.GameContent.Drawing.TileDrawing","wrong source token");
                var target=new MethodDefinition(prefix+(token==Tokens[0]?"":"_"+source.Name),MA.Public|MA.Static,TypeMap(source.ReturnType)){ImplAttributes=source.ImplAttributes};
                target.Parameters.Add(new ParameterDefinition("owner",Mono.Cecil.ParameterAttributes.None,TypeMap(source.DeclaringType)));
                foreach(var p in source.Parameters)target.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,TypeMap(p.ParameterType)));
                hooks.Methods.Add(target);copies.Add((source,target));
                if(token==Tokens[0])sourceMap.Add(Qualified(source),target);
                else
                {
                    var wrapper=new MethodDefinition(prefix+"_Boundary_"+source.Name,MA.Public|MA.Static,module.TypeSystem.Void);
                    foreach(var p in target.Parameters)wrapper.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,p.ParameterType));
                    hooks.Methods.Add(wrapper);wrappers.Add((source,target,wrapper));sourceMap.Add(Qualified(source),wrapper);
                }
            }
            object Member(object o)
            {
                if(o is TypeReference t)return TypeMap(t);
                if(o is FieldReference f)
                {
                    VerifyScope(f.DeclaringType);
                    if(f.DeclaringType.FullName==publicSource.FullName||f.DeclaringType.FullName==sourceState.FullName)
                    {
                        var owner=f.DeclaringType.FullName==publicSource.FullName?publicScratch:state;
                        return owner.Fields.Single(x=>x.Name==f.Name&&x.FieldType.FullName==TypeMap(f.FieldType).FullName);
                    }
                    if(hosts.TryGetValue(f.DeclaringType.FullName,out var host))
                    {
                        var field=host.GetField(f.Name,BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static);Require(field!=null,"unmapped field "+Qualified(f));
                        var mapped=module.ImportReference(field!);var expected=TypeMap(f.FieldType);
                        Require(mapped.FieldType.FullName==expected.FullName||mapped.FieldType is ArrayType aa&&expected is ArrayType ab&&aa.Rank==ab.Rank&&aa.ElementType.FullName==ab.ElementType.FullName,"field type mismatch "+Qualified(f));mapped.FieldType=expected;return mapped;
                    }
                    return new FieldReference(f.Name,TypeMap(f.FieldType),TypeMap(f.DeclaringType));
                }
                if(o is GenericInstanceMethod g)
                {
                    if(g.DeclaringType.FullName=="Terraria.Utils"&&g.Name=="IndexInRange")return module.ImportReference(typeof(CostUtils).GetMethod("IndexInRange")!);
                    if(g.DeclaringType.FullName=="Terraria.DataStructures.TileEntity"&&g.Name=="TryGet")return module.ImportReference(typeof(CostEntity).GetMethod("TryGet")!);
                    var element=(MethodReference)Member(g.ElementMethod);var n=new GenericInstanceMethod(element);foreach(var arg in g.GenericArguments)n.GenericArguments.Add(TypeMap(arg));return n;
                }
                if(o is MethodReference m)
                {
                    VerifyScope(m.DeclaringType);
                    if(sourceMap.TryGetValue(Qualified(m),out var actual))return actual;
                    if(m.DeclaringType.FullName==publicSource.FullName)return publicScratch.Methods.Single(x=>x.Name==m.Name&&x.Parameters.Count==m.Parameters.Count);
                    if(hosts.TryGetValue(m.DeclaringType.FullName,out var host))
                    {
                        bool Compatible(Type type,TypeReference expected){if(expected is GenericParameter gp&&m.DeclaringType is GenericInstanceType gi)expected=gi.GenericArguments[gp.Position];return module.ImportReference(type).FullName==TypeMap(expected).FullName;}
                        var methods=(m.Name==".ctor"?host.GetConstructors().Cast<MethodBase>():host.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static).Where(x=>x.Name==m.Name)).Where(x=>x.GetParameters().Length==m.Parameters.Count&&x.IsStatic!=m.HasThis&&x.GetParameters().Zip(m.Parameters).All(p=>Compatible(p.First.ParameterType,p.Second.ParameterType))).ToArray();
                        Require(methods.Length==1,"unmapped/ambiguous boundary "+Qualified(m));
                        if(methods[0] is MethodInfo mi)Require(Compatible(mi.ReturnType,m.ReturnType),"return mismatch "+Qualified(m));
                        return module.ImportReference(methods[0]);
                    }
                    var n=new MethodReference(m.Name,TypeMap(m.ReturnType),TypeMap(m.DeclaringType)){HasThis=m.HasThis,ExplicitThis=m.ExplicitThis,CallingConvention=m.CallingConvention};
                    foreach(var gp in m.GenericParameters)n.GenericParameters.Add(new GenericParameter(gp.Name,n));foreach(var p in m.Parameters)n.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));return n;
                }
                return o;
            }
            foreach(var (source,target) in copies)CopyBody(source,target,TypeMap,Member);
            foreach(var (source,target) in copies)SourceAudit.Check(source,target,TypeMap,Member);
            foreach(var (source,target,wrapper) in wrappers)
            {
                var scratch=wrapper.Parameters.Single(p=>p.ParameterType.FullName==state.FullName||p.ParameterType is ByReferenceType r&&r.ElementType==state);
                var observe=new GenericInstanceMethod(module.ImportReference(typeof(FieldFixture).GetMethod("Observe")!));observe.GenericArguments.Add(state);
                var il=wrapper.Body.GetILProcessor();
                void Observe(bool after){il.Append(Instruction.Create(scratch.ParameterType.IsByReference?OpCodes.Ldarg:OpCodes.Ldarga,scratch));il.Append(Instruction.Create(OpCodes.Ldstr,source.Name));il.Append(Instruction.Create(after?OpCodes.Ldc_I4_1:OpCodes.Ldc_I4_0));il.Append(Instruction.Create(OpCodes.Call,observe));}
                Observe(false);foreach(var p in wrapper.Parameters)il.Append(Instruction.Create(OpCodes.Ldarg,p));il.Append(Instruction.Create(OpCodes.Call,target));Observe(true);il.Append(Instruction.Create(OpCodes.Ret));
            }
            allCopies.AddRange(copies);
        }
        module.Mvid=new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",allCopies.Select(p=>Body(p.target))))).AsSpan(0,16));
        using var stream=new MemoryStream();probe.Write(stream,new WriterParameters{Timestamp=0});var bytes=stream.ToArray();
        using var serialized=AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach(var (source,target) in allCopies)
        {
            var actual=Types(serialized.MainModule).SelectMany(t=>t.Methods).Single(m=>m.FullName==target.FullName);
            if(Fingerprint(actual)!=Fingerprint(target))
            {
                var expectedLines=Body(target).Split('\n');var actualLines=Body(actual).Split('\n');
                int difference=Enumerable.Range(0,Math.Min(expectedLines.Length,actualLines.Length)).FirstOrDefault(i=>expectedLines[i]!=actualLines[i],-1);
                throw new InvalidDataException("serialized clone changed IL "+target.FullName+" line="+difference+" expected="+(difference<0?expectedLines.Length.ToString():expectedLines[difference])+" actual="+(difference<0?actualLines.Length.ToString():actualLines[difference]));
            }
            Require(actual.Body.Instructions.Count==source.Body.Instructions.Count&&actual.Body.Variables.Count==source.Body.Variables.Count&&actual.Body.ExceptionHandlers.Count==source.Body.ExceptionHandlers.Count&&actual.Body.InitLocals==source.Body.InitLocals,"clone shape changed");
            var bindings=source.Body.Instructions.Select((i,index)=>(i,index)).Where(x=>x.i.Operand is MemberReference).Select(x=>new{index=x.index,offset=x.i.Offset,opcode=x.i.OpCode.Name,source=Qualified((MemberReference)x.i.Operand),destination=Qualified((MemberReference)actual.Body.Instructions[x.index].Operand)}).ToArray();
            receipts.Add(new{source=Qualified(source),sourceToken=source.MetadataToken.ToInt32(),sourceMvid=source.Module.Mvid,sourceBodySha256=Fingerprint(source),fixture=actual.FullName,fixtureBodySha256=Fingerprint(actual),instructions=actual.Body.Instructions.Count,initLocals=actual.Body.InitLocals,parameters=source.Parameters.Select((p,i)=>new{source=Qualified(p.ParameterType),destination=Qualified(actual.Parameters[i+(source.IsConstructor?0:1)].ParameterType)}),locals=source.Body.Variables.Select((v,i)=>new{source=Qualified(v.VariableType),destination=Qualified(actual.Body.Variables[i].VariableType)}),bindings});
        }
        receipts.Add(new{sourceInstructionAuditPassed=true,sourceInstructionsChecked=allCopies.Sum(p=>p.source.Body.Instructions.Count),methodsChecked=allCopies.Count,normalization="Argument/local/constant macro encodings and short/long branch encoding only; branch indices, constants, qualified member remaps, signatures, locals, InitLocals and EH must match source"});
        receipts.Add(new{qualifiedTypes});return bytes;
    }
}
