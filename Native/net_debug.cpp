// net_debug.cpp — Winsock hooks for diagnosing EQ server disconnects
// Hooks send, recv, sendto, recvfrom, closesocket from WS2_32.dll via IAT.
// Logs packet sizes, timing, hex dumps of last packets before disconnect.
// Uses the same IAT patching approach as iat_hook.cpp.

// Avoid winsock.h/winsock2.h conflict — dinput.h already pulled in winsock.h
// via windows.h in other translation units. We declare only what we need.
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include "net_debug.h"

// Winsock types/constants we need (avoiding winsock2.h include entirely)
typedef UINT_PTR SOCKET;
#define SOCKET_ERROR (-1)
#define WSAEWOULDBLOCK 10035
#define WSAECONNRESET  10054
#define AF_INET 2
#define INVALID_SOCKET (~(SOCKET)0)

#ifndef WSAAPI
#define WSAAPI PASCAL
#endif

struct nd_sockaddr { uint16_t sa_family; char sa_data[14]; };
struct nd_sockaddr_in { int16_t sin_family; uint16_t sin_port; uint32_t sin_addr; char sin_zero[8]; };

// Import WSAGetLastError and ntohs dynamically to avoid link conflicts
typedef int (WSAAPI *PFN_WSAGetLastError)();
typedef uint16_t (WSAAPI *PFN_ntohs)(uint16_t);
static PFN_WSAGetLastError g_pfnWSAGetLastError = nullptr;
static PFN_ntohs g_pfnNtohs = nullptr;

static int GetWsaError() { return g_pfnWSAGetLastError ? g_pfnWSAGetLastError() : 0; }
static uint16_t NetToHost16(uint16_t v) { return g_pfnNtohs ? g_pfnNtohs(v) : ((v >> 8) | (v << 8)); }

void DI8Log(const char *fmt, ...);

// --- Original function pointers ---
typedef int (WSAAPI *PFN_send)(SOCKET s, const char *buf, int len, int flags);
typedef int (WSAAPI *PFN_recv)(SOCKET s, char *buf, int len, int flags);
typedef int (WSAAPI *PFN_sendto)(SOCKET s, const char *buf, int len, int flags,
                                  const nd_sockaddr *to, int tolen);
typedef int (WSAAPI *PFN_recvfrom)(SOCKET s, char *buf, int len, int flags,
                                    nd_sockaddr *from, int *fromlen);
typedef int (WSAAPI *PFN_closesocket)(SOCKET s);
typedef int (WSAAPI *PFN_connect)(SOCKET s, const nd_sockaddr *name, int namelen);

static PFN_send       g_realSend       = nullptr;
static PFN_recv       g_realRecv       = nullptr;
static PFN_sendto     g_realSendto     = nullptr;
static PFN_recvfrom   g_realRecvfrom   = nullptr;
static PFN_closesocket g_realClosesocket = nullptr;
static PFN_connect    g_realConnect    = nullptr;

// --- Ring buffer for last N packets (so we can dump context on disconnect) ---
struct PacketEntry {
    DWORD tick;
    SOCKET sock;
    bool isSend;       // true = outgoing, false = incoming
    int size;          // bytes transferred (or SOCKET_ERROR)
    int wsaError;      // WSAGetLastError if error
    uint8_t head[48];  // first 48 bytes of payload
    int headLen;       // actual bytes copied into head[]
};

static const int RING_SIZE = 32;
static PacketEntry g_ring[RING_SIZE];
static int g_ringIdx = 0;       // next write position
static int g_ringCount = 0;     // total entries written

static void RecordPacket(SOCKET s, bool isSend, const char *buf, int result) {
    PacketEntry &e = g_ring[g_ringIdx % RING_SIZE];
    e.tick = GetTickCount();
    e.sock = s;
    e.isSend = isSend;
    e.size = result;
    e.wsaError = (result == SOCKET_ERROR) ? GetWsaError() : 0;
    e.headLen = 0;
    if (buf && result > 0) {
        e.headLen = (result < 48) ? result : 48;
        memcpy(e.head, buf, e.headLen);
    }
    g_ringIdx = (g_ringIdx + 1) % RING_SIZE;
    g_ringCount++;
}

// Format hex dump of buffer (up to maxBytes) into a static string
static const char *HexDump(const uint8_t *buf, int len) {
    static char hex[48 * 3 + 1];  // "XX XX XX ..." for up to 48 bytes
    hex[0] = '\0';
    char *p = hex;
    for (int i = 0; i < len && i < 48; i++) {
        if (i > 0) *p++ = ' ';
        sprintf(p, "%02X", buf[i]);
        p += 2;
    }
    *p = '\0';
    return hex;
}

