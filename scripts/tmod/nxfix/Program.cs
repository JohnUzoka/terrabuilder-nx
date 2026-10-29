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
// 5. Audio is intentionally left enabled. Earlier builds forced TestAudioSupport to return
//    false while the launcher used SDL's dummy driver. The Switch launcher now wraps the real
//    SDL switch audio backend and FNA shares a single FAudio device between SoundEffect and
//    XACT, so tModLoader must exercise its normal audio path.
//
// 6. Process memory reads. System.Diagnostics.Process is unsupported on libnx (it throws
//    PlatformNotSupportedException). MemoryTracking.Finish runs at the end of every mod load
//    and its GetCurrentProcess() threw, failing the load (tmod06: "An error occurred while
//    loading ... tModLoader must be restarted"); InGameUpdate threw every minute. In the four
//    methods that use the current Process only for memory figures (MemoryTracking.Finish,
//    MemoryTracking.InGameUpdate, UIMemoryBar.RecalculateMemory,
//    Logging.FirstChanceExceptionHandler), GetCurrentProcess() now yields null, Refresh() is
//    dropped and WorkingSet64/PrivateMemorySize64 read GC.GetTotalMemory(false). Any other
//    Process member in those methods fails the run. Other Process uses (hosting, URLs,
//    folders, updates, mod development) stay unsupported and only run on explicit actions.
//    GC.GetTotalMemory walks the whole SGen major heap, so patch 14 empties InGameUpdate.
//
// 7. Touch as mouse. tModLoader's own screens and parts of its menu need a pointer. The
//    Switch SDL2 port's SWITCH_InitTouch sets SDL_TOUCH_MOUSE_EVENTS=0 at default priority,
//    so touches never become mouse input (desktop SDL defaults to 1). Main.Initialize (after
//    the window exists) now sets it to 1 at override priority; SDL watches this hint, so it
//    applies immediately. Handheld only: docked Switch has no touchscreen.
//
// 8. Player save encryption. Terraria encrypts .plr files with RijndaelManaged (AES-128-CBC,
//    key = IV = Player.ENCRYPTION_KEY). AES is unsupported on libnx ("Algorithm 'Aes' is not
//    supported"), so creating a character failed to save (tmod08). The vanilla port already
//    replaces these calls with NxCrypto.dll, a managed AES-128-CBC (PKCS7 on encrypt, no
//    padding removal on decrypt, matching Terraria's Padding=None load). The same rewrite is
//    applied to Player.SavePlayerFile_Vanilla and Player.LoadPlayerFromStream: the
//    RijndaelManaged instance load becomes nop and SymmetricAlgorithm.Create*(key, iv) becomes
//    NxCrypto.Create*(key, iv). AchievementManager similarly uses RijndaelManaged with _cryptoKey
//    for both Save (PKCS7 encrypt) and Load / LoadExistingAchievements (PKCS7 decrypt); these calls
//    are rewritten to NxCrypto.Create*(key, iv) with the RijndaelManaged ctor popped/nopped.
//
// The output gets a new MVID: a hash of the assembly as written with the stock MVID, so any
// IL change yields a different MVID. Mono binds an AOT image to its assembly by MVID; the
// earlier patchers kept the stock MVID while changing IL, so an AOT object compiled from
// different IL (tmod01) still loaded. Now a stale object is rejected.
//
// Usage: NxFix <in.dll> <out.dll> --nxcrypto <NxCrypto.dll> [--reference <unpatched tModLoader.dll>]
using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

string Option(string name) { var i = Array.IndexOf(args, name); return i >= 2 && i + 1 < args.Length ? args[i + 1] : null; }
string reference = Option("--reference");
string nxCryptoPath = Option("--nxcrypto");
if (args.Length < 2 || nxCryptoPath == null)
{
    Console.Error.WriteLine("usage: NxFix <in.dll> <out.dll> --nxcrypto <NxCrypto.dll> [--reference <unpatched.dll>]");
    return 64;
}
string input = args[0], output = args[1];
if (Path.GetFullPath(input) == Path.GetFullPath(output))
    throw new ArgumentException("write to a new file, not in place");

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input)));
var inputDir = Path.GetDirectoryName(Path.GetFullPath(input));
if (Directory.Exists("/home/john_uzoka/.cache/terraria-switch-build/tmod/flat_libs"))
    resolver.AddSearchDirectory("/home/john_uzoka/.cache/terraria-switch-build/tmod/flat_libs");
