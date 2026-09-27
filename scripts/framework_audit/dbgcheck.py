"""Report DebuggableAttribute modes and surviving Debug.Assert/Fail call sites.

usage: uv run --with dnfile python3 dbgcheck.py <assembly.dll> [...]
modes 0x107 = Default|DisableOptimizations|EnableEditAndContinue|IgnoreSymbolStoreSequencePoints (Debug IL).
"""
import sys, struct, dnfile
for path in sys.argv[1:]:
    pe = dnfile.dnPE(path)
    md = pe.net.mdtables
    # DebuggableAttribute on the assembly
    attrs = []
    for ca in md.CustomAttribute:
        if ca.Parent.table.name != "Assembly": continue
        t = ca.Type
        row = t.row
        name = getattr(row, "Name", None)
        parent = getattr(row, "Class", None)
        tn = parent.row.TypeName.value if parent is not None and hasattr(parent.row, "TypeName") else "?"
        if tn == "DebuggableAttribute":
            blob = ca.Value.value
            modes = struct.unpack_from("<I", blob, 2)[0] if len(blob) >= 6 else None
            attrs.append(hex(modes) if modes is not None else blob.hex())
    # count calls to System.Diagnostics.Debug::Assert / Fail in method bodies
    dbg_tokens = set()
    for i, r in enumerate(md.MemberRef or []):
        c = r.Class
        if c.table.name == "TypeRef" and c.row.TypeName.value == "Debug" and c.row.TypeNamespace.value == "System.Diagnostics" and r.Name.value in ("Assert", "Fail"):
            dbg_tokens.add(0x0A000000 | (i + 1))
    debug_type_rows = [i + 1 for i, t in enumerate(md.TypeDef) if t.TypeName.value == "Debug" and t.TypeNamespace.value == "System.Diagnostics"]
    if debug_type_rows:
        td = md.TypeDef[debug_type_rows[0] - 1]
        for mref in td.MethodList:
            if mref.row.Name.value in ("Assert", "Fail"):
                dbg_tokens.add(0x06000000 | mref.row_index)
    data = pe.__data__
    calls = 0
    for m in (md.MethodDef or []):
        if not m.Rva: continue
        off = pe.get_offset_from_rva(m.Rva); h = data[off]
        if h & 3 == 2: size, code = h >> 2, off + 1
        else:
            size = struct.unpack_from("<I", data, off + 4)[0]; code = off + (struct.unpack_from("<H", data, off)[0] >> 12) * 4
        body = data[code:code + size]
        for p in range(len(body) - 4):
            if body[p] == 0x28 and struct.unpack_from("<I", body, p + 1)[0] in dbg_tokens: calls += 1
    print(path.split("/")[-1], "DebuggableAttribute modes:", attrs or "none", "| Debug.Assert/Fail call sites:", calls)
