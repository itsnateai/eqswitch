using Xunit;
using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.Tests;

public class AffinityManagerTests
{
    private static EQClient MakeClient(int slot, string? charName = null, int pid = 0)
    {
        return new EQClient
        {
            SlotIndex = slot,
            ProcessId = pid > 0 ? pid : (slot + 100),
            WindowHandle = new IntPtr(slot + 1000),
            CharacterName = charName
        };
    }

    // ─── ApplyAffinityRules ─────────────────────────────────────────

    [Fact]
    public void ApplyAffinityRules_Disabled_DoesNothing()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = false;
        var mgr = new AffinityManager(config);

        // Should not throw or do anything
        mgr.ApplyAffinityRules(new[] { MakeClient(0) }, MakeClient(0));
    }

    [Fact]
    public void ApplyAffinityRules_EmptyClients_DoesNothing()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = true;
        var mgr = new AffinityManager(config);

        mgr.ApplyAffinityRules(Array.Empty<EQClient>(), null);
        // No exception = pass
    }

    [Fact]
    public void ApplyAffinityRules_SkipsIfActiveUnchanged()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = true;
        var mgr = new AffinityManager(config);
        var client = MakeClient(0);

        // First call applies
        mgr.ApplyAffinityRules(new[] { client }, client);
        // Second call with same active should be skipped (no work done)
        mgr.ApplyAffinityRules(new[] { client }, client);
        // No way to verify externally without mocking, but at least it doesn't throw
    }

    [Fact]
    public void ForceApplyAffinityRules_ResetsCache()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = true;
        var mgr = new AffinityManager(config);
        var client = MakeClient(0);

        mgr.ApplyAffinityRules(new[] { client }, client);
        // Force should reset the cache and re-apply
        mgr.ForceApplyAffinityRules(new[] { client }, client);
        // No exception = pass
    }

    // ─── Retry Logic ────────────────────────────────────────────────

    [Fact]
    public void ScheduleRetry_Disabled_DoesNothing()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = false;
        var mgr = new AffinityManager(config);

        mgr.ScheduleRetry(MakeClient(0));
        // ProcessRetries should return false (no retries scheduled)
        Assert.False(mgr.ProcessRetries(new[] { MakeClient(0) }));
    }

    [Fact]
    public void ProcessRetries_NoRetries_ReturnsFalse()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = true;
        var mgr = new AffinityManager(config);

        Assert.False(mgr.ProcessRetries(Array.Empty<EQClient>()));
    }

    [Fact]
    public void CancelRetry_RemovesTracking()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = true;
        config.Affinity.LaunchRetryCount = 3;
        var mgr = new AffinityManager(config);
        var client = MakeClient(0, pid: 42);

        mgr.ScheduleRetry(client);
        mgr.CancelRetry(42);
        Assert.False(mgr.ProcessRetries(new[] { client }));
    }

    [Fact]
    public void ProcessRetries_ClientGone_CleanedUp()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = true;
        config.Affinity.LaunchRetryCount = 3;
        var mgr = new AffinityManager(config);
        var client = MakeClient(0, pid: 42);

        mgr.ScheduleRetry(client);
        // Process retries with empty list (client gone)
        Assert.False(mgr.ProcessRetries(Array.Empty<EQClient>()));
        // Should be cleaned up now
        Assert.False(mgr.ProcessRetries(new[] { client }));
    }

    // ─── Character Overrides ────────────────────────────────────────

    [Fact]
    public void ApplyAffinityRules_PerCharacterPriorityOverride_ExercisesPath()
    {
        var config = new AppConfig();
        config.Affinity.Enabled = true;
        config.Characters.Add(new CharacterProfile
        {
            Name = "TestChar",
            PriorityOverride = "AboveNormal"
        });

        var mgr = new AffinityManager(config);
        var active = MakeClient(0, "OtherChar", pid: 100);
        var overridden = MakeClient(1, "TestChar", pid: 200);

        // Exercises the priority override path without crashing
        mgr.ApplyAffinityRules(new[] { active, overridden }, active);
    }
}