if (Directory.Exists("/home/john_uzoka/.cache/terraria-switch-build/tmod/tmod09/romfs"))
    resolver.AddSearchDirectory("/home/john_uzoka/.cache/terraria-switch-build/tmod/tmod09/romfs");
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
var nxRoot = contentManager.Methods.FirstOrDefault(m => m.Name == "NxRoot");
if (nxRoot == null)
{
    nxRoot = new MethodDefinition("NxRoot", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.String);
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
}
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
// The non-Windows branch is the tail `ldloc.2; callvirt Action::Invoke(); ret` or already `call NxRunOnLargeStack`.
if (!monoLaunch.Methods.Any(m => m.Name == "NxRunOnLargeStack"))
{
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
}

Console.WriteLine("KEEP SoundEngine: audio support path left enabled for SDL switch + shared FAudio");

var tcpSocket = module.GetType("Terraria.Net.Sockets.TcpSocket") ?? throw new InvalidOperationException("TcpSocket not found");
var isConnected = tcpSocket.Methods.Single(m => m.Name == "Terraria.Net.Sockets.ISocket.IsConnected");
{
    var body = isConnected.Body;
    if (body.ExceptionHandlers.Any(h => h.CatchType.FullName == "System.ObjectDisposedException"))
    {
        Console.WriteLine("FIX TcpSocket.ISocket.IsConnected: already patched for ObjectDisposedException");
    }
    else if (body.Instructions.Count != 13 ||
        body.Instructions[0].OpCode != OpCodes.Ldarg_0 ||
        body.Instructions[1].OpCode != OpCodes.Ldfld ||
        body.Instructions[5].Operand is not MethodReference { Name: "get_Client" } ||
        body.Instructions[11].Operand is not MethodReference { Name: "get_Connected" })
    {
        throw new InvalidOperationException("unexpected TcpSocket.ISocket.IsConnected shape");
    }
    else
    {
        var fConnection = (FieldReference)body.Instructions[1].Operand;
        var mGetClient = (MethodReference)body.Instructions[5].Operand;
        var mGetConnected = (MethodReference)body.Instructions[11].Operand;
        var tObjectDisposedException = new TypeReference("System", "ObjectDisposedException", module, module.TypeSystem.CoreLibrary);
        var vConnection = new VariableDefinition(fConnection.FieldType);
        var vSocket = new VariableDefinition(mGetClient.ReturnType);
        var vResult = new VariableDefinition(module.TypeSystem.Boolean);
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Add(vConnection);
        body.Variables.Add(vSocket);
        body.Variables.Add(vResult);
        body.InitLocals = true;

        var il = body.GetILProcessor();
        var returnFalse = il.Create(OpCodes.Ldc_I4_0);
        var tryStart = il.Create(OpCodes.Ldloc, vConnection);
        var returnResult = il.Create(OpCodes.Ldloc, vResult);
        var handler = il.Create(OpCodes.Pop);
        var falseInTry = il.Create(OpCodes.Ldc_I4_0);

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fConnection));
        il.Append(il.Create(OpCodes.Stloc, vConnection));
        il.Append(il.Create(OpCodes.Ldloc, vConnection));
        il.Append(il.Create(OpCodes.Brfalse, returnFalse));
        il.Append(tryStart);
        il.Append(il.Create(OpCodes.Callvirt, mGetClient));
        il.Append(il.Create(OpCodes.Stloc, vSocket));
        il.Append(il.Create(OpCodes.Ldloc, vSocket));
        il.Append(il.Create(OpCodes.Brfalse, falseInTry));
        il.Append(il.Create(OpCodes.Ldloc, vConnection));
        il.Append(il.Create(OpCodes.Callvirt, mGetConnected));
        il.Append(il.Create(OpCodes.Stloc, vResult));
        il.Append(il.Create(OpCodes.Leave, returnResult));
        il.Append(falseInTry);
        il.Append(il.Create(OpCodes.Stloc, vResult));
        il.Append(il.Create(OpCodes.Leave, returnResult));
        il.Append(handler);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, vResult));
        il.Append(il.Create(OpCodes.Leave, returnResult));
        il.Append(returnResult);
        il.Append(il.Create(OpCodes.Ret));
        il.Append(returnFalse);
        il.Append(il.Create(OpCodes.Ret));
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = tObjectDisposedException,
            TryStart = tryStart,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = returnResult,
        });
        if (UnderflowingPops(isConnected, unrepairable).Count > 0 || unrepairable.Count > 0)
            throw new InvalidOperationException("TcpSocket.ISocket.IsConnected does not verify after patch: " + string.Join("; ", unrepairable));
        Console.WriteLine("FIX TcpSocket.ISocket.IsConnected: snapshot connection/client and return false on ObjectDisposedException");
    }
}

