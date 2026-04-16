"""
EQSwitch v7 — Login Widget Discovery via Live Memory Analysis

Scans a running eqgame.exe process for CXWnd UI widget objects by:
1. Finding known SIDL name strings (e.g. "LOGIN_UsernameEdit") in process memory
2. Tracing back from string addresses to CXStr structs (Ptr, Length, Alloc, RefCount)
3. Tracing from CXStr to CXWnd objects that contain them
4. Mapping the CXWnd struct layout (vtable offset, SIDL name offset)

Usage: python analyze_login_widgets.py [--pid PID]
  If --pid not given, auto-detects eqgame.exe
  Run while EQ is sitting at the login screen (password prompt visible)

No procdump needed — uses ReadProcessMemory directly.
"""

import ctypes
import ctypes.wintypes as wt
import struct
import sys
import os
import argparse
from collections import defaultdict

# ─── Win32 API setup ───────────────────────────────────────────

kernel32 = ctypes.windll.kernel32

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

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
    """Enumerate committed, readable memory regions."""
    addr = 0x10000  # skip null page area
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
            mbi.Protect & 0xFE):  # any readable
            regions.append((base, size, mbi.Protect, mbi.Type))
        addr = base + size
        if addr <= base:
            addr = base + 0x1000
    return regions

def find_pid_by_name(name="eqgame.exe"):
    """Find PID of process by name using tasklist."""
    import subprocess
    result = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {name}", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, creationflags=0x08000000  # CREATE_NO_WINDOW
    )
    for line in result.stdout.strip().split('\n'):
        if name.lower() in line.lower():
            parts = line.strip('"').split('","')
            if len(parts) >= 2:
                return int(parts[1].strip('"'))
    return None

# ─── Module info ───────────────────────────────────────────────

def get_module_ranges(handle, pid):
    """Get base address and size of key modules."""
    import subprocess
    result = subprocess.run(
        ["tasklist", "/M", "/FI", f"PID eq {pid}", "/FO", "CSV"],
        capture_output=True, text=True, creationflags=0x08000000
    )
    # For detailed module info we'll read the PEB, but for now
    # scan regions for module signatures
    modules = {}
    regions = enum_regions(handle)
    for base, size, prot, rtype in regions:
        if rtype == 0x01000000 and size >= 0x1000:  # MEM_IMAGE
            data = read_mem(handle, base, 2)
            if data and data[:2] == b'MZ':
                name = f"module_0x{base:08X}"
                # Try to read the module name from PE headers
                pe_data = read_mem(handle, base, 0x400)
                if pe_data:
                    modules[base] = {"base": base, "size": size}
    return modules

# ─── Main analysis ─────────────────────────────────────────────

TARGET_STRINGS = [
    b"LOGIN_UsernameEdit\x00",
    b"LOGIN_PasswordEdit\x00",
    b"LOGIN_ConnectButton\x00",
    b"LOGIN_EQNewsButton\x00",
    b"LOGIN_DeleteButton\x00",
    b"OK_Display\x00",
    b"OK_OKButton\x00",
    b"YESNO_YesButton\x00",
    b"Character_List\x00",
    b"CLW_EnterWorldButton\x00",
    b"EQLS_ServerList\x00",
    b"SERVERSELECT_SelectButton\x00",
]

# Also search for these to understand the CXWnd naming convention
EXTRA_STRINGS = [
    b"CXWndManager\x00",
    b"CSidlScreenWnd\x00",
    b"LoginScreenWnd\x00",
    b"LoginViewManager\x00",
    b"ServerSelectWnd\x00",
]

