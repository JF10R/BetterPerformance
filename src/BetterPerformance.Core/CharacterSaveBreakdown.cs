using System;

namespace BetterPerformance.Core
{
    // Phases of one character save that sum to a parent without overlapping. Nested probes
    // (inventory/skills inside PlayerData, catalog reload/backup copy inside a disk phase) and
    // the overlapping CharacterSaveWrite span are deliberately not listed here.
    public enum CharacterSavePhase
    {
        PlayerData, MapData, ToDisk,
        CloudChecks, Build, Hash, GetArray, Open, BufferWrite, Finish, Replace, Backup
    }

    public enum CharacterSaveTrigger { Periodic, ServerRpc, Logout, SleepWake, Other }

    // Accumulates one save's exclusive phase times (ms) and derives what no probe covered.
    // Single-threaded by design: one instance per saving thread, cleared at each save start.
    public sealed class CharacterSaveBreakdown
    {
        public static readonly CharacterSavePhase[] ProfileChildren =
            { CharacterSavePhase.PlayerData, CharacterSavePhase.MapData, CharacterSavePhase.ToDisk };
        public static readonly CharacterSavePhase[] ToDiskChildren =
        {
            CharacterSavePhase.CloudChecks, CharacterSavePhase.Build, CharacterSavePhase.Hash, CharacterSavePhase.GetArray,
            CharacterSavePhase.Open, CharacterSavePhase.BufferWrite, CharacterSavePhase.Finish,
            CharacterSavePhase.Replace, CharacterSavePhase.Backup
        };

        private readonly double[] milliseconds = new double[Enum.GetValues(typeof(CharacterSavePhase)).Length];

        public void Clear() => Array.Clear(milliseconds, 0, milliseconds.Length);

        public void Add(CharacterSavePhase phase, double ms)
        {
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0) return;
            milliseconds[(int)phase] += ms;
        }

        public double Get(CharacterSavePhase phase) => milliseconds[(int)phase];

        // Game.SavePlayerProfile total minus its timed children: mount/unmount, loading-screen
        // push/pop, SaveLogoutPoint, the cloud-capacity check and achievement sync.
        public double ProfileUnaccounted(double totalMs) => Remainder(totalMs, ProfileChildren);

        // SavePlayerToDisk total minus its timed and derived children: SavingStarted/Finished
        // events, mount/unmount, folder checks, cache invalidation, a failed-cloud local dump.
        public double ToDiskUnaccounted(double toDiskMs) => Remainder(toDiskMs, ToDiskChildren);

        private double Remainder(double parentMs, CharacterSavePhase[] children)
        {
            if (double.IsNaN(parentMs) || double.IsInfinity(parentMs) || parentMs <= 0) return 0;
            double sum = 0;
            foreach (var child in children) sum += milliseconds[(int)child];
            // Children run strictly inside the parent's own prefix/finalizer on the same clock;
            // only rounding can push the sum past the parent.
            return Math.Max(0, parentMs - sum);
        }

        // Game.SavePlayerProfile callers (1.0.16): ZNet.RPC_SavePlayerProfile passes isFromRpc;
        // Game.Shutdown (logout/quit) sets m_shuttingDown first; SleepStop is the only caller
        // with setLogoutPoint=false; UpdateSaving calls only once m_saveTimer > m_saveInterval,
        // read before the method zeroes it. Everything else (menu, console, admin save) is Other.
        public static CharacterSaveTrigger Classify(bool fromRpc, bool shuttingDown, bool setLogoutPoint, float saveTimer, float saveInterval)
        {
            if (fromRpc) return CharacterSaveTrigger.ServerRpc;
            if (shuttingDown) return CharacterSaveTrigger.Logout;
            if (!setLogoutPoint) return CharacterSaveTrigger.SleepWake;
            if (saveTimer > saveInterval) return CharacterSaveTrigger.Periodic;
            return CharacterSaveTrigger.Other;
        }

        public static string Name(CharacterSaveTrigger trigger) => trigger switch
        {
            CharacterSaveTrigger.Periodic => "periodic",
            CharacterSaveTrigger.ServerRpc => "server_rpc",
            CharacterSaveTrigger.Logout => "logout",
            CharacterSaveTrigger.SleepWake => "sleep_wake",
            _ => "other"
        };
    }
}
