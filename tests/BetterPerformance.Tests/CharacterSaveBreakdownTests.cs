using System;
using BetterPerformance.Core;

internal static class CharacterSaveBreakdownTests
{
    public static void Run()
    {
        // A 200 ms save: 3 ms player data, 60 ms map data, 120 ms to disk.
        var b = new CharacterSaveBreakdown();
        b.Add(CharacterSavePhase.PlayerData, 3);
        b.Add(CharacterSavePhase.MapData, 60);
        b.Add(CharacterSavePhase.ToDisk, 120);
        // Inside to disk: 1 + 20 + 10 + 4 + 0.5 + 2 + 40 + 1.5 + 30 = 109 ms.
        b.Add(CharacterSavePhase.CloudChecks, 1);
        b.Add(CharacterSavePhase.Build, 20);
        b.Add(CharacterSavePhase.Hash, 10);
        b.Add(CharacterSavePhase.GetArray, 4);
        b.Add(CharacterSavePhase.Open, 0.5);
        b.Add(CharacterSavePhase.BufferWrite, 2);
        b.Add(CharacterSavePhase.Finish, 40);
        b.Add(CharacterSavePhase.Replace, 1.5);
        b.Add(CharacterSavePhase.Backup, 30);
        Check(Near(b.ProfileUnaccounted(200), 17), "profile remainder = total - player data - map data - to disk");
        Check(Near(b.ToDiskUnaccounted(120), 11), "to-disk remainder excludes the profile-level children");

        // The to-disk children never count against the profile total twice.
        Check(Array.IndexOf(CharacterSaveBreakdown.ProfileChildren, CharacterSavePhase.Finish) < 0 &&
            Array.IndexOf(CharacterSaveBreakdown.ToDiskChildren, CharacterSavePhase.ToDisk) < 0 &&
            Array.IndexOf(CharacterSaveBreakdown.ToDiskChildren, CharacterSavePhase.MapData) < 0, "the two levels are disjoint");
        Check(CharacterSaveBreakdown.ProfileChildren.Length + CharacterSaveBreakdown.ToDiskChildren.Length ==
            Enum.GetValues(typeof(CharacterSavePhase)).Length, "every phase belongs to exactly one level");

        // Repeated calls within one save accumulate (e.g. a retried hash); bad samples are dropped.
        b.Add(CharacterSavePhase.Hash, 5);
        b.Add(CharacterSavePhase.Hash, double.NaN);
        b.Add(CharacterSavePhase.Hash, -1);
        b.Add(CharacterSavePhase.Hash, double.PositiveInfinity);
        Check(Near(b.Get(CharacterSavePhase.Hash), 15), "accumulates valid samples only");

        // Rounding can push the children past the parent: never a negative remainder.
        Check(b.ToDiskUnaccounted(100) == 0, "clamped at zero");
        Check(b.ProfileUnaccounted(0) == 0 && b.ProfileUnaccounted(double.NaN) == 0, "no parent, no remainder");

        // A missing probe leaves its phase at zero, so its time lands in the remainder.
        b.Clear();
        b.Add(CharacterSavePhase.ToDisk, 50);
        b.Add(CharacterSavePhase.Finish, 20);
        Check(Near(b.ToDiskUnaccounted(50), 30), "unaccounted absorbs phases nobody timed");
        Check(b.Get(CharacterSavePhase.PlayerData) == 0 && Near(b.ProfileUnaccounted(80), 30), "Clear starts a fresh save");

        // Trigger classification, per the native callers of Game.SavePlayerProfile.
        const float interval = 1800f;
        Check(CharacterSaveBreakdown.Classify(true, false, true, 10, interval) == CharacterSaveTrigger.ServerRpc, "RPC wins");
        Check(CharacterSaveBreakdown.Classify(true, true, true, 2000, interval) == CharacterSaveTrigger.ServerRpc, "RPC wins over everything");
        Check(CharacterSaveBreakdown.Classify(false, true, true, 2000, interval) == CharacterSaveTrigger.Logout, "shutdown before the timer");
        Check(CharacterSaveBreakdown.Classify(false, false, false, 2000, interval) == CharacterSaveTrigger.SleepWake, "only SleepStop passes false");
        Check(CharacterSaveBreakdown.Classify(false, false, true, 1800.02f, interval) == CharacterSaveTrigger.Periodic, "timer past the interval");
        Check(CharacterSaveBreakdown.Classify(false, false, true, 1800f, interval) == CharacterSaveTrigger.Other, "UpdateSaving needs strictly greater");
        Check(CharacterSaveBreakdown.Classify(false, false, true, 120, interval) == CharacterSaveTrigger.Other, "menu/console save");
        Check(CharacterSaveBreakdown.Name(CharacterSaveTrigger.ServerRpc) == "server_rpc" &&
            CharacterSaveBreakdown.Name(CharacterSaveTrigger.SleepWake) == "sleep_wake", "exported names");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
