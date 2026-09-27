using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using FieldDefinition = Mono.Cecil.FieldDefinition;
using ParameterDefinition = Mono.Cecil.ParameterDefinition;

internal static class ReuseAudit
{
    static bool Changed(MethodDefinition method) => method.FullName is Program.DrawName or Program.SingleName;
    static bool New(MethodDefinition method) => ReusePatcher.IsAdded(method.DeclaringType) || ReusePatcher.IsReset(method);
    internal static object Preservation(AssemblyDefinition original, AssemblyDefinition changed, ReusePatcher.Plan[] plans)
    {
        var a=original.MainModule; var b=changed.MainModule;
        string Meta(string method, object value) => (string)Invoke("Preservation",method,value)!;
        Require(a.Name==b.Name && a.Kind==b.Kind && a.Architecture==b.Architecture && a.Attributes==b.Attributes && a.RuntimeVersion==b.RuntimeVersion && a.EntryPoint?.FullName==b.EntryPoint?.FullName && original.Name.FullName==changed.Name.FullName,"assembly metadata changed");
        Require(a.AssemblyReferences.Select(r=>r.FullName).SequenceEqual(b.AssemblyReferences.Select(r=>r.FullName)) && a.ModuleReferences.Select(r=>r.Name).SequenceEqual(b.ModuleReferences.Select(r=>r.Name)),"original references changed");
        foreach(string name in new[]{"Attributes","Security"}) Require(Meta(name,original)==Meta(name,changed),"assembly attributes/security changed");
        Require(Meta("Attributes",a)==Meta("Attributes",b),"module attributes changed");
        var oldTypes=Types(a).ToArray(); var retained=Types(b).Where(t=>!ReusePatcher.IsAdded(t)).ToArray();
        Require(oldTypes.Select(t=>t.FullName).SequenceEqual(retained.Select(t=>t.FullName)),"original types changed");
        int bodies=0,fields=0;
        foreach(var before in oldTypes)
        {
            var after=retained.Single(t=>t.FullName==before.FullName);
            Require(Meta("TypeMetadata",before)==Meta("TypeMetadata",after),"type metadata changed "+before.FullName);
            Require(before.Fields.Count==after.Fields.Count && before.Methods.Count==after.Methods.Count(m=>!ReusePatcher.IsReset(m)),"original member counts changed");
            foreach(var field in before.Fields){Require(Meta("FieldMetadata",field)==Meta("FieldMetadata",after.Fields.Single(f=>f.Name==field.Name)),"original field changed");fields++;}
            foreach(var method in before.Methods)
            {
                var target=after.Methods.Single(m=>m.FullName==method.FullName);
                Require(Meta("MethodMetadata",method)==Meta("MethodMetadata",target),"method signature/flags changed "+method.FullName);
                if(!Changed(method)){Require(Body(method)==Body(target),"unrelated method body changed "+method.FullName);bodies++;}
            }
        }
        Require(a.Resources.Select(r=>Meta("ResourceMetadata",r)).SequenceEqual(b.Resources.Select(r=>Meta("ResourceMetadata",r))),"managed resources changed");
        var restoration=new List<object>();
        foreach(var plan in plans)
        {
            var before=Types(a).SelectMany(t=>t.Methods).Single(m=>m.FullName==plan.Method);
            var after=Types(b).SelectMany(t=>t.Methods).Single(m=>m.FullName==plan.Method);
            MethodDefinition Shell(MethodDefinition source)
            {
                var result=new MethodDefinition(source.Name,source.Attributes,source.ReturnType){ImplAttributes=source.ImplAttributes};
                foreach(var p in source.Parameters) result.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,p.ParameterType));
                Copy(source,result,t=>t,o=>o);return result;
            }
            var copy=Shell(after);var all=copy.Body.Instructions.ToArray();var keep=plan.OriginalIndices.Select(i=>all[i]).ToHashSet();
            foreach(var instruction in keep)
            {
                if(instruction.Operand is Instruction target && plan.Entries.TryGetValue(Array.IndexOf(all,target),out int position)) instruction.Operand=all[position];
                else if(instruction.Operand is Instruction[] targets)for(int i=0;i<targets.Length;i++)if(plan.Entries.TryGetValue(Array.IndexOf(all,targets[i]),out int at))targets[i]=all[at];
            }
            foreach(int at in plan.ReturnsChanged){Require(all[at].OpCode==OpCodes.Leave,"unexpected return replacement");all[at].OpCode=OpCodes.Ret;all[at].Operand=null;}
            if(plan.ReplacedAllocation){var first=all[plan.OriginalIndices[0]];Require(first.OpCode==OpCodes.Call && first.Operand is MethodReference m && m.Name=="Acquire","unexpected allocation replacement");first.OpCode=before.Body.Instructions[0].OpCode;first.Operand=before.Body.Instructions[0].Operand;}
            foreach(var instruction in all)if(!keep.Contains(instruction))copy.Body.Instructions.Remove(instruction);
            Require(copy.Body.ExceptionHandlers.Count==plan.OriginalHandlers+1,"unexpected cleanup handler count");
            while(copy.Body.ExceptionHandlers.Count>plan.OriginalHandlers)copy.Body.ExceptionHandlers.RemoveAt(copy.Body.ExceptionHandlers.Count-1);
            while(copy.Body.Variables.Count>plan.OriginalLocals)copy.Body.Variables.RemoveAt(copy.Body.Variables.Count-1);
            var normalized=Shell(before);Widen(normalized);
            Require(Body(copy)==Body(normalized),"reverse splice does not reconstruct original "+plan.Method);
            restoration.Add(new{method=plan.Method,instructions=before.Body.Instructions.Count,locals=plan.OriginalLocals,handlers=plan.OriginalHandlers,exact=true});
        }
        var helper=Types(b).Single(t=>t.Name==ReusePatcher.HelperName);var current=helper.Fields.Single(f=>f.Name=="current");
        Require(!helper.IsBeforeFieldInit && current.IsStatic && current.CustomAttributes.Count==1 && current.CustomAttributes[0].AttributeType.FullName=="System.ThreadStaticAttribute","context initialization/ThreadStatic differs");
        Require(!Types(b).Any(t=>t.Name is "NXFrameProfile43" or "NXTileProfile44" or "NXTileCost45"),"detailed profiler leaked into clean experiment");
        return new{unchangedBodies=bodies,unchangedFields=fields,unchangedTypes=oldTypes.Length,managedResources=a.Resources.Count,restoration,originalSignaturesAndFlagsPreserved=true,threadStaticPreserved=true,noDetailedProfilers=true,preservedNoOptimizationMethods=oldTypes.SelectMany(t=>t.Methods).Where(m=>(m.ImplAttributes&Mono.Cecil.MethodImplAttributes.NoOptimization)!=0).Select(m=>m.FullName)};
    }
    internal static object Signatures(byte[] bytes)
    {
        using var stream=new MemoryStream(bytes);using var pe=new PEReader(stream);var reader=pe.GetMetadataReader();
        var decoder=(ISignatureTypeProvider<string,object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder",true)!,true)!;
        using var game=AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var methods=Types(game.MainModule).SelectMany(t=>t.Methods).Where(m=>m.HasBody&&(Changed(m)||New(m))).ToArray();
        string Sig(MethodSignature<string> value)=>value.ReturnType+"("+string.Join(',',value.ParameterTypes)+")";
        var rows=new List<object>();
        foreach(var method in methods)
        {
            var definition=reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            rows.Add(new{kind="MethodDef",name=method.FullName,raw=Convert.ToHexString(reader.GetBlobBytes(definition.Signature)),decoded=Sig(definition.DecodeSignature(decoder,(object?)null))});
            var body=pe.GetMethodBody(definition.RelativeVirtualAddress);
            if(!body.LocalSignature.IsNil){var locals=reader.GetStandaloneSignature(body.LocalSignature);rows.Add(new{kind="Locals",name=method.FullName,raw=Convert.ToHexString(reader.GetBlobBytes(locals.Signature)),decoded=string.Join(',',locals.DecodeLocalSignature(decoder,(object?)null))});}
        }
        foreach(var field in Types(game.MainModule).Where(ReusePatcher.IsAdded).SelectMany(t=>t.Fields))
        {
            var definition=reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)field.MetadataToken.RID));
            rows.Add(new{kind="FieldDef",name=field.FullName,raw=Convert.ToHexString(reader.GetBlobBytes(definition.Signature)),decoded=definition.DecodeSignature(decoder,(object?)null)});
        }
        var members=methods.SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand).OfType<Mono.Cecil.MemberReference>().Concat(Types(game.MainModule).Where(ReusePatcher.IsAdded).SelectMany(t=>t.Fields).SelectMany(f=>f.CustomAttributes).Select(a=>a.Constructor)).Where(r=>r.MetadataToken.TokenType==TokenType.MemberRef).DistinctBy(r=>r.MetadataToken.ToInt32());
        foreach(var member in members)
        {
            var definition=reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)member.MetadataToken.RID));
            rows.Add(new{kind="MemberRef",name=member.FullName,raw=Convert.ToHexString(reader.GetBlobBytes(definition.Signature)),decoded=definition.GetKind()==MemberReferenceKind.Method?Sig(definition.DecodeMethodSignature(decoder,(object?)null)):definition.DecodeFieldSignature(decoder,(object?)null)});
        }
        return new{passed=true,independentlyDecodedRawMetadata=true,primitiveAliasesAllowed=false,rows};
    }
    internal static object Sdk(byte[] bytes,string input)
    {
        const string core="/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
        Require(Sha(File.ReadAllBytes(core))=="ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd","target CoreLib changed");
        var resolverType=Support.GetType("ReferenceAudit+PinnedResolver",true)!;
        using var resolver=(IAssemblyResolver)Activator.CreateInstance(resolverType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{Path.GetDirectoryName(input)!},null)!;
        using var game=AssemblyDefinition.ReadAssembly(new MemoryStream(bytes),new ReaderParameters{AssemblyResolver=resolver});
        var references=Types(game.MainModule).SelectMany(t=>t.Methods).Where(New).SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand).OfType<Mono.Cecil.MemberReference>().Where(r=>r.DeclaringType?.Namespace.StartsWith("System")==true);
        references=references.Concat(Types(game.MainModule).Where(ReusePatcher.IsAdded).SelectMany(t=>t.Fields).SelectMany(f=>f.CustomAttributes).Select(a=>a.Constructor));
        var rows=new List<object>();
        foreach(var reference in references.DistinctBy(r=>r.FullName))
        {
            IMemberDefinition member=reference switch{MethodReference m=>m.Resolve(),FieldReference f=>f.Resolve(),_=>throw new InvalidDataException("unsupported SDK member")};
            Require(member is MethodDefinition{IsPublic:true} or FieldDefinition{IsPublic:true},"unresolved/nonpublic SDK member "+reference.FullName);
            rows.Add(new{reference=reference.FullName,resolved=member.FullName,image=member.DeclaringType.Module.FileName,sha256=Sha(File.ReadAllBytes(member.DeclaringType.Module.FileName))});
        }
        return new{targetCorelibSha256=Sha(File.ReadAllBytes(core)),checkedMembers=rows,hostFallbackAllowed=false};
    }
}
