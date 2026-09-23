using System;
using System.Collections.Generic;
using System.Linq;
using BetterPerformance.Core;

internal static class PregenerationFrontierTests
{
    public static void Run()
    {
        Planning();
        Activity();
        Persistence();
    }

    private static void Planning()
    {
        var origin = new ZoneCoord(0, 0);
        var none = new HashSet<ZoneCoord>();
        var one = PregenerationFrontier.Plan(new[] { origin }, 1, none.Contains, 100);
        Check(one.Count == 9 && one[0].Equals(origin), "Radius 1 with the ghost margin covers the 3x3 square, nearest first.");
        var two = PregenerationFrontier.Plan(new[] { origin }, 2, none.Contains, 100);
        Check(two.Count == 21 && !two.Contains(new ZoneCoord(2, 2)) && two.Contains(new ZoneCoord(2, 1)),
            "Radius 2 follows the vanilla ghost circle (2.8 zones): corners at distance 2.83 are outside.");
        Check(two.Take(5).All(z => Math.Abs(z.X) + Math.Abs(z.Y) <= 1) && two.Skip(5).Take(4).All(z => Math.Abs(z.X) == 1 && Math.Abs(z.Y) == 1),
            "Candidates are ordered by distance to their activity zone.");

        var generated = new HashSet<ZoneCoord>(two.Where(z => z.X * z.X + z.Y * z.Y <= 1));
        var frontier = PregenerationFrontier.Plan(new[] { origin }, 2, generated.Contains, 100);
        Check(frontier.Count == 16 && frontier.All(z => !generated.Contains(z)), "Generated zones are never candidates.");

        var recent = new ZoneCoord(10, 0);
        var ranked = PregenerationFrontier.Plan(new[] { recent, origin }, 1, none.Contains, 100);
        Check(ranked.Count == 18 && ranked.Take(9).All(z => z.X >= 9) && ranked[0].Equals(recent),
            "Zones around the most recent activity come first.");
        var overlap = PregenerationFrontier.Plan(new[] { new ZoneCoord(1, 0), origin }, 1, none.Contains, 100);
        Check(overlap.Count == 12 && overlap.Distinct().Count() == 12, "A zone reached by two activity zones is listed once.");
        Check(overlap.Take(9).All(z => z.X >= 0), "A shared zone is ranked by the most recent activity zone that reaches it.");

        Check(PregenerationFrontier.Plan(new[] { origin }, 2, none.Contains, 5).Count == 5, "The limit caps the plan.");
        Check(PregenerationFrontier.Plan(new[] { origin }, 2, none.Contains, 0).Count == 0, "A zero limit plans nothing.");
        Check(PregenerationFrontier.Plan(Array.Empty<ZoneCoord>(), 2, none.Contains, 10).Count == 0, "No activity, no candidates.");
        var twice = PregenerationFrontier.Plan(new[] { recent, origin }, 2, none.Contains, 100);
        Check(twice.SequenceEqual(PregenerationFrontier.Plan(new[] { recent, origin }, 2, none.Contains, 100)), "Planning is deterministic.");

        Check(PregenerationFrontier.InWorld(new ZoneCoord(164, 0)) && !PregenerationFrontier.InWorld(new ZoneCoord(165, 0)) &&
            !PregenerationFrontier.InWorld(new ZoneCoord(117, 117)), "Zones beyond the 10,500 m water edge are out of the world.");
        var edge = PregenerationFrontier.Plan(new[] { new ZoneCoord(164, 0) }, 2, none.Contains, 100);
        Check(edge.Count > 0 && edge.All(PregenerationFrontier.InWorld), "Candidates past the world edge are dropped.");
        Check(Throws(() => PregenerationFrontier.Plan(new[] { origin }, PregenerationFrontier.MaxRadius + 1, none.Contains, 1)) &&
            Throws(() => PregenerationFrontier.Plan(new[] { origin }, -1, none.Contains, 1)), "The radius is bounded.");
    }

