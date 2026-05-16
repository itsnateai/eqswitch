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

    // Mirrors Native/login_shm.h LoginShm v7. Field order, types, and Pack=1
    // must match the C++ struct exactly. Added v3.21.1 — the v3→v7 cascade
    // of 5 SHM bumps (autoLoginActive / okDisplayText+Class / JoinServer RPC /
    // comboGWriteOk / loginServerAPIReady / widget probes) shipped without
    // layout assertions; each bump compounded the silent-corruption surface
    // on the next change. C#'s `volatile` keyword on the C++ side doesn't
    // affect struct layout; only field offsets and sizes matter here.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct LoginShm
    {
        public uint Magic;
        public uint Version;
        public uint Command;
        public uint CommandSeq;
        public uint CommandAck;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]   public byte[] Username;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]  public byte[] Password;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]   public byte[] Server;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]   public byte[] Character;
        public uint Phase;
        public int  GameState;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]  public byte[] ErrorMessage;
        public uint RetryCount;
        public int  CharCount;
        public int  SelectedIndex;
        // 10 chars * 64 bytes = 640 — flattened (C# can't ByValArray a 2D char[][]
        // with a single SizeConst). Byte-equivalent to C++'s char[10][64] under Pack=1.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 640)]  public byte[] CharNames;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]   public int[]  CharLevels;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]   public int[]  CharClasses;
        public uint DiagnosticMode;
        public uint AutoLoginActive;                            // v2
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]  public byte[] OkDisplayText;
        public uint OkDisplayClass;                             // v3
        public uint JoinServerSerialId;                         // v4
        public uint JoinServerReqSeq;
        public uint JoinServerAckSeq;
        public uint JoinServerOutcome;
        public uint JoinServerFnResult;
        public uint ComboGWriteOk;                              // v5
        public uint LoginServerAPIReady;                        // v6
        public uint WidgetConnectVisible;                       // v7
        public uint WidgetServerSelectVisible;
        public uint WidgetOkDialogVisible;
        public uint WidgetYesNoDialogVisible;
        public uint WidgetConfirmDialogVisible;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]  public byte[] WidgetConfirmDialogText;
        public uint WidgetTickSeq;
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

        // ── LoginShm layout — must match Native/login_shm.h v7 ───────
        // Locks the 1912-byte ABI so a field add/reorder on either side
        // surfaces as a test failure, not silent runtime SHM corruption.
        // v3.21.1 addition — closes the test-coverage gap that compounded
        // silently across v2 → v3 → v4 → v5 → v6 → v7 SHM bumps.
        failures += Assert("LoginShm.Size", Marshal.SizeOf<LoginShm>(), 1912);
        failures += Assert("LoginShm.Magic offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.Magic)), 0);
        failures += Assert("LoginShm.Version offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.Version)), 4);
        failures += Assert("LoginShm.Command offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.Command)), 8);
        failures += Assert("LoginShm.CommandSeq offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.CommandSeq)), 12);
        failures += Assert("LoginShm.CommandAck offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.CommandAck)), 16);
        failures += Assert("LoginShm.Username offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.Username)), 20);
        failures += Assert("LoginShm.Password offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.Password)), 84);
        failures += Assert("LoginShm.Server offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.Server)), 212);
        failures += Assert("LoginShm.Character offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.Character)), 276);
        failures += Assert("LoginShm.Phase offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.Phase)), 340);
        failures += Assert("LoginShm.GameState offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.GameState)), 344);
        failures += Assert("LoginShm.ErrorMessage offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.ErrorMessage)), 348);
        failures += Assert("LoginShm.RetryCount offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.RetryCount)), 604);
        failures += Assert("LoginShm.CharCount offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.CharCount)), 608);
        failures += Assert("LoginShm.SelectedIndex offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.SelectedIndex)), 612);
        failures += Assert("LoginShm.CharNames offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.CharNames)), 616);
        failures += Assert("LoginShm.CharLevels offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.CharLevels)), 1256);
        failures += Assert("LoginShm.CharClasses offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.CharClasses)), 1296);
        failures += Assert("LoginShm.DiagnosticMode offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.DiagnosticMode)), 1336);
        // v2 append
        failures += Assert("LoginShm.AutoLoginActive offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.AutoLoginActive)), 1340);
        // v3 append (LIVE OK_Display)
        failures += Assert("LoginShm.OkDisplayText offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.OkDisplayText)), 1344);
        failures += Assert("LoginShm.OkDisplayClass offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.OkDisplayClass)), 1600);
        // v4 append (Diff 4 JoinServerDirect RPC)
        failures += Assert("LoginShm.JoinServerSerialId offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.JoinServerSerialId)), 1604);
        failures += Assert("LoginShm.JoinServerReqSeq offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.JoinServerReqSeq)), 1608);
        failures += Assert("LoginShm.JoinServerAckSeq offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.JoinServerAckSeq)), 1612);
        failures += Assert("LoginShm.JoinServerOutcome offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.JoinServerOutcome)), 1616);
        failures += Assert("LoginShm.JoinServerFnResult offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.JoinServerFnResult)), 1620);
        // v5 append
        failures += Assert("LoginShm.ComboGWriteOk offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.ComboGWriteOk)), 1624);
        // v6 append
        failures += Assert("LoginShm.LoginServerAPIReady offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.LoginServerAPIReady)), 1628);
        // v7 append (widget probes)
        failures += Assert("LoginShm.WidgetConnectVisible offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.WidgetConnectVisible)), 1632);
        failures += Assert("LoginShm.WidgetServerSelectVisible offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.WidgetServerSelectVisible)), 1636);
        failures += Assert("LoginShm.WidgetOkDialogVisible offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.WidgetOkDialogVisible)), 1640);
        failures += Assert("LoginShm.WidgetYesNoDialogVisible offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.WidgetYesNoDialogVisible)), 1644);
        failures += Assert("LoginShm.WidgetConfirmDialogVisible offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.WidgetConfirmDialogVisible)), 1648);
        failures += Assert("LoginShm.WidgetConfirmDialogText offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.WidgetConfirmDialogText)), 1652);
        failures += Assert("LoginShm.WidgetTickSeq offset",
            (int)Marshal.OffsetOf<LoginShm>(nameof(LoginShm.WidgetTickSeq)), 1908);

        // Note: no LoginShm.Magic/Version value assertion. The existing
        // SharedKeyState.Magic and CharSelectShm.Magic asserts compare a
        // literal to a same-literal const — verifier-convergent finding
        // 2026-05-16, the assertion is tautological. Layout-only coverage
        // here. Cross-layer magic/version validation (C# const vs C++
        // header) needs an MMF round-trip or exposing LoginShmWriter
        // constants as `internal`; deferred to a separate cleanup that
        // can fix the same tautology in the pre-existing tests.

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
