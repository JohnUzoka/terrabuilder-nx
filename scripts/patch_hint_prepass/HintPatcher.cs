using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class HintPatcher
{
    internal const string Prefix = "_NXHint52";
    internal const string ChatName = "Terraria.UI.Chat.ChatManager";
    internal const string FontName = "ReLogic.Graphics.DynamicSpriteFont";
    internal sealed record HintReceipt(int RootInstruction, int OriginalCallsiteCount, string[] GameHelpers, string[] FontHelpers, string OriginalCallsitesSha256);
    internal static bool IsAdded(MethodDefinition method) => method.Name.StartsWith(Prefix, StringComparison.Ordinal);
    internal static bool IsAdded(TypeDefinition type) => false;
    internal static IAssemblyResolver Resolver(string input) => new PinnedResolver(Path.GetDirectoryName(Path.GetFullPath(input))!);

    internal static string[] Calls(ModuleDefinition module) => Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions.Select((i, n) => (m, i, n)))
        .Where(x => x.i.OpCode.FlowControl == FlowControl.Call)
        .Select(x => module.Assembly.Name.FullName + "|" + x.m.FullName + "|" + x.n + "|" + x.i.OpCode.Name + "|" +
            (x.i.Operand is MemberReference r ? HintAudit.MemberIdentity(r) : throw new InvalidDataException("unsupported original calli"))).ToArray();

    internal static HintReceipt Inject(ModuleDefinition game, ModuleDefinition relogic)
    {
        Require(game.Mvid == Guid.Parse("18ab5d9f-3419-30c0-509e-cf5a34c42f0b") && relogic.Mvid == Guid.Parse("9192551c-5bb1-4da1-9e37-6d31c1a140c0"), "Hint52 requires exact adopted50 modules");
        Require(!Types(game).Concat(Types(relogic)).SelectMany(t => t.Methods).Any(IsAdded), "Hint52 method prefix already exists");
        // Freeze every original callsite before generating any methods. New helper calls
        // must never leak into the original inventory used for inverse verification.
        var calls = Calls(game).Concat(Calls(relogic)).ToArray();
        var root = Program.Target(game);
        var site = root.Body.Instructions.Single(i => i.Offset == 0x007a);
        Require(site.OpCode == OpCodes.Call && site.Operand is MethodReference original && original.FullName == Program.SizeName &&
            ReferenceEquals(original.Resolve(), Program.Method(game, Program.SizeName)), "root007a is not the exact original string GetStringSize");
        int index = root.Body.Instructions.IndexOf(site);
        Require(root.Body.Instructions.Count(i => i.Operand is MethodReference r && r.FullName == Program.SizeName) == 1, "root string size call count changed");
        var gameRefs = game.AssemblyReferences.Select(r => r.FullName).ToArray();
        var fontRefs = relogic.AssemblyReferences.Select(r => r.FullName).ToArray();
        using var source = AssemblyDefinition.ReadAssembly(typeof(GameGuardTemplate).Assembly.Location);
        var chat = Types(game).Single(t => t.FullName == ChatName);
        var font = Types(relogic).Single(t => t.FullName == FontName);
        TypeDefinition Game(string name) => Types(game).Single(t => t.FullName == name);
        var fna = game.AssemblyResolver.Resolve(game.AssemblyReferences.Single(r => r.Name == "FNA")).MainModule;
        TypeDefinition Fna(string name) => Types(fna).Single(t => t.FullName == "Microsoft.Xna.Framework." + name);
        var mapped = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal) {
            [nameof(GameGuardTemplate)] = chat, [nameof(FontGuardTemplate)] = font,
            [nameof(Hint52Chat)] = chat, [nameof(Hint52Font)] = font,
            [nameof(Hint52Character)] = Types(relogic).Single(t => t.FullName == FontName + "/SpriteCharacterData"),
            [nameof(Hint52Vector2)] = Fna("Vector2"), [nameof(Hint52Vector3)] = Fna("Vector3"),
            [nameof(Hint52Color)] = Fna("Color"), [nameof(Hint52Rectangle)] = Fna("Rectangle"),
            [nameof(Hint52Snippet)] = Game("Terraria.UI.Chat.TextSnippet"),
            [nameof(Hint52GlyphSnippet)] = Game("Terraria.GameContent.UI.Chat.GlyphTagHandler/GlyphSnippet"),
            [nameof(Hint52GlyphHandler)] = Game("Terraria.GameContent.UI.Chat.GlyphTagHandler"),
            [nameof(Hint52Culture)] = Game("Terraria.Localization.GameCulture"),
            [nameof(Hint52Language)] = Game("Terraria.Localization.Language"),
            [nameof(Hint52LanguageManager)] = Game("Terraria.Localization.LanguageManager")
        };
        var pairs = new[] {
            (Source: source.MainModule.Types.Single(t => t.Name == nameof(FontGuardTemplate)), Destination: font, Mapper: new RuntimeMapper(relogic, mapped)),
            (Source: source.MainModule.Types.Single(t => t.Name == nameof(GameGuardTemplate)), Destination: chat, Mapper: new RuntimeMapper(game, mapped))
        };
        foreach (var pair in pairs)
        {
            Require(pair.Source.Fields.Count == 0 && pair.Source.NestedTypes.Count == 0 && pair.Source.Methods.All(m => IsAdded(m) && m.IsStatic && m.HasBody && !m.HasGenericParameters), "guard template acquired state, constructor, nested or unprefixed method");
            foreach (var method in pair.Source.Methods)
            {
                var destination = new MethodDefinition(method.Name, method.Attributes, pair.Mapper.Type(method.ReturnType)) {
                    ImplAttributes = method.ImplAttributes, CallingConvention = method.CallingConvention
                };
                foreach (var p in method.Parameters) destination.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, pair.Mapper.Type(p.ParameterType)));
                pair.Destination.Methods.Add(destination);
            }
        }
        foreach (var pair in pairs) foreach (var method in pair.Source.Methods)
            CopyBody(method, pair.Destination.Methods.Single(m => pair.Mapper.SignatureMatches(method, m)), pair.Mapper.Type, pair.Mapper.Member);
        var replacement = chat.Methods.Single(m => m.Name == "_NXHint52Measure");
        var originalSize = Program.Method(game, Program.SizeName);
        Require(replacement.IsStatic && HintAudit.TypeIdentity(replacement.ReturnType) == HintAudit.TypeIdentity(originalSize.ReturnType) &&
            replacement.Parameters.Select(p => HintAudit.TypeIdentity(p.ParameterType)).SequenceEqual(originalSize.Parameters.Select(p => HintAudit.TypeIdentity(p.ParameterType))), "root helper signature mismatch");
        site.Operand = replacement;
        Require(gameRefs.SequenceEqual(game.AssemblyReferences.Select(r => r.FullName)) && fontRefs.SequenceEqual(relogic.AssemblyReferences.Select(r => r.FullName)), "guard introduced assembly reference");
        return new(index, calls.Length, chat.Methods.Where(IsAdded).Select(m => m.FullName).ToArray(), font.Methods.Where(IsAdded).Select(m => m.FullName).ToArray(), Sha(Encoding.UTF8.GetBytes(string.Join('\n', calls))));
    }

    internal static void CopyBody(MethodDefinition source, MethodDefinition target, Func<TypeReference, TypeReference> type, Func<object, object> member)
    {
        target.Body = new MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
        foreach (var local in source.Body.Variables) target.Body.Variables.Add(new VariableDefinition(type(local.VariableType)));
        var map = source.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
        foreach (var instruction in source.Body.Instructions)
        {
            var copy = map[instruction]; copy.OpCode = instruction.OpCode;
            copy.Operand = instruction.Operand switch {
                null => null, Instruction branch => map[branch], Instruction[] branches => branches.Select(b => map[b]).ToArray(),
                VariableDefinition local => target.Body.Variables[local.Index], ParameterDefinition p => target.Parameters[p.Index],
                var operand => member(operand)
            };
            target.Body.Instructions.Add(copy);
        }
        foreach (var h in source.Body.ExceptionHandlers) target.Body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType) {
            TryStart = map[h.TryStart], TryEnd = h.TryEnd == null ? null : map[h.TryEnd], HandlerStart = map[h.HandlerStart],
            HandlerEnd = h.HandlerEnd == null ? null : map[h.HandlerEnd], FilterStart = h.FilterStart == null ? null : map[h.FilterStart],
            CatchType = h.CatchType == null ? null : type(h.CatchType)
        });
    }

    sealed class RuntimeMapper
    {
        readonly ModuleDefinition module;
        readonly Dictionary<string, TypeDefinition> mapped;
        internal RuntimeMapper(ModuleDefinition module, Dictionary<string, TypeDefinition> mapped) { this.module = module; this.mapped = mapped; }
        internal TypeReference Type(TypeReference source)
        {
            if (mapped.TryGetValue(source.FullName, out var target)) return module.ImportReference(target);
            if (source is ByReferenceType reference) return new ByReferenceType(Type(reference.ElementType));
            if (source is ArrayType array) { Require(array.IsVector, "guard multidimensional array unsupported"); return new ArrayType(Type(array.ElementType)); }
            if (source is GenericInstanceType generic) { var result = new GenericInstanceType(Type(generic.ElementType)); foreach (var arg in generic.GenericArguments) result.GenericArguments.Add(Type(arg)); return result; }
            if (source is GenericParameter) return source;
            if (source.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.IntPtr or MetadataType.UIntPtr or MetadataType.Object or MetadataType.TypedByReference) return Primitive(module, source.FullName);
            Require(source.Namespace.StartsWith("System", StringComparison.Ordinal) && source.DeclaringType == null, "unmapped guard type " + source.FullName);
            return new TypeReference(source.Namespace, source.Name, module, module.AssemblyReferences.Single(r => r.Name == "mscorlib"), source.IsValueType);
        }
        internal bool SignatureMatches(MethodReference source, MethodReference target) => source.Name == target.Name && source.HasThis == target.HasThis &&
            Type(source.ReturnType).FullName == target.ReturnType.FullName && source.Parameters.Select(p => Type(p.ParameterType).FullName).SequenceEqual(target.Parameters.Select(p => p.ParameterType.FullName));
        internal object Member(object value)
        {
            if (value is TypeReference type) return Type(type);
            if (value is FieldReference field)
            {
                if (mapped.TryGetValue(field.DeclaringType.FullName, out var owner))
                {
                    var target = owner.Fields.Single(f => f.Name == field.Name);
                    Require(Type(field.FieldType).FullName == target.FieldType.FullName, "guard field type mismatch " + field.FullName);
                    return module.ImportReference(target);
                }
                return new FieldReference(field.Name, Type(field.FieldType), Type(field.DeclaringType));
            }
            if (value is MethodReference method)
            {
                if (mapped.TryGetValue(method.DeclaringType.FullName, out var owner)) return module.ImportReference(owner.Methods.Single(m => SignatureMatches(method, m)));
                Require(!method.HasGenericParameters && method is not GenericInstanceMethod, "guard generic BCL method unsupported " + method.FullName);
                var target = new MethodReference(method.Name, Type(method.ReturnType), Type(method.DeclaringType)) { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
                foreach (var parameter in method.Parameters) target.Parameters.Add(new ParameterDefinition(Type(parameter.ParameterType)));
                return target;
            }
            Require(value is not CallSite, "guard calli unsupported");
            return value;
        }
    }

    internal sealed class PinnedResolver : IAssemblyResolver
    {
        internal const string Core = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
        internal const string Runtime = "/mono-nx/dotnet_runtime/artifacts/bin/runtime/net9.0-libnx-Debug-arm64";
        readonly string game;
        readonly Dictionary<string, AssemblyDefinition> loaded = new(StringComparer.Ordinal);
        internal PinnedResolver(string game) { this.game = game; }
        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (loaded.TryGetValue(name.FullName, out var assembly)) return assembly;
            bool bcl = name.Name == "mscorlib" || name.Name == "System" || name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name == "Microsoft.CSharp";
            string path = name.Name == "System.Private.CoreLib" ? Core : Path.Combine(bcl ? Runtime : game, name.Name + ".dll");
            Require(File.Exists(path), "no pinned resolver image: " + name.FullName);
            string? hash = name.Name switch { "FNA" => Program.FnaHash, "ReLogic" => Program.ReLogicHash, "System.Private.CoreLib" => Program.CoreHash, _ => null };
            if (hash != null) Require(Sha(File.ReadAllBytes(path)) == hash, "pinned resolver image hash mismatch: " + name.Name);
            parameters.AssemblyResolver = this;
            assembly = AssemblyDefinition.ReadAssembly(path, parameters); loaded.Add(name.FullName, assembly); return assembly;
        }
        public void Dispose() { foreach (var assembly in loaded.Values) assembly.Dispose(); }
    }
}