string[] processMemoryMethods =
{
    "Terraria.ModLoader.Core.MemoryTracking::Finish", "Terraria.ModLoader.Core.MemoryTracking::InGameUpdate",
    "Terraria.ModLoader.UI.UIMemoryBar::RecalculateMemory", "Terraria.ModLoader.Logging::FirstChanceExceptionHandler",
};
var gcType = new TypeReference("System", "GC", module, module.TypeSystem.CoreLibrary);
var totalMemory = new MethodReference("GetTotalMemory", module.TypeSystem.Int64, gcType);
totalMemory.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
int processRewrites = 0;
foreach (var name in processMemoryMethods)
{
    var method = AllTypes(module.Types).SelectMany(t => t.Methods).Single(m => $"{m.DeclaringType.FullName}::{m.Name}" == name);
    var code = method.Body.Instructions;
    foreach (var instruction in code.ToList())
    {
        if (instruction.Operand is not MethodReference r || r.DeclaringType.FullName != "System.Diagnostics.Process")
            continue;
        switch (r.Name)
        {
            case "GetCurrentProcess":
                instruction.OpCode = OpCodes.Ldnull;
                instruction.Operand = null;
                break;
            case "Refresh":
                instruction.OpCode = OpCodes.Pop;
                instruction.Operand = null;
                break;
            case "get_WorkingSet64":
            case "get_PrivateMemorySize64":
                instruction.OpCode = OpCodes.Pop;
                instruction.Operand = null;
                var index = code.IndexOf(instruction);
                code.Insert(index + 1, Instruction.Create(OpCodes.Ldc_I4_0));
                code.Insert(index + 2, Instruction.Create(OpCodes.Call, totalMemory));
                break;
            case "GetProcesses":
                // Finish sums other processes inside a try/catch that falls back to "Unknown".
                if (name != "Terraria.ModLoader.Core.MemoryTracking::Finish")
                    throw new InvalidOperationException($"unexpected Process.GetProcesses in {name}");
                continue;
            default:
                throw new InvalidOperationException($"unexpected Process.{r.Name} in {name}");
        }
        processRewrites++;
    }
    if (UnderflowingPops(method, unrepairable).Count > 0 || unrepairable.Count > 0)
        throw new InvalidOperationException($"{method.FullName} does not verify after patch: " + string.Join("; ", unrepairable));
}
Console.WriteLine($"FIX Process memory reads -> GC.GetTotalMemory in {processMemoryMethods.Length} methods ({processRewrites} instructions)");

var fnaAssembly = resolver.Resolve(module.AssemblyReferences.Single(r => r.Name == "FNA"));
var sdlType = fnaAssembly.MainModule.GetType("SDL2.SDL");
var setHint = module.ImportReference(sdlType.Methods.Single(m => m.Name == "SDL_SetHintWithPriority"));
var overridePriority = sdlType.NestedTypes.Single(t => t.Name == "SDL_HintPriority").Fields.Single(f => f.Name == "SDL_HINT_OVERRIDE");
var mainInitialize = module.GetType("Terraria.Main").Methods.Single(m => m.Name == "Initialize" && !m.HasParameters && !m.IsStatic);
var initIl = mainInitialize.Body.GetILProcessor();
var initFirst = mainInitialize.Body.Instructions[0];
initIl.InsertBefore(initFirst, initIl.Create(OpCodes.Ldstr, "SDL_TOUCH_MOUSE_EVENTS"));
initIl.InsertBefore(initFirst, initIl.Create(OpCodes.Ldstr, "1"));
initIl.InsertBefore(initFirst, initIl.Create(OpCodes.Ldc_I4, Convert.ToInt32(overridePriority.Constant)));
initIl.InsertBefore(initFirst, initIl.Create(OpCodes.Call, setHint));
initIl.InsertBefore(initFirst, initIl.Create(OpCodes.Pop));
if (UnderflowingPops(mainInitialize, unrepairable).Count > 0 || unrepairable.Count > 0)
    throw new InvalidOperationException("Main.Initialize does not verify after patch: " + string.Join("; ", unrepairable));
