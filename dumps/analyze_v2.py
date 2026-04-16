"""
EQSwitch v7 — Widget Discovery v2: Heap-focused scan

v1 found string constants inside DLL code/data sections. The REAL CXWnd objects
live on the heap. This script:

1. Enumerates modules properly (CreateToolhelp32Snapshot)
2. Finds all occurrences of target strings (DLL .rdata + heap copies)
3. Scans ONLY heap (MEM_PRIVATE) memory for DWORDs pointing to ANY string occurrence
4. For each heap pointer ref, walks backwards looking for a valid vtable
5. Also does a vtable-first scan: finds all heap objects with vtables in eqmain.dll range

Usage: python analyze_v2.py --pid PID
"""

import ctypes
import ctypes.wintypes as wt
import struct
import sys
import os
import argparse
from collections import defaultdict

# ─── Win32 constants ───────────────────────────────────────────

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
MEM_IMAGE = 0x01000000
MEM_PRIVATE = 0x00020000
MEM_MAPPED = 0x00040000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100
TH32CS_SNAPMODULE = 0x00000008
TH32CS_SNAPMODULE32 = 0x00000010

kernel32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

# ─── Structures ────────────────────────────────────────────────

class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wt.DWORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", wt.DWORD),
        ("Protect", wt.DWORD),
        ("Type", wt.DWORD),
    ]

class MODULEENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wt.DWORD),
        ("th32ModuleID", wt.DWORD),
        ("th32ProcessID", wt.DWORD),
        ("GlbcntUsage", wt.DWORD),
        ("ProccntUsage", wt.DWORD),
        ("modBaseAddr", ctypes.POINTER(wt.BYTE)),
        ("modBaseSize", wt.DWORD),
        ("hModule", wt.HMODULE),
        ("szModule", ctypes.c_char * 256),
        ("szExePath", ctypes.c_char * 260),
    ]

# ─── Helpers ───────────────────────────────────────────────────

def open_process(pid):
    handle = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        raise OSError(f"OpenProcess failed for PID {pid} (error {ctypes.GetLastError()})")
    return handle

def read_mem(handle, addr, size):
    buf = ctypes.create_string_buffer(size)
    bytes_read = ctypes.c_size_t(0)
    ok = kernel32.ReadProcessMemory(handle, ctypes.c_void_p(addr), buf, size, ctypes.byref(bytes_read))
    if not ok or bytes_read.value == 0:
        return None
    return buf.raw[:bytes_read.value]

def read_dword(handle, addr):
    data = read_mem(handle, addr, 4)
    if data and len(data) == 4:
        return struct.unpack("<I", data)[0]
    return None

def read_string(handle, addr, max_len=256):
    data = read_mem(handle, addr, max_len)
    if not data:
        return None
    try:
        end = data.index(b'\x00')
        return data[:end].decode('ascii', errors='replace')
    except ValueError:
        return data.decode('ascii', errors='replace')

def enum_regions(handle):
    addr = 0x10000
    mbi = MEMORY_BASIC_INFORMATION()
    regions = []
    while addr < 0x7FFF0000:
        ret = kernel32.VirtualQueryEx(handle, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi))
        if ret == 0:
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize or 0
        if size == 0:
            break
        if (mbi.State == MEM_COMMIT and
            not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) and
            mbi.Protect & 0xFE):
            regions.append((base, size, mbi.Protect, mbi.Type))
        addr = base + size
        if addr <= base:
            addr = base + 0x1000
    return regions

def enum_modules(pid):
    """Enumerate loaded modules using CreateToolhelp32Snapshot."""
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    if snap == -1 or snap == 0xFFFFFFFF:
        print(f"  WARNING: CreateToolhelp32Snapshot failed (error {ctypes.GetLastError()})")
        return []

    modules = []
    me = MODULEENTRY32()
    me.dwSize = ctypes.sizeof(MODULEENTRY32)

    if kernel32.Module32First(snap, ctypes.byref(me)):
        while True:
            base = ctypes.cast(me.modBaseAddr, ctypes.c_void_p).value or 0
            modules.append({
                "name": me.szModule.decode('ascii', errors='replace').lower(),
                "base": base,
                "size": me.modBaseSize,
                "path": me.szExePath.decode('ascii', errors='replace'),
            })
            if not kernel32.Module32Next(snap, ctypes.byref(me)):
                break

    kernel32.CloseHandle(snap)
    return modules

