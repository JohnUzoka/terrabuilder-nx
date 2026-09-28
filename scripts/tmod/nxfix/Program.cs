// NxFix: repairs to the offline-patched tModLoader.dll for the Switch build.
//
// 1. Invalid `pop`s. The earlier console patch replaced every System.Console set_Title /
//    set_ForegroundColor / ResetColor call with `pop`. That is right for the setters (they
//    take one argument) but ResetColor() takes none, so its `pop` underflows the evaluation
//    stack and the method becomes invalid IL. Mono then throws InvalidProgramException the
//    first time the method runs. Logging.FirstChanceExceptionHandler is one of them: every
//    first-chance exception re-raised InvalidProgramException inside the handler, recursing
//    until the native stack overflowed (tmod03 on hardware). This pass computes the stack
//    depth of every instruction and turns each `pop` that would underflow into `nop`.
//    With --reference, the fix count must equal the reference assembly's ResetColor calls.
//
// 2. Content roots. The launcher's working directory is sdmc:/ (saves must land on SD), but
//    tModLoader expects the working directory to be the install folder: TMLContentManager
//    and the XnaDirectContentSource it feeds build System.IO paths from the relative
//    ContentManager.RootDirectory ("Content"), so they looked in sdmc:/Content. FNA's own
//    loads resolve through TitleLocation (romfs:/) and are unaffected. This pass adds
//    TMLContentManager.NxRoot(root) = root has no device ? Path.Combine("romfs:/", root) : root
//    and applies it to the CacheImagePaths argument and to every RootDirectory read inside
//    TMLContentManager and its nested types. FNA's RootDirectory itself stays relative.
//
// 3. Uninstantiated generic references. The earlier patch replaced
//    AssemblyManager.AssemblyRedirects..cctor (a MonoMod Hook, which can't run without JIT)
//    with `newobj Dictionary`2::.ctor()` on the open generic type instead of
//    Dictionary<string, Assembly>. That is invalid IL: the cctor threw "containing type is
//    not fully instantiated" when tModLoader's force-load thread ran static initializers.
//    A `newobj` whose result is stored straight into a field is rebound to that field's
//    closed type; any other member reference on an uninstantiated generic type fails the run.
//
// 4. Game thread stack. MonoLaunch.Main runs the game on a new thread only on Windows; elsewhere
//    it calls Main_End on the calling thread, relying on the OS main-thread stack (~8 MB on
//    Linux). On Switch that is hbloader's 1 MB main thread, and the AOT-compiled
//    NPCID.Sets.GetLeinforsEntries frame alone is >100 KB: tmod04 overflowed it inside
//    NPCID.Sets..cctor. The non-Windows branch now runs the same Action on a 32 MB thread and
//    joins it (MonoLaunch.NxRunOnLargeStack). The Windows branch is unchanged.
//
// 5. Audio. The Switch launcher forces SDL_AUDIODRIVER=dummy (no real audio yet; vanilla is
//    silent too). SDL's dummy driver allows one open device. tModLoader's
//    SoundEngine.TestAudioSupport opens one (a SoundEffect), then LoadContent's XACT
//    AudioEngine opens a second and fails: "Engine initialization failed! Audio device already
//    open" is fatal (tmod05). TestAudioSupport now reports audio unsupported with a log line
//    explaining why, so tModLoader takes its own no-audio path (no XACT). SoundEngine.Initialize
//    skips the modal "audio not supported" notice, which would otherwise block every launch;
//    the log line carries the same information.
//
// The output gets a new MVID: a hash of the assembly as written with the stock MVID, so any
// IL change yields a different MVID. Mono binds an AOT image to its assembly by MVID; the
// earlier patchers kept the stock MVID while changing IL, so an AOT object compiled from
// different IL (tmod01) still loaded. Now a stale object is rejected.
//
// Usage: NxFix <in.dll> <out.dll> [--reference <unpatched tModLoader.dll>]
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: NxFix <in.dll> <out.dll> [--reference <unpatched.dll>]");
    return 64;
}
string input = args[0], output = args[1];
string reference = args.Length >= 4 && args[2] == "--reference" ? args[3] : null;
if (Path.GetFullPath(input) == Path.GetFullPath(output))
    throw new ArgumentException("write to a new file, not in place");

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input)));
var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
var module = asm.MainModule;

