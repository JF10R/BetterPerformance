using System;

namespace BetterPerformance.Core
{
    // What still holds a distant teleport's loading screen; None means the arrival may end before
    // the native 8 s floor. Every check is about the destination around the player.
    public enum ArrivalBlocker { None, Moving, Minimum, ActiveArea, Objects, Floor, Terrain, Grass, Dungeon, Settling }

    public struct ArrivalState
    {
        public double TeleportSeconds;
        public bool ActiveAreaLoaded, AreaReady, FloorFound, TerrainQueued, GrassReady, DungeonPending;
        // How long the destination's received object set has not changed.
        public double StableSeconds;
    }

    // Pure decision behind FastTeleportArrival; docs/teleport-loading.md.
    public static class TeleportArrivalPolicy
    {
        public const double NativeFloorSeconds = 8;

        public static ArrivalBlocker Blocker(in ArrivalState state, double minimumSeconds, double settleSeconds)
        {
            double t = state.TeleportSeconds;
            if (double.IsNaN(t) || t <= AssetUnloadPolicy.TeleportMoveSeconds) return ArrivalBlocker.Moving;
            if (!state.ActiveAreaLoaded) return ArrivalBlocker.ActiveArea;
            if (!state.AreaReady) return ArrivalBlocker.Objects;
            if (!state.FloorFound) return ArrivalBlocker.Floor;
            if (state.TerrainQueued) return ArrivalBlocker.Terrain;
            if (!state.GrassReady) return ArrivalBlocker.Grass;
            if (state.DungeonPending) return ArrivalBlocker.Dungeon;
            if (double.IsNaN(state.StableSeconds) || state.StableSeconds < settleSeconds) return ArrivalBlocker.Settling;
            if (t < minimumSeconds) return ArrivalBlocker.Minimum;
            return ArrivalBlocker.None;
        }

        // Tracks when a count last changed, on one monotonic clock in seconds.
        public sealed class Settle
        {
            private int count = -1;
            private double since = double.NaN;

            public void Reset() { count = -1; since = double.NaN; }

            public void Observe(int value, double now)
            {
                if (value != count) { count = value; since = now; }
            }

            public double StableSeconds(double now) => double.IsNaN(since) || now < since ? double.NaN : now - since;
        }
    }
}
