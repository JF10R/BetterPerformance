using System;
using System.Collections.Generic;
using System.Text;

namespace BetterPerformance.Core
{
    public enum SpawnCounter
    {
        NaturalDay, NaturalNight, Event, CapChecks, PointSearches, CappedPointSearches,
        PointSearchFailed, PointsRejected, PointsInPlayerBase, Crowded
    }

    public struct SpawnRow
    {
        public string Name;
        public long NaturalDay, NaturalNight, Event, CapRefused, PointSearches, PointSearchFailed,
            PointsRejected, PointsInPlayerBase, Crowded;
        public long Activity => NaturalDay + NaturalNight + Event + CapRefused + PointSearches + Crowded;
    }

    // Bounded per-prefab spawn counters. Slot 0 is "other": it absorbs prefabs once the
    // named slots are full. Registrations survive Drain; Reset clears them too.
    public sealed class SpawnTally
    {
        public const int OtherSlot = 0;
        private const int Counters = 10;
        private readonly Dictionary<string, int> slots;
        private readonly string[] names;
        private readonly long[] counts;
        private int used = 1;

        public int Capacity { get; }
        public int RegisteredNames => used - 1;

        public SpawnTally(int capacity = 32)
        {
            if (capacity < 1 || capacity > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            slots = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
            names = new string[capacity + 1];
            names[OtherSlot] = "other";
            counts = new long[(capacity + 1) * Counters];
        }

        public int Register(string? name)
        {
            if (string.IsNullOrEmpty(name)) return OtherSlot;
            if (slots.TryGetValue(name!, out int slot)) return slot;
            if (used > Capacity) return OtherSlot;
            slot = used++;
            names[slot] = name!;
            slots[name!] = slot;
            return slot;
        }

        public void Add(int slot, SpawnCounter counter)
        {
            if ((uint)counter >= Counters) return;
            if ((uint)slot >= (uint)used) slot = OtherSlot;
            counts[slot * Counters + (int)counter]++;
        }

        // Rows with any activity, busiest first; rows past topN fold into "other".
        // cap_refused = cap checks not followed by a point search: in the native
        // UpdateSpawnList the cap break is the only exit between those two calls.
        public SpawnRow[] Drain(int topN)
        {
            if (topN < 0) throw new ArgumentOutOfRangeException(nameof(topN));
            var rows = new List<SpawnRow>(used);
            SpawnRow other = Row(OtherSlot);
            for (int slot = 1; slot < used; slot++)
            {
                SpawnRow row = Row(slot);
                if (row.Activity > 0 || row.PointsRejected > 0) rows.Add(row);
            }
            rows.Sort((a, b) => a.Activity != b.Activity ? b.Activity.CompareTo(a.Activity) : string.CompareOrdinal(a.Name, b.Name));
            for (int i = rows.Count - 1; i >= topN; i--)
            {
                Merge(ref other, rows[i]);
                rows.RemoveAt(i);
            }
            if (other.Activity > 0 || other.PointsRejected > 0) rows.Add(other);
            Array.Clear(counts, 0, counts.Length);
            return rows.ToArray();
        }

        public void Reset()
        {
            Array.Clear(counts, 0, counts.Length);
            for (int i = 1; i < used; i++) names[i] = null!;
            slots.Clear();
            used = 1;
        }

        // Prefab names become gauge-name segments: lowercase ASCII letters, digits, '_'.
        public static string GaugeKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                char lower = char.ToLowerInvariant(c);
                builder.Append((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') ? lower : '_');
            }
            return builder.ToString();
        }

        private SpawnRow Row(int slot)
        {
            int b = slot * Counters;
            long capChecks = counts[b + (int)SpawnCounter.CapChecks];
            long capped = counts[b + (int)SpawnCounter.CappedPointSearches];
            return new SpawnRow
            {
                Name = names[slot],
                NaturalDay = counts[b + (int)SpawnCounter.NaturalDay],
                NaturalNight = counts[b + (int)SpawnCounter.NaturalNight],
                Event = counts[b + (int)SpawnCounter.Event],
                CapRefused = Math.Max(0, capChecks - capped),
                PointSearches = counts[b + (int)SpawnCounter.PointSearches],
                PointSearchFailed = counts[b + (int)SpawnCounter.PointSearchFailed],
                PointsRejected = counts[b + (int)SpawnCounter.PointsRejected],
                PointsInPlayerBase = counts[b + (int)SpawnCounter.PointsInPlayerBase],
                Crowded = counts[b + (int)SpawnCounter.Crowded]
            };
        }

        private static void Merge(ref SpawnRow into, SpawnRow from)
        {
            into.NaturalDay += from.NaturalDay;
            into.NaturalNight += from.NaturalNight;
            into.Event += from.Event;
            into.CapRefused += from.CapRefused;
            into.PointSearches += from.PointSearches;
            into.PointSearchFailed += from.PointSearchFailed;
            into.PointsRejected += from.PointsRejected;
            into.PointsInPlayerBase += from.PointsInPlayerBase;
            into.Crowded += from.Crowded;
        }
    }
}
