// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for the C# half of the v3.15.1 char-select gate / latch / single-char
/// fallback. The bridge side is exercised in-process by attaching a second
/// MemoryMappedFile view to the same "Local\EQSwitchCharSel_{PID}" name that
/// CharSelectReader.Open creates, then writing values that simulate what
/// mq2_bridge.cpp would publish in each scenario.
///
/// Scenarios covered:
///  1. Open() against a non-running fake PID produces a clean header (zeroed counts,
///     mq2Available=false, charSelectReady=false).
///  2. AND-gate: charCount &gt; 0 alone does NOT signal "ready" — mq2Available
///     and charSelectReady must also be set. Closes the v3.15.0 Path B2 race.
///  3. SHM v3 latch (charSelectReady) survives a transient charCount==0 (pinst flutter).
///  4. "Slot N" placeholder names are visible to the reader exactly as written —
///     CharSelectReader has no opinion on placeholder vs. real-name disambiguation
///     (that lives in the AutoLoginManager / CharacterSelector layer); test asserts
///     the byte-faithful read so future placeholder-classification logic has a
///     stable substrate.
///  5. Recycled-PID safety: a second Open() on the same PID re-zeroes the header
///     even when the prior session left charSelectReady=1 + non-empty names. The
///     ResetShmHeader extraction on existing-mapping path is the v3.15.1 fix.
///  6. Single-char-by-elimination: charCount=1 + a real name + selection request
///     for slot 1 is the structural fallback path. Test verifies the reader's
///     RequestSelection writes back to the SHM with monotonically increasing
///     RequestSeq.
///
/// Run via: EQSwitch.exe --test-charselect-reader (DEBUG build).
/// Returns 0 on all passes, &gt;0 on any failure.
/// </summary>
public static class CharSelectReaderTests
{
    private const string SharedMemoryPrefix = "Local\\EQSwitchCharSel_";
    private const int ShmSize = 772;
    private const uint Magic = 0x45534353; // "ESCS"
    private const int OFF_CHARCOUNT = 12;
    private const int OFF_MQ2AVAILABLE = 20;
    private const int OFF_REQUESTEDINDEX = 24;
    private const int OFF_REQUESTSEQ = 28;
    private const int OFF_NAMES = 48;
    private const int NameLen = 64;
    private const int OFF_CHARSELREADY = 768;

    public static int RunAll()
    {
        int failures = 0;

        failures += Run("clean Open zeroes header", TestCleanOpen);
        failures += Run("AND-gate: count>0 alone is not ready", TestAndGate);
        failures += Run("latch survives transient charCount=0", TestLatchSurvivesFlutter);
        failures += Run("placeholder 'Slot N' read byte-faithful", TestSlotPlaceholderRead);
        failures += Run("recycled-PID re-zeroes header on Open", TestRecycledPidReset);
        failures += Run("single-char structural fallback select+ack", TestSingleCharSelectFallback);
        failures += Run("ReadAllCharNames clamps to charCount", TestReadAllCharNamesClamp);
        failures += Run("ReadCharName out-of-range returns empty", TestReadCharNameBounds);

        Console.WriteLine(failures == 0
            ? "CharSelectReaderTests: all assertions PASSED"
            : $"CharSelectReaderTests: {failures} test(s) FAILED");
        return failures;
    }

    // ─── Tests ───────────────────────────────────────────────────────

    private static void TestCleanOpen()
    {
        int pid = TakeFakePid();
        using var reader = new CharSelectReader();
        AssertTrue("Open returned true", reader.Open(pid));
        AssertEqual("clean charCount", 0, reader.ReadCharCount(pid));
        AssertEqual("clean mq2Available", false, reader.IsMQ2Available(pid));
        AssertEqual("clean charSelectReady", false, reader.IsCharSelectReady(pid));
        AssertEqual("clean ReadAllCharNames length", 0, reader.ReadAllCharNames(pid).Length);
    }