# ─── Target strings ───────────────────────────────────────────

TARGET_NAMES = [
    "LOGIN_UsernameEdit",
    "LOGIN_PasswordEdit",
    "LOGIN_ConnectButton",
    "LOGIN_EQNewsButton",
    "LOGIN_DeleteButton",
    "OK_Display",
    "OK_OKButton",
    "YESNO_YesButton",
    "Character_List",
    "CLW_EnterWorldButton",
    "EQLS_ServerList",
    "SERVERSELECT_SelectButton",
]

def main():
    parser = argparse.ArgumentParser(description="EQSwitch v7 Widget Discovery v2")
    parser.add_argument("--pid", type=int, required=True)
    args = parser.parse_args()

    handle = open_process(args.pid)
    print(f"Process opened: PID {args.pid}")

    # ── Step 1: Enumerate modules ──────────────────────────────

    print("\n=== MODULES ===")
    modules = enum_modules(args.pid)
    eqmain = None
    dinput8 = None
    eqgame = None

    for m in sorted(modules, key=lambda x: x["base"]):
        tag = ""
        if "eqmain" in m["name"]:
            eqmain = m
            tag = " *** EQMAIN ***"
        elif "dinput8" in m["name"]:
            dinput8 = m
            tag = " *** DINPUT8 (MQ2) ***"
        elif "eqgame" in m["name"]:
            eqgame = m
            tag = " *** EQGAME ***"
        print(f"  0x{m['base']:08X} - 0x{m['base']+m['size']:08X} ({m['size']/1024:7.0f} KB) {m['name']}{tag}")

    if eqmain:
        print(f"\n  eqmain.dll: base=0x{eqmain['base']:08X}, size=0x{eqmain['size']:X} ({eqmain['size']/1024:.0f} KB)")
        print(f"  eqmain vtable range: 0x{eqmain['base']:08X} - 0x{eqmain['base']+eqmain['size']:08X}")
    else:
        print("\n  WARNING: eqmain.dll NOT FOUND — may have already unloaded (charselect?)")

    if dinput8:
        print(f"  dinput8.dll: base=0x{dinput8['base']:08X}, size=0x{dinput8['size']:X} ({dinput8['size']/1024:.0f} KB)")

    # ── Step 2: Enumerate memory regions ───────────────────────

    regions = enum_regions(handle)
    heap_regions = [(b, s, p, t) for b, s, p, t in regions if t in (MEM_PRIVATE, MEM_MAPPED)]
    image_regions = [(b, s, p, t) for b, s, p, t in regions if t == MEM_IMAGE]

    print(f"\n=== MEMORY MAP ===")
    print(f"  Total: {len(regions)} regions, {sum(s for _,s,_,_ in regions)/(1024*1024):.0f} MB")
    print(f"  Heap/Private: {len(heap_regions)} regions, {sum(s for _,s,_,_ in heap_regions)/(1024*1024):.0f} MB")
    print(f"  Image (DLL/EXE): {len(image_regions)} regions, {sum(s for _,s,_,_ in image_regions)/(1024*1024):.0f} MB")

    # ── Step 3: Find ALL occurrences of target strings ─────────

    print(f"\n=== STRING SCAN (all memory) ===")
    all_string_addrs = defaultdict(list)  # name -> [addr, ...]
    CHUNK = 0x100000

    for name in TARGET_NAMES:
        target = name.encode('ascii') + b'\x00'
        for base, size, prot, rtype in regions:
            for offset in range(0, size, CHUNK):
                chunk_size = min(CHUNK + 256, size - offset)
                if chunk_size <= 0:
                    continue
                data = read_mem(handle, base + offset, chunk_size)
                if not data:
                    continue
                pos = 0
                while True:
                    pos = data.find(target, pos)
                    if pos == -1:
                        break
                    abs_addr = base + offset + pos
                    region_type = "HEAP" if rtype in (MEM_PRIVATE, MEM_MAPPED) else "IMAGE"
                    all_string_addrs[name].append((abs_addr, region_type))
                    pos += 1

    for name, locs in sorted(all_string_addrs.items()):
        heap_locs = [a for a, t in locs if t == "HEAP"]
        image_locs = [a for a, t in locs if t == "IMAGE"]
        print(f"  '{name}': {len(locs)} total ({len(heap_locs)} heap, {len(image_locs)} image)")
        for addr, rtype in locs[:8]:
            # Identify which module (if image)
            mod_name = ""
            if rtype == "IMAGE":
                for m in modules:
                    if m["base"] <= addr < m["base"] + m["size"]:
                        mod_name = f" [{m['name']}]"
                        break
            print(f"    0x{addr:08X} ({rtype}){mod_name}")
        if len(locs) > 8:
            print(f"    ... and {len(locs)-8} more")

    # Collect ALL string addresses (both heap and image)
    all_str_addr_set = set()
    str_addr_to_name = {}
    for name, locs in all_string_addrs.items():
        for addr, rtype in locs:
            all_str_addr_set.add(addr)
            str_addr_to_name[addr] = name

    # ── Step 4: Scan HEAP memory for DWORDs pointing to strings ──

    print(f"\n=== HEAP POINTER SCAN ===")
    print(f"  Looking for {len(all_str_addr_set)} string addresses in {len(heap_regions)} heap regions...")

    # Build lookup table for fast matching
    target_bytes = {}
    for addr in all_str_addr_set:
        target_bytes[struct.pack("<I", addr)] = addr

    heap_refs = defaultdict(list)  # string_addr -> [heap_ref_addr, ...]
    scanned = 0

    for base, size, prot, rtype in heap_regions:
        for offset in range(0, size, CHUNK):
            chunk_size = min(CHUNK, size - offset)
            if chunk_size < 4:
                continue
            data = read_mem(handle, base + offset, chunk_size)
            if not data or len(data) < 4:
                continue
            scanned += len(data)

            for i in range(0, len(data) - 3, 4):
                dword = data[i:i+4]
                if dword in target_bytes:
                    ref_addr = base + offset + i
                    str_addr = target_bytes[dword]
                    heap_refs[str_addr].append(ref_addr)

    print(f"  Scanned {scanned/(1024*1024):.1f} MB of heap memory")
    total_refs = sum(len(v) for v in heap_refs.values())
    print(f"  Found {total_refs} heap pointer references to string addresses")

    # Group refs by widget name
    refs_by_name = defaultdict(list)
    for str_addr, ref_addrs in heap_refs.items():
        name = str_addr_to_name.get(str_addr, "???")
        for ref_addr in ref_addrs:
            refs_by_name[name].append((ref_addr, str_addr))

    for name in sorted(refs_by_name.keys()):
        refs = refs_by_name[name]
        print(f"\n  '{name}': {len(refs)} heap refs")
        for ref_addr, str_addr in refs[:10]:
            # Read surrounding context
            context = read_mem(handle, ref_addr - 16, 48)
            context_hex = ""
            if context:
                context_hex = " ".join(f"{b:02X}" for b in context)
            print(f"    heap ref @ 0x{ref_addr:08X} -> string @ 0x{str_addr:08X}  ctx: {context_hex}")

    # ── Step 5: For each heap ref, walk backwards to find vtable ──

    print(f"\n=== CXWND CANDIDATE ANALYSIS ===")

    # Valid vtable ranges (code inside loaded DLLs)
    vtable_ranges = []
    if eqmain:
        vtable_ranges.append((eqmain["base"], eqmain["base"] + eqmain["size"], "eqmain"))
    if dinput8:
        vtable_ranges.append((dinput8["base"], dinput8["base"] + dinput8["size"], "dinput8"))
    if eqgame:
        vtable_ranges.append((eqgame["base"], eqgame["base"] + eqgame["size"], "eqgame"))
    # Also add generic DLL range as fallback
    vtable_ranges.append((0x60000000, 0x80000000, "generic_dll"))

    def is_valid_vtable(handle, vt_addr):
        """Check if vt_addr looks like a real vtable (array of code pointers)."""
        data = read_mem(handle, vt_addr, 32)  # 8 entries
        if not data or len(data) < 32:
            return False, 0
        valid = 0
        for i in range(8):
            entry = struct.unpack("<I", data[i*4:i*4+4])[0]
            for lo, hi, _ in vtable_ranges:
                if lo <= entry < hi:
                    valid += 1
                    break
        return valid >= 4, valid

    candidates = defaultdict(list)  # name -> [candidate_info, ...]

    for name, refs in refs_by_name.items():
        for ref_addr, str_addr in refs:
            # Walk backwards up to 0x400 bytes looking for a vtable
            MAX_WALK = 0x400
            walk_data = read_mem(handle, ref_addr - MAX_WALK, MAX_WALK)
            if not walk_data or len(walk_data) < MAX_WALK:
                continue

            best = None
            for back_off in range(4, MAX_WALK, 4):
                candidate_base = ref_addr - back_off
                idx = MAX_WALK - back_off
                if idx < 0 or idx + 4 > len(walk_data):
                    continue
                vt_ptr = struct.unpack("<I", walk_data[idx:idx+4])[0]

                # Check if this looks like a vtable pointer
                in_range = False
                vt_module = ""
                for lo, hi, mname in vtable_ranges:
                    if lo <= vt_ptr < hi:
                        in_range = True
                        vt_module = mname
                        break

                if not in_range:
                    continue

                ok, vt_valid = is_valid_vtable(handle, vt_ptr)
                if not ok:
                    continue

                sidl_offset = ref_addr - candidate_base

                # Prefer: closest vtable with most valid entries, in eqmain range
                score = vt_valid
                if vt_module == "eqmain":
                    score += 10  # prefer eqmain vtables
                if vt_module == "dinput8":
                    score += 5

                if best is None or score > best["score"]:
                    best = {
                        "cxwnd_base": candidate_base,
                        "vtable": vt_ptr,
                        "vt_valid": vt_valid,
                        "vt_module": vt_module,
                        "sidl_offset": sidl_offset,
                        "str_addr": str_addr,
                        "ref_addr": ref_addr,
                        "score": score,
                    }

            if best:
                candidates[name].append(best)

    # Print candidates
    for name in sorted(candidates.keys()):
        cands = candidates[name]
        print(f"\n  '{name}': {len(cands)} candidate(s)")
        for c in cands:
            print(f"    CXWnd @ 0x{c['cxwnd_base']:08X}")
            print(f"      vtable = 0x{c['vtable']:08X} ({c['vt_valid']}/8 valid) [{c['vt_module']}]")
            print(f"      SIDL name ptr at offset +0x{c['sidl_offset']:X}")
            print(f"      string @ 0x{c['str_addr']:08X}")

    # ── Step 6: Cross-validate SIDL offsets ────────────────────

    print(f"\n=== CROSS-VALIDATION ===")
    offset_votes = defaultdict(list)
    for name, cands in candidates.items():
        for c in cands:
            offset_votes[c["sidl_offset"]].append((name, c))

    if offset_votes:
        for offset, entries in sorted(offset_votes.items()):
            names = [e[0] for e in entries]
            modules = set(e[1]["vt_module"] for e in entries)
            print(f"  +0x{offset:X}: {len(entries)} widgets ({', '.join(names)}) modules={modules}")

        best_offset = max(offset_votes.keys(), key=lambda k: len(offset_votes[k]))
        print(f"\n  BEST SIDL offset: +0x{best_offset:X} ({len(offset_votes[best_offset])} widgets)")
    else:
        print("  No candidates found via string→heap→vtable path")
        best_offset = None

    # ── Step 7: Vtable-first scan (eqmain range) ──────────────

    if eqmain:
        print(f"\n=== VTABLE-FIRST SCAN (eqmain objects on heap) ===")
        eqm_lo = eqmain["base"]
        eqm_hi = eqmain["base"] + eqmain["size"]
        print(f"  Scanning heap for objects with vtable in 0x{eqm_lo:08X}-0x{eqm_hi:08X}...")

        eqmain_objects = []
        for base, size, prot, rtype in heap_regions:
            for offset in range(0, size, CHUNK):
                chunk_size = min(CHUNK, size - offset)
                if chunk_size < 4:
                    continue
                data = read_mem(handle, base + offset, chunk_size)
                if not data or len(data) < 4:
                    continue
                for i in range(0, len(data) - 3, 4):
                    val = struct.unpack("<I", data[i:i+4])[0]
                    if eqm_lo <= val < eqm_hi:
                        obj_addr = base + offset + i
                        # Quick vtable validation (just check first 2 entries)
                        vt_data = read_mem(handle, val, 8)
                        if vt_data and len(vt_data) == 8:
                            e0, e1 = struct.unpack("<II", vt_data)
                            if (eqm_lo <= e0 < eqm_hi) and (eqm_lo <= e1 < eqm_hi):
                                eqmain_objects.append((obj_addr, val))

        print(f"  Found {len(eqmain_objects)} heap objects with eqmain vtables")

        if len(eqmain_objects) > 0 and len(eqmain_objects) < 5000:
            # For each object, scan its body for pointers to our target strings
            print(f"  Scanning object bodies for SIDL name pointers...")
            SCAN_SIZE = 0x400  # scan first 1KB of each object

            vtable_first_candidates = defaultdict(list)
            for obj_addr, vt_addr in eqmain_objects:
                obj_data = read_mem(handle, obj_addr, SCAN_SIZE)
                if not obj_data:
                    continue
                for off in range(0, len(obj_data) - 3, 4):
                    ptr_val = struct.unpack("<I", obj_data[off:off+4])[0]
                    if ptr_val in all_str_addr_set:
                        name = str_addr_to_name[ptr_val]
                        vtable_first_candidates[name].append({
                            "cxwnd_base": obj_addr,
                            "vtable": vt_addr,
                            "sidl_offset": off,
                            "str_addr": ptr_val,
                        })

            if vtable_first_candidates:
                print(f"\n  VTABLE-FIRST RESULTS:")
                vt_offset_votes = defaultdict(list)
                for name, cands in sorted(vtable_first_candidates.items()):
                    print(f"    '{name}': {len(cands)} hit(s)")
                    for c in cands:
                        ok, vv = is_valid_vtable(handle, c["vtable"])
                        print(f"      CXWnd @ 0x{c['cxwnd_base']:08X} vt=0x{c['vtable']:08X} ({vv}/8) sidl=+0x{c['sidl_offset']:X}")
                        vt_offset_votes[c["sidl_offset"]].append(name)

                print(f"\n  VTABLE-FIRST offset distribution:")
                for off, names in sorted(vt_offset_votes.items()):
                    print(f"    +0x{off:X}: {len(names)} widgets ({', '.join(names)})")
            else:
                print("  No eqmain objects contain pointers to our target strings")
        elif len(eqmain_objects) >= 5000:
            print(f"  Too many candidates ({len(eqmain_objects)}), narrowing with vtable uniqueness...")
            # Count vtable occurrences — real CXWnd subclasses have unique vtables
            vt_counts = defaultdict(list)
            for obj_addr, vt_addr in eqmain_objects:
                vt_counts[vt_addr].append(obj_addr)
            print(f"  Unique vtables: {len(vt_counts)}")
            # Show distribution
            for vt, objs in sorted(vt_counts.items(), key=lambda x: -len(x[1]))[:20]:
                ok, vv = is_valid_vtable(handle, vt)
                print(f"    vt=0x{vt:08X} ({vv}/8 valid): {len(objs)} instances")
                # For the first instance of each vtable, check for string pointers
                for obj_addr in objs[:2]:
                    obj_data = read_mem(handle, obj_addr, 0x400)
                    if not obj_data:
                        continue
                    for off in range(0, len(obj_data) - 3, 4):
                        ptr_val = struct.unpack("<I", obj_data[off:off+4])[0]
                        if ptr_val in all_str_addr_set:
                            name = str_addr_to_name[ptr_val]
                            print(f"      ★ obj@0x{obj_addr:08X} +0x{off:X} -> '{name}' @ 0x{ptr_val:08X}")

    # ── Step 8: Also scan dinput8 vtable objects ───────────────

    if dinput8 and (not candidates or not any(candidates.values())):
        print(f"\n=== VTABLE-FIRST SCAN (dinput8 objects on heap) ===")
        di_lo = dinput8["base"]
        di_hi = dinput8["base"] + dinput8["size"]
        print(f"  Scanning heap for objects with vtable in 0x{di_lo:08X}-0x{di_hi:08X}...")

        di_objects = []
        for base, size, prot, rtype in heap_regions:
            for offset in range(0, size, CHUNK):
                chunk_size = min(CHUNK, size - offset)
                if chunk_size < 4:
                    continue
                data = read_mem(handle, base + offset, chunk_size)
                if not data or len(data) < 4:
                    continue
                for i in range(0, len(data) - 3, 4):
                    val = struct.unpack("<I", data[i:i+4])[0]
                    if di_lo <= val < di_hi:
                        obj_addr = base + offset + i
                        vt_data = read_mem(handle, val, 8)
                        if vt_data and len(vt_data) == 8:
                            e0, e1 = struct.unpack("<II", vt_data)
                            if (di_lo <= e0 < di_hi) or (eqmain and eqmain["base"] <= e0 < eqmain["base"] + eqmain["size"]):
                                di_objects.append((obj_addr, val))

        print(f"  Found {len(di_objects)} heap objects with dinput8 vtables")
        if 0 < len(di_objects) < 5000:
            for obj_addr, vt_addr in di_objects:
                obj_data = read_mem(handle, obj_addr, 0x400)
                if not obj_data:
                    continue
                for off in range(0, len(obj_data) - 3, 4):
                    ptr_val = struct.unpack("<I", obj_data[off:off+4])[0]
                    if ptr_val in all_str_addr_set:
                        name = str_addr_to_name[ptr_val]
                        ok, vv = is_valid_vtable(handle, vt_addr)
                        print(f"    ★ obj@0x{obj_addr:08X} vt=0x{vt_addr:08X} ({vv}/8) +0x{off:X} -> '{name}'")

    # ── Step 9: Dump best candidates ──────────────────────────

    # Collect all candidates from both methods
    all_final = {}
    for name, cands in candidates.items():
        all_final[name] = cands

    if all_final:
        print(f"\n=== STRUCT DUMP (best candidates) ===")
        for name, cands in sorted(all_final.items()):
            c = cands[0]  # best candidate
            base = c["cxwnd_base"]
            print(f"\n  --- {name} @ 0x{base:08X} (vt=0x{c['vtable']:08X}) ---")
            data = read_mem(handle, base, 0x300)
            if not data:
                print("    (unreadable)")
                continue
            for off in range(0, min(len(data), 0x300), 4):
                if off + 4 > len(data):
                    break
                val = struct.unpack("<I", data[off:off+4])[0]
                ann = ""
                if off == 0:
                    ann = " <-- vtable"
                elif off == c["sidl_offset"]:
                    ann = f" <-- SIDL name ptr -> '{name}'"
                elif val != 0:
                    if val in all_str_addr_set:
                        ann = f" -> '{str_addr_to_name[val]}'"
                    elif 0x10000 < val < 0x7FFFFFFF:
                        s = read_string(handle, val, 64)
                        if s and len(s) >= 2 and all(32 <= ord(ch) < 127 for ch in s):
                            ann = f' -> "{s[:40]}"'

                if ann or off <= 0x10 or val != 0:
                    print(f"    +0x{off:04X}: 0x{val:08X}{ann}")

    # ── Summary ────────────────────────────────────────────────

    print(f"\n{'='*70}")
    print("SUMMARY")
    print(f"{'='*70}")
    if eqmain:
        print(f"  eqmain.dll: 0x{eqmain['base']:08X} (size=0x{eqmain['size']:X})")
    if dinput8:
        print(f"  dinput8.dll: 0x{dinput8['base']:08X} (size=0x{dinput8['size']:X})")
    print(f"  String scan: {sum(len(v) for v in all_string_addrs.values())} total occurrences")
    print(f"  Heap pointer refs: {total_refs}")
    print(f"  CXWnd candidates (string-first): {sum(len(v) for v in candidates.values())}")
    if best_offset:
        print(f"  Best SIDL offset: +0x{best_offset:X}")

    # Save results
    outpath = os.path.join(os.path.dirname(__file__), "widget_discovery_v2_results.txt")
    with open(outpath, "w") as f:
        f.write(f"# Widget Discovery v2 Results\n")
        f.write(f"# PID: {args.pid}\n")
        if eqmain:
            f.write(f"# eqmain: 0x{eqmain['base']:08X} size=0x{eqmain['size']:X}\n")
        if dinput8:
            f.write(f"# dinput8: 0x{dinput8['base']:08X} size=0x{dinput8['size']:X}\n")
        f.write(f"# SIDL offset: {'0x%X' % best_offset if best_offset else 'UNKNOWN'}\n\n")
        for name, cands in candidates.items():
            for c in cands:
                f.write(f"{name}: base=0x{c['cxwnd_base']:08X} vt=0x{c['vtable']:08X} "
                        f"sidl=+0x{c['sidl_offset']:X} module={c.get('vt_module','?')}\n")
    print(f"\n  Results saved to: {outpath}")

    kernel32.CloseHandle(handle)

if __name__ == "__main__":
    main()
