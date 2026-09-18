using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // MineRock5.DamageArea places the drops, the hit effect, the destroy effect, the damage
    // text and the 10 m AddNoise origin at hitArea.m_collider.bounds.center when the prefab field
    // m_hitEffectAreaCenter is true, and at hit.m_point when it is false. A buried chunk
    // therefore drops ore underground until ItemDrop.SlowUpdate lifts it. This module
    // clears that field on the listed prefabs, selecting the hit point the native code
    // already supports. No RPC, ownership, drop table or damage change.
    internal static class MiningDropPlacement
    {
        private const int TrackedLimit = 4096;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".MiningDropPlacement");
        private static readonly Dictionary<MineRock5, bool> Tracked = new Dictionary<MineRock5, bool>();
        private static readonly HashSet<string> Prefabs = new HashSet<string>(StringComparer.Ordinal);
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<string>? prefabList;
        private static long applied, restored, seen, capped, failures;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        internal static int TrackedCount => Tracked.Count;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("Mining", "DropAtHitPointEnabled", false,
                "Experimental. Place mined drops and hit effects at the pickaxe impact point instead of the destroyed chunk centre for the listed fractured-rock prefabs. Owner-side field override; remote clients need no plugin. Requires restart to install.");
            prefabList = config.Bind("Mining", "DropAtHitPointPrefabs", "rock4_copper_frac",
                "Comma-separated MineRock5 prefab names the hit-point placement applies to. Any other prefab keeps the native chunk-centre placement.");
            if (!option.Value) { Status = "disabled"; return; }
            try
            {
                MethodInfo lifecycle = ValidateContracts();
                Prefabs.Clear();
                foreach (string name in prefabList.Value.Split(','))
                {
                    string trimmed = name.Trim();
                    if (trimmed.Length > 0) Prefabs.Add(trimmed);
                }
                Patches.Patch(lifecycle, postfix: new HarmonyMethod(typeof(MiningDropPlacement), nameof(AfterAwake)));
                Installed = Enabled = true;
                Status = "installed";
                logger.LogWarning("Experimental mined-drop hit-point placement installed for " + Prefabs.Count + " prefab name(s).");
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = "unavailable";
                Prefabs.Clear();
                try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
                logger.LogWarning("Mined-drop hit-point placement unavailable; native placement retained: " +
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        // Awake is the lifecycle method that registers RPC_Damage, so the field is set
        // before any damage can reach this instance.
        private static MethodInfo ValidateContracts()
        {
            MethodInfo awake = AccessTools.DeclaredMethod(typeof(MineRock5), "Awake", Type.EmptyTypes)
                ?? throw new InvalidOperationException("MineRock5.Awake is missing.");
            if (awake.IsStatic || awake.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported MineRock5.Awake signature.");
            FieldInfo? center = AccessTools.DeclaredField(typeof(MineRock5), "m_hitEffectAreaCenter");
            if (center == null || center.IsStatic || !center.IsPublic || center.FieldType != typeof(bool))
                throw new InvalidOperationException("Unsupported MineRock5.m_hitEffectAreaCenter field.");
            MethodInfo damage = AccessTools.DeclaredMethod(typeof(MineRock5), "DamageArea", new[] { typeof(int), typeof(HitData) })
                ?? throw new InvalidOperationException("MineRock5.DamageArea(int, HitData) is missing.");
            if (damage.ReturnType != typeof(bool) || AccessTools.DeclaredField(typeof(HitData), "m_point")?.FieldType != typeof(UnityEngine.Vector3))
                throw new InvalidOperationException("Unsupported MineRock5 damage contract.");
            if (AccessTools.DeclaredMethod(typeof(Utils), "GetPrefabName", new[] { typeof(string) })?.ReturnType != typeof(string))
                throw new InvalidOperationException("Utils.GetPrefabName(string) is missing.");
            return awake;
        }

        private static void AfterAwake(MineRock5 __instance)
        {
            if (!Installed || __instance == null) return;
            try
            {
                Interlocked.Increment(ref seen);
                if (!Prefabs.Contains(Utils.GetPrefabName(__instance.gameObject.name))) return;
                if (Tracked.ContainsKey(__instance)) return;
                if (Tracked.Count >= TrackedLimit) Prune();
                // Past the cap the rock keeps vanilla placement; count it so a healthy
                // "applied" figure cannot hide instances the override never reached.
                if (Tracked.Count >= TrackedLimit) { Interlocked.Increment(ref capped); return; }
                Tracked.Add(__instance, __instance.m_hitEffectAreaCenter);
                if (Enabled) Override(__instance, false);
            }
            catch { Interlocked.Increment(ref failures); }
        }

        private static void Prune()
        {
            var dead = new List<MineRock5>();
            // Unity's == reports a destroyed object as null while the reference is alive.
            foreach (var entry in Tracked) if (entry.Key == null) dead.Add(entry.Key!);
            foreach (var instance in dead) Tracked.Remove(instance);
        }

        private static void Override(MineRock5 instance, bool restore)
        {
            bool wanted = restore && Tracked[instance];
            if (instance.m_hitEffectAreaCenter == wanted) return;
            instance.m_hitEffectAreaCenter = wanted;
            if (restore) Interlocked.Increment(ref restored);
            else Interlocked.Increment(ref applied);
        }

        // Main-thread runtime toggle for an A/B of the hit-effect position inside one
        // session. Returns the number of live tracked instances it reached.
        internal static int SetEnabled(bool enabled)
        {
            Enabled = Installed && enabled;
            int reached = 0;
            try
            {
                Prune();
                foreach (var entry in Tracked)
                {
                    if (entry.Key == null) continue;
                    Override(entry.Key, !Enabled);
                    reached++;
                }
            }
            catch { Interlocked.Increment(ref failures); }
            return reached;
        }

        internal static int OverriddenCount()
        {
            int count = 0;
            foreach (var entry in Tracked)
                if (entry.Key != null && entry.Value && !entry.Key.m_hitEffectAreaCenter) count++;
            return count;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("mining_hitpoint_instances_applied", Interlocked.Exchange(ref applied, 0), "instances"));
            gauges.Add(new NumberValue("mining_hitpoint_instances_restored", Interlocked.Exchange(ref restored, 0), "instances"));
            gauges.Add(new NumberValue("mining_hitpoint_instances_seen", Interlocked.Exchange(ref seen, 0), "instances"));
            gauges.Add(new NumberValue("mining_hitpoint_instances_capped", Interlocked.Exchange(ref capped, 0), "instances"));
            gauges.Add(new NumberValue("mining_hitpoint_probe_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            labels.Add(new TextValue("mining_hitpoint_status", Status));
            labels.Add(new TextValue("mining_hitpoint_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("mining_hitpoint_prefabs", prefabList?.Value ?? string.Empty));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref applied, 0);
            Interlocked.Exchange(ref restored, 0);
            Interlocked.Exchange(ref seen, 0);
            Interlocked.Exchange(ref capped, 0);
            Interlocked.Exchange(ref failures, 0);
        }

        internal static void Uninstall()
        {
            if (Installed) SetEnabled(false);
            Reset();
            Tracked.Clear();
            Prefabs.Clear();
            try { Patches.UnpatchSelf(); } catch { }
            Installed = Enabled = false;
            Status = "disabled";
            option = null;
            prefabList = null;
        }
    }
}
