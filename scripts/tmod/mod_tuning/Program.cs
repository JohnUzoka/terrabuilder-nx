// Exact-package tuning for mod assemblies. prepare_modset.py runs it once per mod/library
// assembly before the first hook scan, so lowering, hook identities and AOT all see the final
// bytes and MVID.
//
// 1. Debug-built mods. Roslyn's Debug configuration emits [assembly: Debuggable(...)] with
//    DisableOptimizations set (Fargo's Souls, Fargowiltas and Luminance ship that way: modes
//    0x107). Mono reads the flag and compiles every method of the assembly with inlining
//    disabled, AOT included, so each property getter, Vector2 operator and extension helper
//    stays a call through the PLT. Clearing the bit changes nothing else: the IL is untouched
//    and Mono's inliner keeps its own rules (no EH clauses, NoInlining, StackCrawlMark methods,
//    class-init checks).
// 2. Fargo's Souls "music mod not found" banner. NoMusicNotification.Update counts down only
//    while timeLeft <= 30 or > 200, so the banner holds at 200 until it is clicked, and drawing it
//    costs about 2% of the game thread for as long as it stays. Comparing against -1 instead of
//    200 restores the 5-second countdown of the example notification it was copied from; the
//    fade-in, fade-out and click-to-dismiss paths are unchanged.
//
// Usage: ModTuning <in.dll> <out.dll> [--ref-dir <dir>]... The reference directories resolve
// types the writer needs (enum-typed parameter defaults, e.g. FNA's). An assembly that needs no
// change is copied byte for byte; a changed one gets a content-derived MVID, like NxFix's output.
using Mono.Cecil;
using Mono.Cecil.Cil;

const string Usage = "usage: ModTuning <in.dll> <out.dll> [--ref-dir <dir>]...";
if (args.Length < 2 || args.Length % 2 != 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}
string input = args[0], output = args[1];
if (Path.GetFullPath(input) == Path.GetFullPath(output))
{
    Console.Error.WriteLine("ModTuning: output must differ from input");
    return 2;
}
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input)));
for (int a = 2; a < args.Length; a += 2)
{
    if (args[a] != "--ref-dir" || !Directory.Exists(args[a + 1]))
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }
    resolver.AddSearchDirectory(args[a + 1]);
}

var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
var module = asm.MainModule;
string name = asm.Name.Name;
bool changed = false;

const int DisableOptimizations = 0x100; // DebuggableAttribute.DebuggingModes.DisableOptimizations
var attributes = asm.CustomAttributes;
for (int i = 0; i < attributes.Count; i++)
{
    var attribute = attributes[i];
    if (attribute.AttributeType.FullName != "System.Diagnostics.DebuggableAttribute")
        continue;
    // Edit the blob directly: decoding the DebuggingModes argument would need the enum resolved
    // through the reference assemblies. Blob: prolog 01 00, fixed arguments, 00 00 named count.
    var blob = attribute.GetBlob();
    var parameters = attribute.Constructor.Parameters;
    bool modesForm = parameters.Count == 1 && blob.Length == 8;
    bool boolsForm = parameters.Count == 2 && blob.Length == 6
        && parameters.All(p => p.ParameterType.MetadataType == MetadataType.Boolean);
    if (blob[0] != 1 || blob[1] != 0 || !(modesForm || boolsForm))
        throw new InvalidOperationException($"{name}: unexpected DebuggableAttribute {attribute.Constructor} blob {Convert.ToHexString(blob)}");
    var updated = (byte[])blob.Clone();
    if (modesForm)
    {
        int modes = BitConverter.ToInt32(blob, 2);
        if ((modes & DisableOptimizations) == 0)
            continue;
        BitConverter.GetBytes(modes & ~DisableOptimizations).CopyTo(updated, 2);
        Console.WriteLine($"FIX {name}: DebuggableAttribute modes 0x{modes:x} -> 0x{modes & ~DisableOptimizations:x}; Mono inlines this assembly again");
    }
    else
    {
        if (blob[3] == 0)
            continue;
        updated[3] = 0;
        Console.WriteLine($"FIX {name}: DebuggableAttribute isJITOptimizerDisabled cleared; Mono inlines this assembly again");
    }
    attributes[i] = new CustomAttribute(attribute.Constructor, updated);
    changed = true;
}

if (name == "FargowiltasSouls")
{
    var notification = module.GetType("FargowiltasSouls.Content.UI.NoMusicNotification")
        ?? throw new InvalidOperationException("FargowiltasSouls: NoMusicNotification not found");
    var update = notification.Methods.Single(m => m.Name == "Update" && !m.HasParameters && !m.IsStatic);
    var holds = update.Body.Instructions.Where(i => i.OpCode.Code == Code.Ldc_I4 && (int)i.Operand == 200
        && i.Previous is { OpCode.Code: Code.Ldfld, Operand: FieldReference { Name: "timeLeft" } field }
        && field.DeclaringType.FullName == notification.FullName
        && i.Next?.OpCode.Code == Code.Cgt).ToList();
    if (holds.Count != 1)
        throw new InvalidOperationException($"FargowiltasSouls: expected one `timeLeft > 200` hold in {update.FullName}, found {holds.Count}");
    holds[0].Operand = -1;
    Console.WriteLine("FIX FargowiltasSouls NoMusicNotification.Update: timer no longer holds at 200; the banner dismisses itself after 5 s");
    changed = true;
}

if (!changed)
{
    File.Copy(input, output, overwrite: true);
    Console.WriteLine("UNCHANGED " + output);
    return 0;
}

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
