"""
Static reverse-engineering of Dalaya's eqmain.dll to find LoginController::GiveTime.
Pattern based on MQ2 source comment (macroquest-rof2-emu/src/eqlib/src/game/Globals.cpp:1660-1665):

  .text:10014B00 ?? ?? ??                   ; 3-byte preamble (this-pointer setup)
  .text:10014B03 E8 ?? ?? ?? ??             ; CALL rel32 -> ProcessKeyboardEvents
  .text:10014B08 ?? ?? ??                   ; 3 bytes between
  .text:10014B0B E9 ?? ?? ?? ??             ; JMP rel32 -> ProcessMouseEvents (tail call)

Total function size: ~16 bytes, CALL+JMP tail-call idiom. Rare enough in .text that
the number of candidates is small.
"""

import struct
import sys
from pathlib import Path

EQMAIN = Path(r"C:\Users\nate\proggy\Everquest\Eqfresh\eqmain.dll")

def main():
    if not EQMAIN.exists():
        print(f"ERROR: {EQMAIN} not found", file=sys.stderr)
        return 1

    data = EQMAIN.read_bytes()
    print(f"eqmain.dll: {len(data):,} bytes")

    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[e_lfanew:e_lfanew+4] == b"PE\x00\x00"
    machine = struct.unpack_from("<H", data, e_lfanew + 4)[0]
    print(f"Machine: 0x{machine:04X} ({'i386' if machine == 0x14C else '?'})")

    num_sections = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    opt_hdr_size = struct.unpack_from("<H", data, e_lfanew + 0x14)[0]
    image_base = struct.unpack_from("<I", data, e_lfanew + 0x18 + 0x1C)[0]
    print(f"ImageBase: 0x{image_base:08X}")
    print(f"Sections: {num_sections}")

    sections_off = e_lfanew + 0x18 + opt_hdr_size
    text = None
    for i in range(num_sections):
        so = sections_off + i * 40
        name = data[so:so+8].rstrip(b"\x00").decode("latin-1")
        vsize = struct.unpack_from("<I", data, so + 8)[0]
        va = struct.unpack_from("<I", data, so + 12)[0]
        rsize = struct.unpack_from("<I", data, so + 16)[0]
        rva = struct.unpack_from("<I", data, so + 20)[0]
        print(f"  {name:10s} VA=0x{va:08X} VSize=0x{vsize:08X} raw=0x{rva:08X} rsize=0x{rsize:X}")
        if name == ".text":
            text = (va, vsize, rva, rsize)

    if not text:
        return 1
    text_va, text_vsize, text_raw, text_rsize = text
    text_bytes = data[text_raw:text_raw + text_rsize]

    # Scan for: 3-byte-preamble + E8 rel32 + 3-bytes + E9 rel32 (the GiveTime idiom)
    hits = []
    for i in range(len(text_bytes) - 16):
        if text_bytes[i + 3] == 0xE8 and text_bytes[i + 11] == 0xE9:
            rva = text_va + i
            va = image_base + rva
            call_rel = struct.unpack_from("<i", text_bytes, i + 4)[0]
            jmp_rel = struct.unpack_from("<i", text_bytes, i + 12)[0]
            call_target = va + 3 + 5 + call_rel
            jmp_target = va + 11 + 5 + jmp_rel
            hits.append((va, rva, text_bytes[i:i+16], call_target, jmp_target))

    print(f"\nFound {len(hits)} candidate tail-call-pattern functions")

    target_va = 0x10014B00
    print(f"\nMQ2 stock ROF2 reports GiveTime at VA=0x{target_va:08X}")

    # Print the top 20 and flag matches
    for va, rva, bts, ct, jt in hits[:30]:
        flag = ""
        if va == target_va:
            flag = " <-- EXACT MQ2 MATCH"
        elif abs(va - target_va) < 0x200:
            flag = f" <-- near target (delta {va - target_va:+d})"
        print(f"  VA=0x{va:08X} RVA=0x{rva:06X} call->0x{ct:08X} jmp->0x{jt:08X}  {bts[:16].hex()}{flag}")

    # Deep-dive at exactly 0x14B00
    offset = 0x14B00
    if offset >= text_va and offset - text_va + 16 <= len(text_bytes):
        i = offset - text_va
        bts = text_bytes[i:i+16]
        print(f"\n=== Deep dive at RVA 0x{offset:06X} (MQ2 stock) ===")
        print(f"bytes: {bts.hex()}")
        b3, b11 = bts[3], bts[11]
        print(f"[+0:3] preamble   : {bts[0]:02x} {bts[1]:02x} {bts[2]:02x}")
        print(f"[+3]   opcode     : 0x{b3:02X} ({'CALL rel32 (E8) ok' if b3 == 0xE8 else 'UNEXPECTED — should be E8'})")
        if b3 == 0xE8:
            rel = struct.unpack_from("<i", bts, 4)[0]
            target = image_base + offset + 3 + 5 + rel
            print(f"[+3]   target     : CALL -> 0x{target:08X} (ProcessKeyboardEvents?)")
        print(f"[+8:3] between    : {bts[8]:02x} {bts[9]:02x} {bts[10]:02x}")
        print(f"[+11]  opcode     : 0x{b11:02X} ({'JMP rel32 (E9) ok' if b11 == 0xE9 else 'UNEXPECTED — should be E9'})")
        if b11 == 0xE9:
            rel = struct.unpack_from("<i", bts, 12)[0]
            target = image_base + offset + 11 + 5 + rel
            print(f"[+11]  target     : JMP -> 0x{target:08X} (ProcessMouseEvents?)")

    # Also scan for the JoinServer and WndProc signatures if possible.
    # Scan for import-looking names in .rdata
    print("\n=== String scan for symbolic hints ===")
    interesting = [b"LoginController", b"LoginServerAPI", b"JoinServer", b"GiveTime",
                   b"CEditWnd", b"SetText", b"CXWndManager", b"ProcessKeyboardEvents",
                   b"ProcessMouseEvents", b"LOGIN_USERNAME", b"username", b"password"]
    for needle in interesting:
        idx = data.find(needle)
        if idx != -1:
            print(f"  found '{needle.decode()}' at file off 0x{idx:08X}")

if __name__ == "__main__":
    sys.exit(main() or 0)