int fixedPops = 0;
var fixedMethods = new List<string>();
var unrepairable = new List<string>();
foreach (var type in AllTypes(module.Types))
foreach (var method in type.Methods)
{
    if (!method.HasBody)
        continue;
    var bad = UnderflowingPops(method, unrepairable);
    foreach (var instruction in bad)
    {
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
    }
    if (bad.Count > 0)
    {
        fixedPops += bad.Count;
        fixedMethods.Add($"{method.FullName} x{bad.Count}");
    }
}
foreach (var line in fixedMethods)
    Console.WriteLine("FIX pop->nop " + line);
Console.WriteLine($"FIXED_POPS {fixedPops} in {fixedMethods.Count} methods");
if (unrepairable.Count > 0)
{
    foreach (var line in unrepairable)
        Console.Error.WriteLine("UNREPAIRABLE " + line);
    return 2;
}

if (reference != null)
{
    var original = AssemblyDefinition.ReadAssembly(reference, new ReaderParameters { AssemblyResolver = resolver });
    int resetColorCalls = AllTypes(original.MainModule.Types)
        .SelectMany(t => t.Methods).Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions)
        .Count(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Console" && r.Name == "ResetColor");
    Console.WriteLine($"REFERENCE_RESETCOLOR_CALLS {resetColorCalls}");
    if (resetColorCalls != fixedPops)
    {
        Console.Error.WriteLine($"MISMATCH: fixed {fixedPops} pops but reference has {resetColorCalls} ResetColor calls");
        return 3;
    }
}

int rebound = 0;
foreach (var type in AllTypes(module.Types))
foreach (var method in type.Methods.Where(m => m.HasBody))
foreach (var instruction in method.Body.Instructions)
{
    TypeReference declaring = instruction.Operand switch { MethodReference m => m.DeclaringType, FieldReference f => f.DeclaringType, _ => null };
    if (declaring == null || declaring is GenericInstanceType || declaring.IsArray)
        continue;
    // A reference to a generic type without type arguments. Cecil leaves HasGenericParameters
    // false on a bare TypeReference, so check the definition it resolves to.
    var definition = declaring.Resolve();
    if (definition == null || !definition.HasGenericParameters || definition == method.DeclaringType)
        continue;
    var ctor = instruction.Operand as MethodReference;
    if (instruction.OpCode == OpCodes.Newobj && ctor.Parameters.Count == 0
        && instruction.Next is { } store && (store.OpCode == OpCodes.Stsfld || store.OpCode == OpCodes.Stfld)
        && store.Operand is FieldReference field && field.FieldType is GenericInstanceType closed
        && closed.ElementType.FullName == declaring.FullName)
    {
        instruction.Operand = new MethodReference(".ctor", module.TypeSystem.Void, closed) { HasThis = true };
        rebound++;
        Console.WriteLine($"FIX newobj {declaring.Name} -> {closed.FullName} in {method.FullName}");
        continue;
    }
    unrepairable.Add($"{method.FullName}: member of uninstantiated generic type at {instruction}");
}
Console.WriteLine($"FIXED_OPEN_GENERIC_CTORS {rebound}");
if (unrepairable.Count > 0)
{
    foreach (var line in unrepairable)
        Console.Error.WriteLine("UNREPAIRABLE " + line);
    return 2;
}

var contentManager = module.GetType("Terraria.ModLoader.Engine.TMLContentManager")
    ?? throw new InvalidOperationException("TMLContentManager not found");