    /// <summary>
    /// v3.15.1 closed the Path B2 placeholder race by AND-gating
    /// `mq2Available && charSelectReady && charCount > 0`. The reader exposes
    /// each as a separate query — test asserts each independent state and
    /// that the conjunction is what the caller must check, not any single one.
    /// </summary>
    private static void TestAndGate()
    {
        int pid = TakeFakePid();
        using var reader = new CharSelectReader();
        AssertTrue("Open returned true", reader.Open(pid));

        using (var writer = AttachWriter(pid))
        {
            // count=2 alone — mq2 unset, latch unset
            writer.Accessor.Write(OFF_CHARCOUNT, 2);
            WriteAsciiName(writer, 0, "Natedogg");
            WriteAsciiName(writer, 1, "Gotquiz");
            AssertEqual("count seen", 2, reader.ReadCharCount(pid));
            AssertEqual("mq2 still false", false, reader.IsMQ2Available(pid));
            AssertEqual("latch still false", false, reader.IsCharSelectReady(pid));
            AssertEqual("ALL gate is OFF",
                false, reader.IsMQ2Available(pid) && reader.IsCharSelectReady(pid) && reader.ReadCharCount(pid) > 0);

            // mq2=1, latch still 0
            writer.Accessor.Write(OFF_MQ2AVAILABLE, (uint)1);
            AssertEqual("ALL gate still OFF (no latch)",
                false, reader.IsMQ2Available(pid) && reader.IsCharSelectReady(pid) && reader.ReadCharCount(pid) > 0);

            // latch set — now all three true
            writer.Accessor.Write(OFF_CHARSELREADY, (uint)1);
            AssertEqual("ALL gate ON",
                true, reader.IsMQ2Available(pid) && reader.IsCharSelectReady(pid) && reader.ReadCharCount(pid) > 0);
        }
    }

    /// <summary>
    /// SHM v3's monotonic latch is the headline fix for the v3.15.0 pinst-flutter
    /// race: even if charCount transiently drops to 0 between polls (heap reuse,
    /// cache rebuild), the latch stays set so AutoLoginManager doesn't restart
    /// the 30s "waiting for charlist" gate.
    /// </summary>
    private static void TestLatchSurvivesFlutter()
    {
        int pid = TakeFakePid();
        using var reader = new CharSelectReader();
        AssertTrue("Open returned true", reader.Open(pid));

        using var writer = AttachWriter(pid);
        writer.Accessor.Write(OFF_MQ2AVAILABLE, (uint)1);
        writer.Accessor.Write(OFF_CHARSELREADY, (uint)1);
        writer.Accessor.Write(OFF_CHARCOUNT, 3);
        WriteAsciiName(writer, 0, "Natedogg");
        WriteAsciiName(writer, 1, "Gotquiz");
        WriteAsciiName(writer, 2, "Auburn");

        AssertEqual("initial count=3", 3, reader.ReadCharCount(pid));
        AssertEqual("initial latch true", true, reader.IsCharSelectReady(pid));

        // Pinst flutters — bridge transiently sees charCount=0 (cache stale, rescan)
        writer.Accessor.Write(OFF_CHARCOUNT, 0);
        AssertEqual("transient count=0", 0, reader.ReadCharCount(pid));
        AssertEqual("latch survives flutter", true, reader.IsCharSelectReady(pid));

        // Bridge re-publishes count after rescan
        writer.Accessor.Write(OFF_CHARCOUNT, 3);
        AssertEqual("count restored", 3, reader.ReadCharCount(pid));
    }

    /// <summary>
    /// Bridge can publish "Slot N" placeholders before real names resolve.
    /// CharSelectReader has no opinion on placeholder vs. real — it returns
    /// the bytes as written. AutoLoginManager / CharacterSelector layer
    /// classifies. Test pins the reader's byte-faithfulness so future
    /// placeholder logic has a stable substrate.
    /// </summary>
    private static void TestSlotPlaceholderRead()
    {
        int pid = TakeFakePid();
        using var reader = new CharSelectReader();
        AssertTrue("Open returned true", reader.Open(pid));

        using var writer = AttachWriter(pid);
        writer.Accessor.Write(OFF_MQ2AVAILABLE, (uint)1);
        // NOTE: bridge does NOT set charSelectReady on placeholder publish (per v3.15.1
        // spec — see project_eqswitch_v3151_shipped.md companion fix #1). We simulate
        // exactly that: count > 0, names = placeholders, latch unset.
        writer.Accessor.Write(OFF_CHARCOUNT, 1);
        WriteAsciiName(writer, 0, "Slot 1");

        AssertEqual("count=1", 1, reader.ReadCharCount(pid));
        AssertEqual("latch still false (placeholder publish doesn't set latch)",
            false, reader.IsCharSelectReady(pid));
        AssertEqual("name reads as 'Slot 1' verbatim", "Slot 1", reader.ReadCharName(pid, 0));

        // The AND-gate excludes this (latch is the load-bearing flag).
        AssertEqual("ALL gate refuses placeholder cycle",
            false, reader.IsMQ2Available(pid) && reader.IsCharSelectReady(pid) && reader.ReadCharCount(pid) > 0);
    }

