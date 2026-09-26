using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // A peer that receives another player's terrain edit reloads the whole TerrainComp and pokes its
    // heightmap for a Full rebuild (collision + render mesh, ~5 ms), even for a paint-only edit (hoe
    // path, cultivator): the ZDO does not say which. The owner itself uses Poke(1, paintOnly) for those
    // (TerrainComp.DoOperation). This compares the height arrays before and after Load and, when they
    // are identical, asks for the same PaintOnly regeneration: the meshes depend only on heights and
    // biome colours, so they are unchanged. docs/terrain-paint-only-reload.md.
    internal static class TerrainPaintOnlyReload
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".TerrainPaintOnlyReload");
        private static ConfigEntry<bool>? option;
        private static AccessTools.FieldRef<TerrainComp, bool[]>? modifiedHeight;
        private static AccessTools.FieldRef<TerrainComp, float[]>? levelDelta, smoothDelta;
        private static AccessTools.FieldRef<TerrainComp, uint>? lastRevision;
        private static AccessTools.FieldRef<TerrainComp, ZNetView>? view;
        private static AccessTools.FieldRef<TerrainComp, MonoBehaviour>? heightmapOf;
        // One reload at a time: CheckLoad is main-thread and not re-entrant.
        private static TerrainComp? pending;
        private static bool[] savedModified = Array.Empty<bool>();
        private static float[] savedLevel = Array.Empty<float>(), savedSmooth = Array.Empty<float>();
        private static long paintOnlyReloads, fullReloads, failures;
        private static double compareMsMax;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("Terrain", "PaintOnlyReloadEnabled", false,
                "When another player's terrain edit arrives and no height changed (hoe path, cultivator), refresh only the paint like the editing player does, " +
                "instead of rebuilding the collision and render meshes. Requires restart.");
            if (!option.Value) return;
            try
            {
                Type heightmap = typeof(ZNet).Assembly.GetType("Heightmap", false) ?? throw new InvalidOperationException("Heightmap is unavailable.");
                modifiedHeight = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
                levelDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
                smoothDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
                lastRevision = AccessTools.FieldRefAccess<TerrainComp, uint>("m_lastDataRevision");
                view = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
                heightmapOf = AccessTools.FieldRefAccess<TerrainComp, MonoBehaviour>("m_hmap");
                var checkLoad = AccessTools.DeclaredMethod(typeof(TerrainComp), "CheckLoad", Type.EmptyTypes);
                var poke = AccessTools.DeclaredMethod(heightmap, "Poke", new[] { typeof(int), typeof(bool) });
                if (checkLoad == null || checkLoad.ReturnType != typeof(void) || poke == null || poke.ReturnType != typeof(void))
                    throw new InvalidOperationException("TerrainComp.CheckLoad or Heightmap.Poke(int, bool) is missing.");
                Patches.Patch(checkLoad, prefix: new HarmonyMethod(typeof(TerrainPaintOnlyReload), nameof(BeforeCheckLoad)),
                    finalizer: new HarmonyMethod(typeof(TerrainPaintOnlyReload), nameof(AfterCheckLoad)));
                Patches.Patch(poke, prefix: new HarmonyMethod(typeof(TerrainPaintOnlyReload), nameof(BeforePoke)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Terrain paint-only reload installed.");
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                Installed = false;
                Status = "unavailable";
                logger.LogWarning("Terrain paint-only reload unavailable; every reload stays a full rebuild: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        // Runs every frame for every TerrainComp: only a new data revision costs anything.
        private static void BeforeCheckLoad(TerrainComp __instance)
        {
            pending = null;
            try
            {
                ZNetView nview = view!(__instance);
                if (nview == null || !nview.IsValid() || nview.GetZDO().DataRevision == lastRevision!(__instance)) return;
                bool[] modified = modifiedHeight!(__instance);
                float[] level = levelDelta!(__instance), smooth = smoothDelta!(__instance);
                if (modified == null || level == null || smooth == null) return;
                Copy(modified, ref savedModified);
                Copy(level, ref savedLevel);
                Copy(smooth, ref savedSmooth);
                pending = __instance;
            }
            catch { failures++; pending = null; }
        }

        private static void AfterCheckLoad() => pending = null;

        // CheckLoad's own m_hmap.Poke(): immediate (delayed 0) and Full. Anything else is left alone.
        private static void BeforePoke(object __instance, int delayed, ref bool paintOnly)
        {
            TerrainComp? comp = pending;
            if (comp == null || paintOnly || delayed != 0) return;
            pending = null;
            try
            {
                if (!ReferenceEquals(heightmapOf!(comp), __instance)) return;
                long started = Stopwatch.GetTimestamp();
                bool same = TerrainHeightSnapshot.Equal(savedModified, savedLevel, savedSmooth, modifiedHeight!(comp), levelDelta!(comp), smoothDelta!(comp));
                double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (ms > compareMsMax) compareMsMax = ms;
                if (same) { paintOnly = true; paintOnlyReloads++; }
                else fullReloads++;
            }
            catch { failures++; }
        }

        private static void Copy<T>(T[] source, ref T[] target)
        {
            if (target.Length != source.Length) target = new T[source.Length];
            Array.Copy(source, target, source.Length);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("terrain_paint_only_status", Status));
            if (!Installed) return;
            gauges.Add(new NumberValue("terrain_reload_paint_only", Take(ref paintOnlyReloads), "reloads"));
            gauges.Add(new NumberValue("terrain_reload_full", Take(ref fullReloads), "reloads"));
            gauges.Add(new NumberValue("terrain_reload_compare_ms_max", Math.Round(compareMsMax, 3), "ms"));
            gauges.Add(new NumberValue("terrain_paint_only_failures", Take(ref failures), "calls"));
            compareMsMax = 0;
        }

        internal static void Reset()
        {
            paintOnlyReloads = fullReloads = failures = 0;
            compareMsMax = 0;
        }

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { }
            Installed = false;
            Status = "disabled";
            pending = null;
            Reset();
        }

        private static long Take(ref long counter)
        {
            long value = counter;
            counter = 0;
            return value;
        }
    }
}