var cacheImagePaths = contentManager.Methods.Single(m => m.Name == "<.ctor>g__CacheImagePaths|5_0");
var combine = module.GetMemberReferences().OfType<MethodReference>()
    .First(r => r.DeclaringType.FullName == "System.IO.Path" && r.Name == "Combine" && r.Parameters.Count == 2);
var indexOfChar = module.GetMemberReferences().OfType<MethodReference>()
    .First(r => r.DeclaringType.FullName == "System.String" && r.Name == "IndexOf" && r.Parameters.Count == 1 && r.Parameters[0].ParameterType.FullName == "System.Char");
if (contentManager.Methods.Any(m => m.Name == "NxRoot"))
    throw new InvalidOperationException("input already has NxRoot; run on the un-fixed assembly");

var nxRoot = new MethodDefinition("NxRoot", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.String);
nxRoot.Parameters.Add(new ParameterDefinition("root", ParameterAttributes.None, module.TypeSystem.String));
var rootIl = nxRoot.Body.GetILProcessor();
var prefix = rootIl.Create(OpCodes.Ldstr, "romfs:/");
rootIl.Append(rootIl.Create(OpCodes.Ldarg_0));
rootIl.Append(rootIl.Create(OpCodes.Ldc_I4_S, (sbyte)':'));
rootIl.Append(rootIl.Create(OpCodes.Callvirt, indexOfChar));
rootIl.Append(rootIl.Create(OpCodes.Ldc_I4_0));
rootIl.Append(rootIl.Create(OpCodes.Blt_S, prefix));
rootIl.Append(rootIl.Create(OpCodes.Ldarg_0));
rootIl.Append(rootIl.Create(OpCodes.Ret));
rootIl.Append(prefix);
rootIl.Append(rootIl.Create(OpCodes.Ldarg_0));
rootIl.Append(rootIl.Create(OpCodes.Call, combine));
rootIl.Append(rootIl.Create(OpCodes.Ret));
contentManager.Methods.Add(nxRoot);

var entry = cacheImagePaths.Body.Instructions[0];
if (entry.OpCode != OpCodes.Ldarg_1)
    throw new InvalidOperationException("unexpected CacheImagePaths body: " + entry);
var cacheIl = cacheImagePaths.Body.GetILProcessor();
cacheIl.InsertBefore(entry, cacheIl.Create(OpCodes.Ldarg_1));
cacheIl.InsertBefore(entry, cacheIl.Create(OpCodes.Call, nxRoot));
cacheIl.InsertBefore(entry, cacheIl.Create(OpCodes.Starg_S, cacheImagePaths.Parameters[0]));
Console.WriteLine("FIX TMLContentManager.CacheImagePaths: root = NxRoot(root)");

int rootReads = 0;
foreach (var type in AllTypes(new[] { contentManager }))
foreach (var method in type.Methods.Where(m => m.HasBody && m != nxRoot))
{
    var reads = method.Body.Instructions
        .Where(i => i.Operand is MethodReference r && r.Name == "get_RootDirectory" && r.DeclaringType.FullName == "Microsoft.Xna.Framework.Content.ContentManager")
        .ToList();
    var methodIl = method.Body.GetILProcessor();
    foreach (var read in reads)
        methodIl.InsertAfter(read, methodIl.Create(OpCodes.Call, nxRoot));
    if (reads.Count > 0)
    {
        rootReads += reads.Count;
        Console.WriteLine($"FIX RootDirectory -> NxRoot in {method.FullName} x{reads.Count}");
    }
}
Console.WriteLine($"FIXED_ROOT_READS {rootReads}");
if (UnderflowingPops(nxRoot, unrepairable).Count > 0 || unrepairable.Count > 0)
    throw new InvalidOperationException("generated NxRoot does not verify: " + string.Join("; ", unrepairable));
foreach (var method in AllTypes(new[] { contentManager }).SelectMany(t => t.Methods).Where(m => m.HasBody))
    if (UnderflowingPops(method, unrepairable).Count > 0 || unrepairable.Count > 0)
        throw new InvalidOperationException($"{method.FullName} does not verify after patch: " + string.Join("; ", unrepairable));

