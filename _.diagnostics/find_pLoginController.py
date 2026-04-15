#!/usr/bin/env python3
"""
Find pLoginController: the static pointer that callers of LoginController::GiveTime
dereference before the call to load `this` into ECX.

Approach:
  1. Scan .text for direct calls to GiveTime: `E8 <rel32 to RVA 0x128B0>`.
  2. For each hit, disassemble the ~16 preceding bytes looking for
     `8B 0D <imm32>`  (mov ecx, [imm32])  OR
     `A1 <imm32>`     (mov eax, [imm32])  followed by `8B C8` (mov ecx, eax) OR
     `FF 35 <imm32>`  (push [imm32])  then pattern through the call.
  3. The imm32 is the address of pLoginController (a static pointer in .data).
  4. Cross-check: all call sites should use the same pLoginController address.

Writes: _.diagnostics/pLoginController.txt
"""
import struct
from pathlib import Path

DLL = Path(r"C:\Users\nate\proggy\Everquest\Eqfresh\eqmain.dll")
OUT = Path(r"X:\_Projects\EQSwitch\_.diagnostics\pLoginController.txt")

GIVETIME_RVA = 0x128B0

data = DLL.read_bytes()
e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
coff = e_lfanew + 4
nsections = struct.unpack_from("<H", data, coff + 2)[0]
opt_size = struct.unpack_from("<H", data, coff + 16)[0]
opt_off = coff + 20
sec_off = opt_off + opt_size
sections = []
for i in range(nsections):
    off = sec_off + i * 40
    nm = data[off : off + 8].rstrip(b"\0").decode("ascii", errors="replace")
    vsize, vaddr, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
    sections.append((nm, vaddr, vsize, raw, rsize))
text = next(s for s in sections if s[0] == ".text")
_, text_va, _, text_raw, text_rsize = text
image_base = struct.unpack_from("<I", data, opt_off + 28)[0]


def rva_to_file(rva: int) -> int | None:
    for nm, va, vsize, raw, rsize in sections:
        if va <= rva < va + max(vsize, rsize):
            return raw + (rva - va)
    return None


def rva_section(rva: int) -> str:
    for nm, va, vsize, raw, rsize in sections:
        if va <= rva < va + max(vsize, rsize):
            return nm
    return "?"


out = []
out.append(f"Searching for callers of LoginController::GiveTime @ RVA 0x{GIVETIME_RVA:X}")
out.append(f"eqmain.dll ImageBase 0x{image_base:08X}")
out.append("")

# Step 1: find all direct CALL sites (E8 rel32)
text_bytes = data[text_raw : text_raw + text_rsize]
call_sites = []
for i in range(len(text_bytes) - 5):
    if text_bytes[i] == 0xE8:
        rel = struct.unpack_from("<i", text_bytes, i + 1)[0]
        # call target RVA = (rva of next instruction) + rel = (text_va + i + 5) + rel
        target_rva = text_va + i + 5 + rel
        if target_rva == GIVETIME_RVA:
            call_sites.append(text_va + i)  # RVA of the call

out.append(f"Direct CALL sites to GiveTime: {len(call_sites)}")
for cs in call_sites:
    out.append(f"  RVA 0x{cs:08X}  (VA 0x{image_base + cs:08X} at default base)")
out.append("")

# Step 2: for each call site, dump 24 bytes of preceding context
# Also look specifically for 8B 0D <imm32> (mov ecx,[imm32]) and 8B 4D XX (mov ecx,[ebp+disp])
# that immediately precedes the call.
out.append("=== Preceding context (24 bytes before each call) ===")
for cs in call_sites:
    file_off = rva_to_file(cs)
    if file_off is None:
        continue
    ctx_start = file_off - 24
    ctx = data[ctx_start:file_off]
    hex_str = " ".join(f"{b:02X}" for b in ctx)
    out.append(f"RVA 0x{cs:08X}: ... {hex_str}  <CALL>")
    # Heuristic: look backward for 8B 0D XX XX XX XX (mov ecx,[imm32])
    # within last 16 bytes
    for look in range(len(ctx) - 6, -1, -1):
        if ctx[look] == 0x8B and ctx[look + 1] == 0x0D:
            addr = struct.unpack_from("<I", ctx, look + 2)[0]
            addr_rva = addr - image_base  # convert VA to RVA (preferred VA)
            out.append(f"    mov ecx,[0x{addr:08X}]  -> pLoginController candidate")
            out.append(f"    that's RVA 0x{addr_rva:X} in {rva_section(addr_rva)} section")
            break
    # Also look for A1 XX XX XX XX (mov eax,[imm32]) then 8B C8 (mov ecx,eax)
    for look in range(len(ctx) - 7, -1, -1):
        if ctx[look] == 0xA1 and ctx[look + 5] == 0x8B and ctx[look + 6] == 0xC8:
            addr = struct.unpack_from("<I", ctx, look + 1)[0]
            addr_rva = addr - image_base
            out.append(f"    mov eax,[0x{addr:08X}]; mov ecx,eax  -> pLoginController candidate")
            out.append(f"    that's RVA 0x{addr_rva:X} in {rva_section(addr_rva)} section")
            break
out.append("")

# Step 3: if any pattern consistently yielded the same address, that's pLoginController.
# Otherwise callers may be pushing `this` obtained from elsewhere (member access, param).
out.append("=== Summary ===")
if not call_sites:
    out.append("NO direct CALL sites found — GiveTime may only be called via indirect/vtable.")
    out.append("Look for the vtable that contains GiveTime: scan for a 4-byte value equal to")
    out.append(f"GiveTime's VA (0x{image_base + GIVETIME_RVA:08X} at default base) in .rdata.")
    out.append("")
    # Do the vtable scan: find the VA as a 4-byte value in .rdata
    gt_va = image_base + GIVETIME_RVA
    pattern = struct.pack("<I", gt_va)
    for nm, va, vsize, raw, rsize in sections:
        if nm in (".rdata", ".data"):
            sec_bytes = data[raw : raw + rsize]
            pos = 0
            hits = []
            while True:
                idx = sec_bytes.find(pattern, pos)
                if idx == -1:
                    break
                hits.append(raw + idx)
                pos = idx + 1
                if len(hits) >= 5:
                    break
            for h in hits:
                rva_in_sec = va + (h - raw)
                out.append(f"  pointer to GiveTime found in {nm} at file 0x{h:X} (RVA 0x{rva_in_sec:X})")
                # Disassemble context: -4 to +12 to see what vtable this sits in
                ctx = data[h - 16 : h + 20]
                out.append(f"    context bytes (±16): {' '.join(f'{b:02X}' for b in ctx)}")

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text("\n".join(out), encoding="utf-8")
print(OUT.read_text(encoding="utf-8"))