Console.WriteLine($"FIX Main.Initialize: SDL_TOUCH_MOUSE_EVENTS=1 at override priority ({overridePriority.Constant})");

var nxCrypto = AssemblyDefinition.ReadAssembly(nxCryptoPath).MainModule.GetType("Terraria.NxCrypto.NxCrypto")
    ?? throw new InvalidOperationException("Terraria.NxCrypto.NxCrypto not found in " + nxCryptoPath);
var player = module.GetType("Terraria.Player");
int cryptoSites = 0;
foreach (var (methodName, factory) in new[] { ("SavePlayerFile_Vanilla", "CreateEncryptor"), ("LoadPlayerFromStream", "CreateDecryptor") })
{
    var method = player.Methods.Single(m => m.Name == methodName);
    var code = method.Body.Instructions;
    var call = code.FirstOrDefault(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r
        && r.DeclaringType.FullName == "System.Security.Cryptography.SymmetricAlgorithm" && r.Name == factory);
    if (call == null)
        continue;
    var iv = call.Previous; var key = iv.Previous; var instance = key.Previous;
    if (!(iv.OpCode == OpCodes.Ldsfld && key.OpCode == OpCodes.Ldsfld && ((FieldReference)iv.Operand).Name == "ENCRYPTION_KEY"
        && ((FieldReference)key.Operand).Name == "ENCRYPTION_KEY" && instance.OpCode.Code is Code.Ldloc_1 or Code.Ldloc_S))
        throw new InvalidOperationException($"unexpected crypto call shape in Player.{methodName}");
    instance.OpCode = OpCodes.Nop;
    instance.Operand = null;
    call.OpCode = OpCodes.Call;
    call.Operand = module.ImportReference(nxCrypto.Methods.Single(m => m.Name == factory));
    if (UnderflowingPops(method, unrepairable).Count > 0 || unrepairable.Count > 0)
        throw new InvalidOperationException($"Player.{methodName} does not verify after patch: " + string.Join("; ", unrepairable));
    cryptoSites++;
}
Console.WriteLine($"FIX Player save/load: RijndaelManaged -> NxCrypto ({cryptoSites} sites)");