    private static void Activity()
    {
        var zones = new ActivityZones(3);
        Check(zones.Touch(new ZoneCoord(1, 1)) && !zones.Touch(new ZoneCoord(1, 1)), "Touching the current front zone is not a change.");
        zones.Touch(new ZoneCoord(2, 2));
        zones.Touch(new ZoneCoord(3, 3));
        Check(zones.Touch(new ZoneCoord(1, 1)) && zones.MostRecentFirst[0].Equals(new ZoneCoord(1, 1)) && zones.Count == 3,
            "A revisited zone moves to the front without duplication.");
        zones.Touch(new ZoneCoord(4, 4));
        Check(zones.Count == 3 && !zones.MostRecentFirst.Contains(new ZoneCoord(2, 2)), "The least recent zone is evicted at capacity.");
        zones.Replace(new[] { new ZoneCoord(9, 9), new ZoneCoord(9, 9), new ZoneCoord(8, 8), new ZoneCoord(7, 7), new ZoneCoord(6, 6) });
        Check(zones.Count == 3 && zones.MostRecentFirst.SequenceEqual(new[] { new ZoneCoord(9, 9), new ZoneCoord(8, 8), new ZoneCoord(7, 7) }),
            "A loaded list keeps order, drops duplicates and respects capacity.");
        Check(Throws(() => new ActivityZones(0)), "Capacity must be positive.");
    }

    private static void Persistence()
    {
        var list = new[] { new ZoneCoord(-3, 7), new ZoneCoord(0, 0), new ZoneCoord(120, -45) };
        string text = PregenerationFrontier.Serialize(list);
        Check(PregenerationFrontier.TryParse(text, 64, out var parsed, out string ok) && ok == "ok" && parsed.SequenceEqual(list),
            "Serialize and parse round-trip in order.");
        Check(PregenerationFrontier.TryParse(text.Replace("\n", "\r\n"), 64, out parsed, out _) && parsed.SequenceEqual(list),
            "CRLF line endings are accepted.");
        Check(PregenerationFrontier.TryParse(text, 2, out parsed, out _) && parsed.Count == 2 && parsed[0].Equals(list[0]),
            "Parsing keeps only the most recent zones up to capacity.");
        Check(PregenerationFrontier.TryParse(PregenerationFrontier.Serialize(Array.Empty<ZoneCoord>()), 64, out parsed, out _) && parsed.Count == 0,
            "An empty list is a valid file.");

        Rejects(null!, "missing");
        Rejects("", "header");
        Rejects("BPIP 2\n0\n", "header");
        Rejects("BPIP 1\n-1\n", "count");
        Rejects("BPIP 1\nabc\n", "count");
        Rejects("BPIP 1\n5000\n", "count");
        Rejects("BPIP 1\n3\n1 2\n3 4\n", "truncated");
        Rejects("BPIP 1\n1\n1 2\n3 4\n", "truncated");
        Rejects("BPIP 1\n1\n1,2\n", "line");
        Rejects("BPIP 1\n1\n1 x\n", "line");
        Rejects("BPIP 1\n1\n99999999999 0\n", "line");
        Rejects("BPIP 1\n1\n400 0\n", "range");
        Check(PregenerationFrontier.TryParse("BPIP 1\n2\n1 2\n1 2\n", 64, out parsed, out _) && parsed.Count == 1,
            "Duplicate lines collapse to one zone.");

        string key = PregenerationFrontier.WorldKey("sept_2026", 12345);
        Check(key.Length == 16 && key.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), "World keys are 16 lowercase hex characters.");
        Check(key == PregenerationFrontier.WorldKey("sept_2026", 12345), "The world key is stable.");
        Check(key != PregenerationFrontier.WorldKey("sept_2026", 12346) && key != PregenerationFrontier.WorldKey("sept_2027", 12345),
            "Name and seed both change the key.");
    }

    private static void Rejects(string text, string expected)
    {
        Check(!PregenerationFrontier.TryParse(text, 64, out var zones, out string failure) && failure == expected && zones.Count == 0,
            "Corrupt input '" + (text ?? "null").Replace("\n", "|") + "' fails as " + expected + " without zones (got " + failure + ").");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
