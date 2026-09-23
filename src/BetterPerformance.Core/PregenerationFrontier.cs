using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BetterPerformance.Core
{
    // A ZoneSystem zone index (64 m zones), without a Unity type.
    public readonly struct ZoneCoord : IEquatable<ZoneCoord>
    {
        public ZoneCoord(int x, int y) { X = x; Y = y; }

        public int X { get; }
        public int Y { get; }

        public bool Equals(ZoneCoord other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is ZoneCoord other && Equals(other);
        public override int GetHashCode() => unchecked(X * 397 ^ Y);
        public override string ToString() => X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture);
    }

    // Zones where players were recently seen, most recent first, no duplicates, bounded.
    public sealed class ActivityZones
    {
        private readonly List<ZoneCoord> zones = new List<ZoneCoord>();

        public ActivityZones(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public int Capacity { get; }
        public int Count => zones.Count;
        public IReadOnlyList<ZoneCoord> MostRecentFirst => zones;

        // True when the order or the contents changed, i.e. when a save would differ.
        public bool Touch(ZoneCoord zone)
        {
            if (zones.Count > 0 && zones[0].Equals(zone)) return false;
            int index = zones.IndexOf(zone);
            if (index >= 0) zones.RemoveAt(index);
            else if (zones.Count == Capacity) zones.RemoveAt(zones.Count - 1);
            zones.Insert(0, zone);
            return true;
        }

        public void Replace(IReadOnlyList<ZoneCoord> mostRecentFirst)
        {
            zones.Clear();
            foreach (ZoneCoord zone in mostRecentFirst)
            {
                if (zones.Count == Capacity) break;
                if (!zones.Contains(zone)) zones.Add(zone);
            }
        }
    }

    // Idle pre-generation planning and its per-world activity file. Pure: the caller
    // supplies what the game knows (activity, the generated-zone test) and gets an order.
    public static class PregenerationFrontier
    {
        public const string Header = "BPIP 1";
        public const int MaxRadius = 32;
        public const int MaxStoredZones = 4096;
        // Vanilla ZonesWithinRadius(..., ghostZone: true) accepts a zone at radius + 0.8 zones.
        public const double GhostMargin = 0.8;
        private const long WaterEdgeMeters = 10500, ZoneMeters = 64;

        // Zone centre inside the 10,500 m water edge; nothing a player can reach lies beyond.
        public static bool InWorld(ZoneCoord zone) =>
            ((long)zone.X * zone.X + (long)zone.Y * zone.Y) * ZoneMeters * ZoneMeters <= WaterEdgeMeters * WaterEdgeMeters;

        // Ungenerated zones within `radius` of any activity zone. Each zone is ranked by the most
        // recent activity zone that reaches it, then by distance to that zone; ties by y, x.
        public static List<ZoneCoord> Plan(IReadOnlyList<ZoneCoord> activityMostRecentFirst, int radius,
            Func<ZoneCoord, bool> isGenerated, int limit)
        {
            if (activityMostRecentFirst == null) throw new ArgumentNullException(nameof(activityMostRecentFirst));
            if (isGenerated == null) throw new ArgumentNullException(nameof(isGenerated));
            if (radius < 0 || radius > MaxRadius) throw new ArgumentOutOfRangeException(nameof(radius));
            var result = new List<ZoneCoord>();
            if (limit <= 0) return result;
            double reach = radius + GhostMargin;
            double reachSquared = reach * reach;
            var seen = new HashSet<ZoneCoord>();
            var picks = new List<(int Rank, int Distance, ZoneCoord Zone)>();
            for (int rank = 0; rank < activityMostRecentFirst.Count; rank++)
            {
                ZoneCoord center = activityMostRecentFirst[rank];
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int distance = dx * dx + dy * dy;
                        if (distance >= reachSquared) continue;
                        var zone = new ZoneCoord(center.X + dx, center.Y + dy);
                        if (!InWorld(zone) || !seen.Add(zone) || isGenerated(zone)) continue;
                        picks.Add((rank, distance, zone));
                    }
            }
            picks.Sort((a, b) =>
            {
                int order = a.Rank.CompareTo(b.Rank);
                if (order == 0) order = a.Distance.CompareTo(b.Distance);
                if (order == 0) order = a.Zone.Y.CompareTo(b.Zone.Y);
                return order != 0 ? order : a.Zone.X.CompareTo(b.Zone.X);
            });
            for (int i = 0; i < picks.Count && result.Count < limit; i++) result.Add(picks[i].Zone);
            return result;
        }

        // 16 hex characters of SHA-256 over name and seed: a stable file name per world.
        public static string WorldKey(string worldName, int seed)
        {
            if (worldName == null) throw new ArgumentNullException(nameof(worldName));
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(worldName + "\n" + seed.ToString(CultureInfo.InvariantCulture)));
            var text = new StringBuilder(16);
            for (int i = 0; i < 8; i++) text.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        // Header, count, then one "x y" line per zone, most recent first.
        public static string Serialize(IReadOnlyList<ZoneCoord> mostRecentFirst)
        {
            if (mostRecentFirst == null) throw new ArgumentNullException(nameof(mostRecentFirst));
            int count = Math.Min(mostRecentFirst.Count, MaxStoredZones);
            var text = new StringBuilder(Header.Length + 16 + count * 12);
            text.Append(Header).Append('\n').Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < count; i++)
                text.Append(mostRecentFirst[i].X.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(mostRecentFirst[i].Y.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return text.ToString();
        }

        // Never throws on bad text: the file is untrusted input and a bad one costs only history.
        // The count line makes a truncated write fail as a whole instead of loading half a list.
        public static bool TryParse(string text, int capacity, out List<ZoneCoord> zones, out string failure)
        {
            zones = new List<ZoneCoord>();
            if (text == null) { failure = "missing"; return false; }
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int end = lines.Length;
            while (end > 0 && lines[end - 1].Length == 0) end--;
            if (end < 2 || lines[0] != Header) { failure = "header"; return false; }
            if (!int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count > MaxStoredZones)
            { failure = "count"; return false; }
            if (end - 2 != count) { failure = "truncated"; return false; }
            var seen = new HashSet<ZoneCoord>();
            for (int i = 0; i < count; i++)
            {
                string[] parts = lines[i + 2].Split(' ');
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x) ||
                    !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y))
                { zones.Clear(); failure = "line"; return false; }
                var zone = new ZoneCoord(x, y);
                if (!InWorld(zone)) { zones.Clear(); failure = "range"; return false; }
                if (zones.Count < capacity && seen.Add(zone)) zones.Add(zone);
            }
            failure = "ok";
            return true;
        }
    }
}
