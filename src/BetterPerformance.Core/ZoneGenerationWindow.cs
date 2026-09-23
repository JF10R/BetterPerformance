using System;

namespace BetterPerformance.Core
{
    public enum ZoneGenerationMode { Client, Full, Ghost, Unknown }

    // The phases travel with one native SpawnZone call. They are inclusive, may
    // overlap (dungeon generation is inside locations), and must not be added.
    public struct ZoneGenerationCall
    {
        public ZoneGenerationMode Mode;
        public double ElapsedMs, HeightmapMs, LocationsMs, VegetationMs, DungeonMs;
        public bool Succeeded, Failed;
    }

    public struct ZoneGenerationSummary
    {
        public long Calls, Succeeded, Failed, Over50Ms;
        public double SumMs;
        public ZoneGenerationCall Peak;
    }

    // Four fixed slots, no world coordinates, object IDs, or per-call history.
    public sealed class ZoneGenerationWindow
    {
        private readonly object gate = new object();
        private readonly ZoneGenerationSummary[] modes = new ZoneGenerationSummary[4];

        public bool Record(ZoneGenerationCall call)
        {
            if (!Valid(call.ElapsedMs) || !Valid(call.HeightmapMs) || !Valid(call.LocationsMs) ||
                !Valid(call.VegetationMs) || !Valid(call.DungeonMs)) return false;
            int index = (int)call.Mode;
            if (index < 0 || index >= modes.Length) index = (int)ZoneGenerationMode.Unknown;
            call.Mode = (ZoneGenerationMode)index;
            lock (gate)
            {
                ref ZoneGenerationSummary summary = ref modes[index];
                if (summary.Calls == 0 || call.ElapsedMs > summary.Peak.ElapsedMs) summary.Peak = call;
                summary.Calls++;
                if (call.Failed) summary.Failed++;
                else if (call.Succeeded) summary.Succeeded++;
                if (call.ElapsedMs > 50) summary.Over50Ms++;
                summary.SumMs += call.ElapsedMs;
            }
            return true;
        }

        public ZoneGenerationSummary[] Drain()
        {
            lock (gate)
            {
                var snapshot = (ZoneGenerationSummary[])modes.Clone();
                Array.Clear(modes, 0, modes.Length);
                return snapshot;
            }
        }

        public void Reset()
        {
            lock (gate) Array.Clear(modes, 0, modes.Length);
        }

        private static bool Valid(double value) => value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