def scan_for_strings(handle, regions, targets):
    """Find all occurrences of target strings in process memory."""
    results = defaultdict(list)
    total_scanned = 0
    CHUNK = 0x100000  # 1MB chunks

    print(f"\n[1/5] Scanning {len(regions)} memory regions for {len(targets)} target strings...")

    for base, size, prot, rtype in regions:
        for offset in range(0, size, CHUNK):
            chunk_size = min(CHUNK + 256, size - offset)  # overlap for boundary strings
            if chunk_size <= 0:
                continue
            data = read_mem(handle, base + offset, chunk_size)
            if not data:
                continue
            total_scanned += len(data)

            for target in targets:
                search_name = target.rstrip(b'\x00').decode('ascii')
                pos = 0
                while True:
                    pos = data.find(target, pos)
                    if pos == -1:
                        break
                    abs_addr = base + offset + pos
                    results[search_name].append(abs_addr)
                    pos += 1

    print(f"    Scanned {total_scanned / (1024*1024):.1f} MB")
    for name, addrs in sorted(results.items()):
        print(f"    '{name}': {len(addrs)} occurrence(s)")
        for a in addrs[:5]:
            print(f"      0x{a:08X}")
        if len(addrs) > 5:
            print(f"      ... and {len(addrs)-5} more")

    return results

def find_pointer_refs(handle, regions, target_addrs, label=""):
    """Scan all memory for DWORDs that point to any of the target addresses."""
    refs = defaultdict(list)  # target_addr -> [ref_addr, ...]
    target_set = set(target_addrs)

    # Build a fast lookup: pack all targets as little-endian DWORDs
    target_bytes = {}
    for addr in target_addrs:
        target_bytes[struct.pack("<I", addr)] = addr

    print(f"\n[2/5] Scanning for DWORD pointers to {len(target_addrs)} string addresses ({label})...")

    CHUNK = 0x100000  # 1MB
    scanned = 0

    for base, size, prot, rtype in regions:
        for offset in range(0, size, CHUNK):
            chunk_size = min(CHUNK, size - offset)
            if chunk_size < 4:
                continue
            data = read_mem(handle, base + offset, chunk_size)
            if not data or len(data) < 4:
                continue
            scanned += len(data)

            # Scan at 4-byte alignment for efficiency
            for i in range(0, len(data) - 3, 4):
                dword = data[i:i+4]
                if dword in target_bytes:
                    ref_addr = base + offset + i
                    target = target_bytes[dword]
                    refs[target].append(ref_addr)

    print(f"    Scanned {scanned / (1024*1024):.1f} MB")
    total_refs = sum(len(v) for v in refs.values())
    print(f"    Found {total_refs} total pointer references")

    return refs

