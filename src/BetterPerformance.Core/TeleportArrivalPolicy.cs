using System;

namespace BetterPerformance.Core
{
    // What still holds a distant teleport's loading screen; None means the arrival may end before
    // the native 8 s floor. Every check is about the destination around the player.
    // Server: the server has not yet been sent the destination position, so it is still streaming the old area.
    public enum ArrivalBlocker { None, Moving, Minimum, ActiveArea, Objects, Floor, Terrain, Grass, Dungeon, Settling, Server }

    public struct ArrivalState
    {
        public double TeleportSeconds;
        public bool ActiveAreaLoaded, AreaReady, FloorFound, TerrainQueued, GrassReady, DungeonPending;
        // The destination position was sent to the server during this teleport.
        public bool ServerInformed;
        // How long the destination's received static object set has not changed, counted from when
        // the server could first have answered the destination position.
        public double StableSeconds;
    }

    // What a received object near the destination is, for the settle rule. Pieces and other static
    // objects (trees, rocks, locations) are what the player sees missing; creatures, item drops and
    // other moving objects spawn and leave on their own and must not hold the screen.
    public enum SettleCategory { Piece, Static, Creature, Item, Other }

    // Pure decision behind FastTeleportArrival; docs/teleport-loading.md.
    public static class TeleportArrivalPolicy
    {
        public const double NativeFloorSeconds = 8;
        // Time for the server to process a received position and send the first objects around it:
        // one per-peer send cycle (about 100 ms at 30 fps) plus the link. Before this, "nothing new
        // arrived" says nothing about the destination.
        public const double ServerLeadSeconds = 0.3;
        // Without a position hook, the native 2 s periodic send is the latest the server can learn.
        public const double NativeSendSeconds = 2;

        public static ArrivalBlocker Blocker(in ArrivalState state, double minimumSeconds, double settleSeconds)
        {
            double t = state.TeleportSeconds;
            if (double.IsNaN(t) || t <= AssetUnloadPolicy.TeleportMoveSeconds) return ArrivalBlocker.Moving;
            if (!state.ActiveAreaLoaded) return ArrivalBlocker.ActiveArea;
            if (!state.ServerInformed) return ArrivalBlocker.Server;
            if (!state.AreaReady) return ArrivalBlocker.Objects;
            if (!state.FloorFound) return ArrivalBlocker.Floor;
            if (state.TerrainQueued) return ArrivalBlocker.Terrain;
            if (!state.GrassReady) return ArrivalBlocker.Grass;
            if (state.DungeonPending) return ArrivalBlocker.Dungeon;
            if (double.IsNaN(state.StableSeconds) || state.StableSeconds < settleSeconds) return ArrivalBlocker.Settling;
            if (t < minimumSeconds) return ArrivalBlocker.Minimum;
            return ArrivalBlocker.None;
        }

        public static bool IsDynamic(SettleCategory category) => category >= SettleCategory.Creature;

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

            // Stability that only starts counting at notBefore (NaN: never). A set that was quiet before
            // the server could answer is not proof that the destination is complete.
            public double StableSeconds(double now, double notBefore)
            {
                if (double.IsNaN(notBefore) || double.IsNaN(since)) return double.NaN;
                double start = Math.Max(since, notBefore);
                return now < start ? double.NaN : now - start;
            }
        }

        // Per-check census of the received objects around the destination, by category and distance
        // band. Commit reports which cells changed since the previous check (what reset the settle clock).
        public sealed class SettleCensus
        {
            public const int Bands = 3;
            public static readonly double[] BandUpperMetres = { 32, 64 };
            public static readonly string[] BandNames = { "near", "mid", "far" };
            public static readonly int Cells = Enum.GetValues(typeof(SettleCategory)).Length * Bands;
            private readonly int[] current = new int[Cells], previous = new int[Cells];
            private bool hasPrevious;

            public int StaticCount { get; private set; }
            public int TotalCount { get; private set; }

            public static int Band(double metres)
            {
                for (int i = 0; i < BandUpperMetres.Length; i++)
                    if (metres < BandUpperMetres[i]) return i;
                return Bands - 1;
            }

            public static int Cell(SettleCategory category, int band) => (int)category * Bands + band;

            public void Reset()
            {
                Array.Clear(current, 0, Cells);
                Array.Clear(previous, 0, Cells);
                hasPrevious = false;
                StaticCount = TotalCount = 0;
            }

            public void Add(SettleCategory category, double metres) => current[Cell(category, Band(metres))]++;

            // Closes one check; changes[cell] counts the checks in which that cell's count changed.
            public void Commit(long[] changes)
            {
                if (changes == null || changes.Length < Cells) throw new ArgumentException("changes must hold every cell", nameof(changes));
                int stat = 0, total = 0;
                for (int i = 0; i < Cells; i++)
                {
                    if (hasPrevious && current[i] != previous[i]) changes[i]++;
                    previous[i] = current[i];
                    total += current[i];
                    if (!IsDynamic((SettleCategory)(i / Bands))) stat += current[i];
                    current[i] = 0;
                }
                hasPrevious = true;
                StaticCount = stat;
                TotalCount = total;
            }
        }
    }
}
