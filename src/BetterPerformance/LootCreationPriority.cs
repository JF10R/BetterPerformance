using System;
using System.Collections.Generic;
using BetterPerformance.Core;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace BetterPerformance
{
    internal static class LootCreationPriority
    {
        private const int CandidateLimit = 16384, PrefabCacheLimit = 2048;
        private static readonly List<ZDO> Scratch = new List<ZDO>();
        private static readonly HashSet<int> LootPrefabs = new HashSet<int>();
        private static readonly Dictionary<int, bool> PrefabKinds = new Dictionary<int, bool>();
        private static readonly Func<ZDO, int> Tier = zdo => (int)zdo.Type;
        private static readonly Predicate<ZDO> IsNearbyLoot = zdo =>
            zdo.m_tempSortValue <= radiusSquared && LootPrefabs.Contains(zdo.GetPrefab());
        private static ConfigEntry<bool> enabled = null!;
        private static ConfigEntry<float> radius = null!;
        private static WeakReference<ZNetScene>? scene;
        private static ManualLogSource logger = null!;
        private static int priorityTurns;
        private static float radiusSquared;
        private static bool failed;
        private static long priorityPasses, vanillaPasses, classifiedCandidates, boundedSkips;

        internal static void Configure(ConfigFile config, ManualLogSource log)
        {
            logger = log;
            enabled = config.Bind("ObjectLoading", "PrioritizeNearbyLoot", false,
                "Experimental stable loot priority inside vanilla object-type tiers. Every fourth eligible pass retains vanilla ordering. Requires the object budget; unmeasured.");
            radius = config.Bind("ObjectLoading", "LootPriorityRadius", 20f,
                new ConfigDescription("Maximum loot-priority distance from the local simulation reference position, in metres.",
                    new AcceptableValueRange<float>(1f, 64f)));
        }

        internal static void Apply(List<ZDO> candidates)
        {
            if (!enabled.Value || failed || candidates.Count < 2) return;
            var currentScene = ZNetScene.instance;
            if (currentScene == null) return;
            if (scene == null || !scene.TryGetTarget(out var previousScene) || !ReferenceEquals(previousScene, currentScene))
            {
                scene = new WeakReference<ZNetScene>(currentScene);
                PrefabKinds.Clear();
                priorityTurns = 0;
            }
            if (candidates.Count > CandidateLimit) { boundedSkips++; return; }
            float distance = radius.Value;
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 1 || distance > 64) return;
            radiusSquared = distance * distance;
            if (!CreationScheduling.TakePriorityTurn(ref priorityTurns)) { vanillaPasses++; return; }
            try
            {
                // Resolve prefab components once per cached hash, outside sorting.
                // Missing prefabs are not cached so later registration can succeed.
                foreach (var zdo in candidates)
                {
                    if (!(zdo.m_tempSortValue <= radiusSquared)) continue;
                    int hash = zdo.GetPrefab();
                    if (!PrefabKinds.TryGetValue(hash, out bool isLoot))
                    {
                        var prefab = currentScene.GetPrefab(hash);
                        if (prefab == null) continue;
                        isLoot = prefab.GetComponent<ItemDrop>() != null;
                        if (PrefabKinds.Count >= PrefabCacheLimit) { boundedSkips++; return; }
                        PrefabKinds.Add(hash, isLoot);
                    }
                    if (isLoot) LootPrefabs.Add(hash);
                }
                if (LootPrefabs.Count == 0) return;
                classifiedCandidates += CreationScheduling.PrioritizeWithinTiers(candidates, Scratch, Tier, IsNearbyLoot);
                priorityPasses++;
            }
            catch (Exception exception)
            {
                // Classification finishes before list mutation; retain vanilla
                // order and stop retrying a failing optimization for this process.
                failed = true;
                logger.LogWarning("Loot priority disabled after classification failure: " + exception.GetType().Name);
            }
            finally { LootPrefabs.Clear(); Scratch.Clear(); }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels, bool budgetEnabled)
        {
            labels.Add(new TextValue("loot_priority_enabled", budgetEnabled && enabled.Value && !failed ? "true" : "false"));
            labels.Add(new TextValue("loot_priority_status", failed ? "failed" : "available"));
            gauges.Add(new NumberValue("loot_priority_radius", radius.Value, "metres"));
            gauges.Add(new NumberValue("loot_priority_passes_total", priorityPasses, "passes"));
            gauges.Add(new NumberValue("loot_vanilla_order_passes_total", vanillaPasses, "passes"));
            gauges.Add(new NumberValue("loot_priority_candidates_total", classifiedCandidates, "candidates"));
            gauges.Add(new NumberValue("loot_priority_bounded_skips_total", boundedSkips, "passes"));
        }

        internal static void Reset()
        {
            scene = null;
            PrefabKinds.Clear();
            LootPrefabs.Clear();
            Scratch.Clear();
            priorityTurns = 0;
        }
    }
}
