using Mono.Cecil;

// usage: SuppressGc --list <FNA.dll>
//        SuppressGc <in FNA.dll> <out FNA.dll> <EntryPoint> [EntryPoint...]
// Adds [SuppressGCTransition] to the named FNA3D P/Invokes. The attribute type is
// referenced from System.Private.CoreLib, where Mono's marshaller looks it up.
if (args.Length == 2 && args[0] == "--list")
{
    var listed = AssemblyDefinition.ReadAssembly(args[1]);
    Console.WriteLine($"# mvid {listed.MainModule.Mvid}");
    foreach (var t in listed.MainModule.GetTypes())
    foreach (var m in t.Methods.Where(m => m.HasPInvokeInfo))
        Console.WriteLine($"{m.PInvokeInfo.Module.Name}\t{m.PInvokeInfo.EntryPoint}\t{t.FullName}::{m.Name}\t{string.Join(",", m.CustomAttributes.Select(a => a.AttributeType.Name))}");
    return 0;
}
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: SuppressGc --list <dll> | <in> <out> <EntryPoint>...");
    return 2;
}
var wanted = new HashSet<string>(args.Skip(2));
var asm = AssemblyDefinition.ReadAssembly(args[0]);
var module = asm.MainModule;
var corlib = module.AssemblyReferences.FirstOrDefault(r => r.Name == "System.Private.CoreLib");
if (corlib == null)
{
    corlib = new AssemblyNameReference("System.Private.CoreLib", new Version(9, 0, 0, 0))
    {
        PublicKeyToken = new byte[] { 0x7c, 0xec, 0x85, 0xd7, 0xbe, 0xa7, 0x79, 0x8e },
    };
    module.AssemblyReferences.Add(corlib);
}
var attrType = new TypeReference("System.Runtime.InteropServices", "SuppressGCTransitionAttribute", module, corlib);
var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attrType) { HasThis = true };
var done = new HashSet<string>();
foreach (var t in module.GetTypes())
foreach (var m in t.Methods.Where(m => m.HasPInvokeInfo && m.PInvokeInfo.Module.Name == "FNA3D" && wanted.Contains(m.PInvokeInfo.EntryPoint)))
{
    if (m.CustomAttributes.Any(a => a.AttributeType.Name == attrType.Name))
        throw new Exception("already patched: " + m.FullName);
    m.CustomAttributes.Add(new CustomAttribute(ctor));
    done.Add(m.PInvokeInfo.EntryPoint);
    Console.WriteLine("suppress " + m.PInvokeInfo.EntryPoint + "  " + m.FullName);
}
var missing = wanted.Except(done).ToList();
if (missing.Count > 0)
    throw new Exception("entry points not found: " + string.Join(", ", missing));
asm.Write(args[1]);
return 0;
