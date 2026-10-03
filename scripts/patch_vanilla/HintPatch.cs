using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

static class HintPatch
{
    const string ChatName="Terraria.UI.Chat.ChatManager"; const string FontName="ReLogic.Graphics.DynamicSpriteFont"; const string SizeName="Microsoft.Xna.Framework.Vector2 Terraria.UI.Chat.ChatManager::GetStringSize(ReLogic.Graphics.DynamicSpriteFont,System.String,Microsoft.Xna.Framework.Vector2,System.Single)";
    public static void Apply(ModuleDefinition game, ModuleDefinition relogic, ModuleDefinition fna)
    {
        if (Types(game).SelectMany(t=>t.Methods).Any(m=>m.Name.StartsWith("_NXHint52"))) return;
        using var source = AssemblyDefinition.ReadAssembly(typeof(GameGuardTemplate).Assembly.Location);
        var chat=Type(game,ChatName); var font=Type(relogic,FontName);
        var mapped = new Dictionary<string,TypeDefinition> {
            [nameof(GameGuardTemplate)]=chat,[nameof(FontGuardTemplate)]=font,[nameof(Hint52Chat)]=chat,[nameof(Hint52Font)]=font,
            [nameof(Hint52Character)]=Types(relogic).Single(t=>t.FullName==FontName+"/SpriteCharacterData"),[nameof(Hint52Vector2)]=Type(fna,"Microsoft.Xna.Framework.Vector2"),[nameof(Hint52Vector3)]=Type(fna,"Microsoft.Xna.Framework.Vector3"),[nameof(Hint52Color)]=Type(fna,"Microsoft.Xna.Framework.Color"),[nameof(Hint52Rectangle)]=Type(fna,"Microsoft.Xna.Framework.Rectangle"),[nameof(Hint52Snippet)]=Type(game,"Terraria.UI.Chat.TextSnippet"),[nameof(Hint52GlyphSnippet)]=Type(game,"Terraria.GameContent.UI.Chat.GlyphTagHandler/GlyphSnippet"),[nameof(Hint52GlyphHandler)]=Type(game,"Terraria.GameContent.UI.Chat.GlyphTagHandler"),[nameof(Hint52Culture)]=Type(game,"Terraria.Localization.GameCulture"),[nameof(Hint52Language)]=Type(game,"Terraria.Localization.Language"),[nameof(Hint52LanguageManager)]=Type(game,"Terraria.Localization.LanguageManager")};
        foreach (var pair in new[]{(S:source.MainModule.Types.Single(t=>t.Name==nameof(FontGuardTemplate)),D:font,M:new Mapper(relogic,mapped)),(S:source.MainModule.Types.Single(t=>t.Name==nameof(GameGuardTemplate)),D:chat,M:new Mapper(game,mapped))})
        {
            foreach (var sm in pair.S.Methods.Where(m=>m.Name.StartsWith("_NXHint52"))) { var dm=new MethodDefinition(sm.Name,sm.Attributes,pair.M.Type(sm.ReturnType)){ImplAttributes=sm.ImplAttributes,CallingConvention=sm.CallingConvention}; foreach(var p in sm.Parameters) dm.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,pair.M.Type(p.ParameterType))); pair.D.Methods.Add(dm); }
        }
        foreach (var pair in new[]{(S:source.MainModule.Types.Single(t=>t.Name==nameof(FontGuardTemplate)),D:font,M:new Mapper(relogic,mapped)),(S:source.MainModule.Types.Single(t=>t.Name==nameof(GameGuardTemplate)),D:chat,M:new Mapper(game,mapped))})
            foreach (var sm in pair.S.Methods.Where(m=>m.Name.StartsWith("_NXHint52"))) CopyBody(sm, pair.D.Methods.Single(dm=>pair.M.SignatureMatches(sm,dm)), pair.M.Type, pair.M.Member);
        var draw = Method(game,"System.Void Terraria.Main::DrawGamepadInstructions()"); var site=draw.Body.Instructions.Single(i=>i.Operand is MethodReference r && r.FullName==SizeName); site.Operand=chat.Methods.Single(m=>m.Name=="_NXHint52Measure");
    }
    sealed class Mapper
    {
        readonly ModuleDefinition module; readonly Dictionary<string,TypeDefinition> mapped; public Mapper(ModuleDefinition m, Dictionary<string,TypeDefinition> d){module=m; mapped=d;}
        public TypeReference Type(TypeReference s){ if(mapped.TryGetValue(s.FullName,out var t)) return module.ImportReference(t); if(s is ByReferenceType br) return new ByReferenceType(Type(br.ElementType)); if(s is ArrayType a) return new ArrayType(Type(a.ElementType)); if(s is GenericInstanceType g){var r=new GenericInstanceType(Type(g.ElementType)); foreach(var x in g.GenericArguments) r.GenericArguments.Add(Type(x)); return r;} if(s is GenericParameter) return s; if(s.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.IntPtr or MetadataType.UIntPtr or MetadataType.Object) return s.FullName switch { "System.Void"=>module.TypeSystem.Void, "System.Boolean"=>module.TypeSystem.Boolean, "System.Char"=>module.TypeSystem.Char, "System.SByte"=>module.TypeSystem.SByte, "System.Byte"=>module.TypeSystem.Byte, "System.Int16"=>module.TypeSystem.Int16, "System.UInt16"=>module.TypeSystem.UInt16, "System.Int32"=>module.TypeSystem.Int32, "System.UInt32"=>module.TypeSystem.UInt32, "System.Int64"=>module.TypeSystem.Int64, "System.UInt64"=>module.TypeSystem.UInt64, "System.Single"=>module.TypeSystem.Single, "System.Double"=>module.TypeSystem.Double, "System.String"=>module.TypeSystem.String, "System.Object"=>module.TypeSystem.Object, "System.IntPtr"=>module.TypeSystem.IntPtr, "System.UIntPtr"=>module.TypeSystem.UIntPtr, _=>throw new InvalidDataException("primitive "+s.FullName)}; return new TypeReference(s.Namespace,s.Name,module,module.AssemblyReferences.Single(r=>r.Name=="mscorlib"),s.IsValueType); }
        public bool SignatureMatches(MethodReference s, MethodReference t)=>s.Name==t.Name&&s.HasThis==t.HasThis&&Type(s.ReturnType).FullName==t.ReturnType.FullName&&s.Parameters.Select(p=>Type(p.ParameterType).FullName).SequenceEqual(t.Parameters.Select(p=>p.ParameterType.FullName));
        public object Member(object v){ if(v is TypeReference tr) return Type(tr); if(v is FieldReference f){ if(mapped.TryGetValue(f.DeclaringType.FullName,out var o)) return module.ImportReference(o.Fields.Single(x=>x.Name==f.Name)); return new FieldReference(f.Name,Type(f.FieldType),Type(f.DeclaringType)); } if(v is MethodReference mr){ if(mapped.TryGetValue(mr.DeclaringType.FullName,out var o)) return module.ImportReference(o.Methods.Single(x=>SignatureMatches(mr,x))); var r=new MethodReference(mr.Name,Type(mr.ReturnType),Type(mr.DeclaringType)){HasThis=mr.HasThis,ExplicitThis=mr.ExplicitThis,CallingConvention=mr.CallingConvention}; foreach(var p in mr.Parameters) r.Parameters.Add(new ParameterDefinition(Type(p.ParameterType))); return r;} return v; }
    }
}
