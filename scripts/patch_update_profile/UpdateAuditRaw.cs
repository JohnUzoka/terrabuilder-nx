using System.Reflection;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using static Common;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;

internal static partial class UpdateAudit
{
    // Reuse the source-bound raw stream decoder, retaining per-use CLASS/VALUETYPE
    // kinds independently of Cecil's shared TypeReference objects.
    static Dictionary<string, string> RawRows(byte[] bytes, ModuleDefinition module)
    {
        using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream);
        var rows = (IEnumerable<KeyValuePair<string, string>>)typeof(RenderRawSignatures).GetMethod("Rows", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { pe, module })!;
        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    }

    static object PreserveRaw(byte[] originalBytes, byte[] candidateBytes, ModuleDefinition original, ModuleDefinition candidate, UpdatePatcher.Receipt[] receipts)
    {
        var before = RawRows(originalBytes, original); var after = RawRows(candidateBytes, candidate);
        var changed = receipts.ToDictionary(r => r.Method, StringComparer.Ordinal);
        int typeIndex = 0, originalUses = 0, mappedInstructions = 0;
        foreach (var type in Types(original)) {
            string owner = "type:" + typeIndex++ + ":" + type.FullName;
            for (int index = 0; index < type.Methods.Count; index++) {
                var method = type.Methods[index];
                if (!changed.TryGetValue(method.FullName, out var receipt)) continue;
                string key = owner + ":method:" + index;
                for (int n = 0; n < method.Body.Instructions.Count; n++) {
                    string sourceKey = key + ":il:" + n;
                    if (!before.TryGetValue(sourceKey, out var expected)) continue;
                    string destinationKey = key + ":il:" + receipt.OriginalIndices[n];
                    Require(after.TryGetValue(destinationKey, out var actual) && expected == actual, "original raw instruction signature changed: " + method.FullName + " originalIndex=" + n);
                    before.Remove(sourceKey); mappedInstructions++;
                }
                string localKey = key + ":locals";
                if (receipt.Kind != "flush") {
                    string prior = before[localKey], current = after[localKey];
                    string added = "primitive:Int32,primitive:Boolean";
                    Require(method.ReturnType.MetadataType == MetadataType.Void, "update56 plan unexpectedly returns a value");
                    Require(current == (prior == "none" ? added : prior + "," + added), "original raw locals changed: " + method.FullName);
                    before.Remove(localKey);
                }
            }
        }
        foreach (var (key, expected) in before) {
            Require(after.TryGetValue(key, out var actual) && actual == expected, "original raw definition/use changed: " + key);
            originalUses++;
        }
        return new { passed = true, unchangedOriginalSignatureUses = originalUses, mappedOriginalInstructionUses = mappedInstructions, originalNamedKindsPreserved = true, originalDefinitionOrderingPreserved = true };
    }
}
