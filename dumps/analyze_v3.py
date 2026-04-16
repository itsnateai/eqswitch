"""
EQSwitch v7 — Widget Discovery v3: CXStr buffer header probe

v2 failed because CXStr doesn't point directly to the string chars.
CXStr uses a ref-counted buffer: { header..., char data[] }.
The CXStr.Ptr points to the BUFFER BASE, and the string starts at
buffer_base + header_size.

This script:
1. Finds target strings on the heap
2. Probes bytes BEFORE each string to determine the buffer header layout
3. Searches for pointers to (string_addr - header_size)
4. Walks back from those refs to find CXWnd objects with vtables

Usage: python analyze_v3.py --pid PID
"""

import ctypes
import ctypes.wintypes as wt
import struct
import sys
import argparse
from collections import defaultdict

kernel32 = ctypes.windll.kernel32

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

def open_process(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        raise OSError(f"OpenProcess failed (err {ctypes.GetLastError()})")
    return h

def read_mem(h, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = ctypes.c_size_t(0)
    if not kernel32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(n)):
        return None
    return buf.raw[:n.value] if n.value else None

def read_dword(h, addr):
    d = read_mem(h, addr, 4)
    return struct.unpack("<I", d)[0] if d and len(d) == 4 else None

def read_string(h, addr, maxlen=256):
    d = read_mem(h, addr, maxlen)
    if not d: return None
    try:
        end = d.index(b'\x00')
        return d[:end].decode('ascii', errors='replace')
    except ValueError:
        return d.decode('ascii', errors='replace')

def enum_regions(h):
    addr = 0x10000
    mbi = MEMORY_BASIC_INFORMATION()
    regions = []
    while addr < 0x7FFF0000:
        if not kernel32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize or 0
        if size == 0: break
        if (mbi.State == MEM_COMMIT and
            not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) and
            mbi.Protect & 0xFE):
            regions.append((base, size, mbi.Protect, mbi.Type))
        addr = base + size
        if addr <= base: addr = base + 0x1000
    return regions

def enum_modules(pid):
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    if snap in (-1, 0xFFFFFFFF): return []
    modules = []
    me = MODULEENTRY32()
    me.dwSize = ctypes.sizeof(MODULEENTRY32)
    if kernel32.Module32First(snap, ctypes.byref(me)):
        while True:
            base = ctypes.cast(me.modBaseAddr, ctypes.c_void_p).value or 0
            modules.append({
                "name": me.szModule.decode('ascii', errors='replace').lower(),
                "base": base, "size": me.modBaseSize,
            })
            if not kernel32.Module32Next(snap, ctypes.byref(me)): break
    kernel32.CloseHandle(snap)
    return modules

def is_in_module(addr, modules):
    for m in modules:
        if m["base"] <= addr < m["base"] + m["size"]:
            return m["name"]
    return None

TARGET_NAMES = [
    "LOGIN_UsernameEdit",
    "LOGIN_PasswordEdit",
    "LOGIN_ConnectButton",
    "OK_Display",
    "OK_OKButton",
    "YESNO_YesButton",
]