    /// <summary>
    /// Recycled-PID safety: a prior session that crashed without ClientLost firing
    /// can leave a dangling MappingEntry in the dictionary. v3.15.1 extracted
    /// ResetShmHeader so the existing-mapping Open() path re-zeroes the header.
    /// Without this fix, the new session would see the prior session's
    /// charSelectReady=1 + names and skip the autologin handshake.
    /// </summary>
    private static void TestRecycledPidReset()
    {
        int pid = TakeFakePid();
        using var reader = new CharSelectReader();

        // First session: open + populate + leave dirty
        AssertTrue("Open #1", reader.Open(pid));
        using (var writer = AttachWriter(pid))
        {
            writer.Accessor.Write(OFF_MQ2AVAILABLE, (uint)1);
            writer.Accessor.Write(OFF_CHARSELREADY, (uint)1);
            writer.Accessor.Write(OFF_CHARCOUNT, 4);
            WriteAsciiName(writer, 0, "DirtySession");
        }
        AssertEqual("dirty latch=true", true, reader.IsCharSelectReady(pid));
        AssertEqual("dirty count=4", 4, reader.ReadCharCount(pid));

        // Second Open() on same PID — must re-zero
        AssertTrue("Open #2 (recycled)", reader.Open(pid));
        AssertEqual("recycled latch reset", false, reader.IsCharSelectReady(pid));
        AssertEqual("recycled count reset", 0, reader.ReadCharCount(pid));
        AssertEqual("recycled mq2 reset", false, reader.IsMQ2Available(pid));
    }

    /// <summary>
    /// v3.15.1's single-char structural fallback: when count==1 and the name
    /// target is set but no explicit slot, pick slot 1 by elimination.
    /// Test exercises RequestSelection at the reader layer: the request is
    /// ack'd via RequestSeq round-trip when the simulated bridge mirrors
    /// requestSeq → ackSeq.
    /// </summary>
    private static void TestSingleCharSelectFallback()
    {
        int pid = TakeFakePid();
        using var reader = new CharSelectReader();
        AssertTrue("Open returned true", reader.Open(pid));

        using var writer = AttachWriter(pid);
        writer.Accessor.Write(OFF_MQ2AVAILABLE, (uint)1);
        writer.Accessor.Write(OFF_CHARSELREADY, (uint)1);
        writer.Accessor.Write(OFF_CHARCOUNT, 1);
        WriteAsciiName(writer, 0, "Natedogg");

        AssertEqual("ack false before any request", false, reader.IsSelectionAcknowledged(pid));

        // Request selection at slot 1 (1-based) → index 0
        AssertTrue("RequestSelectionBySlot(1)", reader.RequestSelectionBySlot(pid, 1));
        AssertEqual("ack still false (bridge hasn't ack'd yet)", false, reader.IsSelectionAcknowledged(pid));

        // Simulate bridge: mirror requestSeq into ackSeq
        uint reqSeq = ReadUInt32(writer, OFF_REQUESTSEQ);
        AssertTrue("requestSeq advanced", reqSeq == 1);
        AssertEqual("requestedIndex written = 0", 0, ReadInt32(writer, OFF_REQUESTEDINDEX));

        const int OFF_ACKSEQ = 32;
        writer.Accessor.Write(OFF_ACKSEQ, reqSeq);
        AssertEqual("ack true after bridge mirror", true, reader.IsSelectionAcknowledged(pid));
    }

