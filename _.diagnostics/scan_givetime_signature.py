#!/usr/bin/env python3
"""
Scan Dalaya's eqmain.dll for MQ2's documented 32-bit LoginController::GiveTime
signature (from macroquest-rof2-emu Globals.cpp:1657-1665):

    56                 push esi
    8B F1              mov esi, ecx
    E8 ?? ?? ?? ??     call ProcessKeyboardEvents
    8B CE              mov ecx, esi
    5E                 pop esi
    E9 ?? ?? ?? ??     jmp ProcessMouseEvents (tail call)

16 bytes with 8 wildcard bytes (the two E8/E9 rel32 operands).
Writes: _.diagnostics/givetime_signature_matches.txt
"""
import struct
from pathlib import Path

DLL = Path(r"C:\Users\nate\proggy\Everquest\Eqfresh\eqmain.dll")
OUT = Path(r"X:\_Projects\EQSwitch\_.diagnostics\givetime_signature_matches.txt")

data = DLL.read_bytes()

# PE header parse (copy of logic from pe_exports_and_strings.py)
e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
coff = e_lfanew + 4
nsections = struct.unpack_from("<H", data, coff + 2)[0]
opt_size = struct.unpack_from("<H", data, coff + 16)[0]
opt_off = coff + 20
sec_off = opt_off + opt_size
sections = []
for i in range(nsections):
    off = sec_off + i * 40
    name = data[off : off + 8].rstrip(b"\0").decode("ascii", errors="replace")
    vsize, vaddr, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
    sections.append((name, vaddr, vsize, raw, rsize))

# Find .text section
text_sec = next(s for s in sections if s[0] == ".text")
_, text_va, text_vsize, text_raw, text_rsize = text_sec

# The 8 fixed bytes at their offsets:
#   [0]  = 0x56
#   [1]  = 0x8B
#   [2]  = 0xF1
#   [3]  = 0xE8
#   [8]  = 0x8B
#   [9]  = 0xCE
#   [10] = 0x5E
#   [11] = 0xE9
FIXED = {0: 0x56, 1: 0x8B, 2: 0xF1, 3: 0xE8, 8: 0x8B, 9: 0xCE, 10: 0x5E, 11: 0xE9}

matches = []
text_bytes = data[text_raw : text_raw + text_rsize]
for i in range(len(text_bytes) - 16):
    if all(text_bytes[i + o] == v for o, v in FIXED.items()):
        # Compute call/jmp targets
        call_rel = struct.unpack_from("<i", text_bytes, i + 4)[0]
        jmp_rel = struct.unpack_from("<i", text_bytes, i + 12)[0]
        # RVA of this match
        match_rva = text_va + i
        # Call target RVA: next instruction + rel32 = (rva + 8) + call_rel
        call_target = match_rva + 8 + call_rel
        # Jmp target RVA: (rva + 16) + jmp_rel
        jmp_target = match_rva + 16 + jmp_rel
        matches.append((match_rva, call_target, jmp_target, text_bytes[i : i + 16]))

out = []
out.append(f"eqmain.dll: {len(data)} bytes")
out.append(f".text section: VA=0x{text_va:X} size=0x{text_rsize:X} (file off 0x{text_raw:X})")
out.append("")
out.append(f"Signature: 56 8B F1 E8 ?? ?? ?? ?? 8B CE 5E E9 ?? ?? ?? ??")
out.append(f"  (LoginController::GiveTime per MQ2 ROF2 32-bit reference)")
out.append("")
out.append(f"Found {len(matches)} match(es) in .text:")
out.append("")
for rva, call_tgt, jmp_tgt, raw in matches:
    hex_str = " ".join(f"{b:02X}" for b in raw)
    out.append(f"  RVA 0x{rva:08X}  bytes: {hex_str}")
    out.append(f"    CALL target -> RVA 0x{call_tgt:08X}  (ProcessKeyboardEvents?)")
    out.append(f"    JMP  target -> RVA 0x{jmp_tgt:08X}  (ProcessMouseEvents?)")
    out.append("")

# Also check: if exactly one match, runtime VA at eqmain_base=0x71E30000 would be:
if len(matches) == 1:
    rva = matches[0][0]
    runtime_va = 0x71E30000 + rva
    out.append(f"** SINGLE MATCH — HIGH CONFIDENCE **")
    out.append(f"   LoginController::GiveTime RVA: 0x{rva:X}")
    out.append(f"   Runtime VA (at current eqmain base 0x71E30000): 0x{runtime_va:08X}")
    out.append(f"   ProcessKeyboardEvents RVA: 0x{matches[0][1]:X}")
    out.append(f"   ProcessMouseEvents RVA:    0x{matches[0][2]:X}")
elif len(matches) == 0:
    out.append("** NO MATCHES — Dalaya's GiveTime has a different prologue than stock ROF2 **")
    out.append("")
    out.append("Try relaxing the signature. Possibilities:")
    out.append("  * Different this-reg save/restore (e.g. ebx instead of esi)")
    out.append("  * Different calling pattern (e.g. no tail call, both function calls sequential)")
    out.append("  * Function was inlined or split in Dalaya's build")
else:
    out.append(f"** {len(matches)} MATCHES — need additional narrowing **")

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text("\n".join(out), encoding="utf-8")
print(OUT.read_text(encoding="utf-8"))
