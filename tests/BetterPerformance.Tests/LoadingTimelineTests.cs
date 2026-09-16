using System;
using BetterPerformance.Core;

internal static class LoadingTimelineTests
{
    internal static void Run()
    {
        var timeline = new LoadingTimeline();
        timeline.Begin("initial_join", 10);
        timeline.Mark(LoadingMilestone.SceneAwake, 12);
        timeline.Mark(LoadingMilestone.RequestScheduled, 13);
        timeline.Mark(LoadingMilestone.RespawnStarted, 15);
        timeline.Record(LoadingOperation.FindSpawnPoint, 16, 16.002, false, false);
        timeline.Record(LoadingOperation.AreaReady, 16, 16.001, false, false);
        timeline.Mark(LoadingMilestone.SpawnPointReady, 45);
        timeline.Mark(LoadingMilestone.PlayerSpawned, 46);
        timeline.Mark(LoadingMilestone.RespawnCompleted, 47);
        Check(timeline.Elapsed(47) == 37 && timeline.Duration(LoadingMilestone.RespawnStarted, LoadingMilestone.SpawnPointReady) == 30,
            "wall timeline preserves waiting; nested operation costs are not subtracted");
        Check(timeline.Operations[(int)LoadingOperation.FindSpawnPoint].NotReady == 1 &&
            timeline.Operations[(int)LoadingOperation.AreaReady].NotReady == 1, "readiness outcomes remain scoped and distinct");
        timeline.Mark(LoadingMilestone.PlayerSpawned, 48);
        Check(timeline.Duration(LoadingMilestone.PlayerSpawned, LoadingMilestone.RespawnCompleted) == 1,
            "duplicate observations preserve first milestone");
        Check(double.IsNaN(timeline.Duration(LoadingMilestone.RespawnCompleted, LoadingMilestone.HudReleased)),
            "unobserved HUD completion is unavailable, never zero");
        timeline.Mark(LoadingMilestone.HudReleased, 50);
        Check(timeline.Completed && timeline.Elapsed(100) == 40, "completed elapsed stops at verified HUD milestone");
        timeline.Begin("later_respawn", 100);
        Check(timeline.Sequence == 2 && timeline.Kind == "later_respawn" && timeline.Operations[0].Calls == 0 &&
            double.IsNaN(timeline.Times[(int)LoadingMilestone.PlayerSpawned]), "new respawn cannot inherit join stages or costs");
        timeline.Mark(LoadingMilestone.RespawnStarted, 99);
        Check(timeline.InvalidTimes == 1 && double.IsNaN(timeline.Times[(int)LoadingMilestone.RespawnStarted]),
            "regressing clock does not fabricate a zero stage");
        timeline.Record(LoadingOperation.SpawnPlayer, 103, 102, true, false);
        Check(timeline.InvalidTimes == 2 && timeline.Operations[(int)LoadingOperation.SpawnPlayer].Calls == 0,
            "invalid operation duration excluded");
        timeline.Expire(1900);
        Check(timeline.Censored && !timeline.Completed, "unfinished timeline expires at bounded thirty-minute horizon");
        timeline.Mark(LoadingMilestone.HudReleased, 1901);
        Check(!timeline.Completed, "late observation cannot turn censored load into success");
        timeline.Begin("initial_join", 2000);
        timeline.Begin("initial_join", 2001);
        Check(timeline.ReplacedIncomplete == 1, "superseded incomplete load is counted separately");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("LoadingTimeline: " + message); }
}