def analyze_cxwnd_candidates(handle, refs, string_addrs, name):
    """
    For each pointer ref to a string address, try to identify the containing CXWnd object.

    CXStr layout: { char* Ptr; int Length; int Alloc; int RefCount; }  = 16 bytes
    A CXStr.Ptr pointing to our string means the CXStr starts at (ref_addr).
    The CXStr is embedded IN the CXWnd at some offset.

    Walk backwards from the CXStr to find a vtable pointer (first DWORD of the CXWnd).
    Valid vtables should be in a loaded DLL code range (0x60000000-0x80000000 for dinput8
    or eqmain.dll, or 0x00400000-0x00600000 for eqgame.exe).
    """

    print(f"\n[3/5] Analyzing CXWnd candidates for '{name}'...")

    candidates = []

    for str_addr, ref_addrs in refs.items():
        if str_addr not in string_addrs:
            continue

        for ref_addr in ref_addrs:
            # ref_addr points to a DWORD that contains str_addr
            # This could be CXStr.Ptr — check CXStr plausibility
            # CXStr: { Ptr=str_addr, Length=N, Alloc>=Length, RefCount>=1 }
            cxstr_data = read_mem(handle, ref_addr, 16)
            if not cxstr_data or len(cxstr_data) < 16:
                continue

            ptr, length, alloc, refcount = struct.unpack("<IIII", cxstr_data)
            if ptr != str_addr:
                continue

            # Validate CXStr fields
            str_text = read_string(handle, ptr, 128)
            if not str_text:
                continue
            expected_len = len(str_text)

            is_cxstr = (
                length == expected_len and
                alloc >= length and
                alloc < 4096 and
                refcount >= 0 and refcount < 1000
            )

            # Also check: could be a simple char* pointer (not in a CXStr)
            # In that case Length/Alloc/RefCount won't make sense

            cxstr_info = {
                "str_addr": str_addr,
                "cxstr_addr": ref_addr,
                "text": str_text,
                "length": length,
                "alloc": alloc,
                "refcount": refcount,
                "is_cxstr": is_cxstr,
            }

            # Now walk backwards from this CXStr to find the CXWnd base
            # Max CXWnd size is ~0x400 bytes, so scan back up to 0x400
            MAX_WALK = 0x400
            walk_data = read_mem(handle, ref_addr - MAX_WALK, MAX_WALK + 16)
            if not walk_data:
                continue

            for back_offset in range(MAX_WALK, 0, -4):
                candidate_base = ref_addr - back_offset
                idx = MAX_WALK - back_offset
                if idx < 0 or idx + 4 > len(walk_data):
                    continue

                vtable_candidate = struct.unpack("<I", walk_data[idx:idx+4])[0]

                # Valid vtable ranges for loaded DLLs:
                # dinput8.dll: ~0x70000000-0x73000000
                # eqmain.dll: ASLR but typically in similar range
                # eqgame.exe: ~0x00400000-0x00600000
                is_code_ptr = (
                    (0x00400000 <= vtable_candidate <= 0x00800000) or  # eqgame.exe
                    (0x60000000 <= vtable_candidate <= 0x80000000) or  # DLLs
                    (0x10000000 <= vtable_candidate <= 0x20000000)     # relocated DLLs
                )

                if not is_code_ptr:
                    continue

                # Verify vtable pointer: first entry should also be a code pointer
                vt_first = read_dword(handle, vtable_candidate)
                if vt_first is None:
                    continue
                is_code_vt = (
                    (0x00400000 <= vt_first <= 0x00800000) or
                    (0x60000000 <= vt_first <= 0x80000000) or
                    (0x10000000 <= vt_first <= 0x20000000)
                )
                if not is_code_vt:
                    continue

                # Check a few more vtable entries (real vtables have many valid function ptrs)
                vt_valid = 0
                for vi in range(8):
                    vt_entry = read_dword(handle, vtable_candidate + vi * 4)
                    if vt_entry and (
                        (0x00400000 <= vt_entry <= 0x00800000) or
                        (0x60000000 <= vt_entry <= 0x80000000) or
                        (0x10000000 <= vt_entry <= 0x20000000)
                    ):
                        vt_valid += 1

                if vt_valid < 4:  # at least 4/8 entries must be code pointers
                    continue

                sidl_offset = ref_addr - candidate_base

                candidates.append({
                    "cxwnd_base": candidate_base,
                    "vtable": vtable_candidate,
                    "vtable_entries_valid": vt_valid,
                    "sidl_name_offset": sidl_offset,
                    "is_cxstr": is_cxstr,
                    **cxstr_info,
                })

    # Deduplicate by cxwnd_base
    seen = set()
    unique = []
    for c in candidates:
        if c["cxwnd_base"] not in seen:
            seen.add(c["cxwnd_base"])
            unique.append(c)

    print(f"    Found {len(unique)} unique CXWnd candidate(s)")
    for c in unique:
        print(f"    CXWnd @ 0x{c['cxwnd_base']:08X}")
        print(f"      vtable = 0x{c['vtable']:08X} ({c['vtable_entries_valid']}/8 entries valid)")
        print(f"      SIDL name at offset +0x{c['sidl_name_offset']:X} {'(CXStr)' if c['is_cxstr'] else '(raw ptr)'}")
        print(f"      text = '{c['text']}'")
        print(f"      CXStr @ 0x{c['cxstr_addr']:08X} (len={c['length']}, alloc={c['alloc']}, rc={c['refcount']})")

    return unique

def cross_validate(all_candidates):
    """Check if all discovered CXWnds share the same SIDL name offset."""
    print(f"\n[4/5] Cross-validating SIDL name offset across all widgets...")

    offset_counts = defaultdict(list)
    for name, candidates in all_candidates.items():
        for c in candidates:
            offset_counts[c["sidl_name_offset"]].append(name)

    if not offset_counts:
        print("    NO candidates found — cannot determine SIDL offset")
        return None

    print(f"    Offset distribution:")
    for offset, names in sorted(offset_counts.items()):
        print(f"      +0x{offset:X}: {len(names)} widgets — {', '.join(names)}")

    # The most common offset is likely correct
    best_offset = max(offset_counts.keys(), key=lambda k: len(offset_counts[k]))
    print(f"\n    CONSENSUS SIDL name offset: +0x{best_offset:X} ({len(offset_counts[best_offset])} widgets agree)")

    return best_offset

