using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using static Program;

internal static class PeResources
{
    internal static string[] Fingerprints(byte[] image)
    {
        using var stream = new MemoryStream(image);
        using var pe = new PEReader(stream);
        var directory = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
        if (directory.Size == 0) return Array.Empty<string>();
        var data = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent().ToArray();
        var result = new List<string>();
        var visiting = new HashSet<int>();
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        void Visit(int offset, string path)
        {
            Require(visiting.Add(offset), "cyclic PE resource directory");
            int count = U16(offset + 12) + U16(offset + 14);
            for (int i = 0; i < count; i++)
            {
                uint name = U32(offset + 16 + 8 * i);
                uint value = U32(offset + 20 + 8 * i);
                string label;
                if ((name & 0x80000000) != 0)
                {
                    int nameOffset = checked((int)(name & 0x7fffffff));
                    label = "name:" + Encoding.Unicode.GetString(data, nameOffset + 2, U16(nameOffset) * 2);
                }
                else label = "id:" + name;
                string fullName = path + "/" + label;
                int child = checked((int)(value & 0x7fffffff));
                if ((value & 0x80000000) != 0) Visit(child, fullName);
                else
                {
                    int rva = checked((int)U32(child));
                    int size = checked((int)U32(child + 4));
                    result.Add($"{fullName}|{size}|{U32(child + 8)}|{U32(child + 12)}|{Sha(pe.GetSectionData(rva).GetContent(0, size).ToArray())}");
                }
            }
            visiting.Remove(offset);
        }
        Visit(0, "");
        return result.OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }
}
