// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Cross-language struct-layout tests. Asserts that C#'s SharedKeyState layout
/// matches the native SharedKeyState in Native/key_shm.h. A refactor that adds
/// or reorders a field on either side without mirroring on the other side
/// would produce silent byte-level corruption at runtime — this test fails fast.
///
/// Returns 0 on all passes, 1 on any failure.
/// </summary>
public static class ShmLayoutTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SharedKeyState
    {
        public uint Magic;
        public uint Version;
        public uint Active;
        public uint Suppress;
        public uint Seq;
    }

    // Mirrors Native/mq2_bridge.h CharSelectShm. Field order, types, and
    // Pack=1 must match the C++ struct exactly. v3 added charSelectReady.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct CharSelectShm
    {
        public uint Magic;
        public uint Version;
        public int  GameState;
        public int  CharCount;
        public int  SelectedIndex;
        public uint Mq2Available;
        public int  RequestedIndex;
        public uint RequestSeq;
        public uint AckSeq;
        public uint EnterWorldReq;
        public uint EnterWorldAck;
        public int  EnterWorldResult;
        // 10 chars * 64 bytes = 640
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 640)]
        public byte[] Names;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public int[]  Levels;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public int[]  Classes;
        public uint CharSelectReady;
    }

    public static int RunAll()
    {
        int failures = 0;

        // SharedKeyState layout — must match Native/key_shm.h
        failures += Assert("SharedKeyState.Size", Marshal.SizeOf<SharedKeyState>(), 20);
        failures += Assert("SharedKeyState.Magic offset",
            (int)Marshal.OffsetOf<SharedKeyState>(nameof(SharedKeyState.Magic)), 0);
        failures += Assert("SharedKeyState.Version offset",
            (int)Marshal.OffsetOf<SharedKeyState>(nameof(SharedKeyState.Version)), 4);
        failures += Assert("SharedKeyState.Active offset",
            (int)Marshal.OffsetOf<SharedKeyState>(nameof(SharedKeyState.Active)), 8);
        failures += Assert("SharedKeyState.Suppress offset",
            (int)Marshal.OffsetOf<SharedKeyState>(nameof(SharedKeyState.Suppress)), 12);
        failures += Assert("SharedKeyState.Seq offset",
            (int)Marshal.OffsetOf<SharedKeyState>(nameof(SharedKeyState.Seq)), 16);

        // Magic value must match native KEY_SHM_MAGIC
        const uint ExpectedMagic = 0x45534B53; // "ESKS"
        failures += Assert("SharedKeyState.Magic value", 0x45534B53u, ExpectedMagic);

        // CharSelectShm layout — must match Native/mq2_bridge.h. Locks the v3
        // (772-byte) ABI so a future field add/reorder on either side surfaces
        // as a test failure, not silent runtime SHM corruption.
        failures += Assert("CharSelectShm.Size", Marshal.SizeOf<CharSelectShm>(), 772);
        failures += Assert("CharSelectShm.Magic offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.Magic)), 0);
        failures += Assert("CharSelectShm.Version offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.Version)), 4);
        failures += Assert("CharSelectShm.GameState offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.GameState)), 8);
        failures += Assert("CharSelectShm.CharCount offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.CharCount)), 12);
        failures += Assert("CharSelectShm.SelectedIndex offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.SelectedIndex)), 16);
        failures += Assert("CharSelectShm.Mq2Available offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.Mq2Available)), 20);
        failures += Assert("CharSelectShm.RequestedIndex offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.RequestedIndex)), 24);
        failures += Assert("CharSelectShm.RequestSeq offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.RequestSeq)), 28);
        failures += Assert("CharSelectShm.AckSeq offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.AckSeq)), 32);
        failures += Assert("CharSelectShm.EnterWorldReq offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.EnterWorldReq)), 36);
        failures += Assert("CharSelectShm.EnterWorldAck offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.EnterWorldAck)), 40);
        failures += Assert("CharSelectShm.EnterWorldResult offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.EnterWorldResult)), 44);
        failures += Assert("CharSelectShm.Names offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.Names)), 48);
        failures += Assert("CharSelectShm.Levels offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.Levels)), 688);
        failures += Assert("CharSelectShm.Classes offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.Classes)), 728);
        failures += Assert("CharSelectShm.CharSelectReady offset",
            (int)Marshal.OffsetOf<CharSelectShm>(nameof(CharSelectShm.CharSelectReady)), 768);

        // Magic value must match CHARSEL_SHM_MAGIC
        const uint ExpectedCharSelMagic = 0x45534353; // "ESCS"
        failures += Assert("CharSelectShm.Magic value", 0x45534353u, ExpectedCharSelMagic);

        Console.WriteLine(failures == 0
            ? "ShmLayoutTests: all assertions PASSED"
            : $"ShmLayoutTests: {failures} assertion failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static int Assert<T>(string name, T actual, T expected) where T : IEquatable<T>
    {
        if (actual.Equals(expected))
        {
            Console.WriteLine($"    ok: {name}");
            return 0;
        }
        Console.WriteLine($"    FAIL: {name} (expected '{expected}', got '{actual}')");
        return 1;
    }
}
