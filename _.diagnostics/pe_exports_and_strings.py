#!/usr/bin/env python3
"""
Dump eqmain.dll exports and broader string scan for v7 offset hunting.

Writes: _.diagnostics/eqmain_exports_strings.txt

Unlike the earlier eqmain_scan.py (tail-call pattern scan), this one:
  1. Parses the PE Export Directory — lists every exported symbol + RVA.
  2. Scans .rdata AND .data AND .text for a wider set of strings than
     the previous scan, including mangled MSVC RTTI names (`.?AVC...@@`),
     debug strings ("GiveTime", "DoLoginPulse"), XML widget names, and
     method names that appear in MQ2's reference implementation.
  3. For each string found, emits its RVA (pre-relocation, for stable docs).
"""
import struct
import sys
from pathlib import Path

DLL = Path(r"C:\Users\nate\proggy\Everquest\Eqfresh\eqmain.dll")
OUT = Path(r"X:\_Projects\EQSwitch\_.diagnostics\eqmain_exports_strings.txt")

if not DLL.exists():
    print(f"ERROR: {DLL} not found", file=sys.stderr)
    sys.exit(1)

data = DLL.read_bytes()
emit_lines = []


def emit(s: str = "") -> None:
    emit_lines.append(s)


# ── PE header parse ────────────────────────────────────────────────
e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
coff = e_lfanew + 4
nsections = struct.unpack_from("<H", data, coff + 2)[0]
opt_size = struct.unpack_from("<H", data, coff + 16)[0]
opt_off = coff + 20
magic = struct.unpack_from("<H", data, opt_off)[0]
assert magic == 0x10B, f"not PE32 (magic {magic:#x})"
image_base = struct.unpack_from("<I", data, opt_off + 28)[0]
# DataDirectory[0] = Export, at offset 96 in PE32 optional header
export_rva = struct.unpack_from("<I", data, opt_off + 96)[0]
export_size = struct.unpack_from("<I", data, opt_off + 100)[0]

emit(f"eqmain.dll: {len(data)} bytes, ImageBase=0x{image_base:08X}")
emit(f"Export Directory: RVA=0x{export_rva:X} size=0x{export_size:X}")
emit("")

# ── Section table ──────────────────────────────────────────────────
sections = []  # (name, vaddr, vsize, raw_off, raw_size)
sec_off = opt_off + opt_size
for i in range(nsections):
    off = sec_off + i * 40
    name = data[off : off + 8].rstrip(b"\0").decode("ascii", errors="replace")
    vsize, vaddr, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
    sections.append((name, vaddr, vsize, raw, rsize))
    emit(f"section {name:<8} VA=0x{vaddr:08X} VSize=0x{vsize:X} raw=0x{raw:X} rsize=0x{rsize:X}")
emit("")


def rva_to_file(rva: int) -> int | None:
    for name, vaddr, vsize, raw, rsize in sections:
        if vaddr <= rva < vaddr + max(vsize, rsize):
            return raw + (rva - vaddr)
    return None


def read_cstr(off: int, max_len: int = 256) -> str:
    end = data.find(b"\0", off, off + max_len)
    if end == -1:
        end = off + max_len
    return data[off:end].decode("ascii", errors="replace")


# ── Export directory walk ──────────────────────────────────────────
if export_rva == 0:
    emit("NO EXPORTS (export RVA is zero — eqmain.dll has no export directory)")
