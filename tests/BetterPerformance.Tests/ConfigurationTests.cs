using System;
using System.Collections.Generic;
using System.Linq;
using BetterPerformance.Core;

internal static class ConfigurationTests
{
    internal static void Run()
    {
        var tracker = new ConfigurationTracker();
        tracker.Observe("graphics.active.SimulationDistance", "3", 1, "initial");
        Check(tracker.Drain().Length == 0, "Initial state is a baseline, not a change.");
        tracker.Observe("graphics.active.SimulationDistance", "3", 2, "poll");
        Check(tracker.Drain().Length == 0, "Unchanged polls must not create events.");
        tracker.Observe("graphics.active.SimulationDistance", "4", 3, "graphics_applied");
        tracker.Observe("graphics.active.SimulationDistance", "3", 3.1, "graphics_applied");
        var changes = tracker.Drain();
        Check(changes.Length == 2 && changes[0].Previous == "3" && changes[0].Current == "4"
            && changes[1].Previous == "4" && changes[1].Current == "3",
            "A -> B -> A between polls must preserve both transitions.");
        Check(changes[0].ElapsedSeconds == 3 && changes[1].Source == "graphics_applied",
            "Keep observation time and event source, not export time.");
        Check(tracker.Drain().Length == 0, "Draining must not repeat transitions.");
        for (int i = 0; i < 200; i++) tracker.Observe("graphics.active.SimulationDistance", i.ToString(), 4+i, "poll");
        Check(tracker.Drain().Length == ConfigurationTracker.MaxChanges && tracker.DroppedChanges == 72,
            "Bound pending changes and count overflow.");
        var labels = new List<TextValue>();
        tracker.AppendSnapshot(labels);
        Check(labels.Single().Value == "199", "Retain current state even when history overflows.");
        for (int i = 0; i < ConfigurationTracker.MaxKeys; i++) tracker.Observe("key"+i, "0", 300, "poll");
        labels.Clear(); tracker.AppendSnapshot(labels);
        Check(labels.Count == ConfigurationTracker.MaxKeys && tracker.RejectedKeys == 1, "Bound key cardinality.");
        var fresh = new ConfigurationTracker();
        fresh.Observe("graphics.active.SimulationDistance", "2", 0, "initial");
        Check(fresh.Drain().Length == 0 && fresh.DroppedChanges == 0, "New capture starts an independent baseline.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