// Dump the ring buffer to the log (called on closesocket or removal)
static void DumpRingBuffer(const char *reason) {
    int count = (g_ringCount < RING_SIZE) ? g_ringCount : RING_SIZE;
    if (count == 0) return;

    DI8Log("net_debug: === %s — last %d packets ===", reason, count);

    int start = (g_ringCount < RING_SIZE) ? 0 : (g_ringIdx % RING_SIZE);
    for (int i = 0; i < count; i++) {
        int idx = (start + i) % RING_SIZE;
        PacketEntry &e = g_ring[idx];
        const char *dir = e.isSend ? "SEND" : "RECV";
        if (e.size == SOCKET_ERROR) {
            DI8Log("  [%lu] sock=%llu %s ERROR wsaErr=%d",
                   e.tick, (unsigned long long)e.sock, dir, e.wsaError);
        } else {
            DI8Log("  [%lu] sock=%llu %s %d bytes: %s",
                   e.tick, (unsigned long long)e.sock, dir, e.size,
                   e.headLen > 0 ? HexDump(e.head, e.headLen) : "(empty)");
        }
    }
    DI8Log("net_debug: === end ring dump ===");
}

// --- Counters for summary ---
static volatile LONG g_totalSend = 0;
static volatile LONG g_totalRecv = 0;
static volatile LONG g_totalSendBytes = 0;
static volatile LONG g_totalRecvBytes = 0;

// --- Hook implementations ---

static int WSAAPI HookedSend(SOCKET s, const char *buf, int len, int flags) {
    int result = g_realSend(s, buf, len, flags);
    RecordPacket(s, true, buf, result);
    if (result > 0) {
        InterlockedIncrement(&g_totalSend);
        InterlockedAdd(&g_totalSendBytes, result);
    } else if (result == SOCKET_ERROR) {
        DI8Log("net_debug: send() FAILED sock=%llu len=%d err=%d",
               (unsigned long long)s, len, GetWsaError());
    }
    return result;
}

static int WSAAPI HookedRecv(SOCKET s, char *buf, int len, int flags) {
    int result = g_realRecv(s, buf, len, flags);
    RecordPacket(s, false, buf, result);
    if (result > 0) {
        InterlockedIncrement(&g_totalRecv);
        InterlockedAdd(&g_totalRecvBytes, result);
    } else if (result == 0) {
        DI8Log("net_debug: recv() returned 0 — graceful close by server (sock=%llu)",
               (unsigned long long)s);
        DumpRingBuffer("GRACEFUL CLOSE (recv=0)");
    } else {
        int err = GetWsaError();
        if (err != WSAEWOULDBLOCK) {
            DI8Log("net_debug: recv() FAILED sock=%llu err=%d", (unsigned long long)s, err);
            DumpRingBuffer("RECV ERROR");
        }
    }
    return result;
}

static int WSAAPI HookedSendto(SOCKET s, const char *buf, int len, int flags,
                                const nd_sockaddr *to, int tolen) {
    int result = g_realSendto(s, buf, len, flags, to, tolen);
    RecordPacket(s, true, buf, result);
    if (result > 0) {
        InterlockedIncrement(&g_totalSend);
        InterlockedAdd(&g_totalSendBytes, result);
    }
    return result;
}

static int WSAAPI HookedRecvfrom(SOCKET s, char *buf, int len, int flags,
                                  nd_sockaddr *from, int *fromlen) {
    int result = g_realRecvfrom(s, buf, len, flags, from, fromlen);
    RecordPacket(s, false, buf, result);
    if (result > 0) {
        InterlockedIncrement(&g_totalRecv);
        InterlockedAdd(&g_totalRecvBytes, result);
    } else if (result == SOCKET_ERROR) {
        int err = GetWsaError();
        if (err != WSAEWOULDBLOCK && err != WSAECONNRESET) {
            DI8Log("net_debug: recvfrom() FAILED sock=%llu err=%d", (unsigned long long)s, err);
        }
    }
    return result;
}

static int WSAAPI HookedClosesocket(SOCKET s) {
    DI8Log("net_debug: closesocket(sock=%llu) — totals: %ld sends (%ld bytes), %ld recvs (%ld bytes)",
           (unsigned long long)s, g_totalSend, g_totalSendBytes, g_totalRecv, g_totalRecvBytes);
    DumpRingBuffer("CLOSESOCKET");
    return g_realClosesocket(s);
}

