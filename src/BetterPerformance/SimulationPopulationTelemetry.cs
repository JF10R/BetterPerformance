using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Read-only live list sizes. Never enumerates, indexes or retains a game object.
    internal static class SimulationPopulationTelemetry
    {
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool resolved;
        private static MethodInfo? wearPieces, heightmaps, terrainModifiers, slowUpdateObjects;
        private static FieldInfo? slowUpdatesPerFrame, wearUpdatesPerFrameDefault;
        private static string wearUpdatesPerFrameStatus = "unresolved";

        // IMonoUpdater implementors cannot be type-loaded by a standalone CLR, so every
        // member is resolved by name and every read is guarded independently.
        private static MethodInfo? Accessor(string typeName, string methodName)
        {
            try
            {
                Type? type = typeof(ZNet).Assembly.GetType(typeName, false);
                if (type == null) return null;
                var method = AccessTools.DeclaredMethod(type, methodName, Type.EmptyTypes);
                return method != null && method.IsStatic && typeof(ICollection).IsAssignableFrom(method.ReturnType) ? method : null;
            }
            catch { return null; }
        }

        private static FieldInfo? StaticField(string typeName, string fieldName)
        {
            try
            {
                Type? type = typeof(ZNet).Assembly.GetType(typeName, false);
                var field = type?.GetField(fieldName, AnyStatic);
                return field != null && field.IsStatic && field.FieldType == typeof(int) ? field : null;
            }
            catch { return null; }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            wearPieces = Accessor("WearNTear", "GetAllInstances");
            heightmaps = Accessor("Heightmap", "GetAllHeightmaps");
            terrainModifiers = Accessor("TerrainModifier", "GetAllInstances");
            // Vanilla spells this accessor "GetAllInstaces"; the typo is the real name.
            slowUpdateObjects = Accessor("SlowUpdate", "GetAllInstaces");
            slowUpdatesPerFrame = StaticField("SlowUpdater", "m_updatesPerFrame");
            wearUpdatesPerFrameDefault = StaticField("WearNTearUpdater", "c_UpdatesPerFrame");
            try
            {
                Type? updater = typeof(ZNet).Assembly.GetType("WearNTearUpdater", false);
                FieldInfo? live = updater?.GetField("m_updatesPerFrame",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                wearUpdatesPerFrameStatus = live == null ? "unavailable" : "unavailable_instance_field";
            }
            catch { wearUpdatesPerFrameStatus = "unavailable"; }
        }

        private static void Count(List<NumberValue> gauges, List<TextValue> labels,
            MethodInfo? accessor, string name, string unit)
        {
            string status = "unavailable";
            if (accessor != null)
            {
                try
                {
                    // Only the list's Count is read; the list itself is never held or walked.
                    var list = accessor.Invoke(null, null) as ICollection;
                    if (list == null) status = "unavailable_null_list";
                    else { gauges.Add(new NumberValue(name, list.Count, unit)); status = "enabled"; }
                }
                catch { status = "read_failed"; }
            }
            labels.Add(new TextValue(name + "_status", status));
        }

        private static void Read(List<NumberValue> gauges, List<TextValue> labels,
            FieldInfo? field, string name, string unit)
        {
            string status = "unavailable";
            if (field != null)
            {
                try { gauges.Add(new NumberValue(name, (int)field.GetValue(null), unit)); status = "enabled"; }
                catch { status = "read_failed"; }
            }
            labels.Add(new TextValue(name + "_status", status));
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            try { Resolve(); } catch { }
            labels.Add(new TextValue("simulation_population_scope",
                "live_list_sizes_not_per_frame_work; wear_and_support_timings_are_inclusive; " +
                "wear_can_be_disabled_by_other_mods_so_zero_cost_is_itself_a_finding"));
            Count(gauges, labels, wearPieces, "population_wear_pieces", "pieces");
            Count(gauges, labels, heightmaps, "population_heightmaps", "heightmaps");
            Count(gauges, labels, terrainModifiers, "population_terrain_modifiers_legacy", "modifiers");
            Count(gauges, labels, slowUpdateObjects, "population_slow_update_objects", "objects");
            Read(gauges, labels, slowUpdatesPerFrame, "slow_updates_per_frame", "objects");
            // The live wear budget is an instance field on the updater component and has no
            // static accessor; only the compiled-in default is exported, never a live value.
            labels.Add(new TextValue("wear_updates_per_frame_status", wearUpdatesPerFrameStatus));
            Read(gauges, labels, wearUpdatesPerFrameDefault, "wear_updates_per_frame_default", "pieces");
        }
    }
}