var monoLaunch = module.GetType("Terraria.MonoLaunch") ?? throw new InvalidOperationException("MonoLaunch not found");
var launchMain = monoLaunch.Methods.Single(m => m.Name == "Main");
var launchBody = launchMain.Body.Instructions;
var threadCtor = launchBody.Select(i => i.Operand).OfType<MethodReference>()
    .Single(r => r.Name == ".ctor" && r.DeclaringType.FullName == "System.Threading.Thread");
var threadStart = launchBody.Select(i => i.Operand).OfType<MethodReference>()
    .Single(r => r.Name == "Start" && r.DeclaringType.FullName == "System.Threading.Thread");
var threadStartCtor = launchBody.Select(i => i.Operand).OfType<MethodReference>()
    .Single(r => r.Name == ".ctor" && r.DeclaringType.FullName == "System.Threading.ThreadStart");
var actionInvoke = (MethodReference)launchBody.Single(i => i.OpCode == OpCodes.Ldftn && i.Operand is MethodReference r
    && r.Name == "Invoke" && r.DeclaringType.FullName == "System.Action").Operand;
// The non-Windows branch is the tail `ldloc.2; callvirt Action::Invoke(); ret`.
var tailCall = launchBody.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "Invoke" && r.DeclaringType.FullName == "System.Action").ToList();
if (tailCall.Count != 1 || tailCall[0].Next.OpCode != OpCodes.Ret)
    throw new InvalidOperationException("unexpected MonoLaunch.Main shape");
var sizedThreadCtor = new MethodReference(".ctor", module.TypeSystem.Void, threadCtor.DeclaringType) { HasThis = true };
sizedThreadCtor.Parameters.Add(new ParameterDefinition(threadStartCtor.DeclaringType));
sizedThreadCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
var threadJoin = new MethodReference("Join", module.TypeSystem.Void, threadCtor.DeclaringType) { HasThis = true };

