using System;
using System.Collections.Generic;
using BetterPerformance.Core;

internal static class LootSendLegTests
{
    public static void Run()
    {
        OwnerLegCompletesOnTheFirstSend();
        ServerLegTimesEachOtherPeerOnce();
        BoundsSkipAndRetireInsteadOfGrowing();
        ExpiryCountsDropsNeverSent();
        ClockAndResetRules();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Sent : LootSendLegTracker<int>.ISentSet
    {
        public readonly HashSet<int> Keys = new HashSet<int>();
        public bool Contains(int key) => Keys.Contains(key);
    }

    private static void OwnerLegCompletesOnTheFirstSend()
    {
        var legs = new LootSendLegTracker<int>(4, 5000, firstPeerCompletes: true);
        var sent = new Sent();
        Check(legs.Start(1, 0, 100) && legs.Start(1, 0, 110), "A drop followed twice keeps its first start.");
        Check(legs.Sent(9, sent, 150) == 0 && legs.PendingCount == 1, "A send that did not carry the drop completes nothing.");
        sent.Keys.Add(1);
        Check(legs.Sent(9, sent, 160) == 1 && legs.PendingCount == 0, "The client's one peer completes the drop.");
        Check(legs.Sent(9, sent, 170) == 0, "A later send of the same drop is not a second first send.");
        var summary = legs.Drain(200);
        Check(summary.Started == 1 && summary.Count == 1 && summary.SumMs == 60 && summary.MaxMs == 60 && summary.OverOneSecond == 0,
            "One first-send duration from the first start.");
    }

    private static void ServerLegTimesEachOtherPeerOnce()
    {
        var legs = new LootSendLegTracker<int>(4, 5000, firstPeerCompletes: false);
        var sent = new Sent();
        sent.Keys.Add(1);
        legs.Start(1, 7, 1000);
        Check(legs.Sent(7, sent, 1010) == 0, "The peer that sent the drop already holds it; that is not a delivery.");
        Check(legs.Sent(8, sent, 1050) == 1 && legs.Sent(8, sent, 1060) == 0, "Each other peer is timed once.");
        Check(legs.Sent(9, sent, 2500) == 1 && legs.PendingCount == 1, "A server keeps following until the window ends.");
        var summary = legs.Drain(7000);
        Check(summary.Count == 2 && summary.SumMs == 1550 && summary.MaxMs == 1500 && summary.OverOneSecond == 1,
            "Per-peer samples, with the slow one counted over a second.");
        Check(summary.ExpiredUnsent == 0 && summary.Pending == 0, "A drop that reached a peer expires without counting as unsent.");
    }

    private static void BoundsSkipAndRetireInsteadOfGrowing()
    {
        var legs = new LootSendLegTracker<int>(2, 5000, firstPeerCompletes: false);
        var sent = new Sent();
        for (int key = 0; key < 5; key++) legs.Start(key, 0, 100);
        Check(legs.PendingCount == 2, "Pending drops never exceed capacity.");
        sent.Keys.Add(0);
        for (long peer = 1; peer <= LootSendLegTracker<int>.PeerSlots + 2; peer++) legs.Sent(peer, sent, 200);
        var summary = legs.Drain(300);
        Check(summary.Skipped == 3 && summary.Started == 2, "Drops beyond capacity are counted, not stored.");
        Check(summary.Count == LootSendLegTracker<int>.PeerSlots && summary.PeerSlotsFull == 1 && summary.Pending == 1,
            "A drop that filled its peer slots is retired, so a ninth peer is never resampled.");
        Check(legs.Start(9, 0, 400), "Retired capacity is reusable.");
    }

    private static void ExpiryCountsDropsNeverSent()
    {
        var legs = new LootSendLegTracker<int>(4, 1000, firstPeerCompletes: true);
        legs.Start(1, 0, 100);
        Check(legs.Drain(1099).Pending == 1, "Inside the window the drop is still followed.");
        var summary = legs.Drain(1100);
        Check(summary.ExpiredUnsent == 1 && summary.Pending == 0 && summary.Count == 0,
            "A drop never sent within the window is counted unsent, never timed at the window length.");
    }

    private static void ClockAndResetRules()
    {
        var legs = new LootSendLegTracker<int>(4, 5000, firstPeerCompletes: true);
        var sent = new Sent();
        sent.Keys.Add(1);
        legs.Start(1, 0, 500);
        legs.Sent(3, sent, 400);
        var backwards = legs.Drain(600);
        Check(backwards.NonMonotonic == 1 && backwards.Count == 0, "A send before the start is excluded, not timed as zero.");
        legs.Start(2, 0, 700);
        legs.Clear(true);
        Check(legs.Drain(710).Censored == 1, "A scene change censors followed drops.");
        legs.Start(3, 0, 720);
        legs.Clear(false);
        var reset = legs.Drain(730);
        Check(reset.Started == 0 && reset.Censored == 0 && reset.Pending == 0, "A new capture resets instead of censoring.");
        bool rejected = false;
        try { legs.Start(4, 0, double.NaN); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "An invalid clock is rejected before storing state.");
    }
}
