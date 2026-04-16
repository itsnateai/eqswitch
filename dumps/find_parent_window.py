"""
Find the login screen parent CXWnd by scanning for objects with children.

From disassembly:
- CXWnd+0x00: vtable (eqmain range)
- CXWnd+0x08: next sibling
- CXWnd+0x10: first child
- CXWnd+0xD8: SIDL template ID

We look for heap objects with eqmain vtables that have non-null +0x10
(children), and walk those child lists.
"""

import ctypes
import ctypes.wintypes as wt
import struct
import sys
from collections import defaultdict

kernel32 = ctypes.windll.kernel32

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
TH32CS_SNAPMODULE = 0x00000008
TH32CS_SNAPMODULE32 = 0x00000010

class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wt.DWORD), ("RegionSize", ctypes.c_size_t),
        ("State", wt.DWORD), ("Protect", wt.DWORD), ("Type", wt.DWORD),
    ]

class MODULEENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wt.DWORD), ("th32ModuleID", wt.DWORD), ("th32ProcessID", wt.DWORD),
        ("GlbcntUsage", wt.DWORD), ("ProccntUsage", wt.DWORD),
        ("modBaseAddr", ctypes.POINTER(wt.BYTE)), ("modBaseSize", wt.DWORD),
        ("hModule", wt.HMODULE), ("szModule", ctypes.c_char * 256),
        ("szExePath", ctypes.c_char * 260),
    ]

def open_process(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h: raise OSError(f"OpenProcess failed")
    return h

def rd(handle, addr, sz):
    b = ctypes.create_string_buffer(sz)
    n = ctypes.c_size_t(0)
    kernel32.ReadProcessMemory(handle, ctypes.c_void_p(addr), b, sz, ctypes.byref(n))
    return b.raw[:n.value] if n.value else None

def rd4(handle, addr):
    d = rd(handle, addr, 4)
    return struct.unpack("<I", d)[0] if d and len(d) == 4 else None

def rdstr(handle, addr, mx=64):
    d = rd(handle, addr, mx)
    if not d: return None
    try:
        end = d.index(b'\x00')
        return d[:end].decode('ascii', errors='replace') if end >= 2 else None
    except: return None

def enum_modules(pid):
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    if snap in (-1, 0xFFFFFFFF): return []
    modules = []
    me = MODULEENTRY32(); me.dwSize = ctypes.sizeof(MODULEENTRY32)
    if kernel32.Module32First(snap, ctypes.byref(me)):
        while True:
            base = ctypes.cast(me.modBaseAddr, ctypes.c_void_p).value or 0
            modules.append({"name": me.szModule.decode('ascii', errors='replace').lower(),
                          "base": base, "size": me.modBaseSize})
            if not kernel32.Module32Next(snap, ctypes.byref(me)): break
    kernel32.CloseHandle(snap)
    return modules

def enum_regions(handle):
    addr = 0x10000; mbi = MEMORY_BASIC_INFORMATION(); regions = []
    while addr < 0x7FFF0000:
        if not kernel32.VirtualQueryEx(handle, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)): break
        base = mbi.BaseAddress or 0; size = mbi.RegionSize or 0
        if size == 0: break
        if mbi.State == 0x1000 and not (mbi.Protect & 0x101) and mbi.Protect & 0xFE:
            regions.append((base, size, mbi.Protect, mbi.Type))
        addr = base + size
        if addr <= base: addr = base + 0x1000
    return regions