var runLarge = new MethodDefinition("NxRunOnLargeStack", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
runLarge.Parameters.Add(new ParameterDefinition("body", ParameterAttributes.None, actionInvoke.DeclaringType));
var runIl = runLarge.Body.GetILProcessor();
runIl.Append(runIl.Create(OpCodes.Ldarg_0));
runIl.Append(runIl.Create(OpCodes.Ldftn, actionInvoke));
runIl.Append(runIl.Create(OpCodes.Newobj, threadStartCtor));
runIl.Append(runIl.Create(OpCodes.Ldc_I4, 32 * 1024 * 1024));
runIl.Append(runIl.Create(OpCodes.Newobj, sizedThreadCtor));
runIl.Append(runIl.Create(OpCodes.Dup));
runIl.Append(runIl.Create(OpCodes.Callvirt, threadStart));
runIl.Append(runIl.Create(OpCodes.Callvirt, threadJoin));
runIl.Append(runIl.Create(OpCodes.Ret));
monoLaunch.Methods.Add(runLarge);
tailCall[0].OpCode = OpCodes.Call;
tailCall[0].Operand = runLarge;
if (UnderflowingPops(runLarge, unrepairable).Count > 0 || UnderflowingPops(launchMain, unrepairable).Count > 0 || unrepairable.Count > 0)
    throw new InvalidOperationException("MonoLaunch patch does not verify: " + string.Join("; ", unrepairable));
Console.WriteLine("FIX MonoLaunch.Main: non-Windows branch runs Main_End on a 32 MB thread and joins it");

var soundEngine = module.GetType("Terraria.Audio.SoundEngine") ?? throw new InvalidOperationException("SoundEngine not found");
var testAudio = soundEngine.Methods.Single(m => m.Name == "TestAudioSupport");
var getTml = testAudio.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Single(r => r.Name == "get_tML");
var warn = testAudio.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Single(r => r.Name == "Warn");
testAudio.Body.Instructions.Clear();
testAudio.Body.ExceptionHandlers.Clear();
testAudio.Body.Variables.Clear();
var audioIl = testAudio.Body.GetILProcessor();
audioIl.Append(audioIl.Create(OpCodes.Call, getTml));
audioIl.Append(audioIl.Create(OpCodes.Ldstr, "Switch port: audio disabled. The launcher uses SDL's dummy audio driver, which allows one open device; FNA's SoundEffect and XACT engines need two."));
audioIl.Append(audioIl.Create(OpCodes.Callvirt, warn));
audioIl.Append(audioIl.Create(OpCodes.Ldc_I4_0));
audioIl.Append(audioIl.Create(OpCodes.Ret));
var soundInit = soundEngine.Methods.Single(m => m.Name == "Initialize").Body.Instructions;
var noticeBranch = soundInit.Single(i => i.OpCode == OpCodes.Brtrue_S || i.OpCode == OpCodes.Brtrue);
if (!soundInit.Any(i => i.Operand is MethodReference r && r.Name == "ShowFancyErrorMessage"))
    throw new InvalidOperationException("unexpected SoundEngine.Initialize shape");
noticeBranch.OpCode = OpCodes.Br_S;
soundInit.Insert(soundInit.IndexOf(noticeBranch), Instruction.Create(OpCodes.Pop));
foreach (var method in new[] { testAudio, soundEngine.Methods.Single(m => m.Name == "Initialize") })
    if (UnderflowingPops(method, unrepairable).Count > 0 || unrepairable.Count > 0)
        throw new InvalidOperationException($"{method.FullName} does not verify after patch: " + string.Join("; ", unrepairable));
Console.WriteLine("FIX SoundEngine: audio reported unsupported (logged), modal notice skipped");

var oldMvid = module.Mvid;
using (var image = new MemoryStream())
{
    asm.Write(image);
    module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(image.ToArray()).AsSpan(0, 16));
}
Console.WriteLine($"MVID {oldMvid} -> {module.Mvid}");

asm.Write(output);
Console.WriteLine("WROTE " + output);
return 0;

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types)
    {
        yield return type;
        foreach (var nested in AllTypes(type.NestedTypes))
            yield return nested;
    }
}

static bool IsVoid(TypeReference type)
{
    while (type is IModifierType modified)
        type = modified.ElementType;
    return type.MetadataType == MetadataType.Void;
}

// Forward dataflow over the evaluation-stack depth. Returns the `pop` instructions whose
// entry depth is 0; any other underflow or inconsistent merge is reported as unrepairable.
static List<Instruction> UnderflowingPops(MethodDefinition method, List<string> problems)
{
    var body = method.Body;
    var depth = new Dictionary<Instruction, int>();
    var work = new Stack<Instruction>();
    var bad = new List<Instruction>();
    void Enqueue(Instruction target, int d)
    {
        if (target == null)
            return;
        if (depth.TryGetValue(target, out var known))
        {
            if (known != d)
                problems.Add($"{method.FullName}: inconsistent stack depth at {target} ({known} vs {d})");
            return;
        }
        depth[target] = d;
        work.Push(target);
    }
    Enqueue(body.Instructions[0], 0);
    foreach (var handler in body.ExceptionHandlers)
    {
        Enqueue(handler.TryStart, 0);
        Enqueue(handler.HandlerStart, handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ? 1 : 0);
        if (handler.HandlerType == ExceptionHandlerType.Filter)
            Enqueue(handler.FilterStart, 1);
    }
    while (work.Count > 0)
    {
        var instruction = work.Pop();
        int d = depth[instruction];
        int pops = Pops(instruction, method, d);
        if (Environment.GetEnvironmentVariable("NXFIX_TRACE") is string trace && method.FullName.Contains(trace))
            Console.Error.WriteLine($"TRACE {instruction} depth={d} pops={pops} push={Pushes(instruction)}");
        if (pops > d)
        {
            if (instruction.OpCode.Code == Code.Pop && d == 0)
            {
                bad.Add(instruction);
                pops = 0;
            }
            else
            {
                problems.Add($"{method.FullName}: stack underflow at {instruction} (depth {d}, pops {pops})");
                continue;
            }
        }
        int after = d - pops + Pushes(instruction);
        switch (instruction.OpCode.FlowControl)
        {
            case FlowControl.Branch:
                Enqueue((Instruction)instruction.Operand, instruction.OpCode.Code is Code.Leave or Code.Leave_S ? 0 : after);
                break;
            case FlowControl.Cond_Branch:
                if (instruction.Operand is Instruction[] targets)
                    foreach (var target in targets)
                        Enqueue(target, after);
                else
                    Enqueue((Instruction)instruction.Operand, after);
                Enqueue(instruction.Next, after);
                break;
            case FlowControl.Return:
            case FlowControl.Throw:
                break;
            default:
                Enqueue(instruction.Next, after);
                break;
        }
    }
    return bad;
}