CHUNK = 0x100000

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pid", type=int, required=True)
    args = parser.parse_args()

    h = open_process(args.pid)
    print(f"PID {args.pid} opened\n")

    modules = enum_modules(args.pid)
    eqmain = next((m for m in modules if "eqmain" in m["name"]), None)
    dinput8_mods = [m for m in modules if "dinput8" in m["name"]]
    # Use the larger one as the real MQ2 dinput8
    dinput8 = max(dinput8_mods, key=lambda m: m["size"]) if dinput8_mods else None

    if eqmain:
        print(f"eqmain.dll: 0x{eqmain['base']:08X} size=0x{eqmain['size']:X}")
    if dinput8:
        print(f"dinput8.dll: 0x{dinput8['base']:08X} size=0x{dinput8['size']:X}")

    regions = enum_regions(h)
    all_regions = regions
    heap_regions = [(b,s,p,t) for b,s,p,t in regions if t in (MEM_PRIVATE, MEM_MAPPED)]

    # ── Step 1: Find heap occurrences of target strings ────────

    print(f"\n{'='*60}")
    print("STEP 1: Find target strings in heap memory")
    print(f"{'='*60}")

    string_locs = {}  # name -> [addr, ...]
    for name in TARGET_NAMES:
        target = name.encode('ascii') + b'\x00'
        locs = []
        for base, size, prot, rtype in heap_regions:
            for off in range(0, size, CHUNK):
                csz = min(CHUNK + 256, size - off)
                if csz <= 0: continue
                data = read_mem(h, base + off, csz)
                if not data: continue
                pos = 0
                while True:
                    pos = data.find(target, pos)
                    if pos == -1: break
                    locs.append(base + off + pos)
                    pos += 1
        string_locs[name] = locs
        print(f"  '{name}': {len(locs)} heap occurrence(s)")
        for a in locs:
            print(f"    0x{a:08X}")

    # ── Step 2: Probe bytes BEFORE each string to find header ──

    print(f"\n{'='*60}")
    print("STEP 2: Probe CXStr buffer header (bytes before string)")
    print(f"{'='*60}")

    # Common CXStr buffer layouts:
    # Layout A: { int refcount; int length; int alloc; char data[] } — header = 12 bytes
    # Layout B: { int length; int alloc; char data[] }               — header = 8 bytes
    # Layout C: { int length; char data[] }                          — header = 4 bytes
    # Layout D: direct char* (no header)                             — header = 0 bytes

    header_candidates = defaultdict(int)  # header_size -> vote_count

    for name, locs in string_locs.items():
        str_len = len(name)
        for str_addr in locs:
            # Read 32 bytes before the string
            pre = read_mem(h, str_addr - 32, 32)
            if not pre or len(pre) < 32:
                continue

            print(f"\n  '{name}' @ 0x{str_addr:08X} — 32 bytes before:")
            for i in range(0, 32, 4):
                val = struct.unpack("<I", pre[i:i+4])[0]
                off_from_str = i - 32
                print(f"    [{off_from_str:+3d}] 0x{val:08X} ({val})", end="")

                # Annotate possible header fields
                if val == str_len:
                    print(f"  <== matches string length ({str_len})", end="")
                    # This tells us the distance from this field to the string
                    header_from_here = 32 - i - 4  # bytes from this DWORD to the string start
                    # But the buffer base would be at this DWORD's position or earlier
                elif val == 1:
                    print(f"  <== could be refcount=1", end="")
                elif str_len < val < 256:
                    print(f"  <== could be alloc size", end="")
                print()

            # Check specific header layouts:
            # Layout A (12 bytes): at str_addr-12 should be {refcount, length, alloc}
            pre12 = read_mem(h, str_addr - 12, 12)
            if pre12 and len(pre12) == 12:
                rc, ln, al = struct.unpack("<III", pre12)
                if ln == str_len and al >= str_len and 0 < rc < 100:
                    print(f"    → Layout A match (12-byte header): rc={rc}, len={ln}, alloc={al}")
                    header_candidates[12] += 1
                # Try {length, alloc, refcount}
                if rc == str_len and ln >= str_len and 0 < al < 100:
                    print(f"    → Layout A' match (len,alloc,rc): len={rc}, alloc={ln}, rc={al}")
                    header_candidates[12] += 1

            # Layout B (8 bytes): at str_addr-8 should be {length, alloc}
            pre8 = read_mem(h, str_addr - 8, 8)
            if pre8 and len(pre8) == 8:
                a, b_ = struct.unpack("<II", pre8)
                if a == str_len and b_ >= str_len:
                    print(f"    → Layout B match (8-byte header): len={a}, alloc={b_}")
                    header_candidates[8] += 1
                if b_ == str_len and a >= 0 and a < 100:
                    print(f"    → Layout B' match (rc/?, len): first={a}, len={b_}")
                    header_candidates[8] += 1

            # Layout C (4 bytes): at str_addr-4 should be {length}
            pre4 = read_mem(h, str_addr - 4, 4)
            if pre4 and len(pre4) == 4:
                v = struct.unpack("<I", pre4)[0]
                if v == str_len:
                    print(f"    → Layout C match (4-byte header): len={v}")
                    header_candidates[4] += 1

    print(f"\n  Header layout votes:")
    for sz, votes in sorted(header_candidates.items()):
        print(f"    {sz}-byte header: {votes} votes")

    if not header_candidates:
        print("  NO header layout matches! Trying raw pointer approach...")
        best_header = 0
    else:
        best_header = max(header_candidates.keys(), key=lambda k: header_candidates[k])
        print(f"\n  BEST: {best_header}-byte header (buf_base = string_addr - {best_header})")

    # ── Step 3: Search for pointers to buffer base ─────────────

    print(f"\n{'='*60}")
    print(f"STEP 3: Search ALL memory for pointers to buffer bases")
    print(f"{'='*60}")

    # Try multiple header sizes (0, 4, 8, 12, 16, 20)
    for hdr_size in [0, 4, 8, 12, 16, 20]:
        buf_bases = {}  # buf_addr -> (name, str_addr)
        for name, locs in string_locs.items():
            for str_addr in locs:
                buf_addr = str_addr - hdr_size
                buf_bases[struct.pack("<I", buf_addr)] = (name, str_addr, buf_addr)

        if not buf_bases:
            continue

        print(f"\n  --- Header size = {hdr_size} bytes ---")
        refs_found = defaultdict(list)
        total_scanned = 0

        # Scan ALL memory (not just heap) for these pointers
        for base, size, prot, rtype in all_regions:
            for off in range(0, size, CHUNK):
                csz = min(CHUNK, size - off)
                if csz < 4: continue
                data = read_mem(h, base + off, csz)
                if not data or len(data) < 4: continue
                total_scanned += len(data)

                for i in range(0, len(data) - 3, 4):
                    dw = data[i:i+4]
                    if dw in buf_bases:
                        ref_addr = base + off + i
                        name, str_addr, buf_addr = buf_bases[dw]
                        region_type = "HEAP" if rtype in (MEM_PRIVATE, MEM_MAPPED) else "IMAGE"
                        mod = is_in_module(ref_addr, modules)
                        refs_found[name].append({
                            "ref_addr": ref_addr,
                            "buf_addr": buf_addr,
                            "str_addr": str_addr,
                            "region": region_type,
                            "module": mod,
                        })

        total = sum(len(v) for v in refs_found.values())
        print(f"    Scanned {total_scanned/(1024*1024):.0f} MB, found {total} refs")

        if total > 0:
            for name, refs in sorted(refs_found.items()):
                print(f"    '{name}': {len(refs)} ref(s)")
                for r in refs[:5]:
                    print(f"      @ 0x{r['ref_addr']:08X} ({r['region']}"
                          f"{' ['+r['module']+']' if r['module'] else ''}) "
                          f"-> buf 0x{r['buf_addr']:08X}")

            # ── Step 4: Walk back from refs to find vtables ────

            print(f"\n  STEP 4: Walk back from refs to find CXWnd vtables (header={hdr_size})")

            vtable_ranges = []
            if eqmain:
                vtable_ranges.append((eqmain["base"], eqmain["base"] + eqmain["size"], "eqmain"))
            if dinput8:
                vtable_ranges.append((dinput8["base"], dinput8["base"] + dinput8["size"], "dinput8"))
            vtable_ranges.append((0x00400000, 0x02200000, "eqgame"))

            cxwnd_results = defaultdict(list)

            for name, refs in refs_found.items():
                for r in refs:
                    if r["module"]:  # skip refs inside DLL image sections
                        continue
                    ref_addr = r["ref_addr"]

                    # Walk backwards up to 0x400 bytes
                    MAX_WALK = 0x400
                    walk = read_mem(h, ref_addr - MAX_WALK, MAX_WALK)
                    if not walk or len(walk) < MAX_WALK:
                        continue

                    for back in range(4, MAX_WALK, 4):
                        cand_base = ref_addr - back
                        idx = MAX_WALK - back
                        if idx < 0 or idx + 4 > len(walk): continue
                        vt = struct.unpack("<I", walk[idx:idx+4])[0]

                        in_range = False
                        vt_mod = ""
                        for lo, hi, mn in vtable_ranges:
                            if lo <= vt < hi:
                                in_range = True
                                vt_mod = mn
                                break
                        if not in_range: continue

                        # Validate vtable
                        vt_data = read_mem(h, vt, 32)
                        if not vt_data or len(vt_data) < 32: continue
                        valid = 0
                        for vi in range(8):
                            e = struct.unpack("<I", vt_data[vi*4:vi*4+4])[0]
                            for lo, hi, _ in vtable_ranges:
                                if lo <= e < hi:
                                    valid += 1
                                    break
                            # Also accept entries in any loaded DLL
                            if 0x60000000 <= e < 0x80000000:
                                valid += 1

                        if valid < 3: continue

                        sidl_off = ref_addr - cand_base
                        cxwnd_results[name].append({
                            "base": cand_base,
                            "vtable": vt,
                            "vt_valid": min(valid, 8),
                            "vt_module": vt_mod,
                            "sidl_offset": sidl_off,
                            "hdr_size": hdr_size,
                        })

            if cxwnd_results:
                print(f"\n  CXWnd CANDIDATES (header={hdr_size}):")
                offset_votes = defaultdict(list)
                for name, cands in sorted(cxwnd_results.items()):
                    # Deduplicate by base address
                    seen = set()
                    unique = []
                    for c in cands:
                        if c["base"] not in seen:
                            seen.add(c["base"])
                            unique.append(c)
                    print(f"    '{name}': {len(unique)} unique candidate(s)")
                    for c in unique[:3]:
                        print(f"      CXWnd @ 0x{c['base']:08X} vt=0x{c['vtable']:08X} "
                              f"({c['vt_valid']}/8) [{c['vt_module']}] sidl=+0x{c['sidl_offset']:X}")
                        offset_votes[c['sidl_offset']].append(name)

                print(f"\n  SIDL offset distribution:")
                for off, names in sorted(offset_votes.items()):
                    print(f"    +0x{off:X}: {len(names)} widgets ({', '.join(set(names))})")

                # Dump the best candidate for each widget
                best_off = max(offset_votes.keys(), key=lambda k: len(offset_votes[k]))
                print(f"\n  CONSENSUS SIDL offset: +0x{best_off:X}")

                print(f"\n  STRUCT DUMPS:")
                for name, cands in sorted(cxwnd_results.items()):
                    for c in cands:
                        if c["sidl_offset"] != best_off: continue
                        base = c["base"]
                        data = read_mem(h, base, 0x200)
                        if not data: continue
                        print(f"\n    === {name} @ 0x{base:08X} (vt=0x{c['vtable']:08X}) ===")
                        for off in range(0, min(len(data), 0x200), 4):
                            if off + 4 > len(data): break
                            val = struct.unpack("<I", data[off:off+4])[0]
                            ann = ""
                            if off == 0: ann = " <-- vtable"
                            elif off == best_off: ann = f" <-- SIDL name (CXStr buf ptr)"
                            elif val != 0 and 0x10000 < val < 0x7FFFFFFF:
                                s = read_string(h, val, 48)
                                if s and len(s) >= 2 and all(32 <= ord(ch) < 127 for ch in s):
                                    ann = f' -> "{s[:40]}"'
                            if ann or off <= 0x10 or (off >= best_off - 8 and off <= best_off + 16) or val != 0:
                                print(f"      +0x{off:04X}: 0x{val:08X}{ann}")
                        break  # first matching candidate only

    kernel32.CloseHandle(h)

if __name__ == "__main__":
    main()