def main():
    pid = int(sys.argv[1]) if len(sys.argv) > 1 else 20552
    h = open_process(pid)

    modules = enum_modules(pid)
    eqmain = next((m for m in modules if "eqmain" in m["name"]), None)
    if not eqmain:
        print("eqmain.dll not found!")
        return

    EQM_LO = eqmain["base"]
    EQM_HI = eqmain["base"] + eqmain["size"]
    print(f"eqmain: 0x{EQM_LO:08X}-0x{EQM_HI:08X}")

    regions = enum_regions(h)
    heap_regions = [(b,s,t) for b,s,_,t in regions if t in (0x00020000, 0x00040000)]

    # Phase 1: Find all heap objects with eqmain vtables that have children
    print(f"\nPhase 1: Finding parent CXWnd objects on heap...")
    CHUNK = 0x100000
    parents = []  # (obj_addr, vtable, child_addr, child_count)

    for base, size, rtype in heap_regions:
        for off in range(0, size, CHUNK):
            csz = min(CHUNK, size - off)
            if csz < 4: continue
            data = rd(h, base + off, csz)
            if not data or len(data) < 4: continue

            for i in range(0, len(data) - 3, 4):
                vt = struct.unpack("<I", data[i:i+4])[0]
                if not (EQM_LO <= vt < EQM_HI):
                    continue

                obj_addr = base + off + i
                # Read +0x10 (first child)
                child = rd4(h, obj_addr + 0x10)
                if not child or child < 0x10000:
                    continue

                # Check child has eqmain vtable
                child_vt = rd4(h, child)
                if not child_vt or not (EQM_LO <= child_vt < EQM_HI):
                    continue

                # Walk child list and count
                child_count = 0
                c = child
                seen = set()
                while c and c > 0x10000 and child_count < 100:
                    if c in seen: break  # cycle detection
                    seen.add(c)
                    cv = rd4(h, c)
                    if not cv: break
                    child_count += 1
                    c = rd4(h, c + 0x08)  # next sibling

                if child_count >= 5:  # interesting parents have 5+ children
                    parents.append((obj_addr, vt, child, child_count))

    print(f"  Found {len(parents)} parent CXWnds with 5+ children")

    # Phase 2: For each parent, walk children and try to identify login widgets
    print(f"\nPhase 2: Walking child lists...")

    for obj_addr, vt, first_child, child_count in sorted(parents, key=lambda x: -x[3]):
        # Read parent's SIDL ID
        parent_sidl = rd4(h, obj_addr + 0xD8)

        print(f"\n  Parent @ 0x{obj_addr:08X} vt=0x{vt:08X} children={child_count} sidl=0x{parent_sidl:08X}" if parent_sidl else
              f"\n  Parent @ 0x{obj_addr:08X} vt=0x{vt:08X} children={child_count}")

        # Walk children and look for SIDL IDs or name pointers
        c = first_child
        ci = 0
        seen = set()
        interesting = False
        while c and c > 0x10000 and ci < 60:
            if c in seen: break
            seen.add(c)
            child_vt = rd4(h, c)
            child_sidl = rd4(h, c + 0xD8)
            child_child = rd4(h, c + 0x10)
            has_sub = child_child and child_child > 0x10000

            # Try to get any string from the template system
            # Read a chunk of the child object and look for CXStr pointers
            # that resolve to known widget names
            child_data = rd(h, c, 0x200)
            found_name = None
            if child_data:
                for off in range(0, min(len(child_data), 0x200), 4):
                    if off + 4 > len(child_data): break
                    ptr = struct.unpack("<I", child_data[off:off+4])[0]
                    if ptr < 0x10000 or ptr > 0x7FFFFFFF: continue
                    # Check if this is a CXStr buf_base pointing to LOGIN_* string
                    s = rdstr(h, ptr + 20, 48)  # buf_base + 20 = string data
                    if s and "LOGIN_" in s:
                        found_name = (off, s)
                        interesting = True
                        break

            if found_name:
                off, name = found_name
                print(f"    child[{ci}] @ 0x{c:08X} vt=0x{child_vt:08X} "
                      f"sidl=0x{child_sidl:08X} +0x{off:X}->\"{name}\" ★★★")
            elif ci < 5 or has_sub:  # print first few and any with subchildren
                print(f"    child[{ci}] @ 0x{c:08X} vt=0x{child_vt:08X} "
                      f"sidl={'0x%08X' % child_sidl if child_sidl else 'null'} "
                      f"{'(has children)' if has_sub else ''}")

            ci += 1
            c = rd4(h, c + 0x08)

        if interesting:
            print(f"  ★★★ THIS IS THE LOGIN SCREEN PARENT! ★★★")
            # Full child dump with name resolution
            print(f"\n  Full child list with name resolution:")
            c = first_child
            ci = 0
            seen2 = set()
            while c and c > 0x10000 and ci < 60:
                if c in seen2: break
                seen2.add(c)
                child_data = rd(h, c, 0x200)
                name_str = None
                name_off = None
                if child_data:
                    for off in range(0, min(len(child_data), 0x200), 4):
                        if off + 4 > len(child_data): break
                        ptr = struct.unpack("<I", child_data[off:off+4])[0]
                        if ptr < 0x10000 or ptr > 0x7FFFFFFF: continue
                        s = rdstr(h, ptr + 20, 64)
                        if s and len(s) >= 4 and ("LOGIN_" in s or "EQLS_" in s
                                                   or "Main" in s or "Label" in s
                                                   or "Button" in s or "Edit" in s):
                            name_str = s
                            name_off = off
                            break
                child_vt = rd4(h, c)
                print(f"    [{ci:2d}] CXWnd@0x{c:08X} vt=0x{child_vt:08X} "
                      f"name=\"{name_str}\" at +0x{name_off:X}" if name_str else
                      f"    [{ci:2d}] CXWnd@0x{c:08X} vt=0x{child_vt:08X}")
                ci += 1
                c = rd4(h, c + 0x08)
            break  # found it, stop

    kernel32.CloseHandle(h)

if __name__ == "__main__":
    main()