static int Pops(Instruction instruction, MethodDefinition method, int depth)
{
    var op = instruction.OpCode;
    switch (op.StackBehaviourPop)
    {
        case StackBehaviour.Pop0: return 0;
        case StackBehaviour.Pop1:
        case StackBehaviour.Popi:
        case StackBehaviour.Popref:
            return 1;
        case StackBehaviour.Pop1_pop1:
        case StackBehaviour.Popi_pop1:
        case StackBehaviour.Popi_popi:
        case StackBehaviour.Popi_popi8:
        case StackBehaviour.Popi_popr4:
        case StackBehaviour.Popi_popr8:
        case StackBehaviour.Popref_pop1:
        case StackBehaviour.Popref_popi:
            return 2;
        case StackBehaviour.Popi_popi_popi:
        case StackBehaviour.Popref_popi_popi:
        case StackBehaviour.Popref_popi_popi8:
        case StackBehaviour.Popref_popi_popr4:
        case StackBehaviour.Popref_popi_popr8:
        case StackBehaviour.Popref_popi_popref:
            return 3;
        case StackBehaviour.PopAll:
            return depth;
        case StackBehaviour.Varpop:
            switch (op.Code)
            {
                case Code.Ret:
                    return IsVoid(method.ReturnType) ? 0 : 1;
                case Code.Newobj:
                    return ((MethodReference)instruction.Operand).Parameters.Count;
                case Code.Calli:
                {
                    var site = (CallSite)instruction.Operand;
                    return site.Parameters.Count + (site.HasThis && !site.ExplicitThis ? 1 : 0) + 1;
                }
                default:
                {
                    var callee = (MethodReference)instruction.Operand;
                    return callee.Parameters.Count + (callee.HasThis && !callee.ExplicitThis ? 1 : 0);
                }
            }
        default:
            throw new NotSupportedException($"pop behaviour {op.StackBehaviourPop} at {instruction}");
    }
}

static int Pushes(Instruction instruction)
{
    var op = instruction.OpCode;
    switch (op.StackBehaviourPush)
    {
        case StackBehaviour.Push0: return 0;
        case StackBehaviour.Push1_push1: return 2;
        case StackBehaviour.Push1:
        case StackBehaviour.Pushi:
        case StackBehaviour.Pushi8:
        case StackBehaviour.Pushr4:
        case StackBehaviour.Pushr8:
        case StackBehaviour.Pushref:
            return 1;
        case StackBehaviour.Varpush:
            if (op.Code == Code.Newobj)
                return 1;
            var returnType = instruction.Operand is CallSite site ? site.ReturnType : ((MethodReference)instruction.Operand).ReturnType;
            return IsVoid(returnType) ? 0 : 1;
        default:
            throw new NotSupportedException($"push behaviour {op.StackBehaviourPush} at {instruction}");
    }
}