def dump_cxwnd_layout(handle, candidates, sidl_offset):
    """Dump the layout of discovered CXWnd objects for struct mapping."""
    print(f"\n[5/5] Dumping CXWnd struct layout (first 0x200 bytes)...")

    for name, cands in candidates.items():
        for c in cands:
            if c["sidl_name_offset"] != sidl_offset:
                continue
            base = c["cxwnd_base"]
            print(f"\n  === {name} === CXWnd @ 0x{base:08X}, vtable 0x{c['vtable']:08X}")

            data = read_mem(handle, base, 0x200)
            if not data:
                print("    (unreadable)")
                continue

            # Dump as annotated hex + DWORD values
            for off in range(0, min(len(data), 0x200), 4):
                if off + 4 > len(data):
                    break
                val = struct.unpack("<I", data[off:off+4])[0]

                annotation = ""
                if off == 0:
                    annotation = " <-- vtable"
                elif off == sidl_offset:
                    annotation = f" <-- SIDL name CXStr.Ptr -> '{c['text']}'"
                elif off == sidl_offset + 4:
                    annotation = " <-- CXStr.Length"
                elif off == sidl_offset + 8:
                    annotation = " <-- CXStr.Alloc"
                elif off == sidl_offset + 12:
                    annotation = " <-- CXStr.RefCount"
                else:
                    # Try to identify: is this a pointer to a string?
                    if 0x10000 < val < 0x7FFFFFFF:
                        s = read_string(handle, val, 64)
                        if s and len(s) >= 3 and all(32 <= ord(ch) < 127 for ch in s):
                            annotation = f" -> \"{s[:50]}\""
                    # Is this a pointer to an object with a vtable?
                    if 0x10000 < val < 0x7FFFFFFF and not annotation:
                        inner_vt = read_dword(handle, val)
                        if inner_vt and (
                            (0x00400000 <= inner_vt <= 0x00800000) or
                            (0x60000000 <= inner_vt <= 0x80000000)
                        ):
                            annotation = f" -> obj@0x{val:08X} (vt=0x{inner_vt:08X})"

                # Only print interesting lines
                if annotation or off < 0x10 or off == sidl_offset or val != 0:
                    print(f"    +0x{off:04X}: 0x{val:08X}{annotation}")

            # Only dump the first candidate per widget name
            break

