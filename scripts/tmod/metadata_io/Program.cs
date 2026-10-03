using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: MetadataIo <original-System.Reflection.Metadata.dll> <output.dll>");
    return 64;
}

try
{
    string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
    Require(input != output, "Do not overwrite the original input");
    using var assembly = AssemblyDefinition.ReadAssembly(input);
    var module = assembly.MainModule;
    Require(assembly.Name.Name == "System.Reflection.Metadata", "Unexpected assembly");
    var type = module.GetType("System.Reflection.Internal.StreamMemoryBlockProvider")
        ?? throw new InvalidDataException("Missing stream memory provider");
    var constructor = type.Methods.Single(m => m.IsConstructor && !m.IsStatic &&
        string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) ==
        "System.IO.Stream,System.Int64,System.Int32,System.Boolean");
    Require(constructor.HasBody && constructor.Body.ExceptionHandlers.Count == 0, "Unexpected constructor body");
    var instructions = constructor.Body.Instructions;
    var store = instructions.Single(i => i.OpCode == OpCodes.Stfld &&
        i.Operand is FieldReference f && f.Name == "_useMemoryMap" && f.DeclaringType.FullName == type.FullName);
    int end = instructions.IndexOf(store);
    Require(end >= 5 && instructions[end - 5].OpCode == OpCodes.Ldarg_0 &&
        instructions[end - 4].OpCode == OpCodes.Ldarg_1 &&
        instructions[end - 3].OpCode == OpCodes.Isinst &&
        instructions[end - 3].Operand is TypeReference streamType && streamType.FullName == "System.IO.FileStream" &&
        instructions[end - 2].OpCode == OpCodes.Ldnull &&
        instructions[end - 1].OpCode == OpCodes.Cgt_Un, "Expected _useMemoryMap = stream is FileStream");
    var value = instructions.Skip(end - 4).Take(4).ToArray();
    Require(!instructions.Any(i => i.Operand is Instruction target && value.Contains(target) ||
        i.Operand is Instruction[] targets && targets.Any(value.Contains)), "Unexpected branch into assignment");

    // libnx cannot map files. Select the library's existing locked stream-read path;
    // retain its bounded reads, disposal, EOF checks and exception propagation.
    value[0].OpCode = OpCodes.Ldc_I4_0;
    value[0].Operand = null;
    foreach (var instruction in value.Skip(1))
    {
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
    }
    Guid originalMvid = module.Mvid;
    module.Mvid = Guid.Empty;
    using (var image = new MemoryStream())
    {
        assembly.Write(image);
        module.Mvid = new Guid(SHA256.HashData(image.GetBuffer().AsSpan(0, checked((int)image.Length))).AsSpan(0, 16));
    }
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    assembly.Write(output);
    Console.WriteLine(JsonSerializer.Serialize(new {
        input, output, originalMvid, mvid = module.Mvid,
        method = constructor.FullName,
        change = "_useMemoryMap=false; existing ReadMemoryBlockNoLock path retained"
    }));
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("MetadataIo: " + e.Message);
    return 1;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
