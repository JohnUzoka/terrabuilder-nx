// TextCache: chat-text splitting and parsing in ChatManager's per-frame text draws.
//
// Run 41 (game thread, 375-560 s): ChatManager.DrawColorCodedString(TextSnippet[]) spent 4.9-6.3% of
// the game thread (1.1-1.4 ms/frame) in two static Regex.Split calls. For each snippet it runs
// String.Split('\n'), overwrites that result with Regex.Split(text, "(\n)"), and then for each line runs
// Regex.Split(line, "( )"), whose result the next String.Split(' ') overwrites. Every *WithShadow draw
// calls it five times (four shadow offsets plus the text), so hotbar stack counts, gamepad hints and mod
// overlays are split five times a frame. ParseMessage, which turns the string into snippets once per
// *WithShadow or GetStringSize(string) call, cost another 1.4%, 0.8% of it in its Format regex scan.
//
// 1. DrawColorCodedString(TextSnippet[]): both splits whose results are overwritten are removed.
//    Regex.Split(text, "(\n)") becomes ChatManager.NxSplitKeepingNewlines(text), which builds the same
//    array: the pieces between '\n's with a "\n" element between them, or [text] when there is no '\n'.
//    A null Text still throws NullReferenceException, as the removed String.Split did.
// 2. ParseMessage: the Format regex scan is memoized per text. ChatManager.NxFormatParts(text) returns
//    the scan result as strings: [text, then per match: the plain text before it (null when empty), the
//    tag, text and options groups, and the whole match, then the trailing plain text (null when empty)].
//    Texts up to 512 chars are kept in a 512-slot direct-mapped table, keyed by the whole text.
//    ParseMessage then runs its original loop over those strings: new TextSnippets on every call, and
//    GetHandler and ITagHandler.Parse on every call for every tag, so the current handlers run with
//    their side effects (item tags load items; mods can register or replace handlers). The scan depends
//    only on the text and the readonly Regexes.Format, so an entry never goes stale; language, input mode
//    and base color are not part of it. Entries are immutable arrays published after a memory barrier,
//    and readers compare the stored text, so concurrent callers and slot collisions only cost a rescan.
using System.Globalization;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

static class TextCachePatch
{
    const int CacheSlots = 512; // a power of two: the slot is the text's hash code masked by CacheSlots - 1
    const int MaxCachedLength = 512;

    // ParseMessage as shipped in tModLoader 1.4.4.9; NxFormatParts and the replayed loop reproduce it.
    const string ParseMessageShape = """
        ldarg.0; ldstr \r; ldstr ; callvirt String::Replace/2; starg.s text; ldsfld Regexes::Format; ldarg.0;
        callvirt Regex::Matches/1; newobj List`1::.ctor/0; stloc.0; ldc.i4.0; stloc.1;
        callvirt MatchCollection::GetEnumerator/0; stloc.2; br @85; ldloc.2; callvirt IEnumerator::get_Current/0;
        castclass Match; stloc.3; ldloc.3; callvirt Capture::get_Index/0; ldloc.1; ble.s @35; ldloc.0; ldarg.0; ldloc.1;
        ldloc.3; callvirt Capture::get_Index/0; ldloc.1; sub; callvirt String::Substring/2; ldarg.1; ldc.r4 1;
        newobj TextSnippet::.ctor/3; callvirt List`1::Add/1; ldloc.3; callvirt Capture::get_Index/0; ldloc.3;
        callvirt Capture::get_Length/0; add; stloc.1; ldloc.3; callvirt Match::get_Groups/0; ldstr tag;
        callvirt GroupCollection::get_Item/1; callvirt Capture::get_Value/0; ldloc.3; callvirt Match::get_Groups/0;
        ldstr text; callvirt GroupCollection::get_Item/1; callvirt Capture::get_Value/0; stloc.s V_4; ldloc.3;
        callvirt Match::get_Groups/0; ldstr options; callvirt GroupCollection::get_Item/1;
        callvirt Capture::get_Value/0; stloc.s V_5; call ChatManager::GetHandler/1; stloc.s V_6; ldloc.s V_6;
        brfalse.s @79; ldloc.0; ldloc.s V_6; ldloc.s V_4; ldarg.1; ldloc.s V_5; callvirt ITagHandler::Parse/3;
        callvirt List`1::Add/1; ldloc.0; ldloc.0; callvirt List`1::get_Count/0; ldc.i4.1; sub;
        callvirt List`1::get_Item/1; ldloc.3; callvirt Object::ToString/0; stfld TextSnippet::TextOriginal; br.s @85;
        ldloc.0; ldloc.s V_4; ldarg.1; ldc.r4 1; newobj TextSnippet::.ctor/3; callvirt List`1::Add/1; ldloc.2;
        callvirt IEnumerator::MoveNext/0; brtrue @15; leave.s @97; ldloc.2; isinst IDisposable; stloc.s V_7;
        ldloc.s V_7; brfalse.s @96; ldloc.s V_7; callvirt IDisposable::Dispose/0; endfinally; ldarg.0;
        callvirt String::get_Length/0; ldloc.1; ble.s @113; ldloc.0; ldarg.0; ldloc.1; ldarg.0;
        callvirt String::get_Length/0; ldloc.1; sub; callvirt String::Substring/2; ldarg.1; ldc.r4 1;
        newobj TextSnippet::.ctor/3; callvirt List`1::Add/1; ldloc.0; ret
        """;

