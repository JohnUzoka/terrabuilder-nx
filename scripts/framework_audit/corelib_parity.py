"""Check a Release CoreLib is ABI-compatible with the Debug-config native Mono runtime.

usage: uv run --with dnfile python3 corelib_parity.py <debug CoreLib.dll> <release CoreLib.dll>

The native runtime mirrors some managed types' field layouts (object-internals.h)
and binds internal calls by name+signature. Mixing a Release CoreLib with the
proven runtime is only safe if both are unchanged. Compares, per TypeDef:
instance-field name/signature order and flags, explicit layout (ClassLayout,
FieldLayout); and per method: every InternalCall (MethodImpl 0x1000) name+sig.
Exits 1 on any difference that is not a known Debug-only member.
"""
import sys, dnfile


def type_name(td):
    return td.TypeNamespace.value + "." + td.TypeName.value


def comp(b, p):
    x = b[p]
    if x & 0x80 == 0: return x, p + 1
    if x & 0xC0 == 0x80: return ((x & 0x3F) << 8) | b[p + 1], p + 2
    return ((x & 0x1F) << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3], p + 4


PRIM = {0x01: "void", 0x02: "bool", 0x03: "char", 0x04: "i1", 0x05: "u1", 0x06: "i2", 0x07: "u2",
        0x08: "i4", 0x09: "u4", 0x0a: "i8", 0x0b: "u8", 0x0c: "r4", 0x0d: "r8", 0x0e: "string",
        0x16: "typedref", 0x18: "native int", 0x19: "native uint", 0x1c: "object"}


def summarize(path):
    md = dnfile.dnPE(path).net.mdtables
    tds = list(md.TypeDef)
    trs = list(md.TypeRef or [])
    tss = list(md.TypeSpec or [])
    # nested types share names; qualify with enclosing type
    enclosing = {}
    for nc in (md.NestedClass or []):
        enclosing[nc.NestedClass.row_index] = nc.EnclosingClass.row_index
    def qname(i):
        n = type_name(tds[i - 1])
        return (qname(enclosing[i]) + "/" + n) if i in enclosing else n
    def trname(i):
        r = trs[i - 1]
        n = type_name(r)
        scope = r.ResolutionScope
        if scope is not None and scope.table is not None and scope.table.name == "TypeRef":
            return trname(scope.row_index) + "/" + n
        return n
    def coded(tok):
        tag, row = tok & 3, tok >> 2
        if tag == 0: return qname(row)
        if tag == 1: return trname(row)
        return "spec(" + sig_type(tss[row - 1].Signature.value, 0)[0] + ")"
    def sig_type(b, p):
        e = b[p]; p += 1
        if e in PRIM: return PRIM[e], p
        if e in (0x0f, 0x10, 0x1d, 0x45):
            t, p = sig_type(b, p); return {0x0f: "*", 0x10: "&", 0x1d: "[]", 0x45: "pinned "}[e] + t, p
        if e in (0x11, 0x12):
            tok, p = comp(b, p); return ("vt " if e == 0x11 else "") + coded(tok), p
        if e in (0x13, 0x1e):
            n, p = comp(b, p); return ("!" if e == 0x13 else "!!") + str(n), p
        if e == 0x15:
            kind = b[p]; tok, p = comp(b, p + 1); n, p = comp(b, p); args = []
            for _ in range(n):
                t, p = sig_type(b, p); args.append(t)
            return ("vt " if kind == 0x11 else "") + coded(tok) + "<" + ",".join(args) + ">", p
        if e == 0x14:
            t, p = sig_type(b, p); rank, p = comp(b, p); ns, p = comp(b, p)
            for _ in range(ns): _, p = comp(b, p)
            nl, p = comp(b, p)
            for _ in range(nl): _, p = comp(b, p)
            return f"{t}[rank{rank}]", p
        if e in (0x1f, 0x20):
            tok, p = comp(b, p); t, p = sig_type(b, p)
            return ("modreq(" if e == 0x1f else "modopt(") + coded(tok) + ") " + t, p
        if e == 0x1b:
            return method_sig(b, p)
        raise ValueError(f"unhandled element type 0x{e:02x}")
    def method_sig(b, p):
        cc = b[p]; p += 1
        gen = 0
        if cc & 0x10: gen, p = comp(b, p)
        n, p = comp(b, p)
        ret, p = sig_type(b, p)
        ps = []
        for _ in range(n):
            if b[p] == 0x41: p += 1
            t, p = sig_type(b, p); ps.append(t)
        return f"cc{cc:02x}`{gen} {ret}({','.join(ps)})", p
    def field_sig(b):
        assert b[0] == 0x06
        return sig_type(b, 1)[0]
    layout = {}
    for cl in (md.ClassLayout or []):
        layout[cl.Parent.row_index] = (cl.PackingSize, cl.ClassSize)
    field_offset = {}
    for fl in (md.FieldLayout or []):
        field_offset[fl.Field.row_index] = fl.Offset
    types, icalls = {}, {}
    for i, td in enumerate(tds, 1):
        q = qname(i)
        fields = []
        for fref in (td.FieldList or []):
            f = fref.row
            if f.Flags.fdStatic or f.Flags.fdLiteral:
                continue
            fields.append((f.Name.value, field_sig(f.Signature.value), field_offset.get(fref.row_index)))
        types[q] = dict(fields=fields, layout=layout.get(i), seq=bool(td.Flags.tdSequentialLayout), explicit=bool(td.Flags.tdExplicitLayout))
        for mref in (td.MethodList or []):
            m = mref.row
            if m.ImplFlags.miInternalCall:
                icalls.setdefault(q + "::" + m.Name.value, set()).add(method_sig(m.Signature.value, 0)[0])
    return types, icalls


