"""
Quick targeted probe: understand CXStr buffer layout and find pointer refs.

Approach:
1. Probe 0x71F52BD8 (the eqmain.dll pointer found at [-4] of every string)
2. For each known string address, try offsets 0,4,8,12,16,20 and scan ALL memory
3. Report which offset produces real hits
"""

import ctypes
import ctypes.wintypes as wt
import struct
import sys
from collections import defaultdict

kernel32 = ctypes.windll.kernel32

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010

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

def open_process(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        raise OSError(f"OpenProcess failed (err {ctypes.GetLastError()})")
    return h

def read_mem(handle, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = ctypes.c_size_t(0)
    ok = kernel32.ReadProcessMemory(handle, ctypes.c_void_p(addr), buf, size, ctypes.byref(n))
    if not ok or n.value == 0:
        return None
    return buf.raw[:n.value]

def read_dword(handle, addr):
    d = read_mem(handle, addr, 4)
    return struct.unpack("<I", d)[0] if d and len(d) == 4 else None

def read_string(handle, addr, maxlen=128):
    d = read_mem(handle, addr, maxlen)
    if not d: return None
    try:
        end = d.index(b'\x00')
        return d[:end].decode('ascii', errors='replace')
    except ValueError:
        return None

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
        if (mbi.State == 0x1000 and
            not (mbi.Protect & 0x101) and
            mbi.Protect & 0xFE):
            regions.append((base, size, mbi.Type))
        addr = base + size
        if addr <= base:
            addr = base + 0x1000
    return regions

def search_dword_in_regions(handle, regions, target_val):
    """Search all regions for a DWORD value. Returns list of (addr, region_type)."""
    target_bytes = struct.pack("<I", target_val)
    results = []
    CHUNK = 0x100000

    for base, size, rtype in regions:
        for off in range(0, size, CHUNK):
            csz = min(CHUNK, size - off)
            if csz < 4:
                continue
            data = read_mem(handle, base + off, csz)
            if not data or len(data) < 4:
                continue
            pos = 0
            while True:
                pos = data.find(target_bytes, pos)
                if pos == -1:
                    break
                if pos % 4 == 0:  # aligned
                    ref_addr = base + off + pos
                    rt = "IMAGE" if rtype == 0x01000000 else "HEAP"
                    results.append((ref_addr, rt))
                pos += 4  # next aligned position
    return results

def main():
    pid = int(sys.argv[1]) if len(sys.argv) > 1 else 20552
    h = open_process(pid)
    print(f"PID {pid}")

    # ── Part 1: Probe the eqmain pointer ──────────────────────
    print("\n=== Probing 0x71F52BD8 ===")
    for addr in [0x71F52BD8]:
        data = read_mem(h, addr, 32)
        if data:
            for i in range(8):
                e = struct.unpack("<I", data[i*4:i*4+4])[0]
                in_eqmain = 0x71E20000 <= e < 0x72179000
                print(f"  [{i}] 0x{e:08X}  {'eqmain' if in_eqmain else ''}")
        else:
            print("  UNREADABLE")

    # ── Part 2: Probe more strings to see the pattern ─────────
    # Also check LOGIN_PasswordEdit and LOGIN_ConnectButton
    print("\n=== Probing all heap string occurrences ===")
    str_addresses = {
        "LOGIN_UsernameEdit": [0x108AB104, 0x108B7B14],
        "LOGIN_PasswordEdit": [0x108AB404, 0x108B7F74],
        "LOGIN_ConnectButton": [0x1088053C, 0x108B7074],
    }

    for name, addrs in str_addresses.items():
        for str_addr in addrs:
            pre = read_mem(h, str_addr - 24, 24)
            if not pre:
                continue
            print(f"\n  '{name}' @ 0x{str_addr:08X}:")
            for i in range(0, 24, 4):
                val = struct.unpack("<I", pre[i:i+4])[0]
                off = i - 24
                note = ""
                slen = len(name)
                if val == slen:
                    note = f" ← strlen={slen} ✓"
                elif val == 1:
                    note = " ← refcount=1?"
                elif slen < val < 256:
                    note = f" ← alloc={val}?"
                elif 0x71E20000 <= val < 0x72179000:
                    note = " ← eqmain.dll ptr ★"
                print(f"    [{off:+3d}] 0x{val:08X}{note}")

    # ── Part 3: Search for all possible CXStr pointer targets ──
    print("\n=== Searching ALL memory for pointer refs ===")
    regions = enum_regions(h)
    total_mem = sum(s for _, s, _ in regions)
    print(f"  {len(regions)} regions, {total_mem/(1024*1024):.0f} MB")

    # Use one representative string from each widget
    test_strings = {
        "LOGIN_UsernameEdit": 0x108B7B14,  # the one with clear refcount pattern
        "LOGIN_PasswordEdit": 0x108B7F74,
        "LOGIN_ConnectButton": 0x108B7074,
    }

    for hdr_off in [0, 4, 8, 12, 16, 20, 24]:
        print(f"\n  --- CXStr.Ptr = string_addr - {hdr_off} ---")
        any_found = False
        for name, str_addr in test_strings.items():
            target = str_addr - hdr_off
            results = search_dword_in_regions(h, regions, target)
            if results:
                any_found = True
                print(f"    '{name}': target=0x{target:08X} -> {len(results)} hits")
                for ref_addr, rt in results[:8]:
                    # Read context around the ref
                    ctx = read_mem(h, ref_addr - 8, 24)
                    ctx_str = ""
                    if ctx:
                        vals = struct.unpack("<IIIIII", ctx[:24])
                        # Check if the DWORD at ref_addr-8 or ref_addr-4 could be a vtable
                        for ci, cv in enumerate(vals):
                            if 0x71E20000 <= cv < 0x72179000:
                                ctx_str += f" (val[{ci-2}]=eqmain!)"
                            elif 0x72EE0000 <= cv < 0x7303A000:
                                ctx_str += f" (val[{ci-2}]=dinput8!)"
                    print(f"      @ 0x{ref_addr:08X} ({rt}){ctx_str}")
            else:
                pass  # don't spam for no-hits
        if not any_found:
            print(f"    (no hits for any widget)")

    # ── Part 4: Also try: maybe CXStr is just 4 bytes { char* Ptr }
    # and Ptr points to the START of the buffer (before the refcount)
    # Buffer layout: { rc=1, alloc=128, len=18, pad=0, type=eqmain, data="LOGIN_..." }
    # So Ptr would be at string_addr - 20
    print("\n\n=== ALSO: Direct scan for the eqmain type ptr 0x71F52BD8 on heap ===")
    results = search_dword_in_regions(h, regions, 0x71F52BD8)
    heap_results = [(a, t) for a, t in results if t == "HEAP"]
    print(f"  Found {len(results)} total ({len(heap_results)} on heap)")
    for ref_addr, rt in heap_results[:20]:
        # Read string at ref_addr + 4
        s = read_string(h, ref_addr + 4, 64)
        note = ""
        if s and len(s) >= 2:
            note = f' string_at+4="{s[:40]}"'
        print(f"    0x{ref_addr:08X} ({rt}){note}")

    kernel32.CloseHandle(h)

if __name__ == "__main__":
    main()
