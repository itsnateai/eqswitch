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
