using System;
using System.IO.MemoryMappedFiles;
using EQSwitch.Core;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for the KeyInputWriter MMF contract. Invoked via the
/// --test-key-input-writer CLI flag from Program.cs. Guards the hotfix v3
/// write-order (zero keys[] BEFORE active=0) by opening a second MMF view
/// on the same PID and inspecting raw bytes.
///
/// Observer handle is held OPEN across Close/Dispose to prevent the MMF
/// backing object from being released — the tests verify that the final
/// state written by Close/Dispose is "buffer zeroed + active=0".
///
/// Returns 0 on all passes, 1 on any assertion failure. Program.cs maps
/// unhandled exceptions to exit code 2.
/// </summary>
public static class KeyInputWriterTests
{
    // Mirror of KeyInputWriter.HeaderSize + offsets for raw MMF inspection.
    private const int HeaderSize = 20;
    private const int KeysSize = 256;
    private const int ActiveOffset = 8;
    private const string SharedMemoryPrefix = "Local\\EQSwitchDI8_";

    public static int RunAll()
    {
        int failures = 0;
        uint fakePid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id ^ 0xA5A5u;

        // Case 1: Deactivate zeros keys buffer AND clears active
        {
            using var writer = new KeyInputWriter();
            writer.Open((int)fakePid);

            // Open observer BEFORE any Close/Dispose so the mapping lives long enough
            using var observer = OpenObserver(fakePid);

            writer.Activate((int)fakePid);
            writer.SetKey((int)fakePid, 0x1E /* 'a' */, true);

            // Round-trip: verify byte is 0x80 via observer
            byte preDeactivate = ReadKeyByteVia(observer, 0x1E);
            failures += Assert("case1 pre-Deactivate key=0x80", preDeactivate, (byte)0x80);

            writer.Deactivate((int)fakePid);

            // Observer is still open — mapping is live — and we never closed the writer
            // in this case, so read via writer's own handle (observer also works).
            byte postKey = ReadKeyByteVia(observer, 0x1E);
            uint postActive = ReadActiveVia(observer);
            failures += Assert("case1 post-Deactivate key=0x00", postKey, (byte)0x00);
            failures += Assert("case1 post-Deactivate active=0", postActive, (uint)0);

            writer.Close((int)fakePid);
        }

        // Case 2: Close zeros AND deactivates (defense-in-depth)
        {
            using var writer = new KeyInputWriter();
            writer.Open((int)fakePid);

            using var observer = OpenObserver(fakePid);

            writer.Activate((int)fakePid);
            writer.SetKey((int)fakePid, 0x20 /* 'd' */, true);
            writer.SetKey((int)fakePid, 0x48 /* up arrow */, true);
            writer.Close((int)fakePid);

            // Observer keeps the mapping alive past writer's Close — we observe
            // the final state the writer wrote on the way out.
            byte k20 = ReadKeyByteVia(observer, 0x20);
            byte k48 = ReadKeyByteVia(observer, 0x48);
            uint active = ReadActiveVia(observer);
            failures += Assert("case2 post-Close key[0x20]=0", k20, (byte)0x00);
            failures += Assert("case2 post-Close key[0x48]=0", k48, (byte)0x00);
            failures += Assert("case2 post-Close active=0", active, (uint)0);
        }

        // Case 3: SetKey cycle between bursts leaves clean state
        {
            using var writer = new KeyInputWriter();
            writer.Open((int)fakePid);

            using var observer = OpenObserver(fakePid);

            writer.Activate((int)fakePid);
            writer.SetKey((int)fakePid, 0x0D /* '=' */, true);
            writer.SetKey((int)fakePid, 0x0D, false);
            writer.SetKey((int)fakePid, 0x09 /* backspace */, true);
            writer.SetKey((int)fakePid, 0x09, false);
            writer.Deactivate((int)fakePid);

            // Burst 2 starts
            writer.Activate((int)fakePid);

            byte k0D = ReadKeyByteVia(observer, 0x0D);
            byte k09 = ReadKeyByteVia(observer, 0x09);
            failures += Assert("case3 between-bursts key[0x0D]=0", k0D, (byte)0x00);
            failures += Assert("case3 between-bursts key[0x09]=0", k09, (byte)0x00);

            writer.Close((int)fakePid);
        }

        // Case 4: Reactivate zeros during the gap (hotfix v3)
        {
            using var writer = new KeyInputWriter();
            writer.Open((int)fakePid);

            using var observer = OpenObserver(fakePid);

            writer.Activate((int)fakePid);
            writer.SetKey((int)fakePid, 0x1E, true);
            writer.Reactivate((int)fakePid, 10);

            byte key = ReadKeyByteVia(observer, 0x1E);
            uint active = ReadActiveVia(observer);
            failures += Assert("case4 post-Reactivate key[0x1E]=0", key, (byte)0x00);
            failures += Assert("case4 post-Reactivate active=1", active, (uint)1);

            writer.Close((int)fakePid);
        }

        // Case 5: Dispose zeros all mappings
        {
            uint fakePid2 = fakePid + 1;
            var writer = new KeyInputWriter();
            writer.Open((int)fakePid);
            writer.Open((int)fakePid2);

            using var observer1 = OpenObserver(fakePid);
            using var observer2 = OpenObserver(fakePid2);

            writer.Activate((int)fakePid);
            writer.Activate((int)fakePid2);
            writer.SetKey((int)fakePid, 0x1E, true);
            writer.SetKey((int)fakePid2, 0x20, true);
            writer.Dispose();

            // Observers keep the mappings alive — we read the final state
            // Dispose wrote.
            byte k1 = ReadKeyByteVia(observer1, 0x1E);
            uint a1 = ReadActiveVia(observer1);
            byte k2 = ReadKeyByteVia(observer2, 0x20);
            uint a2 = ReadActiveVia(observer2);
            failures += Assert("case5 Dispose pid1 key=0", k1, (byte)0x00);
            failures += Assert("case5 Dispose pid1 active=0", a1, (uint)0);
            failures += Assert("case5 Dispose pid2 key=0", k2, (byte)0x00);
            failures += Assert("case5 Dispose pid2 active=0", a2, (uint)0);
        }

        Console.WriteLine(failures == 0
            ? "KeyInputWriterTests: all 5 cases PASSED"
            : $"KeyInputWriterTests: {failures} assertion failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static MemoryMappedFile OpenObserver(uint pid)
    {
        return MemoryMappedFile.OpenExisting($"{SharedMemoryPrefix}{pid}");
    }

    private static byte ReadKeyByteVia(MemoryMappedFile mmf, byte scan)
    {
        using var accessor = mmf.CreateViewAccessor(0, HeaderSize + KeysSize, MemoryMappedFileAccess.Read);
        return accessor.ReadByte(HeaderSize + scan);
    }

    private static uint ReadActiveVia(MemoryMappedFile mmf)
    {
        using var accessor = mmf.CreateViewAccessor(0, HeaderSize + KeysSize, MemoryMappedFileAccess.Read);
        return accessor.ReadUInt32(ActiveOffset);
    }

    private static int Assert<T>(string name, T actual, T expected)
    {
        if (Equals(actual, expected))
        {
            Console.WriteLine($"    ok: {name}");
            return 0;
        }
        Console.WriteLine($"    FAIL: {name} (expected '{expected}', got '{actual}')");
        return 1;
    }
}
