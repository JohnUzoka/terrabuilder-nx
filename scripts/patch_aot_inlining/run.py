#!/usr/bin/env python3
"""Build and exercise the raw build41 metadata patcher inside the mono-nx container.

Use a fresh --output directory under /build/inline41/window-guards. The accepted image is
never overwritten. This checks metadata/byte preservation, not AOT or gameplay.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import uuid

INPUT_SHA = "34e3b59f5736ec5ea3d590f87cf9d09aa8f7e8e67ed16d7f7f59b8df8d3344f3"
OUTPUT_SHA = "d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298"
INPUT_MVID = "7b2a493d-1dc3-66fe-a3d1-620d76453f0c"
OUTPUT_MVID = "2a9040da-3f4b-844f-a14b-3056cdda95ba"
# Independently pinned offsets from the exact input's metadata inspection.
METHODS = (
    {"token": "0x06001116", "name": "SetDisplayMode", "signature": "000301080802",
     "flags_offset": 24372908, "body_offset": 4378972, "body_size": 1265,
     "body_sha": "f6dea10223366c36102d156a3b3426e29394e70ad7e3d4be08071a73ea650de7",
     "il_sha": "dc6fa0fccca4af832f27668172faf8dc3b736c67722bbfe8952bd7a573d10e47"},
    {"token": "0x06002B42", "name": "TryMovingToScreen", "signature": "2001010E",
     "flags_offset": 24493508, "body_offset": 5899524, "body_size": 144,
     "body_sha": "98ae9c98e550c85f1de5693bc0e4e7d2c4d51b8b907f5e83826da1e3a4e3c2a7",
     "il_sha": "4d49f2c483d9fed4fc96601d95738b55e009f40cdfcb3e6c14f324a480192c6d"},
)
MVID_OFFSET = 26218284
DOMAIN = "Terraria.AotInlining.SetDisplayMode.TryMovingToScreen.NoOptimization.v1\n"


def sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def fixture_layout(image: bytes) -> tuple[int, int, dict[str, int]]:
    """Locate fields only for corrupt fixtures; never used to produce an accepted image."""
    pe = struct.unpack_from("<I", image, 0x3C)[0]
    require(image[pe:pe + 4] == b"PE\0\0", "fixture source is not a PE")
    section_count = struct.unpack_from("<H", image, pe + 6)[0]
    optional_size = struct.unpack_from("<H", image, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", image, optional)[0]
    require(magic in (0x10B, 0x20B), "unsupported fixture PE")
    directories = optional + (96 if magic == 0x10B else 112)
    sections = [struct.unpack_from("<IIII", image, optional + optional_size + i * 40 + 8)
                for i in range(section_count)]

    def file_offset(rva: int) -> int:
        matches = [raw + rva - virtual for _, virtual, size, raw in sections
                   if virtual <= rva < virtual + size]
        require(len(matches) == 1, "ambiguous fixture RVA")
        return matches[0]

    cli = file_offset(struct.unpack_from("<I", image, directories + 14 * 8)[0])
    metadata = file_offset(struct.unpack_from("<I", image, cli + 8)[0])
    require(image[metadata:metadata + 4] == b"BSJB", "invalid fixture metadata")
    version_size = struct.unpack_from("<I", image, metadata + 12)[0]
    header = metadata + 16 + version_size
    stream_count = struct.unpack_from("<H", image, header + 2)[0]
    cursor = header + 4
    strings = None
    for _ in range(stream_count):
        offset, size = struct.unpack_from("<II", image, cursor)
        start = cursor + 8
        end = image.index(b"\0", start)
        name = image[start:end]
        cursor = start + ((end - start + 1 + 3) & ~3)
        if name == b"#Strings":
            strings = (metadata + offset, size)
    require(strings is not None, "fixture Strings heap missing")
    start, size = strings
    name_offsets = {method["name"]: image.index(method["name"].encode() + b"\0", start, start + size)
                    for method in METHODS}
    return cli, directories + 4 * 8, name_offsets


def verify_image(source: bytes, path: Path, manifest_path: Path) -> dict:
    image = path.read_bytes()
    manifest = json.loads(manifest_path.read_text())
    require(hashlib.sha256(image).hexdigest() == OUTPUT_SHA, "accepted SHA changed")
    require(len(image) == len(source) == 26587136, "image length changed")
    require(manifest["input"] == {"sha256": INPUT_SHA, "mvid": INPUT_MVID, "length": len(source)}, "input manifest differs")
    require(manifest["output"] == {"sha256": OUTPUT_SHA, "mvid": OUTPUT_MVID, "length": len(image)}, "output manifest differs")
    require(manifest["schemaVersion"] == 2 and len(manifest["methods"]) == len(METHODS) == 2,
            "expected exactly the two guarded methods")
    for target, method, offsets in zip(METHODS, manifest["methods"], manifest["offsets"]["Methods"], strict=True):
        require(method["token"] == target["token"] and method["Signature"] == target["signature"] and
                method["BodySha256"] == target["body_sha"] and method["IlSha256"] == target["il_sha"] and
                method["oldImplFlags"] == "0x0000" and method["newImplFlags"] == "0x0040",
                "unexpected caller signature, flags or body manifest")
        require(offsets["ImplFlagsOffset"] == target["flags_offset"], "unexpected method patch offset")
    require(manifest["offsets"]["MvidOffset"] == MVID_OFFSET, "unexpected MVID patch offset")
    permitted = [(method["flags_offset"], 2) for method in METHODS] + [(MVID_OFFSET, 16)]
    require([(entry["offset"], entry["length"]) for entry in manifest["permittedRanges"]] == permitted,
            "manifest permits unexpected mutation ranges")
    expected_mvid = bytearray(hashlib.sha256((DOMAIN + INPUT_SHA).encode()).digest()[:16])
    expected_mvid[7] = (expected_mvid[7] & 15) | 0x80
    expected_mvid[8] = (expected_mvid[8] & 63) | 0x80
    require(str(uuid.UUID(bytes_le=bytes(expected_mvid))) == OUTPUT_MVID, "MVID derivation differs")
    require(str(uuid.UUID(bytes_le=source[MVID_OFFSET:MVID_OFFSET + 16])) == INPUT_MVID, "old raw GUID differs")
    require(image[MVID_OFFSET:MVID_OFFSET + 16] == expected_mvid, "new raw GUID differs")
    for method in METHODS:
        flags, start, size = method["flags_offset"], method["body_offset"], method["body_size"]
        require(source[flags:flags + 2] == b"\0\0" and image[flags:flags + 2] == b"\x40\0",
                f"{method['name']} ImplFlags is not precisely OR 64")
        require(source[start:start + size] == image[start:start + size] and
                hashlib.sha256(image[start:start + size]).hexdigest() == method["body_sha"] and
                hashlib.sha256(image[start + 12:start + size]).hexdigest() == method["il_sha"],
                f"{method['name']} method header or instructions changed")
    old, new = memoryview(source), memoryview(image)
    cursor = 0
    changed = []
    ranges = []
    for start, length in permitted:
        require(old[cursor:start] == new[cursor:start], f"outside-range bytes changed before {start}")
        for offset in range(start, start + length):
            if source[offset] != image[offset]:
                changed.append({"Offset": offset, "Before": source[offset], "After": image[offset]})
        cursor = start + length
    require(old[cursor:] == new[cursor:], "outside-range bytes changed after MVID")
    cursor = 0
    while cursor < len(changed):
        start = changed[cursor]["Offset"]
        end = start + 1
        cursor += 1
        while cursor < len(changed) and changed[cursor]["Offset"] == end:
            end += 1
            cursor += 1
        ranges.append({"Offset": start, "Before": source[start:end].hex().upper(), "After": image[start:end].hex().upper()})
    require(manifest["diffBytes"] == changed and manifest["diffRanges"] == ranges and
            manifest["changedByteCount"] == len(changed) == 18, "manifest byte diff differs")
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=Path("/build/aot41/runtime-romfs/Terraria.exe"))
    parser.add_argument("--output", type=Path, default=Path("/build/inline41/window-guards/repro"))
    parser.add_argument("--reference", type=Path, help="optional immutable accepted image to compare, never modified")
    parser.add_argument("--dotnet", default="/root/.dotnet/dotnet")
    args = parser.parse_args()
    source, root = args.input.resolve(), args.output.absolute()
    require(not os.path.lexists(root), "runner output must be fresh")
    require(not source.is_relative_to(root.resolve()), "runner output contains input")
    require(root.parent.is_dir(), "runner output parent must exist")
    require(sha(source) == INPUT_SHA, "runner input is not pinned build41")
    reference_sha = sha(args.reference) if args.reference else None
    require(reference_sha is None or reference_sha == OUTPUT_SHA, "reference hash differs")
    root.mkdir()
    environment = os.environ.copy()
    environment.update(DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_TieredCompilation="0")
    invocations = []
    guards = []

    def run(command: list[str], label: str, expected: int = 0) -> str:
        log = root / (label + ".log")
        with log.open("x") as stream:
            result = subprocess.run(command, env=environment, stdout=stream, stderr=subprocess.STDOUT)
        text = log.read_text()
        invocations.append({"argv": command, "returncode": result.returncode, "log": str(log)})
        require(result.returncode == expected, f"{label} exited {result.returncode}, expected {expected}: {text}")
        print(f"PASS {label}", flush=True)
        return text

    project = Path(__file__).resolve().with_name("PatchAotInlining.csproj")
    run([args.dotnet, "build", str(project), "-c", "Release", "-o", str(root / "tool"),
         f"-p:BaseIntermediateOutputPath={root}/obj/"], "build")
    tool = [args.dotnet, str(root / "tool/PatchAotInlining.dll"), "accept"]
    original = source.read_bytes()
    for name in ("first", "repeat"):
        run(tool + [str(source), str(root / name)], name)
        verify_image(original, root / name / "Terraria.exe", root / name / "manifest.json")
    require((root / "first/manifest.json").read_bytes() == (root / "repeat/manifest.json").read_bytes(),
            "manifest is not deterministic")

    def reject(label: str, fixture: Path, output: Path, expected_error: str, existing: bool = False) -> None:
        prior_hash = sha(fixture)
        text = run(tool + [str(fixture), str(output)], label, expected=1)
        require("REJECT:" in text and expected_error in text, f"wrong rejection for {label}: {text}")
        require(existing or not os.path.lexists(output), f"guard published an output: {label}")
        require(not list(root.glob(".*.pending-*")), f"guard left staging files: {label}")
        require(sha(fixture) == prior_hash, f"guard changed its input: {label}")
        guards.append(label)

    fixtures = root / "fixtures"
    fixtures.mkdir()
    cli_offset, certificate_offset, method_name_offsets = fixture_layout(original)
    mutations = [
        ("wrong-input", len(original), b"\0", "input SHA differs"),
        ("strongname-flag", cli_offset + 16, struct.pack("<I", struct.unpack_from("<I", original, cli_offset + 16)[0] | 8), "signed or signing-reserved"),
        ("strongname-directory", cli_offset + 32, struct.pack("<II", 8192, 16), "signed or signing-reserved"),
        ("certificate-directory", certificate_offset, struct.pack("<II", len(original), 16), "signed or signing-reserved"),
    ]
    for method in METHODS:
        name = method["name"]
        il_offset = method["body_offset"] + 12
        mutations.extend([
            ("wrong-method-" + name, method_name_offsets[name], b"X", f"unexpected {name} method identity"),
            ("wrong-body-" + name, il_offset, bytes([original[il_offset] ^ 1]), f"unexpected {name} method body"),
            ("already-nooptimization-" + name, method["flags_offset"], b"\x40", f"{name} already has NoOptimization"),
        ])
    for name, offset, replacement, error in mutations:
        fixture = fixtures / (name + ".exe")
        with fixture.open("xb") as stream:
            stream.write(original)
            stream.seek(offset)
            stream.write(replacement)
        reject("reject-" + name, fixture, root / ("reject-" + name), error)
        fixture.unlink()
    invalid = fixtures / "invalid.exe"
    invalid.write_bytes(b"not a PE")
    reject("reject-invalid", invalid, root / "reject-invalid", "REJECT:")
    invalid.unlink()
    reject("reject-already-patched", root / "first/Terraria.exe", root / "reject-already-patched", "already has NoOptimization")
    reject("reject-overwrite", source, root / "first", "output already exists", existing=True)
    verify_image(original, root / "first/Terraria.exe", root / "first/manifest.json")
    empty = fixtures / "empty"
    empty.mkdir()
    reject("reject-existing-empty", source, empty, "output already exists", existing=True)
    require(not list(empty.iterdir()), "empty destination was modified")
    sentinel = fixtures / "sentinel"
    sentinel.write_bytes(b"do not overwrite")
    reject("reject-existing-file", source, sentinel, "output already exists", existing=True)
    require(sentinel.read_bytes() == b"do not overwrite", "existing file was modified")
    reject("reject-input-alias", source, source, "output aliases input", existing=True)
    reject("reject-normalized-alias", source, source.parent / "unused" / ".." / source.name, "output aliases input", existing=True)
    hardlink = fixtures / "hardlink"
    os.link(source, hardlink)
    reject("reject-hardlink-alias", source, hardlink, "output already exists", existing=True)
    require(sha(hardlink) == INPUT_SHA, "hardlink changed")
    hardlink.unlink()
    for name, target in (("file-symlink", source), ("directory-symlink", source.parent),
                         ("dangling-symlink", fixtures / "missing")):
        link = fixtures / name
        link.symlink_to(target)
        reject("reject-" + name, source, link, "output already exists", existing=True)
        require(link.is_symlink() and link.readlink() == target, "output symlink changed")
        link.unlink()
    link_parent = fixtures / "parent-symlink"
    link_parent.symlink_to(fixtures, target_is_directory=True)
    reject("reject-parent-symlink", source, link_parent / "unpublished", "parent contains a symbolic link")
    link_parent.unlink()
    reject("reject-missing-parent", source, fixtures / "missing" / "output", "parent must already exist")
    sentinel.unlink()
    empty.rmdir()
    fixtures.rmdir()
    require(sha(source) == INPUT_SHA, "original source changed")
    require(args.reference is None or sha(args.reference) == reference_sha, "immutable reference changed")
    results = {"passed": True, "inputSha256": INPUT_SHA, "outputSha256": OUTPUT_SHA,
               "outputMvid": OUTPUT_MVID, "changedBytes": 18, "byteIdentityOutsideThreeRanges": True,
               "imageAndManifestReproducible": True, "referenceUnchanged": reference_sha,
               "negativeGuards": guards, "invocations": invocations,
               "scope": "Metadata and byte-level contract only; no compiler, hardware, or performance claim"}
    (root / "runner-results.json").write_text(json.dumps(results, indent=2) + "\n")
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
