using System;
using System.Linq;
using BetterPerformance.Core;

// Covers the composite key that splits an allow-listed routed RPC by its target
// prefab. The hook itself needs the game assemblies and is verified in the game
// harness; what is testable offline is the key algebra and the exported name.
internal static class AttributionTargetSplitTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        PackRoundTrip();
        MixIsStableAndSeparating();
        Naming();
        GroupsByTarget();
        HotPathAllocation();
    }

    private static void PackRoundTrip()
    {
        int[] hashes = { 0, 1, -1, int.MinValue, int.MaxValue, -1234567, 987654321 };
        foreach (int owner in hashes)
            foreach (int target in hashes)
            {
                long packed = CompositeKey.Pack(owner, target);
                Check(CompositeKey.Owner(packed) == owner && CompositeKey.Target(packed) == target,
                    "A packed pair must survive negative hashes unchanged: " + owner + "/" + target);
            }
        Check(CompositeKey.Pack(3, 4) != CompositeKey.Pack(4, 3), "The pair is ordered; owner and target are not interchangeable.");
    }

    private static void MixIsStableAndSeparating()
    {
        Check(CompositeKey.Mix(17, 42) == CompositeKey.Mix(17, 42), "The same pair must always mix to the same key.");
        Check(CompositeKey.Mix(17, 42) != CompositeKey.Mix(17, 43), "Two targets of one RPC must not share a key.");
        Check(CompositeKey.Mix(17, 42) != CompositeKey.Mix(18, 42), "Two RPCs on one target must not share a key.");
        // 128 plausible pairs is half the 256-key group capacity; collisions there
        // would merge rows, so the separation is asserted, not assumed.
        int[] rpcs = Enumerable.Range(0, 8).Select(i => unchecked(i * -1103515245 + 12345)).ToArray();
        int[] prefabs = Enumerable.Range(0, 16).Select(i => unchecked(i * 16777619 - 2147483000)).ToArray();
        int[] keys = rpcs.SelectMany(rpc => prefabs.Select(prefab => CompositeKey.Mix(rpc, prefab))).ToArray();
        Check(keys.Distinct().Count() == keys.Length, "A realistic RPC/prefab cross product must not collide.");
        Check(CompositeKey.Mix(11, 0) != CompositeKey.Mix(11, 1),
            "An unresolved target (prefab 0) must be distinguishable from a real prefab.");
    }

    private static void Naming()
    {
        Check(CompositeKey.Name("RPC_Damage", "Greydwarf") == "RPC_Damage→Greydwarf",
            "An exported key reads as the RPC, the separator, then the target prefab.");
        Check(CompositeKey.Name("RPC_Damage", CompositeKey.NoTarget) == "RPC_Damage→(none)",
            "A destroyed target names the RPC and reports no prefab.");
        Check(CompositeKey.Name("RPC_Damage", null!) == "RPC_Damage→(none)" &&
            CompositeKey.Name("RPC_Damage", "") == "RPC_Damage→(none)",
            "A missing target name falls back rather than exporting an empty key.");
        Check(CompositeKey.Name("", "Greydwarf") == "?→Greydwarf", "An unnamed RPC still exports its target.");
        Check(CompositeKey.Name("hash:7", "prefab:9") == "hash:7→prefab:9",
            "Hash fallbacks pass through both sides of the key unchanged.");
    }

    private static void GroupsByTarget()
    {
        const int damage = 981, greydwarf = 17, troll = 18;
        var group = new KeyedAggregator("routed_rpc_target", 256);
        group.Record(CompositeKey.Mix(damage, greydwarf), 2);
        group.Record(CompositeKey.Mix(damage, troll), 9);
        group.Record(CompositeKey.Mix(damage, greydwarf), 3);
        var pairs = new System.Collections.Generic.Dictionary<int, long>
        {
            [CompositeKey.Mix(damage, greydwarf)] = CompositeKey.Pack(damage, greydwarf),
            [CompositeKey.Mix(damage, troll)] = CompositeKey.Pack(damage, troll)
        };
        string Resolve(int key)
        {
            if (!pairs.TryGetValue(key, out long packed)) return "composite:" + key;
            int prefab = CompositeKey.Target(packed);
            return CompositeKey.Name("RPC_Damage", prefab == greydwarf ? "Greydwarf" : prefab == troll ? "Troll" : CompositeKey.NoTarget);
        }
        var rows = group.Drain(8, Resolve);
        Check(rows.Length == 2, "One row per observed target, with no remainder row when all keys are kept.");
        Check(rows[0].Key == "RPC_Damage→Troll" && rows[0].SumMs == 9, "The heaviest target leads the group.");
        Check(rows[1].Key == "RPC_Damage→Greydwarf" && rows[1].Count == 2 && rows[1].SumMs == 5,
            "Repeated hits on one target accumulate into a single row.");
        Check(rows.All(row => row.Group == "routed_rpc_target"), "Every row carries the new group name.");

        var unmapped = new KeyedAggregator("routed_rpc_target", 256);
        unmapped.Record(CompositeKey.Mix(damage, 4242), 1);
        Check(unmapped.Drain(8, Resolve)[0].Key.StartsWith("composite:", StringComparison.Ordinal),
            "A key whose pair was never recorded exports as the composite, not as a wrong name.");
    }

    private static void HotPathAllocation()
    {
        var pairs = new System.Collections.Generic.Dictionary<int, long>();
        for (int i = 0; i < 64; i++) pairs[CompositeKey.Mix(7, i)] = CompositeKey.Pack(7, i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long sink = 0;
        for (int i = 0; i < 10000; i++)
        {
            int key = CompositeKey.Mix(7, i & 63);
            pairs.TryGetValue(key, out long packed);
            sink += packed + CompositeKey.Owner(packed) + CompositeKey.Target(packed);
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == before && sink != 0,
            "Keying and looking up a warm pair must allocate no managed bytes.");
    }
}
