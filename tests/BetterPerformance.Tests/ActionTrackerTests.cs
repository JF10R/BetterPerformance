using System;
using BetterPerformance.Core;

internal static class ActionTrackerTests
{
    internal static void Run()
    {
        var tracker = new ActionTracker<object>(2, 10);
        var first = new object(); var second = new object(); var skipped = new object();
        tracker.BeginRequest(first, ObservedAction.Pickup, 1);
        tracker.MarkOwnershipRequest(first);
        tracker.BeginRequest(first, ObservedAction.Pickup, 2); // Preserve first request.
        tracker.BeginDirect(first, ObservedAction.Pickup, 3);
        tracker.Complete(first, ObservedAction.Pickup, 3.25, true);
        var pickup = tracker.Drain(ObservedAction.Pickup);
        Check(pickup.Confirmed == 1 && pickup.Duplicates == 1 && pickup.RequestCalls == 2 && pickup.DirectCalls == 1 &&
            pickup.RequestCompleted == 0 && pickup.DirectSumMs == 250 && pickup.OwnershipCompleted == 0 && pickup.AmbiguousConfirmed == 1,
            "duplicate request pairing is excluded while unambiguous direct duration remains");
        tracker.BeginRequest(first, ObservedAction.Pickup, 3.5);
        tracker.MarkOwnershipRequest(first);
        tracker.Complete(first, ObservedAction.Pickup, 3.75, true);
        pickup = tracker.Drain(ObservedAction.Pickup);
        Check(pickup.RequestCompleted == 1 && pickup.OwnershipCompleted == 1 && pickup.RequestSumMs == 250 && pickup.OwnershipSumMs == 250,
            "unambiguous request with proven ownership call has a distinct completed delay");
        tracker.BeginDirect(first, ObservedAction.Pickup, 4);
        tracker.Complete(first, ObservedAction.Pickup, 4.5, true);
        pickup = tracker.Drain(ObservedAction.Pickup);
        Check(pickup.DirectCompleted == 1 && pickup.RequestCompleted == 0 && pickup.DirectSumMs == 500,
            "direct-only attempt cannot invent earlier network wait");
        tracker.BeginRequest(first, ObservedAction.Container, 5);
        tracker.BeginRequest(second, ObservedAction.Pickup, 5);
        tracker.BeginDirect(skipped, ObservedAction.Pickup, 5);
        tracker.Complete(skipped, ObservedAction.Pickup, 6, true);
        tracker.Complete(first, ObservedAction.Container, 6, false);
        var container = tracker.Drain(ObservedAction.Container);
        pickup = tracker.Drain(ObservedAction.Pickup);
        Check(container.Rejected == 1 && container.Confirmed == 0 && pickup.CapacitySkipped == 1 && pickup.Unmatched == 1 && pickup.Pending == 1,
            "shared capacity, explicit rejection and unmatched coverage are separate");
        tracker.Expire(15);
        pickup = tracker.Drain(ObservedAction.Pickup);
        Check(pickup.Censored == 1 && pickup.TimedOut == 1 && pickup.Pending == 0, "inclusive timeout releases references");
        tracker.BeginRequest(first, ObservedAction.Container, 20);
        tracker.Complete(first, ObservedAction.Container, 30, true);
        container = tracker.Drain(ObservedAction.Container);
        Check(container.Confirmed == 0 && container.TimedOut == 1 && container.Unmatched == 1, "late completion is not a successful measured delay");
        tracker.BeginDirect(first, ObservedAction.Pickup, 31);
        tracker.Censor(first);
        tracker.BeginRequest(second, ObservedAction.Container, 31);
        tracker.CensorAll();
        Check(tracker.Drain(ObservedAction.Pickup).Censored == 1 && tracker.Drain(ObservedAction.Container).Censored == 1,
            "unfinished outcomes and capture end are censored, not failures");
        tracker.BeginRequest(first, ObservedAction.Pickup, 40);
        tracker.Reset();
        Check(tracker.Drain(ObservedAction.Pickup).Pending == 0 && tracker.Drain(ObservedAction.Pickup).Started == 0,
            "new capture starts without prior keys or counts");
        tracker.BeginRequest(first, ObservedAction.Pickup, 50);
        tracker.BeginRequest(first, ObservedAction.Pickup, 60);
        tracker.Complete(first, ObservedAction.Pickup, 61, true);
        pickup = tracker.Drain(ObservedAction.Pickup);
        Check(pickup.TimedOut == 1 && pickup.Confirmed == 1 && pickup.RequestSumMs == 1000,
            "expired duplicate starts a new bounded timeline");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("ActionTracker: " + message); }
}
