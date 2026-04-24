"""IDA Free 9.3 batch script — independent cross-validation of rizin findings.

Confirms:
  1. CEditWnd vtable VA = 0x1010be6c
  2. Slot 73 (offset 0x124) function = 0x10097af0
  3. Hex-Rays decompile of 0x10097af0 — InputText offset == 0x1ec, two redraw calls present
"""
import idaapi, idautils, idc, ida_bytes, ida_funcs, ida_hexrays, ida_typeinf, ida_name

OUT = r"X:/_Projects/eqswitch/Native/recon/ida-witness.txt"
log_lines = []

def log(s):
    log_lines.append(s)
    print(s)

# Wait for auto-analysis
idaapi.auto_wait()

log("=" * 60)
log("IDA Free 9.3 cross-validation — eqmain.dll (Dalaya x86)")
log("=" * 60)
log(f"IDA version: {idaapi.get_kernel_version()}")
log(f"Image base : 0x{idaapi.get_imagebase():08x}")
log("")

# === Witness check 1: CEditWnd vtable at 0x1010be6c ===
VT_CEW = 0x1010be6c
log(f"--- Vtable @ 0x{VT_CEW:08x} (CEditWnd, expected per rizin) ---")

# Read first 96 dwords (vtable slots)
slots = []
for i in range(96):
    addr = VT_CEW + i * 4
    val = ida_bytes.get_dword(addr)
    slots.append((i, addr, val))

# Slot 73 should be SetWindowText
SLOT_73_EXPECTED = 0x10097af0
slot_73 = slots[73]
log(f"Slot 73 (offset 0x124): 0x{slot_73[2]:08x}  EXPECTED: 0x{SLOT_73_EXPECTED:08x}  "
    f"{'MATCH' if slot_73[2] == SLOT_73_EXPECTED else 'MISMATCH'}")

# Slot 74 should be DIFFERENT (rizin shows 0x10062a80 — non-override, neighbor function)
log(f"Slot 74 (offset 0x128): 0x{slots[74][2]:08x}  (this was the WRONG slot used 2026-04-16)")

# === Witness check 2: function at 0x10097af0 — find references ===
log("")
log(f"--- Function at 0x{SLOT_73_EXPECTED:08x} ---")
fn = ida_funcs.get_func(SLOT_73_EXPECTED)
if fn:
    log(f"Function bounds: 0x{fn.start_ea:08x} – 0x{fn.end_ea:08x}  ({fn.end_ea - fn.start_ea} bytes)")
    name = ida_name.get_ea_name(fn.start_ea)
    log(f"IDA name: {name}")
else:
    log("WARN: IDA didn't recognize 0x10097af0 as function start — forcing it.")
    ida_funcs.add_func(SLOT_73_EXPECTED)
    idaapi.auto_wait()
    fn = ida_funcs.get_func(SLOT_73_EXPECTED)
    if fn:
        log(f"Function bounds: 0x{fn.start_ea:08x} – 0x{fn.end_ea:08x}")

# === Witness check 3: scan for the key memory accesses (InputText offset 0x1ec, redraw calls) ===
log("")
log("--- Key instruction scan within SetWindowText body ---")

if fn:
    found_inputtext_lea = False
    found_redraw_calls = []
    found_startpos_write = False
    found_endpos_write = False
    text_field_offsets = set()

    ea = fn.start_ea
    while ea < fn.end_ea:
        mnem = idc.print_insn_mnem(ea)
        ops = idc.GetDisasm(ea)

        # Look for `lea ecx, [esi+0x1ec]` or similar — the InputText address
        if "lea" in mnem and "0x1ec" in ops.lower():
            log(f"  0x{ea:08x}: {ops}    <== InputText addr-of (matches rizin offset 0x1ec)")
            found_inputtext_lea = True

        # Direct displacement reads/writes
        if "[esi+1ECh]" in ops or "[esi+0x1ec]" in ops.lower():
            text_field_offsets.add(0x1ec)
        if "[esi+1E0h]" in ops or "[esi+0x1e0]" in ops.lower():
            if "mov" in mnem and ops.lower().split(",")[0].strip().endswith("0x1e0]"):
                found_endpos_write = True
                log(f"  0x{ea:08x}: {ops}    <== EndPos write at 0x1e0")
        if "[esi+1DCh]" in ops or "[esi+0x1dc]" in ops.lower():
            if "mov" in mnem and ops.lower().split(",")[0].strip().endswith("0x1dc]"):
                found_startpos_write = True
                log(f"  0x{ea:08x}: {ops}    <== StartPos write at 0x1dc")

        # Calls — capture target addr to find the two redraw functions
        if mnem == "call":
            target = idc.get_operand_value(ea, 0)
            if 0x10000000 <= target < 0x10200000:
                found_redraw_calls.append((ea, target))

        ea = idc.next_head(ea, fn.end_ea)

    log(f"\n  text_field_offsets seen with [esi+...]: {sorted(hex(o) for o in text_field_offsets)}")
    log(f"  found_inputtext_lea (0x1ec): {found_inputtext_lea}")
    log(f"  found_startpos_write (0x1dc): {found_startpos_write}")
    log(f"  found_endpos_write (0x1e0): {found_endpos_write}")
    log(f"\n  All calls in function body:")
    for ea, target in found_redraw_calls:
        tname = ida_name.get_ea_name(target) or "?"
        log(f"    0x{ea:08x}  call  0x{target:08x}  ({tname})")

# === Witness check 4: Hex-Rays decompile ===
log("")
log("--- Hex-Rays decompile (independent witness #2 within IDA itself) ---")
if not ida_hexrays.init_hexrays_plugin():
    log("WARN: Hex-Rays not available (free tier should support x86)")
else:
    try:
        cfunc = ida_hexrays.decompile(SLOT_73_EXPECTED)
        if cfunc:
            text = str(cfunc)
            # Save full decompile
            with open(r"X:/_Projects/eqswitch/Native/recon/SetWindowText.c", "w", encoding="utf-8") as f:
                f.write(text)
            # Print a trimmed preview
            log("Decompile saved to SetWindowText.c")
            log("Preview (first 60 lines):")
            for i, line in enumerate(text.splitlines()[:60]):
                log(f"  {line}")
    except Exception as e:
        log(f"Decompile failed: {e}")

# === Save log ===
with open(OUT, "w", encoding="utf-8") as f:
    f.write("\n".join(log_lines))
log(f"\nWritten: {OUT}")

# Tell IDA to exit cleanly without saving the IDB (we don't need it persisted)
idc.qexit(0)