    private static void TestReadAllCharNamesClamp()
    {
        int pid = TakeFakePid();
        using var reader = new CharSelectReader();
        AssertTrue("Open returned true", reader.Open(pid));

        using var writer = AttachWriter(pid);
        writer.Accessor.Write(OFF_CHARCOUNT, 2);
        WriteAsciiName(writer, 0, "First");
        WriteAsciiName(writer, 1, "Second");
        WriteAsciiName(writer, 2, "ShouldNotAppear");  // beyond count, must be ignored

        var names = reader.ReadAllCharNames(pid);
        AssertEqual("ReadAllCharNames length matches count", 2, names.Length);
        AssertEqual("[0]", "First", names[0]);
        AssertEqual("[1]", "Second", names[1]);
    }

    private static void TestReadCharNameBounds()
    {
        int pid = TakeFakePid();
        using var reader = new CharSelectReader();
        AssertTrue("Open returned true", reader.Open(pid));
        AssertEqual("idx -1 returns empty", "", reader.ReadCharName(pid, -1));
        AssertEqual("idx 10 (out of range, MaxChars=10) returns empty", "", reader.ReadCharName(pid, 10));
        AssertEqual("idx 999 returns empty", "", reader.ReadCharName(pid, 999));
    }

    // ─── Test plumbing ───────────────────────────────────────────────

    private static int _nextFakePid = 900_000;
    /// <summary>Hand out a unique fake PID per test so SHM names don't collide
    /// when tests run in sequence within one process.</summary>
    private static int TakeFakePid() => System.Threading.Interlocked.Increment(ref _nextFakePid);

    /// <summary>
    /// Owning handle for a writer view onto the test SHM. Wraps both the
    /// MemoryMappedFile and its accessor so test teardown disposes both —
    /// without this the MMF is leaked per test (v3.15.2 fix; previously
    /// only the accessor was disposed via `using`, leaving the underlying
    /// kernel mapping object alive and theoretically enabling cross-test
    /// state pollution under sustained re-entry).
    /// </summary>
    private sealed class WriterHandle : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        public MemoryMappedViewAccessor Accessor { get; }
        public WriterHandle(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
        {
            _mmf = mmf;
            Accessor = accessor;
        }
        public void Dispose()
        {
            Accessor.Dispose();
            _mmf.Dispose();
        }
    }

    /// <summary>
    /// Open a writer view onto the same SHM that CharSelectReader created.
    /// Writes from this view are visible to the reader because both are
    /// backed by the same kernel-shared mapping object. Caller must dispose
    /// the returned handle (use `using var`).
    /// </summary>
    private static WriterHandle AttachWriter(int pid)
    {
        var name = $"{SharedMemoryPrefix}{(uint)pid}";
        var mmf = MemoryMappedFile.OpenExisting(name);
        return new WriterHandle(mmf, mmf.CreateViewAccessor(0, ShmSize));
    }

    private static void WriteAsciiName(WriterHandle w, int slot, string name)
    {
        var bytes = new byte[NameLen];
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, bytes, Math.Min(nameBytes.Length, NameLen - 1));
        w.Accessor.WriteArray(OFF_NAMES + slot * NameLen, bytes, 0, NameLen);
    }

    private static uint ReadUInt32(WriterHandle w, int offset) => w.Accessor.ReadUInt32(offset);
    private static int ReadInt32(WriterHandle w, int offset) => w.Accessor.ReadInt32(offset);

    // ─── Mini assertion harness ──────────────────────────────────────

    private sealed class TestFailure : Exception { public TestFailure(string msg) : base(msg) { } }

    private static int Run(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine($"    ok: {name}");
            return 0;
        }
        catch (TestFailure ex)
        {
            Console.WriteLine($"    FAIL: {name}: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    CRASH: {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void AssertTrue(string label, bool value)
    {
        if (!value) throw new TestFailure($"{label}: expected true, got false");
    }

    private static void AssertEqual<T>(string label, T expected, T actual) where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
            throw new TestFailure($"{label}: expected '{expected}', got '{actual}'");
    }

    private static void AssertEqual(string label, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new TestFailure($"{label}: expected '{expected}', got '{actual}'");
    }
}