static int WSAAPI HookedConnect(SOCKET s, const nd_sockaddr *name, int namelen) {
    // Log the IP:port being connected to
    if (name && name->sa_family == AF_INET && namelen >= (int)sizeof(nd_sockaddr_in)) {
        const nd_sockaddr_in *sin = (const nd_sockaddr_in *)name;
        const unsigned char *ip = (const unsigned char *)&sin->sin_addr;
        DI8Log("net_debug: connect(sock=%llu) -> %d.%d.%d.%d:%d",
               (unsigned long long)s, ip[0], ip[1], ip[2], ip[3], NetToHost16(sin->sin_port));
    }
    int result = g_realConnect(s, name, namelen);
    if (result == SOCKET_ERROR) {
        int err = GetWsaError();
        if (err != WSAEWOULDBLOCK)  // non-blocking connect returns WOULDBLOCK
            DI8Log("net_debug: connect() FAILED err=%d", err);
    } else {
        DI8Log("net_debug: connect() succeeded");
    }
    return result;
}

// --- IAT patching by ordinal (EQ imports Winsock functions by ordinal, not name) ---
// WS2_32.dll ordinals: send=19, recv=16, sendto=20, recvfrom=17,
//                      closesocket=3, connect=4

struct NetPatchEntry { uint32_t *slot; uint32_t original; };
static NetPatchEntry g_netPatches[12];
static int g_netPatchCount = 0;

// Patch IAT entry matching a specific ordinal import.
// Returns the original function pointer, or nullptr if not found.
static void *PatchNetIatByOrdinal(const uint8_t *base, const char *targetDll,
                                   uint16_t targetOrdinal, void *newFn) {
    int32_t eLfanew = *(int32_t *)(base + 0x3C);
    const uint8_t *optHeader = base + eLfanew + 24;

    uint16_t magic = *(uint16_t *)optHeader;
    if (magic != 0x010B) return nullptr;  // PE32 only

    uint32_t importDirRva = *(uint32_t *)(optHeader + 104);
    if (importDirRva == 0) return nullptr;

    const uint8_t *desc = base + importDirRva;
    while (true) {
        uint32_t nameRva = *(uint32_t *)(desc + 12);
        if (nameRva == 0) break;

        const char *dllName = (const char *)(base + nameRva);
        if (_stricmp(dllName, targetDll) == 0) {
            uint32_t origFirstThunk = *(uint32_t *)desc;
            uint32_t firstThunkRva  = *(uint32_t *)(desc + 16);

            if (origFirstThunk == 0) {
                DI8Log("net_debug: matched '%s' but origFirstThunk=0, skipping", dllName);
                desc += 20; continue;
            }

            const uint32_t *orig = (const uint32_t *)(base + origFirstThunk);
            uint32_t *thunk = (uint32_t *)(base + firstThunkRva);

            // Log what we find in this DLL's import table
            int entryIdx = 0;
            while (*orig) {
                if ((*orig & 0x80000000) != 0) {
                    uint16_t ordinal = (uint16_t)(*orig & 0xFFFF);
                    if (entryIdx < 3 || ordinal == targetOrdinal)
                        DI8Log("net_debug: IAT[%d] ordinal=%d (looking for %d) thunk=%p",
                               entryIdx, ordinal, targetOrdinal, thunk);
                    if (ordinal == targetOrdinal) {
                        void *original = (void *)(uintptr_t)*thunk;
                        DWORD oldProtect, dummy;
                        VirtualProtect(thunk, 4, PAGE_READWRITE, &oldProtect);
                        *thunk = (uint32_t)(uintptr_t)newFn;
                        VirtualProtect(thunk, 4, oldProtect, &dummy);
                        if (g_netPatchCount < 12) {
                            g_netPatches[g_netPatchCount].slot = thunk;
                            g_netPatches[g_netPatchCount].original = (uint32_t)(uintptr_t)original;
                            g_netPatchCount++;
                        }
                        return original;
                    }
                } else {
                    // Name-based import — log first few to diagnose
                    if (entryIdx < 3) {
                        const char *fnName = (const char *)(base + *orig + 2);
                        DI8Log("net_debug: IAT[%d] by-name: '%s' (not ordinal)", entryIdx, fnName);
                    }
                }
                entryIdx++;
                orig++;
                thunk++;
            }
        }
        desc += 20;
    }
    return nullptr;
}

// --- Public API ---

