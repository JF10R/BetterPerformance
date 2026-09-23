using System;
using BetterPerformance.Core;

internal static class ZoneGenerationTests
{
    public static void Run()
    {
        var window = new ZoneGenerationWindow();
        window.Record(new ZoneGenerationCall { Mode = ZoneGenerationMode.Ghost, ElapsedMs = 100,
            HeightmapMs = 20, LocationsMs = 15, VegetationMs = 60, DungeonMs = 10, Succeeded = true });
        window.Record(new ZoneGenerationCall { Mode = ZoneGenerationMode.Ghost, ElapsedMs = 200,
            HeightmapMs = 10, LocationsMs = 170, VegetationMs = 5, DungeonMs = 150, Succeeded = true });
        window.Record(new ZoneGenerationCall { Mode = ZoneGenerationMode.Full, ElapsedMs = 50, Succeeded = true });
        window.Record(new ZoneGenerationCall { Mode = ZoneGenerationMode.Client, ElapsedMs = 0 });
        window.Record(new ZoneGenerationCall { Mode = (ZoneGenerationMode)77, ElapsedMs = 2, Failed = true });
        var first = window.Drain();
        var ghost = first[(int)ZoneGenerationMode.Ghost];
        Check(first.Length == 4 && ghost.Calls == 2 && ghost.Succeeded == 2 && ghost.Over50Ms == 2 && ghost.SumMs == 300,
            "Bound the modes and keep attempt, success, sum, and strict stall counts.");
        Check(ghost.Peak.ElapsedMs == 200 && ghost.Peak.HeightmapMs == 10 && ghost.Peak.VegetationMs == 5 &&
            ghost.Peak.LocationsMs == 170 && ghost.Peak.DungeonMs == 150,
            "The phases belong to the same slowest call, never independent phase maxima.");
        Check(ghost.Peak.LocationsMs + ghost.Peak.DungeonMs > ghost.Peak.ElapsedMs,
            "Nested inclusive phases must not be normalized into an invented exclusive partition.");
        Check(first[(int)ZoneGenerationMode.Full].Over50Ms == 0 && first[(int)ZoneGenerationMode.Full].Succeeded == 1,
            "Exactly 50 ms does not exceed the stall threshold.");
        Check(first[(int)ZoneGenerationMode.Client].Calls == 1 && first[(int)ZoneGenerationMode.Client].Succeeded == 0 &&
            first[(int)ZoneGenerationMode.Client].Failed == 0, "Not-ready false is neither success nor exception.");
        Check(first[(int)ZoneGenerationMode.Unknown].Failed == 1, "Unknown future modes remain visible in a bounded slot.");
        Check(window.Drain()[(int)ZoneGenerationMode.Ghost].Calls == 0, "Drain resets every interval.");

        window.Record(new ZoneGenerationCall { Mode = ZoneGenerationMode.Ghost, ElapsedMs = 1 });
        Check(first[(int)ZoneGenerationMode.Ghost].Calls == 2, "Exported snapshots cannot be mutated by later records.");
        window.Reset();
        Check(window.Drain()[(int)ZoneGenerationMode.Ghost].Calls == 0, "A new capture discards previous window samples.");
        Check(!window.Record(new ZoneGenerationCall { ElapsedMs = double.NaN }) &&
            !window.Record(new ZoneGenerationCall { VegetationMs = -1 }) &&
            !window.Record(new ZoneGenerationCall { LocationsMs = double.PositiveInfinity }),
            "Reject invalid clocks and phase durations before recording any counters.");
        Check(window.Drain()[0].Calls == 0, "Rejected samples must not look like valid zero durations.");
        var warm = new ZoneGenerationCall { ElapsedMs = 1 };
        for (int i = 0; i < 1000; i++) window.Record(warm);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) window.Record(warm);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Hot window aggregation must not allocate per call.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