def main():
    parser = argparse.ArgumentParser(description="EQSwitch v7 Login Widget Discovery")
    parser.add_argument("--pid", type=int, help="eqgame.exe PID (auto-detect if omitted)")
    parser.add_argument("--charselect", action="store_true", help="Also scan for charselect widgets")
    args = parser.parse_args()

    pid = args.pid
    if not pid:
        print("Auto-detecting eqgame.exe...")
        pid = find_pid_by_name("eqgame.exe")
        if not pid:
            print("ERROR: eqgame.exe not found. Launch EQ first, then run this script.")
            sys.exit(1)

    print(f"Target: eqgame.exe PID {pid}")

    handle = open_process(pid)
    print(f"Process opened (handle=0x{handle:X})")

    try:
        # Enumerate memory regions
        regions = enum_regions(handle)
        total_mem = sum(size for _, size, _, _ in regions)
        image_regions = [(b, s, p, t) for b, s, p, t in regions if t == 0x01000000]  # MEM_IMAGE
        heap_regions = [(b, s, p, t) for b, s, p, t in regions if t == 0x00020000]   # MEM_PRIVATE

        print(f"\nMemory map: {len(regions)} regions, {total_mem/(1024*1024):.0f} MB total")
        print(f"  Image (DLL/EXE): {len(image_regions)} regions, {sum(s for _,s,_,_ in image_regions)/(1024*1024):.0f} MB")
        print(f"  Private (heap):  {len(heap_regions)} regions, {sum(s for _,s,_,_ in heap_regions)/(1024*1024):.0f} MB")

        # Log loaded modules (MZ headers)
        print("\nLoaded modules (MZ headers found):")
        for base, size, prot, rtype in sorted(regions, key=lambda r: r[0]):
            if rtype == 0x01000000:  # MEM_IMAGE
                data = read_mem(handle, base, 2)
                if data and data[:2] == b'MZ':
                    # Read module name from PE export dir or just show address
                    print(f"  0x{base:08X} - 0x{base+size:08X} ({size/1024:.0f} KB)")

        # Step 1: Find target strings
        targets = TARGET_STRINGS + (EXTRA_STRINGS if not args.charselect else [])
        string_results = scan_for_strings(handle, regions, targets)

        if not string_results:
            print("\nFATAL: No target strings found in process memory!")
            print("Make sure EQ is at the login screen (password field visible)")
            return

        # Collect all string addresses for our login widgets
        login_strings = {}
        for target in TARGET_STRINGS:
            name = target.rstrip(b'\x00').decode('ascii')
            if name in string_results:
                login_strings[name] = string_results[name]

        # Step 2: Find pointer references to each string address
        all_str_addrs = []
        for addrs in login_strings.values():
            all_str_addrs.extend(addrs)

        if not all_str_addrs:
            print("\nFATAL: No login widget strings found!")
            return

        refs = find_pointer_refs(handle, regions, all_str_addrs, "login widget strings")

        # Step 3: Analyze CXWnd candidates per widget
        all_candidates = {}
        for name, str_addrs in login_strings.items():
            str_addr_set = set(str_addrs)
            candidates = analyze_cxwnd_candidates(handle, refs, str_addr_set, name)
            if candidates:
                all_candidates[name] = candidates

        # Step 4: Cross-validate
        sidl_offset = cross_validate(all_candidates)

        # Step 5: Dump struct layout
        if sidl_offset is not None and all_candidates:
            dump_cxwnd_layout(handle, all_candidates, sidl_offset)

        # Summary
        print("\n" + "=" * 70)
        print("SUMMARY")
        print("=" * 70)
        if sidl_offset is not None:
            print(f"  SIDL name offset in CXWnd: +0x{sidl_offset:X}")
            print(f"  Widgets found: {len(all_candidates)}")
            for name, cands in all_candidates.items():
                for c in cands:
                    if c["sidl_name_offset"] == sidl_offset:
                        print(f"    {name}: CXWnd @ 0x{c['cxwnd_base']:08X} (vt=0x{c['vtable']:08X})")

            print(f"\n  NEXT STEP: Update Native/mq2_bridge.cpp FindWindowByName():")
            print(f"    - Scan heap for objects with vtable in DLL code range")
            print(f"    - At offset +0x{sidl_offset:X}, read CXStr.Ptr")
            print(f"    - strcmp the pointed-to string with the target widget name")
            print(f"    - Cache results (widgets don't move during login phase)")
        else:
            print("  FAILED to determine SIDL name offset")
            print("  Try running with --charselect at the character select screen")
            print("  or examine the raw pointer references above")

        # Write results to file for the C++ fix
        outpath = os.path.join(os.path.dirname(__file__), "widget_discovery_results.txt")
        with open(outpath, "w") as f:
            f.write(f"# Widget Discovery Results\n")
            f.write(f"# PID: {pid}\n")
            f.write(f"# SIDL offset: {'0x%X' % sidl_offset if sidl_offset else 'UNKNOWN'}\n\n")
            for name, cands in all_candidates.items():
                for c in cands:
                    f.write(f"{name}: base=0x{c['cxwnd_base']:08X} vt=0x{c['vtable']:08X} "
                            f"sidl_off=+0x{c['sidl_name_offset']:X} "
                            f"cxstr=0x{c['cxstr_addr']:08X}\n")
        print(f"\n  Results saved to: {outpath}")

    finally:
        kernel32.CloseHandle(handle)

if __name__ == "__main__":
    main()