void NetDebug::Install() {
    DI8Log("net_debug: Install() entered");

    __try {
    // Resolve WSAGetLastError and ntohs from WS2_32.dll (already loaded by EQ)
    HMODULE ws2 = GetModuleHandleA("WS2_32.dll");
    if (!ws2) ws2 = GetModuleHandleA("ws2_32.dll");
    DI8Log("net_debug: ws2_32 handle=%p", ws2);
    if (ws2) {
        g_pfnWSAGetLastError = (PFN_WSAGetLastError)GetProcAddress(ws2, "WSAGetLastError");
        g_pfnNtohs = (PFN_ntohs)GetProcAddress(ws2, "ntohs");
    }

    HMODULE base = GetModuleHandleW(nullptr);
    if (!base) { DI8Log("net_debug: base is null, aborting"); return; }
    const uint8_t *basePtr = (const uint8_t *)base;

    int hooked = 0;
    void *p;

    DI8Log("net_debug: Install() starting, base=%p", basePtr);

    // Dump all DLL names in import table so we can see exact casing
    {
        int32_t elf = *(int32_t *)(basePtr + 0x3C);
        const uint8_t *oh = basePtr + elf + 24;
        uint32_t idRva = *(uint32_t *)(oh + 104);
        if (idRva) {
            const uint8_t *d = basePtr + idRva;
            while (true) {
                uint32_t nRva = *(uint32_t *)(d + 12);
                if (nRva == 0) break;
                const char *dn = (const char *)(basePtr + nRva);
                // Log DLLs that look like winsock
                if (_strnicmp(dn, "ws", 2) == 0 || _strnicmp(dn, "WS", 2) == 0)
                    DI8Log("net_debug: import DLL: '%s'", dn);
                d += 20;
            }
        }
    }

    // WS2_32.dll ordinals (EQ imports by ordinal, not name):
    //   3=closesocket, 4=connect, 16=recv, 17=recvfrom, 19=send, 20=sendto
    const char *dll = "WS2_32.dll";

    p = PatchNetIatByOrdinal(basePtr, dll, 19, (void *)HookedSend);
    if (p) { g_realSend = (PFN_send)p; hooked++; DI8Log("net_debug: hooked send (ord 19)"); }

    p = PatchNetIatByOrdinal(basePtr, dll, 16, (void *)HookedRecv);
    if (p) { g_realRecv = (PFN_recv)p; hooked++; DI8Log("net_debug: hooked recv (ord 16)"); }

    p = PatchNetIatByOrdinal(basePtr, dll, 20, (void *)HookedSendto);
    if (p) { g_realSendto = (PFN_sendto)p; hooked++; DI8Log("net_debug: hooked sendto (ord 20)"); }

    p = PatchNetIatByOrdinal(basePtr, dll, 17, (void *)HookedRecvfrom);
    if (p) { g_realRecvfrom = (PFN_recvfrom)p; hooked++; DI8Log("net_debug: hooked recvfrom (ord 17)"); }

    p = PatchNetIatByOrdinal(basePtr, dll, 3, (void *)HookedClosesocket);
    if (p) { g_realClosesocket = (PFN_closesocket)p; hooked++; DI8Log("net_debug: hooked closesocket (ord 3)"); }

    p = PatchNetIatByOrdinal(basePtr, dll, 4, (void *)HookedConnect);
    if (p) { g_realConnect = (PFN_connect)p; hooked++; DI8Log("net_debug: hooked connect (ord 4)"); }

    // Also try WSOCK32.dll with same ordinals (EQ has both in its imports)
    const char *dll2 = "WSOCK32.dll";
    if (!g_realSend) {
        p = PatchNetIatByOrdinal(basePtr, dll2, 19, (void *)HookedSend);
        if (p) { g_realSend = (PFN_send)p; hooked++; DI8Log("net_debug: hooked send (WSOCK32 ord 19)"); }
    }
    if (!g_realRecv) {
        p = PatchNetIatByOrdinal(basePtr, dll2, 16, (void *)HookedRecv);
        if (p) { g_realRecv = (PFN_recv)p; hooked++; DI8Log("net_debug: hooked recv (WSOCK32 ord 16)"); }
    }

    DI8Log("net_debug: %d Winsock function(s) hooked", hooked);

    } __except(EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("net_debug: CRASH in Install() — exception 0x%08X", GetExceptionCode());
    }
}

void NetDebug::Remove() {
    // Dump final summary
    DI8Log("net_debug: final totals — %ld sends (%ld bytes), %ld recvs (%ld bytes)",
           g_totalSend, g_totalSendBytes, g_totalRecv, g_totalRecvBytes);

    // Restore IAT entries
    for (int i = 0; i < g_netPatchCount; i++) {
        DWORD oldProtect, dummy;
        VirtualProtect(g_netPatches[i].slot, 4, PAGE_READWRITE, &oldProtect);
        *g_netPatches[i].slot = g_netPatches[i].original;
        VirtualProtect(g_netPatches[i].slot, 4, oldProtect, &dummy);
    }
    DI8Log("net_debug: restored %d IAT entries", g_netPatchCount);
    g_netPatchCount = 0;
}