var achMgr = module.GetType("Terraria.Achievements.AchievementManager");
int achCryptoSites = 0;
foreach (var (methodName, factory, paramCount) in new[] {
    ("Save", "CreateEncryptor", 2),
    ("Load", "CreateDecryptor", 2),
    ("LoadExistingAchievements", "CreateDecryptor", 2)
})
{
    var method = achMgr.Methods.Single(m => m.Name == methodName && m.Parameters.Count == paramCount);
    var code = method.Body.Instructions;
    var call = code.FirstOrDefault(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r
        && r.DeclaringType.FullName == "System.Security.Cryptography.SymmetricAlgorithm" && r.Name == factory);
    if (call == null)
        continue;
    var iv = call.Previous; var key = iv.Previous; var instance = key.Previous;
    if (!(iv.OpCode == OpCodes.Ldfld && ((FieldReference)iv.Operand).Name == "_cryptoKey"
        && key.OpCode == OpCodes.Ldarg_0
        && instance.OpCode == OpCodes.Ldfld && ((FieldReference)instance.Operand).Name == "_cryptoKey"))
        throw new InvalidOperationException($"unexpected crypto call shape in AchievementManager.{methodName}");
    var newobj = instance.Previous.Previous;
    if (newobj.OpCode != OpCodes.Newobj || !((MethodReference)newobj.Operand).DeclaringType.FullName.Contains("RijndaelManaged"))
        throw new InvalidOperationException($"unexpected RijndaelManaged constructor in AchievementManager.{methodName}");
    newobj.OpCode = OpCodes.Nop;
    newobj.Operand = null;
    call.OpCode = OpCodes.Call;
    call.Operand = module.ImportReference(nxCrypto.Methods.Single(m => m.Name == factory));
    if (UnderflowingPops(method, unrepairable).Count > 0 || unrepairable.Count > 0)
        throw new InvalidOperationException($"AchievementManager.{methodName} does not verify after patch: " + string.Join("; ", unrepairable));
    achCryptoSites++;
}
Console.WriteLine($"FIX AchievementManager save/load: RijndaelManaged -> NxCrypto ({achCryptoSites} sites)");
// 9. WallLoader.ModifyLight early exit if no mod wall hooks exist
var wallLoader = module.GetType("Terraria.ModLoader.WallLoader");
if (wallLoader != null)
{
    var modifyLight = wallLoader.Methods.FirstOrDefault(m => m.Name == "ModifyLight");
    if (modifyLight != null && modifyLight.HasBody)
    {
        var il = modifyLight.Body.GetILProcessor();
        var first = modifyLight.Body.Instructions[0];
        var wallLoaderType = wallLoader;
        var hookField = wallLoader.Fields.FirstOrDefault(f => f.Name == "HookModifyLight");
        if (hookField != null)
        {
            // If (type <= 0 && (HookModifyLight == null || HookModifyLight.Length == 0)) return;
            // Or simply if HookModifyLight.Length == 0 and type <= 0 or unloaded wall.
            // At start: if (type <= 0) return;
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_2)); // type
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
            var continueLabel = il.Create(OpCodes.Nop);
            il.InsertBefore(first, il.Create(OpCodes.Bgt_S, continueLabel));
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, hookField));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first)); // if HookModifyLight is null return
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, hookField));
            il.InsertBefore(first, il.Create(OpCodes.Ldlen));
            il.InsertBefore(first, il.Create(OpCodes.Conv_I4));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, continueLabel));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            il.InsertBefore(first, continueLabel);
            Console.WriteLine("FIX WallLoader.ModifyLight: early exit when wall type <= 0 and no HookModifyLight");
        }
    }
}

// 10. RGB peripherals. The Switch has none, but Main.DoUpdate still runs ChromaEngine.Update twice;
//     in run 39 its ShaderSelector blocked the game thread on the RGB worker's lock for 1.3 s. Both
//     calls now pop their arguments instead.
var mainType = module.GetType("Terraria.Main");
var doUpdate = mainType.Methods.Single(m => m.Name == "DoUpdate" && m.HasBody);
WidenShortBranches(doUpdate.Body);
int chromaCalls = 0;
foreach (var ins in doUpdate.Body.Instructions.ToList())
{
    if (ins.OpCode != OpCodes.Callvirt || ins.Operand is not MethodReference { Name: "Update" } update
        || update.DeclaringType.FullName != "ReLogic.Peripherals.RGB.ChromaEngine")
        continue;
    if (update.Parameters.Count != 1 || !IsVoid(update.ReturnType))
        throw new InvalidOperationException("unexpected ChromaEngine.Update signature: " + update.FullName);
    ins.OpCode = OpCodes.Pop; // the time argument
    ins.Operand = null;
    doUpdate.Body.GetILProcessor().InsertAfter(ins, Instruction.Create(OpCodes.Pop)); // the engine
    chromaCalls++;
}
VerifyPatched(doUpdate, chromaCalls == 2, $"{chromaCalls} ChromaEngine.Update calls, expected 2");
Console.WriteLine($"FIX Main.DoUpdate: skip {chromaCalls} ChromaEngine.Update calls (no RGB devices)");

// 11. Tile access. TileData<T> has a static constructor and is not beforefieldinit, so Mono's AOT
//     inliner rejects TileData<T>.get_ptr, a 6-byte static getter: Tile.Get<T> inlines everywhere
//     but still calls it, about 8% of game-thread time in run 39 and a quarter of the biome scan.
//     With AggressiveInlining, Mono inlines it and emits a class-init check at each site.
var getPtr = module.GetType("Terraria.TileData`1").Methods.Single(m => m.Name == "get_ptr");
getPtr.ImplAttributes |= MethodImplAttributes.AggressiveInlining;
Console.WriteLine("FIX TileData`1.get_ptr: AggressiveInlining");

