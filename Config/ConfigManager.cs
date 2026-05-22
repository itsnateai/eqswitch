// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Text.Json;
using System.Text.Json.Serialization;
using EQSwitch.Core;

namespace EQSwitch.Config;

/// <summary>
/// Handles loading, saving, and backing up the JSON config file.
/// Config lives alongside the exe for portability — no AppData pollution.
/// </summary>
public static class ConfigManager
{
    private static readonly string ConfigDir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "eqswitch-config.json");
    private static readonly string BackupDir = Path.Combine(ConfigDir, "backups");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Load config from disk. Returns defaults if file doesn't exist or is corrupt.
    /// Validates all values after loading.
    /// </summary>
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var defaults = new AppConfig();
                defaults.Validate();
                return defaults;
            }

            var json = File.ReadAllText(ConfigPath);

            // Run versioned migrations on raw JSON before deserialization
            var (migratedJson, didMigrate) = ConfigVersionMigrator.MigrateIfNeeded(json);

            var config = JsonSerializer.Deserialize<AppConfig>(migratedJson, JsonOptions) ?? new AppConfig();
            bool validateMutated = config.Validate();

            // Persist migrated config so migration doesn't re-run.
            // v3.15.10 follow-up: stage write under _saveLock for symmetry
            // with Save() / FlushSave() / SaveImmediate(). Load() is called
            // single-threaded at startup before any background work spins
            // up, so the lock is uncontended here in practice — but the
            // explicit cross-thread contract is "every _pendingSave write
            // happens under _saveLock", and breaking that invariant in one
            // call site silently invalidates it everywhere.
            if (didMigrate || validateMutated)
            {
                lock (_saveLock)
                {
                    _pendingSave = config;
                }
                FlushSave();
            }

            return config;
        }
        catch (Exception ex)
        {
            // Config is corrupt — back it up and start fresh
            TryBackupCorruptConfig();
            FileLogger.Error("Config load failed, using defaults", ex);
            var defaults = new AppConfig();
            defaults.Validate();
            return defaults;
        }
    }

    // Coalescing save — rapid Save() calls (e.g. PiP drag, repeated toggles)
    // are batched into a single write after a short delay. This prevents
    // blocking the UI thread with synchronous file I/O on every interaction.
    private static System.Windows.Forms.Timer? _saveTimer;
    private static AppConfig? _pendingSave;

    // Serializes file I/O across SaveImmediate (background-thread callers) and
    // FlushSave (UI-thread coalesced flush). Without this, a background
    // SaveImmediate could collide with a UI-thread FlushSave mid-write,
    // producing a torn temp-file or a corrupted backup. Held only across the
    // critical section of read-pending + write-disk + reset-pending.
    private static readonly object _saveLock = new();

    /// <summary>
    /// Serializes the SettingsForm-ApplySettings ↔ AutoLoginManager-SaveImmediate
    /// race for the <c>Accounts</c> list and per-Account <c>LastLoginAt</c> /
    /// <c>LastLoginResult</c> fields. Distinct from <c>_saveLock</c>, which guards
    /// only the _pendingSave queue + the WriteToDisk critical section — that lock
    /// cannot defend against a background SaveImmediate firing while the UI
    /// thread is mid-mutation of <c>_config.Accounts</c> (the JsonSerializer.
    /// Serialize call inside WriteToDisk reads every field of the supplied
    /// config, and a UI-thread ReloadConfig is mid-mutating those same fields
    /// — producing a torn JSON write OR an orphan-ref LastLoginResult write
    /// that never persists).
    ///
    /// <para>v3.22.25: introduced to close the ApplySettings + autologin race
    /// surfaced in v3.22.24 backlog. Same class as v3.22.23's X-flag orphan-
    /// reference race but on the config-mutation surface instead of the
    /// in-memory captured-reference surface.</para>
    ///
    /// <para><b>SCOPE — what this lock COVERS:</b>
    /// <list type="bullet">
    ///   <item><c>SettingsForm.ApplySettings</c> across the build-newConfig
    ///     + _onApply call (covers the racy read of
    ///     <c>_config.Accounts.FirstOrDefault(...).LastLoginResult</c> at the
    ///     "Race fix" Accounts.Select).</item>
    ///   <item><c>TrayManager.ReloadConfig</c> body (covers the ~50 field-by-
    ///     field mutations + the <c>_config.Accounts = newConfig.Accounts</c>
    ///     list swap). Reentrant on the UI thread when reached via
    ///     ApplySettings → _onApply → ReloadConfig.</item>
    ///   <item><c>AutoLoginManager</c> SM finally-block "re-resolve + writes +
    ///     SaveImmediate" sequence and the legacy <c>RunLoginSequence</c>
    ///     LastLoginResult fail/ok write sites
    ///     (<see cref="EQSwitch.Core.AutoLoginManager"/>.SaveLastLoginResultLocked).</item>
    ///   <item><c>SettingsForm.OnLoginComplete</c> read of <c>live.LastLoginAt</c> /
    ///     <c>live.LastLoginResult</c> from <c>_config.Accounts</c> (added
    ///     v3.22.25 verifier round 2 — parallel team-login SM may be mid-
    ///     writing a DIFFERENT account while OnLoginComplete iterates).</item>
    ///   <item><c>SettingsForm.PopulateFromConfig</c> form-open snapshot
    ///     <c>_config.Accounts.Select(a => new Account { LastLoginResult = a.LastLoginResult, LastLoginAt = a.LastLoginAt, ... })</c>
    ///     (added v3.22.26). Opening Settings while an autologin SM was mid-
    ///     write could torn-read into the staged _pendingAccounts snapshot.
    ///     ApplySettings re-reads live values back under the same lock so
    ///     the persisted value was always correct, but the dialog's Flag
    ///     glyph could show stale data for the open duration. Locked here
    ///     for completeness symmetry with the other ApplySettings/OnLoginComplete
    ///     read sites.</item>
    ///   <item><c>ConfigManager.WriteToDisk</c> wraps the
    ///     <c>JsonSerializer.Serialize(config, JsonOptions)</c> call (added
    ///     v3.22.26). Closes the form-side gap previously enumerated in this
    ///     doc as "the 9 non-Account-mutating forms" — Save is coalesced
    ///     (250ms timer) so the serializer fires on the timer thread OUTSIDE
    ///     any caller's lock; wrapping form-side Save() calls would not
    ///     protect the serializer. Centralizing at the serializer site catches
    ///     all callers (current + future) with a single re-entrant lock
    ///     acquisition that's a no-op on the SM-finally → SaveImmediate path
    ///     (same-thread re-acquire).</item>
    /// </list></para>
    ///
    /// <para><b>SCOPE — what this lock does NOT cover (accepted scope-limit, not bugs):</b>
    /// <list type="bullet">
    ///   <item><c>PipOverlay</c> drag-end position writes to <c>_config.Pip.SavedPositions</c>
    ///     + UI-thread <c>ConfigManager.Save</c>. UI-thread only — the form-side
    ///     mutation doesn't need this lock. Cross-thread protection against
    ///     concurrent SM Account writes during the eventual JsonSerializer call
    ///     is now provided by the v3.22.26 <c>WriteToDisk</c> serializer-site
    ///     lock above. (Pre-v3.22.26 this entry claimed "sub-ms" JsonSerializer
    ///     window as the justification — corrected post-ship: the serializer
    ///     walks the full AppConfig graph regardless of which form triggered
    ///     the save, so the real defense is the serializer-site lock, not
    ///     per-form window narrowness.)</item>
    ///   <item><b>Closed v3.22.26:</b> previously this list enumerated
    ///     <c>ProcessManagerForm.ApplyAllSettings</c>, <c>EQClientSettingsForm</c>,
    ///     <c>EQModelsForm</c>, <c>EQParticlesForm</c>, <c>EQVideoModeForm</c>,
    ///     <c>EQChatSpamForm</c>, <c>FileOperations</c>, <c>FirstRunDialog</c>,
    ///     and the tray-hotkey <c>OnToggleMultiMonitor</c> as "the 9 forms
    ///     that call <c>ConfigManager.Save</c> while not holding this lock —
    ///     JsonSerializer could torn-write Accounts state". Closed by the
    ///     <c>WriteToDisk</c> serializer-site lock above. Form-side
    ///     mutations don't need this lock; the JsonSerializer site does.</item>
    ///   <item><b>Phantom gap retired v3.22.26:</b> previous versions of
    ///     this doc claimed <c>TrayManager.BuildAccountsSubmenu</c> reads
    ///     <c>captured.Tooltip</c> "derived from <c>LastLoginResult</c>" as
    ///     an unlocked torn-read. Audit during v3.22.26 implementation found
    ///     the claim was wrong: <c>Account.Tooltip</c> is <c>$"{Username}@{Server}"</c>
    ///     (see <c>Models/Account.cs:50</c>) and reads NO fields that SM ever
    ///     writes. The R1 T3-Sonnet verifier was misled by this stale doc
    ///     comment — exactly the failure mode tracked in
    ///     <c>memory/feedback_misleading_comments_fool_verifiers.md</c>.
    ///     Entry retained as a tombstone to prevent re-prescription.</item>
    ///   <item><c>ConfigManager.Shutdown</c> + <c>Application.OnApplicationExit</c>
    ///     do not acquire this lock; the existing <c>_saveLock</c> handles
    ///     the file-write race. A background SM mid-finally at tear-down
    ///     could in principle write a stale LastLoginResult to disk after
    ///     Shutdown's final FlushSave — acceptable: process is exiting.</item>
    /// </list></para>
    ///
    /// <para><b>Lock ordering:</b> when both <c>ConfigMutationLock</c> and
    /// <c>_saveLock</c> are taken, the order is ALWAYS
    /// <c>ConfigMutationLock</c> first, then <c>_saveLock</c> (acquired
    /// inside SaveImmediate). Any future code that takes <c>_saveLock</c>
    /// then tries to acquire <c>ConfigMutationLock</c> would deadlock.</para>
    ///
    /// <para><b>Reentrancy:</b> C# <c>lock</c> is recursive on the same
    /// thread, so ApplySettings (UI) calling ReloadConfig (UI) re-acquires
    /// safely. Cross-thread contention is the only blocking surface —
    /// UI vs. SM background worker. SM finally-block hold time is
    /// dominated by JsonSerializer + disk write (low-ms); ReloadConfig
    /// hold time spans field copies + UI rebuild
    /// (BuildContextMenu / hotkey re-register), which can be tens of ms
    /// but is rare (only on user-initiated Settings → Apply).</para>
    ///
    /// <para>Public so callers in <c>UI/</c> and <c>Core/</c> can lock
    /// against the same monitor without going through a wrapper API.</para>
    /// </summary>
    public static readonly object ConfigMutationLock = new();

    /// <summary>
    /// Save config to disk. Coalesces rapid calls — the actual write happens
    /// after a 250ms quiet period so rapid toggles don't stall the UI.
    /// </summary>
    public static void Save(AppConfig config)
    {
        // v3.15.10: stage _pendingSave under _saveLock for symmetry with
        // FlushSave / SaveImmediate. In current callers `config` is always
        // the live `_config` reference, so the prior unlocked write was
        // benign — but a future caller passing a different config object
        // could collide with a concurrent SaveImmediate from AutoLoginManager.
        // The lock makes the cross-thread contract explicit.
        lock (_saveLock)
        {
            _pendingSave = config;
        }

        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _saveTimer.Tick += (_, _) => FlushSave();
        }

        // Reset the timer on each call to coalesce rapid saves
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// Drain pending saves and dispose the coalescing timer. Safe to call multiple times.
    /// </summary>
    public static void Shutdown()
    {
        FlushSave();           // drain any pending save
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        _saveTimer = null;
        FlushSave();           // catch any save queued between first flush and timer disposal
    }

    /// <summary>
    /// Flush any pending save immediately. Returns true on success.
    /// Guards against lost saves: if Save() is called during the write, the new pending
    /// config is detected and re-queued after the write completes.
    /// </summary>
    public static bool FlushSave()
    {
        _saveTimer?.Stop();

        AppConfig? config;
        lock (_saveLock)
        {
            config = _pendingSave;
            _pendingSave = null;
        }
        if (config == null) return true;

        bool ok = WriteToDisk(config);

        // If Save() was called during the write, re-queue so it's not lost.
        // _saveTimer access is UI-thread-only by contract; FlushSave is invoked
        // from the timer Tick (UI thread) or from Shutdown (UI thread), so this
        // is safe.
        if (_pendingSave != null)
        {
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }
        return ok;
    }

    /// <summary>
    /// Thread-safe synchronous save. Bypasses the WinForms-Timer coalescing path
    /// in <see cref="Save"/>, which is unsafe to call from background threads
    /// (the Timer's Tick fires on the thread that called Start, so a background
    /// caller would create a Timer that never ticks because the worker thread
    /// has no message pump). Use this for any save originating off the UI
    /// thread (e.g. AutoLoginManager status writes from the login worker).
    /// Synchronous file I/O — call sparingly.
    /// </summary>
    public static bool SaveImmediate(AppConfig config)
    {
        lock (_saveLock)
        {
            // Race: a UI-thread Save() may have queued `_pendingSave` already.
            // Three cases:
            //   1. _pendingSave is null               → just write `config`.
            //   2. _pendingSave == config (same ref)  → write `config`, clear
            //      the queue (we just persisted what was queued).
            //   3. _pendingSave is a DIFFERENT ref    → write `config` now,
            //      LEAVE the queued config alone for the timer to flush.
            //      Pre-v3.15.10 this branch silently overwrote and then
            //      cleared `_pendingSave` — losing the UI-thread write.
            //
            // In current callers (AutoLoginManager / TrayManager / Settings)
            // the `config` arg is always the live `_config` reference, so
            // case 3 never fires in practice — but the code now matches what
            // the comment promises. Future callers that pass a different
            // config object won't silently lose user state.
            //
            // Caveat for case 3 (verifier-flagged, R3): if SaveImmediate
            // writes `config`(=B) and then the timer fires writing
            // `_pendingSave`(=A, queued earlier), disk ends up with A —
            // overwriting our fresher B with a stale UI snapshot. This is
            // unreachable in current callers (same-ref invariant) but
            // future callers that hit case 3 should ensure `config` is
            // either authoritative on the same fields A touches OR call
            // FlushSave() afterward to drain the queued config first.
            if (_pendingSave == null || ReferenceEquals(_pendingSave, config))
            {
                _pendingSave = null;
            }
            // else: a different config is queued — leave it; the timer flushes it.
            return WriteToDisk(config);
        }
    }

    /// <summary>
    /// Atomic write of the JSON to disk via temp + Move. Caller is responsible
    /// for any synchronization of the <em>queue</em> (via <c>_saveLock</c>); this
    /// method internally takes <see cref="ConfigMutationLock"/> ONLY around the
    /// <c>JsonSerializer.Serialize</c> call so the serializer's object-graph walk
    /// is serialized against concurrent SM Account writes. Updates LastSaveError
    /// + raises SaveFailed on failure. Returns true on success.
    /// </summary>
    private static bool WriteToDisk(AppConfig config)
    {
        // v3.22.26 R2 (T2 Opus + T3 Opus convergent): hoisted out of the try so
        // the catch block at the bottom can reference the SAME variable. Pre-fix,
        // the catch re-concatenated `ConfigPath + ".tmp"` as a separate local,
        // which would silently drift if the suffix is ever refactored (e.g.
        // .tmp → .new, or per-PID .tmp.{pid}).
        var tempPath = ConfigPath + ".tmp";
        try
        {
            if (File.Exists(ConfigPath))
                CreateBackup();

            // v3.22.26: defends the JsonSerializer's object-graph walk against
            // concurrent SM Account writes. Previously the 9 non-Account-
            // mutating forms (ProcessManagerForm, EQClientSettingsForm,
            // EQModelsForm, EQParticlesForm, EQVideoModeForm, EQChatSpamForm,
            // FileOperations, FirstRunDialog, OnToggleMultiMonitor) called
            // ConfigManager.Save while NOT holding ConfigMutationLock. Save is
            // coalesced (250ms timer) so the JsonSerializer call fired on the
            // timer thread OUTSIDE any caller lock — wrapping the form Save
            // sites in ConfigMutationLock would do nothing for THIS race.
            // Centralizing at the serializer is also future-proof: any new
            // caller of Save/SaveImmediate gets the protection automatically.
            //
            // Lock-ordering note: from the SM-finally path
            // (ConfigMutationLock → SaveImmediate → _saveLock → WriteToDisk →
            // ConfigMutationLock), the second ConfigMutationLock acquisition
            // is on the SAME thread, so C# recursive-lock semantics make it
            // a no-op. From the UI-thread coalesced path
            // (Save → timer → FlushSave → WriteToDisk → ConfigMutationLock),
            // the UI thread holds NO lock when WriteToDisk runs (FlushSave
            // releases _saveLock before calling WriteToDisk), so the fresh
            // acquisition is uncontended unless SM is mid-mutation.
            //
            // Lock is held ONLY for the serializer call — File.WriteAllText
            // and File.Move run unlocked since the in-memory mutations don't
            // affect the already-serialized string.
            //
            // Hold-time honesty (v3.22.26 T3 Opus verifier correction): with
            // JsonOptions.WriteIndented = true and a fully-populated AppConfig
            // (Accounts + Characters + Hotkeys + Launch + Pip + CustomVideoPresets
            // + CharacterAliases), JsonSerializer.Serialize can reach tens of
            // milliseconds — same magnitude as ReloadConfigCore's "tens of ms"
            // UI-rebuild path. Contention is rare in practice (UI Save coalesces
            // at 250ms; SM SaveImmediate fires at end-of-login), but during a
            // concurrent Settings → Apply, an SM finally-block can stall for
            // the serializer duration. Accepted tradeoff: snapshotting the
            // graph for unlocked serialization would add deep-clone cost +
            // GC pressure that exceeds the contention cost in normal use.
            string json;
            lock (ConfigMutationLock)
            {
                json = JsonSerializer.Serialize(config, JsonOptions);
            }
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLogger.Error("Config save failed", ex);
            LastSaveError = ex.Message;
            SaveFailed?.Invoke(ex.Message);
            // v3.22.26 (T3 Opus catch): clean up the orphan .tmp file if
            // WriteAllText succeeded but Move failed (or any error path that
            // leaves the temp file on disk). Pre-fix, repeated save failures
            // leaked eqswitch-config.json.tmp next to the exe. Best-effort —
            // a delete failure here just leaves the orphan for the next save
            // attempt to overwrite. Reuses the hoisted `tempPath` so a future
            // suffix-rename refactor stays consistent.
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { /* best-effort cleanup */ }
            return false;
        }

        LastSaveError = null;
        return true;
    }

    /// <summary>Error message from the last failed save, or null if last save succeeded.</summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>Raised when FlushSave fails. Subscribers can show user-facing warnings.</summary>
    public static event Action<string>? SaveFailed;

    /// <summary>
    /// Create a timestamped backup of the current config.
    /// Keeps the last 10 backups sorted by write time to avoid filling disk.
    /// </summary>
    public static void CreateBackup()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            Directory.CreateDirectory(BackupDir);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var backupPath = Path.Combine(BackupDir, $"eqswitch-config_{timestamp}.json");
            File.Copy(ConfigPath, backupPath, overwrite: true);

            // Prune old backups (keep last 10 by write time — robust against filename changes)
            var backups = Directory.GetFiles(BackupDir, "eqswitch-config_*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Skip(10);

            foreach (var old in backups)
            {
                try { File.Delete(old); }
                catch (Exception ex) { FileLogger.Info($"Backup prune skipped ({Path.GetFileName(old)}): {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Backup failed: {ex.Message}");
        }
    }

    private static void TryBackupCorruptConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            Directory.CreateDirectory(BackupDir);
            var corruptPath = Path.Combine(BackupDir, $"CORRUPT_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.Move(ConfigPath, corruptPath);
            FileLogger.Warn($"Corrupt config backed up to {corruptPath}");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"TryBackupCorruptConfig: failed to move corrupt config: {ex.Message}");
            // Last resort: delete the corrupt file so Load() doesn't hit it on every launch
            try { File.Delete(ConfigPath); }
            catch (Exception ex2) { FileLogger.Error($"TryBackupCorruptConfig: also failed to delete: {ex2.Message}"); }
        }
    }
}
