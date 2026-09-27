"""Which Terraria.exe methods call into non-AOT framework assemblies.

usage: uv run --with dnfile python3 refscan.py <Terraria.exe> [framework_dir] [out.json]
Follows net40 facade type-forwards (System.Core -> System.Linq, ...) in framework_dir.
"""
import sys, struct, collections, dnfile
pe = dnfile.dnPE(sys.argv[1])
md = pe.net.mdtables
TARGET = {"System.Linq", "System.Collections", "System.ObjectModel"}

asmref = {i + 1: r.Name.value for i, r in enumerate(md.AssemblyRef)}
typeref_asm, typeref_name = {}, {}
for i, r in enumerate(md.TypeRef):
    rs = r.ResolutionScope
    tbl, idx = rs.table.name, rs.row_index
    typeref_name[i + 1] = r.TypeNamespace.value + "." + r.TypeName.value
    typeref_asm[i + 1] = (tbl, idx)

import os
FW = (sys.argv[2] if len(sys.argv) > 2 else
      os.path.join(os.path.dirname(os.path.abspath(__file__)), "../../terraria-mono/mono/framework_net9.0")) + "/"
OUT = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "refscan.json")
forward = {}
def load_forwards(asm):
    if asm in forward: return forward[asm]
    forward[asm] = {}
    path = FW + asm + ".dll"
    if not os.path.exists(path): return forward[asm]
    f = dnfile.dnPE(path).net.mdtables
    refs = {i + 1: r.Name.value for i, r in enumerate(f.AssemblyRef or [])}
    for e in (f.ExportedType or []):
        impl = e.Implementation
        if impl and impl.table.name == "AssemblyRef":
            forward[asm][e.TypeNamespace.value + "." + e.TypeName.value] = refs[impl.row_index]
    return forward[asm]

def tr_assembly(i):
    tbl, idx = typeref_asm[i]
    root = i
    while tbl == "TypeRef":
        root = idx; tbl, idx = typeref_asm[idx]
    if tbl != "AssemblyRef": return None
    asm, name = asmref.get(idx), typeref_name[root]
    for _ in range(4):  # follow facade chains
        nxt = load_forwards(asm).get(name)
        if not nxt: break
        asm = nxt
    return asm

def decode_comp(b, p):
    x = b[p]
    if x & 0x80 == 0: return x, p + 1
    if x & 0xC0 == 0x80: return ((x & 0x3F) << 8) | b[p + 1], p + 2
    return ((x & 0x1F) << 24) | (b[p+1] << 16) | (b[p+2] << 8) | b[p+3], p + 4

def typespec_base(i):
    sig = md.TypeSpec[i - 1].Signature.value
    if not sig or sig[0] != 0x15: return None
    _, p = 0, 2
    tok, _ = decode_comp(sig, 2)
    tag, row = tok & 3, tok >> 2
    return row if tag == 1 else None  # 1 = TypeRef

def parent_typeref(coded):
    t, i = coded.table.name, coded.row_index
    if t == "TypeRef": return i
    if t == "TypeSpec": return typespec_base(i)
    return None

memberref_target = {}
for i, r in enumerate(md.MemberRef):
    tr = parent_typeref(r.Class)
    if tr and tr_assembly(tr) in TARGET:
        memberref_target[0x0A000000 | (i + 1)] = (tr_assembly(tr), typeref_name[tr] + "::" + r.Name.value)
# generic method instantiations (e.g. Enumerable.Where<T>)
methodspec_target = {}
for i, r in enumerate(md.MethodSpec or []):
    m = r.Method
    if m.table.name == "MemberRef":
        tok = 0x0A000000 | m.row_index
        if tok in memberref_target:
            methodspec_target[0x2B000000 | (i + 1)] = memberref_target[tok]
targets = {**memberref_target, **methodspec_target}

typedefs = list(md.TypeDef)
methods = list(md.MethodDef)
owner = {}
for t in typedefs:
    for mref in (t.MethodList or []):
        owner[mref.row_index] = t.TypeNamespace.value + "." + t.TypeName.value

data = pe.__data__
callers = collections.defaultdict(set)
for mi, m in enumerate(methods, 1):
    if not m.Rva: continue
    off = pe.get_offset_from_rva(m.Rva)
    h = data[off]
    if h & 3 == 2: size, code = h >> 2, off + 1
    else:
        hs = (struct.unpack_from("<H", data, off)[0] >> 12) * 4
        size = struct.unpack_from("<I", data, off + 4)[0]; code = off + hs
    body = data[code:code + size]
    # scan for call/callvirt/newobj/ldftn tokens: 0x28,0x6F,0x73 + 0xFE06
    for p in range(len(body) - 4):
        op = body[p]
        if op in (0x28, 0x6F, 0x73):
            tok = struct.unpack_from("<I", body, p + 1)[0]
            if tok in targets:
                callers[targets[tok]].add(owner.get(mi, "?") + "::" + m.Name.value)

by_asm = collections.Counter()
for (asm, name), cs in callers.items(): by_asm[asm] += len(cs)
print("call sites (distinct caller methods) per assembly:", dict(by_asm))
import json
json.dump({f"{a}|{n}": sorted(c) for (a, n), c in callers.items()}, open(OUT, "w"), indent=1)
hot = ("TileDrawing", "Main::Draw", "Main::DoDraw", "Main::Update", "Lighting", "LightMap", "LightingEngine",
       "Projectile::Update", "NPC::Update", "NPC::AI", "Player::Update", "Item::UpdateItem", "WorldItem",
       "Main::DrawInterface", "Main::GUI", "Main::DrawNPC", "Main::DrawProj", "Main::DrawPlayer", "Dust::", "Gore::",
       "WorldGen::UpdateWorld", "Collision::", "Liquid::", "SpriteBatch", "TileBatch", "PlayerDrawLayers",
       "LegacyPlayerRenderer", "Wiring::", "SceneMetrics", "TileEntity", "Rain::", "Cloud::")
print("\nhot-path callers:")
for (asm, name), cs in sorted(callers.items()):
    h = [c for c in cs if any(k in c for k in hot)]
    if h: print(f"  {asm}: {name}  <- {', '.join(sorted(h)[:6])}{' …' if len(h) > 6 else ''}")