// 12. Render target refresh. tModLoader moves RenderWalls (renderCount 0) and RenderTiles2 (2) off
//     the RenderTiles frame (3) only when LegacyEngine.Mode == 0, Color lighting. White and Retro
//     lighting stacked all three on one frame about four times a second (the 34-50 ms frames in
//     run 39). Both Mode reads in Main.DoDraw now yield 0.
var doDraw = mainType.Methods.Single(m => m.Name == "DoDraw" && m.HasBody);
WidenShortBranches(doDraw.Body);
var staggered = new List<string>();
foreach (var ins in doDraw.Body.Instructions.ToList())
{
    if (ins.OpCode != OpCodes.Callvirt || ins.Operand is not MethodReference { Name: "get_Mode" } mode
        || mode.DeclaringType.Name != "LegacyLighting")
        continue;
    var render = ins.Next;
    for (int i = 0; i < 8 && render != null && render.Operand is not MethodReference { Name: "RenderTiles2" or "RenderWalls" }; i++)
        render = render.Next;
    if (render?.Operand is not MethodReference { Name: "RenderTiles2" or "RenderWalls" } renderCall)
        throw new InvalidOperationException("Main.DoDraw: LegacyLighting.Mode read not followed by RenderTiles2/RenderWalls: " + ins);
    ins.OpCode = OpCodes.Pop; // the LegacyEngine instance
    ins.Operand = null;
    doDraw.Body.GetILProcessor().InsertAfter(ins, Instruction.Create(OpCodes.Ldc_I4_0));
    staggered.Add(renderCall.Name);
}
VerifyPatched(doDraw, staggered.Count == 2, $"{staggered.Count} LegacyLighting.Mode reads, expected 2");
Console.WriteLine($"FIX Main.DoDraw: {string.Join(" and ", staggered)} refresh on their own frames in every lighting mode");

// 13. Lighting-cycle biome scan. LegacyLighting.PreRenderPhase and LightingEngine.ProcessArea run
//     SceneMetrics.ScanAndExportToMain once per lighting cycle, about four times a second; in run
//     39 each scan took 10-19 ms of the frame it landed on. Those two call sites now scan every
//     second cycle, starting with the first. Player.ForceUpdateBiomes and
//     TeleportPylonsSystem.HandleTeleportRequest still scan on every call.
foreach (var (typeName, methodName) in new[] {
    ("Terraria.Graphics.Light.LegacyLighting", "PreRenderPhase"),
    ("Terraria.Graphics.Light.LightingEngine", "ProcessArea") })
{
    var type = module.GetType(typeName);
    var method = type.Methods.Single(m => m.Name == methodName && m.HasBody);
    var scan = method.Body.Instructions.Single(i => i.Operand is MethodReference { Name: "ScanAndExportToMain" });
    if (method.Body.Instructions.Any(i => i.Operand == scan || i.Operand is Instruction[] targets && targets.Contains(scan))
        || method.Body.ExceptionHandlers.Any(h => h.TryStart == scan || h.TryEnd == scan || h.HandlerStart == scan || h.HandlerEnd == scan))
        throw new InvalidOperationException($"{methodName}: ScanAndExportToMain is a branch or handler boundary");
    WidenShortBranches(method.Body);
    var scannedLast = new FieldDefinition("_nxScannedLastCycle", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Boolean);
    type.Fields.Add(scannedLast);
    var il = method.Body.GetILProcessor();
    // Stack here: [sceneMetrics, settings]. scanNow = !_nxScannedLastCycle; _nxScannedLastCycle = scanNow.
    il.InsertBefore(scan, il.Create(OpCodes.Ldsfld, scannedLast));
    il.InsertBefore(scan, il.Create(OpCodes.Ldc_I4_0));
    il.InsertBefore(scan, il.Create(OpCodes.Ceq));
    il.InsertBefore(scan, il.Create(OpCodes.Dup));
    il.InsertBefore(scan, il.Create(OpCodes.Stsfld, scannedLast));
    il.InsertBefore(scan, il.Create(OpCodes.Brtrue, scan));
    il.InsertBefore(scan, il.Create(OpCodes.Pop)); // settings
    il.InsertBefore(scan, il.Create(OpCodes.Pop)); // sceneMetrics
    il.InsertBefore(scan, il.Create(OpCodes.Br, scan.Next));
    VerifyPatched(method, true, "");
    Console.WriteLine($"FIX {type.Name}.{methodName}: SceneMetrics scan every second lighting cycle");
}