dbg_types, dbg_icalls = summarize(sys.argv[1])
rel_types, rel_icalls = summarize(sys.argv[2])
problems, generated, allowed = [], [], []


def is_generated(q):
    # C# compiler-generated types (async/iterator state machines, closures,
    # <PrivateImplementationDetails>): Debug keeps extra hoisted locals, Release
    # optimizes them away. The runtime never mirrors these layouts.
    return any(part.startswith("<") or part.startswith(".<") for part in q.split("/"))


# Verified 2026-09-24: these differ only by `#if DEBUG` diagnostic fields
# (lock owner tracking, work-item double-execution guard, packed search-value
# variants, MemoryFailPoint state). None is referenced from src/mono/mono.
DEBUG_ONLY_OK = {
    "System.Threading.LowLevelLock", "System.Threading.LowLevelMonitor",
    "System.Threading.QueueUserWorkItemCallbackBase",
    "System.Buffers.Any1CharPackedIgnoreCaseSearchValues",
    "System.Buffers.Any2CharPackedIgnoreCaseSearchValues",
    "System.Buffers.Any2CharPackedSearchValues",
    "System.Runtime.MemoryFailPoint/.MemoryFailPointState",
}


def note(q, message):
    if is_generated(q):
        generated.append(message)
    elif q in DEBUG_ONLY_OK:
        allowed.append(q)
    else:
        problems.append(message)


for q, d in dbg_types.items():
    r = rel_types.get(q)
    if r is None:
        # Debug-only type (e.g. assertion helpers) is fine unless it has instance fields the runtime mirrors
        if d["fields"]:
            note(q, f"type only in Debug with instance fields: {q}")
        continue
    if d != r:
        note(q, f"layout differs: {q}\n  debug={d}\n  release={r}")
for q in rel_types.keys() - dbg_types.keys():
    if rel_types[q]["fields"]:
        note(q, f"type only in Release with instance fields: {q}")
for k in dbg_icalls.keys() | rel_icalls.keys():
    if dbg_icalls.get(k) != rel_icalls.get(k):
        problems.append(f"internal call differs: {k} debug={dbg_icalls.get(k)} release={rel_icalls.get(k)}")
print(f"types: debug={len(dbg_types)} release={len(rel_types)}; internal calls: debug={sum(map(len, dbg_icalls.values()))} release={sum(map(len, rel_icalls.values()))}")
print(f"compiler-generated layout differences (ignored, not runtime-mirrored): {len(generated)}; verified Debug-only field types: {len(allowed)}")
for p in problems[:60]:
    print(p)
print("PARITY OK" if not problems else f"PARITY FAILED: {len(problems)} differences")
sys.exit(1 if problems else 0)
