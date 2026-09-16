using System;

namespace BetterPerformance.Core
{
    public enum LoadingMilestone { SceneTransitionRequested, SceneAwake, RequestScheduled, RespawnStarted, SpawnPointReady, PlayerSpawned, RespawnCompleted, HudReleased }
    public enum LoadingOperation { FindSpawnPoint, AreaReady, SpawnPlayer, UpdateRespawn }

    // One fixed-size, identity-free timeline. Costs are inclusive and may nest;
    // milestone durations are wall elapsed, never charged CPU or exclusive work.
    public sealed class LoadingTimeline
    {
        public struct Operation
        {
            public long Calls, NotReady, Failures;
            public double SumMs, MaxMs;
        }
        public readonly double[] Times = new double[8];
        public readonly Operation[] Operations = new Operation[4];
        public long Sequence { get; private set; }
        public long ReplacedIncomplete { get; private set; }
        public long InvalidTimes { get; private set; }
        public string Kind { get; private set; } = "unobserved";
        public double Started { get; private set; } = double.NaN;
        public double Ended { get; private set; } = double.NaN;
        public bool Completed { get; private set; }
        public bool Censored { get; private set; }
        public bool Active => Sequence != 0 && !Completed && !Censored;
        public LoadingTimeline() { ClearTimes(); }
        public void Begin(string kind, double now)
        {
            if (!Valid(now)) { InvalidTimes++; return; }
            if (Active) ReplacedIncomplete++;
            Sequence++; Kind = kind; Started = now; Ended = double.NaN;
            Completed = Censored = false; ClearTimes(); Array.Clear(Operations, 0, Operations.Length);
        }
        private void ClearTimes() { for (int i = 0; i < Times.Length; i++) Times[i] = double.NaN; }
        public void Mark(LoadingMilestone milestone, double now)
        {
            if (!Active) return;
            if (!Valid(now) || now < Started) { InvalidTimes++; return; }
            int index = (int)milestone;
            if (!double.IsNaN(Times[index])) return;
            Times[index] = now;
            if (milestone == LoadingMilestone.HudReleased) { Completed = true; Ended = now; }
        }
        public void Record(LoadingOperation operation, double start, double end, bool ready, bool failed)
        {
            if (!Active) return;
            if (!Valid(start) || !Valid(end) || start < Started || end < start) { InvalidTimes++; return; }
            int index = (int)operation;
            var value = Operations[index]; value.Calls++;
            if (failed) value.Failures++;
            else if (!ready) value.NotReady++;
            double ms = (end - start) * 1000;
            value.SumMs += ms; value.MaxMs = Math.Max(value.MaxMs, ms); Operations[index] = value;
        }
        public double Duration(LoadingMilestone from, LoadingMilestone to)
        {
            double a = Times[(int)from], b = Times[(int)to];
            return !double.IsNaN(a) && !double.IsNaN(b) && b >= a ? b - a : double.NaN;
        }
        public double Elapsed(double now)
        {
            double end = Active ? now : Ended;
            return Valid(end) && end >= Started ? end - Started : double.NaN;
        }
        public void Expire(double now) { if (Active && Valid(now) && now - Started >= 1800) Censor(now); }
        public void Censor(double now) { if (Active) { Censored = true; Ended = Valid(now) && now >= Started ? now : double.NaN; } }
        private static bool Valid(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
    }
}