    public static void Apply(ModuleDefinition module)
    {
        var chat = module.GetType("Terraria.UI.Chat.ChatManager")
            ?? throw new InvalidOperationException("Terraria.UI.Chat.ChatManager not found");
        var split = AddSplitKeepingNewlines(module, chat);
        PatchDraw(chat, split);
        PatchParseMessage(module, chat);
    }

    static void PatchDraw(TypeDefinition chat, MethodDefinition split)
    {
        var draw = chat.Methods.Single(m => m.Name == "DrawColorCodedString" && m.HasBody && m.Parameters.Count == 11
            && m.Parameters[2].ParameterType.FullName == "Terraria.UI.Chat.TextSnippet[]");
        var body = draw.Body;
        var code = body.Instructions;
        var regexSplits = code.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference
            { Name: "Split", DeclaringType.FullName: "System.Text.RegularExpressions.Regex", Parameters.Count: 2 }).ToList();
        if (regexSplits.Count != 2)
            throw new InvalidOperationException($"ChatManager.DrawColorCodedString: {regexSplits.Count} Regex.Split calls, expected 2");
        var targets = new HashSet<Instruction>(code.SelectMany(i => i.Operand switch
        {
            Instruction t => new[] { t },
            Instruction[] ts => ts,
            _ => Array.Empty<Instruction>(),
        }));
        foreach (var h in body.ExceptionHandlers)
            targets.UnionWith(new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd, h.FilterStart }.Where(i => i != null));

        // Lines: snippet.Text.Split('\n') -> V; Regex.Split(snippet.Text, "(\n)") -> V.
        var lineSplit = regexSplits.Single(i => i.Previous.Operand as string == "(\n)");
        var l = Window(lineSplit, 9, 1);
        if (!(IsLdloc(l[0]) && l[1].OpCode == OpCodes.Ldfld && l[1].Operand is FieldReference { Name: "Text" } text
            && text.DeclaringType.FullName == "Terraria.UI.Chat.TextSnippet"
            && IsLdcI4(l[2], '\n') && IsLdcI4(l[3], 0) && IsStringSplit(l[4]) && IsStloc(l[5])
            && IsLdloc(l[6]) && Local(body, l[6]) == Local(body, l[0]) && l[7].OpCode == OpCodes.Ldfld && l[7].Operand == l[1].Operand
            && l[8].OpCode == OpCodes.Ldstr && l[9] == lineSplit && IsStloc(l[10]) && Local(body, l[10]) == Local(body, l[5])
            && Local(body, l[5]).VariableType.FullName == "System.String[]"))
            throw new InvalidOperationException("ChatManager.DrawColorCodedString: unexpected Regex.Split(\"(\\n)\") shape");
        // Words: [line] dup; Regex.Split(line, "( )") -> W; dup; line.Split(' ') -> W.
        var wordSplit = regexSplits.Single(i => i.Previous.Operand as string == "( )");
        var w = Window(wordSplit, 2, 6);
        if (!(w[0].OpCode == OpCodes.Dup && w[1].OpCode == OpCodes.Ldstr && IsStloc(w[3]) && w[4].OpCode == OpCodes.Dup
            && IsLdcI4(w[5], ' ') && IsLdcI4(w[6], 0) && IsStringSplit(w[7]) && IsStloc(w[8]) && Local(body, w[8]) == Local(body, w[3])))
            throw new InvalidOperationException("ChatManager.DrawColorCodedString: unexpected Regex.Split(\"( )\") shape");
        var removed = l.Take(6).Append(l[8]).Concat(w.Take(4)).ToList();
        if (removed.Skip(1).Any(targets.Contains))
            throw new InvalidOperationException("ChatManager.DrawColorCodedString: a removed instruction is a branch or handler boundary");

        // Instructions only become nops, so every branch offset stays encodable.
        foreach (var ins in removed)
        {
            ins.OpCode = OpCodes.Nop; // l[0] is a branch target: it now falls through to the remaining load of Text
            ins.Operand = null;
        }
        lineSplit.Operand = split;
        VerifyPatched(draw, !code.Any(i => i.Operand is MethodReference { DeclaringType.FullName: "System.Text.RegularExpressions.Regex" }),
            "Regex calls remain");
        Console.WriteLine("FIX ChatManager.DrawColorCodedString(TextSnippet[]): Regex.Split \"(\\n)\" -> NxSplitKeepingNewlines, "
            + "unused String.Split('\\n') and Regex.Split \"( )\" removed");
    }

    // string[] NxSplitKeepingNewlines(string text): Regex.Split(text, "(\n)") without the regex.
    static MethodDefinition AddSplitKeepingNewlines(ModuleDefinition module, TypeDefinition chat)
    {
        var t = module.TypeSystem;
        var indexOf = StringMethod(module, "IndexOf", "System.Char");
        var indexOfFrom = StringMethod(module, "IndexOf", "System.Char", "System.Int32");
        var substring = StringMethod(module, "Substring", "System.Int32", "System.Int32");
        var substringFrom = StringMethod(module, "Substring", "System.Int32");
        var method = new MethodDefinition("NxSplitKeepingNewlines",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, new ArrayType(t.String));
        method.Parameters.Add(new ParameterDefinition("text", ParameterAttributes.None, t.String));
        var body = method.Body;
        body.InitLocals = true;
        var count = Var(body, t.Int32);
        var at = Var(body, t.Int32);
        var result = Var(body, new ArrayType(t.String));
        var start = Var(body, t.Int32);
        var next = Var(body, t.Int32);
        var il = body.GetILProcessor();
        var countTest = il.Create(OpCodes.Ldloc, at);
        var countBody = il.Create(OpCodes.Ldloc, count);
        var splitTest = il.Create(OpCodes.Ldloc, at);
        var splitBody = il.Create(OpCodes.Ldloc, result);

        // count = number of '\n' (the first IndexOf throws NullReferenceException for null, like String.Split)
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, count));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, '\n'));
        il.Append(il.Create(OpCodes.Callvirt, indexOf));
        il.Append(il.Create(OpCodes.Stloc, at));
        il.Append(il.Create(OpCodes.Br, countTest));
        il.Append(countBody);
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, count));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, '\n'));
        il.Append(il.Create(OpCodes.Ldloc, at));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Callvirt, indexOfFrom));
        il.Append(il.Create(OpCodes.Stloc, at));
        il.Append(countTest);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Bge, countBody));
        // result = new string[2 * count + 1]; start = 0; next = 0
        il.Append(il.Create(OpCodes.Ldloc, count));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Mul));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Newarr, t.String));
        il.Append(il.Create(OpCodes.Stloc, result));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, start));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, next));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, '\n'));
        il.Append(il.Create(OpCodes.Callvirt, indexOf));
        il.Append(il.Create(OpCodes.Stloc, at));
        il.Append(il.Create(OpCodes.Br, splitTest));
        // result[next] = text.Substring(start, at - start); result[next + 1] = "\n"; next += 2; start = at + 1
        il.Append(splitBody);
        il.Append(il.Create(OpCodes.Ldloc, next));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, start));
        il.Append(il.Create(OpCodes.Ldloc, at));
        il.Append(il.Create(OpCodes.Ldloc, start));
        il.Append(il.Create(OpCodes.Sub));
        il.Append(il.Create(OpCodes.Callvirt, substring));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(il.Create(OpCodes.Ldloc, result));
        il.Append(il.Create(OpCodes.Ldloc, next));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Ldstr, "\n"));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(il.Create(OpCodes.Ldloc, next));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, next));
        il.Append(il.Create(OpCodes.Ldloc, at));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, start));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, '\n'));
        il.Append(il.Create(OpCodes.Ldloc, start));
        il.Append(il.Create(OpCodes.Callvirt, indexOfFrom));
        il.Append(il.Create(OpCodes.Stloc, at));
        il.Append(splitTest);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Bge, splitBody));
        // result[next] = text.Substring(start); return result
        il.Append(il.Create(OpCodes.Ldloc, result));
        il.Append(il.Create(OpCodes.Ldloc, next));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, start));
        il.Append(il.Create(OpCodes.Callvirt, substringFrom));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(il.Create(OpCodes.Ldloc, result));
        il.Append(il.Create(OpCodes.Ret));
        chat.Methods.Add(method);
        VerifyPatched(method, true, "");
        return method;
    }

    static void PatchParseMessage(ModuleDefinition module, TypeDefinition chat)
    {
        var parse = chat.Methods.Single(m => m.Name == "ParseMessage" && m.IsStatic && m.HasBody && m.Parameters.Count == 2
            && m.Parameters[0].ParameterType.MetadataType == MetadataType.String
            && m.Parameters[1].ParameterType.FullName == "Microsoft.Xna.Framework.Color"
            && m.ReturnType.FullName == "System.Collections.Generic.List`1<Terraria.UI.Chat.TextSnippet>");
        var body = parse.Body;
        var code = body.Instructions;
        var shape = string.Join("; ", code.Select(i => Shape(body, i)));
        var expected = string.Join(" ", ParseMessageShape.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (shape != expected || body.ExceptionHandlers.Count != 1 || body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Finally
            || body.Variables.Count != 8)
            throw new InvalidOperationException("unexpected ChatManager.ParseMessage shape: " + shape);
        // The shape is fixed above, so each (type, name, arity) below names a single member.
        MethodReference Called(string type, string name, int parameters) => (MethodReference)code.First(i => i.Operand is MethodReference m
            && m.DeclaringType.Name == type && m.Name == name && m.Parameters.Count == parameters).Operand;
        var format = (FieldReference)code.First(i => i.OpCode == OpCodes.Ldsfld).Operand;
        var matches = Called("Regex", "Matches", 1);
        var matchType = (TypeReference)code.First(i => i.OpCode == OpCodes.Castclass).Operand;
        var index = Called("Capture", "get_Index", 0);
        var length = Called("Capture", "get_Length", 0);
        var groups = Called("Match", "get_Groups", 0);
        var group = Called("GroupCollection", "get_Item", 1);
        var value = Called("Capture", "get_Value", 0);
        var toString = Called("Object", "ToString", 0);
        var substring = Called("String", "Substring", 2);
        var stringLength = Called("String", "get_Length", 0);
        var newList = Called("List`1", ".ctor", 0);
        var newSnippet = Called("TextSnippet", ".ctor", 3);
        var add = Called("List`1", "Add", 1);
        var count = Called("List`1", "get_Count", 0);
        var item = Called("List`1", "get_Item", 1);
        var getHandler = Called("ChatManager", "GetHandler", 1);
        var tagParse = Called("ITagHandler", "Parse", 3);
        var textOriginal = (FieldReference)code.First(i => i.OpCode == OpCodes.Stfld).Operand;
        var listType = body.Variables[0].VariableType;
        var handlerType = body.Variables[6].VariableType;
        var matchCollection = matches.ReturnType;
        var matchCount = module.ImportReference(new MethodReference("get_Count", module.TypeSystem.Int32, matchCollection) { HasThis = true });
        var matchAt = new MethodReference("get_Item", matchType, matchCollection) { HasThis = true };
        matchAt.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        matchAt = module.ImportReference(matchAt);
        var parts = AddFormatParts(module, chat, format, matches, matchCount, matchAt, matchType, index, length, groups, group,
            value, toString, substring, stringLength);

        // text = text.Replace("\r", "") stays; the rest replays the original loop over NxFormatParts(text).
        var t = module.TypeSystem;
        while (code.Count > 5)
            code.RemoveAt(code.Count - 1);
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var scan = Var(body, new ArrayType(t.String));
        var list = Var(body, listType);
        var i = Var(body, t.Int32);
        var last = Var(body, t.Int32);
        var handler = Var(body, handlerType);
        var il = body.GetILProcessor();
        void Part(int offset)
        {
            il.Append(il.Create(OpCodes.Ldloc, scan));
            il.Append(il.Create(OpCodes.Ldloc, i));
            if (offset != 0)
            {
                il.Append(il.Create(OpCodes.Ldc_I4, offset));
                il.Append(il.Create(OpCodes.Add));
            }
            il.Append(il.Create(OpCodes.Ldelem_Ref));
        }
        void AddPlain(Action loadText)
        {
            il.Append(il.Create(OpCodes.Ldloc, list));
            loadText();
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldc_R4, 1f));
            il.Append(il.Create(OpCodes.Newobj, newSnippet));
            il.Append(il.Create(OpCodes.Callvirt, add));
        }
        var loopTest = il.Create(OpCodes.Ldloc, i);
        var loopBody = il.Create(OpCodes.Nop);
        var noPlain = il.Create(OpCodes.Nop);
        var noHandler = il.Create(OpCodes.Nop);
        var next = il.Create(OpCodes.Ldloc, i);
        var done = il.Create(OpCodes.Ldloc, list);

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, parts));
        il.Append(il.Create(OpCodes.Stloc, scan));
        il.Append(il.Create(OpCodes.Newobj, newList));
        il.Append(il.Create(OpCodes.Stloc, list));
        il.Append(il.Create(OpCodes.Ldloc, scan));
        il.Append(il.Create(OpCodes.Ldlen));
        il.Append(il.Create(OpCodes.Conv_I4));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Sub));
        il.Append(il.Create(OpCodes.Stloc, last));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Stloc, i));
        il.Append(il.Create(OpCodes.Br, loopTest));
        // per match: plain text before it, then the tag through the current handler, else as plain text
        il.Append(loopBody);
        Part(0);
        il.Append(il.Create(OpCodes.Brfalse, noPlain));
        AddPlain(() => Part(0));
        il.Append(noPlain);
        Part(1);
        il.Append(il.Create(OpCodes.Call, getHandler));
        il.Append(il.Create(OpCodes.Stloc, handler));
        il.Append(il.Create(OpCodes.Ldloc, handler));
        il.Append(il.Create(OpCodes.Brfalse, noHandler));
        il.Append(il.Create(OpCodes.Ldloc, list));
        il.Append(il.Create(OpCodes.Ldloc, handler));
        Part(2);
        il.Append(il.Create(OpCodes.Ldarg_1));
        Part(3);
        il.Append(il.Create(OpCodes.Callvirt, tagParse));
        il.Append(il.Create(OpCodes.Callvirt, add));
        il.Append(il.Create(OpCodes.Ldloc, list));
        il.Append(il.Create(OpCodes.Ldloc, list));
        il.Append(il.Create(OpCodes.Callvirt, count));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Sub));
        il.Append(il.Create(OpCodes.Callvirt, item));
        Part(4);
        il.Append(il.Create(OpCodes.Stfld, textOriginal));
        il.Append(il.Create(OpCodes.Br, next));
        il.Append(noHandler);
        AddPlain(() => Part(2));
        il.Append(next);
        il.Append(il.Create(OpCodes.Ldc_I4_5));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, i));
        il.Append(loopTest);
        il.Append(il.Create(OpCodes.Ldloc, last));
        il.Append(il.Create(OpCodes.Blt, loopBody));
        // trailing plain text
        il.Append(il.Create(OpCodes.Ldloc, scan));
        il.Append(il.Create(OpCodes.Ldloc, last));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Brfalse, done));
        AddPlain(() =>
        {
            il.Append(il.Create(OpCodes.Ldloc, scan));
            il.Append(il.Create(OpCodes.Ldloc, last));
            il.Append(il.Create(OpCodes.Ldelem_Ref));
        });
        il.Append(done);
        il.Append(il.Create(OpCodes.Ret));
        VerifyPatched(parse, true, "");
        Console.WriteLine($"FIX ChatManager.ParseMessage: Format regex scan memoized per text ({CacheSlots} slots, texts <= {MaxCachedLength} chars); "
            + "new snippets and tag handler calls on every call");
    }

    // string[] NxFormatParts(string text): ParseMessage's scan of text, from the cache when the text was seen.
    static MethodDefinition AddFormatParts(ModuleDefinition module, TypeDefinition chat, FieldReference format,
        MethodReference matches, MethodReference matchCount, MethodReference matchAt, TypeReference matchType,
        MethodReference index, MethodReference length, MethodReference groups, MethodReference group, MethodReference value,
        MethodReference toString, MethodReference substring, MethodReference stringLength)
    {
        var t = module.TypeSystem;
        var entryType = new ArrayType(t.String);
        var cacheField = new FieldDefinition("_nxFormatParts", FieldAttributes.Private | FieldAttributes.Static, new ArrayType(entryType));
        chat.Fields.Add(cacheField);
        var hashCode = module.ImportReference(new MethodReference("GetHashCode", t.Int32, t.Object) { HasThis = true });
        var equality = StringMethod(module, "op_Equality", "System.String", "System.String");
        var barrier = module.GetMemberReferences().OfType<MethodReference>().Single(m => m.Name == "MemoryBarrier"
            && m.DeclaringType.FullName == "System.Threading.Thread" && m.Parameters.Count == 0 && !m.HasThis);
        var method = new MethodDefinition("NxFormatParts", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, entryType);
        method.Parameters.Add(new ParameterDefinition("text", ParameterAttributes.None, t.String));
        var body = method.Body;
        body.InitLocals = true;
        var slots = Var(body, cacheField.FieldType);
        var slot = Var(body, t.Int32);
        var parts = Var(body, entryType);
        var all = Var(body, matchCount.DeclaringType);
        var found = Var(body, t.Int32);
        var m = Var(body, t.Int32);
        var match = Var(body, matchType);
        var num = Var(body, t.Int32);
        var at = Var(body, t.Int32);
        var cacheable = Var(body, t.Boolean);
        var il = body.GetILProcessor();
        var haveSlots = il.Create(OpCodes.Stloc, slots);
        var scan = il.Create(OpCodes.Ldsfld, format);
        var matchTest = il.Create(OpCodes.Ldloc, m);
        var matchBody = il.Create(OpCodes.Ldloc, all);
        var noPlain = il.Create(OpCodes.Ldloc, match);
        var noTail = il.Create(OpCodes.Ldloc, cacheable);
        var result = il.Create(OpCodes.Ldloc, parts);
        void Group(int offset, string name)
        {
            il.Append(il.Create(OpCodes.Ldloc, parts));
            il.Append(il.Create(OpCodes.Ldloc, at));
            il.Append(il.Create(OpCodes.Ldc_I4, offset));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Ldloc, match));
            il.Append(il.Create(OpCodes.Callvirt, groups));
            il.Append(il.Create(OpCodes.Ldstr, name));
            il.Append(il.Create(OpCodes.Callvirt, group));
            il.Append(il.Create(OpCodes.Callvirt, value));
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }

        // cacheable = text.Length <= MaxCachedLength; slots = _nxFormatParts ??= new string[CacheSlots][]
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, stringLength));
        il.Append(il.Create(OpCodes.Ldc_I4, MaxCachedLength));
        il.Append(il.Create(OpCodes.Cgt));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ceq));
        il.Append(il.Create(OpCodes.Stloc, cacheable));
        il.Append(il.Create(OpCodes.Ldsfld, cacheField));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brtrue, haveSlots));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ldc_I4, CacheSlots));
        il.Append(il.Create(OpCodes.Newarr, entryType));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Stsfld, cacheField));
        il.Append(haveSlots);
        // if (cacheable && slots[slot = hash & mask] is { } entry && entry[0] == text) return entry
        il.Append(il.Create(OpCodes.Ldloc, cacheable));
        il.Append(il.Create(OpCodes.Brfalse, scan));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, hashCode));
        il.Append(il.Create(OpCodes.Ldc_I4, CacheSlots - 1));
        il.Append(il.Create(OpCodes.And));
        il.Append(il.Create(OpCodes.Stloc, slot));
        il.Append(il.Create(OpCodes.Ldloc, slots));
        il.Append(il.Create(OpCodes.Ldloc, slot));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Stloc, parts));
        il.Append(il.Create(OpCodes.Ldloc, parts));
        il.Append(il.Create(OpCodes.Brfalse, scan));
        il.Append(il.Create(OpCodes.Ldloc, parts));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, equality));
        il.Append(il.Create(OpCodes.Brfalse, scan));
        il.Append(il.Create(OpCodes.Ldloc, parts));
        il.Append(il.Create(OpCodes.Ret));
        // all = Regexes.Format.Matches(text); parts = new string[5 * all.Count + 2]; parts[0] = text
        il.Append(scan);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, matches));
        il.Append(il.Create(OpCodes.Stloc, all));
        il.Append(il.Create(OpCodes.Ldloc, all));
        il.Append(il.Create(OpCodes.Callvirt, matchCount));
        il.Append(il.Create(OpCodes.Stloc, found));
        il.Append(il.Create(OpCodes.Ldloc, found));
        il.Append(il.Create(OpCodes.Ldc_I4_5));
        il.Append(il.Create(OpCodes.Mul));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Newarr, t.String));
        il.Append(il.Create(OpCodes.Stloc, parts));
        il.Append(il.Create(OpCodes.Ldloc, parts));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, num));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, m));
        il.Append(il.Create(OpCodes.Br, matchTest));
        // match = all[m]; at = 5 * m + 1; plain text before the match, as ParseMessage's `match.Index > num`
        il.Append(matchBody);
        il.Append(il.Create(OpCodes.Ldloc, m));
        il.Append(il.Create(OpCodes.Callvirt, matchAt));
        il.Append(il.Create(OpCodes.Stloc, match));
        il.Append(il.Create(OpCodes.Ldloc, m));
        il.Append(il.Create(OpCodes.Ldc_I4_5));
        il.Append(il.Create(OpCodes.Mul));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, at));
        il.Append(il.Create(OpCodes.Ldloc, match));
        il.Append(il.Create(OpCodes.Callvirt, index));
        il.Append(il.Create(OpCodes.Ldloc, num));
        il.Append(il.Create(OpCodes.Ble, noPlain));
        il.Append(il.Create(OpCodes.Ldloc, parts));
        il.Append(il.Create(OpCodes.Ldloc, at));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, num));
        il.Append(il.Create(OpCodes.Ldloc, match));
        il.Append(il.Create(OpCodes.Callvirt, index));
        il.Append(il.Create(OpCodes.Ldloc, num));
        il.Append(il.Create(OpCodes.Sub));
        il.Append(il.Create(OpCodes.Callvirt, substring));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        // num = match.Index + match.Length; the tag, text and options groups; the whole match
        il.Append(noPlain);
        il.Append(il.Create(OpCodes.Callvirt, index));
        il.Append(il.Create(OpCodes.Ldloc, match));
        il.Append(il.Create(OpCodes.Callvirt, length));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, num));
        Group(1, "tag");
        Group(2, "text");
        Group(3, "options");
        il.Append(il.Create(OpCodes.Ldloc, parts));
        il.Append(il.Create(OpCodes.Ldloc, at));
        il.Append(il.Create(OpCodes.Ldc_I4_4));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Ldloc, match));
        il.Append(il.Create(OpCodes.Callvirt, toString));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(il.Create(OpCodes.Ldloc, m));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, m));
        il.Append(matchTest);
        il.Append(il.Create(OpCodes.Ldloc, found));
        il.Append(il.Create(OpCodes.Blt, matchBody));
        // trailing plain text, as ParseMessage's `text.Length > num`
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, stringLength));
        il.Append(il.Create(OpCodes.Ldloc, num));
        il.Append(il.Create(OpCodes.Ble, noTail));
        il.Append(il.Create(OpCodes.Ldloc, parts));
        il.Append(il.Create(OpCodes.Ldloc, found));
        il.Append(il.Create(OpCodes.Ldc_I4_5));
        il.Append(il.Create(OpCodes.Mul));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, num));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Callvirt, stringLength));
        il.Append(il.Create(OpCodes.Ldloc, num));
        il.Append(il.Create(OpCodes.Sub));
        il.Append(il.Create(OpCodes.Callvirt, substring));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        // publish the filled entry: the barrier orders its element stores before the slot store
        il.Append(noTail);
        il.Append(il.Create(OpCodes.Brfalse, result));
        il.Append(il.Create(OpCodes.Call, barrier));
        il.Append(il.Create(OpCodes.Ldloc, slots));
        il.Append(il.Create(OpCodes.Ldloc, slot));
        il.Append(il.Create(OpCodes.Ldloc, parts));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(result);
        il.Append(il.Create(OpCodes.Ret));
        chat.Methods.Add(method);
        VerifyPatched(method, true, "");
        return method;
    }

    static MethodReference StringMethod(ModuleDefinition module, string name, params string[] parameters) =>
        module.GetMemberReferences().OfType<MethodReference>().FirstOrDefault(m => m.Name == name
            && m.DeclaringType.FullName == "System.String" && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters))
        ?? throw new InvalidOperationException($"no System.String::{name}({string.Join(", ", parameters)}) reference in the module");

    static VariableDefinition Var(MethodBody body, TypeReference type)
    {
        var variable = new VariableDefinition(type);
        body.Variables.Add(variable);
        return variable;
    }

    // The instructions from `before` instructions before `at` to `after` instructions after it.
    static List<Instruction> Window(Instruction at, int before, int after)
    {
        var start = at;
        for (int i = 0; i < before; i++)
            start = start.Previous ?? throw new InvalidOperationException("window starts before the method body");
        var window = new List<Instruction>();
        for (var ins = start; window.Count < before + 1 + after; ins = ins.Next)
            window.Add(ins ?? throw new InvalidOperationException("window ends after the method body"));
        return window;
    }

    static bool IsStringSplit(Instruction i) => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference
        { Name: "Split", DeclaringType.FullName: "System.String" } m
        && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Char", "System.StringSplitOptions" });

    static bool IsLdcI4(Instruction i, int value) => i.OpCode.Code switch
    {
        Code.Ldc_I4_0 => value == 0,
        Code.Ldc_I4_S => (sbyte)i.Operand == value,
        Code.Ldc_I4 => (int)i.Operand == value,
        _ => false,
    };

    static bool IsLdloc(Instruction i) => i.OpCode.Code is Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 or Code.Ldloc_S or Code.Ldloc;
    static bool IsStloc(Instruction i) => i.OpCode.Code is Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 or Code.Stloc_S or Code.Stloc;

    static VariableDefinition Local(MethodBody body, Instruction i) => i.OpCode.Code switch
    {
        Code.Ldloc_0 or Code.Stloc_0 => body.Variables[0],
        Code.Ldloc_1 or Code.Stloc_1 => body.Variables[1],
        Code.Ldloc_2 or Code.Stloc_2 => body.Variables[2],
        Code.Ldloc_3 or Code.Stloc_3 => body.Variables[3],
        _ => (VariableDefinition)i.Operand,
    };

    static string Shape(MethodBody body, Instruction i) => i.OpCode.Name + i.Operand switch
    {
        null => "",
        MethodReference m => $" {m.DeclaringType.Name}::{m.Name}/{m.Parameters.Count}",
        FieldReference f => $" {f.DeclaringType.Name}::{f.Name}",
        TypeReference type => " " + type.Name,
        string s => " " + s.Replace("\r", "\\r").Replace("\n", "\\n"),
        Instruction target => " @" + body.Instructions.IndexOf(target),
        VariableDefinition v => " V_" + v.Index,
        ParameterDefinition p => " " + p.Name,
        _ => " " + Convert.ToString(i.Operand, CultureInfo.InvariantCulture),
    };
}