else:
    exp_off = rva_to_file(export_rva)
    if exp_off is None:
        emit("ERROR: cannot resolve export directory RVA to file offset")
    else:
        (
            _chars, _stamp, _maj, _min, name_rva, _ord_base,
            addr_count, name_count, addr_table_rva, name_table_rva, ord_table_rva,
        ) = struct.unpack_from("<IIHHIIIIIII", data, exp_off)
        module_name = read_cstr(rva_to_file(name_rva)) if name_rva else "(no name)"
        emit(f"Export DLL name: '{module_name}'")
        emit(f"Exported functions: {addr_count}, Named exports: {name_count}")
        emit("")

        addr_tbl = rva_to_file(addr_table_rva)
        name_tbl = rva_to_file(name_table_rva)
        ord_tbl = rva_to_file(ord_table_rva)

        if name_tbl and ord_tbl and addr_tbl:
            emit("-- Named exports (all) --")
            names = []
            for i in range(name_count):
                name_ptr = struct.unpack_from("<I", data, name_tbl + i * 4)[0]
                fn_ord = struct.unpack_from("<H", data, ord_tbl + i * 2)[0]
                fn_rva = struct.unpack_from("<I", data, addr_tbl + fn_ord * 4)[0]
                nm = read_cstr(rva_to_file(name_ptr))
                names.append((nm, fn_rva, fn_ord))
            # Sort alphabetically for readability
            names.sort(key=lambda x: x[0])
            for nm, fn_rva, fn_ord in names:
                emit(f"  {fn_rva:08X} ord={fn_ord:<4} {nm}")
            emit("")

            # Highlight symbols likely relevant to v7
            keywords = ("Login", "GiveTime", "DoLoginPulse", "Server", "Character",
                        "EditWnd", "SetText", "XWnd", "Sidl", "Process")
            emit("-- v7-relevant named exports (grep) --")
            for nm, fn_rva, fn_ord in names:
                if any(kw in nm for kw in keywords):
                    emit(f"  {fn_rva:08X} ord={fn_ord:<4} {nm}")
            emit("")

emit("")
emit("=== Broader string scan ===")
# Search for these as raw ASCII anywhere in the file.
needles = [
    "GiveTime", "DoLoginPulse", "LoginController", "CLoginController",
    "pLoginController", "LoginFrontend", "ProcessKeyboardEvents",
    "ProcessMouseEvents", "CEditWnd", "SetText", "CSidlManager",
    "LOGIN_USERNAME", "LOGIN_PASSWORD", "ENTER_WORLD", "CLW_",
    "HandleLButtonUp", "HandleLButtonDown",
    ".?AVCLoginController",      # MSVC RTTI name mangling for CLoginController
    ".?AVCEditWnd",
    ".?AVCSidlManager",
    ".?AVLoginServerAPI",
    "_Z",                        # Itanium/GCC mangling (unlikely for MSVC build)
]
for nd in needles:
    nb = nd.encode("ascii")
    off = 0
    hits = []
    while True:
        idx = data.find(nb, off)
        if idx == -1:
            break
        hits.append(idx)
        off = idx + 1
        if len(hits) >= 5:
            break
    if hits:
        hit_strs = []
        for h in hits:
            # Figure out which section this file offset is in
            in_sec = None
            for name, vaddr, vsize, raw, rsize in sections:
                if raw <= h < raw + rsize:
                    in_sec = name
                    break
            # Reconstruct the surrounding short string (up to 60 chars)
            ctx = read_cstr(h, 60).replace("\n", "\\n")
            hit_strs.append(f"0x{h:X} [{in_sec or '?'}]")
        emit(f"  '{nd}' x{len(hits)}: {', '.join(hit_strs)}")
    else:
        emit(f"  '{nd}' not found")

# Also scan for any occurrence of 'LoginController' in various capitalizations
emit("")
emit("=== loose 'login' prefix strings in .rdata/.data (first 20) ===")
hit_count = 0
seen = set()
for name, vaddr, vsize, raw, rsize in sections:
    if name not in (".rdata", ".data"):
        continue
    cursor = raw
    end = raw + rsize
    while cursor < end and hit_count < 20:
        # Find ASCII strings starting with login/Login
        for prefix in (b"Login", b"login", b"L_", b"LOGIN_", b"Char"):
            idx = data.find(prefix, cursor, end)
            if idx != -1:
                s = read_cstr(idx, 50)
                if len(s) > 4 and s not in seen and all(32 <= ord(c) < 127 for c in s[:10]):
                    seen.add(s)
                    emit(f"  [{name}] 0x{idx:X}: '{s}'")
                    hit_count += 1
                cursor = max(cursor, idx + 1)
            else:
                cursor = end
                break

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text("\n".join(emit_lines), encoding="utf-8")
print(f"wrote {OUT} ({len(emit_lines)} lines, {sum(len(l) for l in emit_lines)} bytes)")