// 14. Once-a-minute RAM log. MemoryTracking.InGameUpdate only logs when memory crosses a new GB;
//     after patch 6 each of its three reads is GC.GetTotalMemory, which on SGen walks every
//     major-heap object: about 140 ms of the game thread every 60 s in run 40. It now returns.
var inGameUpdate = module.GetType("Terraria.ModLoader.Core.MemoryTracking").Methods.Single(m => m.Name == "InGameUpdate" && m.HasBody);
if (!inGameUpdate.IsStatic || !IsVoid(inGameUpdate.ReturnType) || inGameUpdate.Parameters.Count != 0)
    throw new InvalidOperationException("unexpected MemoryTracking.InGameUpdate signature");
inGameUpdate.Body.Instructions.Clear();
inGameUpdate.Body.ExceptionHandlers.Clear();
inGameUpdate.Body.Variables.Clear();
inGameUpdate.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
Console.WriteLine("FIX MemoryTracking.InGameUpdate: returns at once (its RAM log walked the GC heap every minute)");

// 15. Tile lookups. Main.tile[x, y] is Tilemap.get_Item(int, int), marked AggressiveInlining, but
//     Mono's LLVM inliner refuses any method containing `throw` (method-to-ir.c: "throw" inline
//     failure), so its bounds check's throw made every lookup a call: 3.1% of game-thread time in
//     run 40. The throw moves into a NoInlining helper; the bounds check itself is unchanged.
var tilemap = module.GetType("Terraria.Tilemap");
var tileGet = tilemap.Methods.Single(m => m.Name == "get_Item" && m.Parameters.Count == 2
    && m.Parameters.All(p => p.ParameterType.MetadataType == MetadataType.Int32));
var tileThrows = tileGet.Body.Instructions.Where(i => i.OpCode == OpCodes.Throw).ToList();
if (tileThrows.Count != 1 || tileThrows[0].Previous?.OpCode != OpCodes.Newobj
    || tileThrows[0].Previous.Operand is not MethodReference { DeclaringType.FullName: "System.IndexOutOfRangeException" } outOfRangeCtor
    || outOfRangeCtor.Parameters.Count != 0)
    throw new InvalidOperationException("unexpected Tilemap.get_Item shape");
var throwOutOfRange = new MethodDefinition("NxThrowIndexOutOfRange",
    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void)
{
    ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.NoInlining,
};
throwOutOfRange.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Newobj, outOfRangeCtor));
throwOutOfRange.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Throw));
tilemap.Methods.Add(throwOutOfRange);
var outOfRange = tileThrows[0].Previous;
outOfRange.OpCode = OpCodes.Call; // the bounds-check branch still targets this instruction
outOfRange.Operand = throwOutOfRange;
tileThrows[0].OpCode = OpCodes.Nop; // unreachable: the helper always throws
tileThrows[0].Operand = null;
VerifyPatched(tileGet, true, "");
Console.WriteLine("FIX Tilemap.get_Item(int, int): out-of-range throw moved to a NoInlining helper");

// 16. Lighting-cycle black refresh (LightingCyclePatch.cs): RenderBlack, about 4.7 ms on the
//     renderCount 3 frame, runs on the idle renderCount 4 frame instead.
LightingCyclePatch.Apply(module);

// 17. Chat text (TextCachePatch.cs): DrawColorCodedString stops running Regex.Split per draw and
//     ParseMessage memoizes its Format regex scan; snippets and tag handlers still run every call.
TextCachePatch.Apply(module);

// 18. Section framing (SectionFramingPatch.cs): TileDrawing.Update frames the unframed section
//     nearest the drawn area for up to 1.5 ms per frame, so first visits no longer stall 90-160 ms.
SectionFramingPatch.Apply(module);

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
