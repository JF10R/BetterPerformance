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
    // InventoryGui.SetActiveGroup(int, bool) creates m_setActiveGroupEffects whenever
    // playSound is true, including when the requested index is already m_activeGroup, so a
    // Craft/Upgrade tab click stacks that effect on the button's own ButtonSfx click. This
    // prefix clears playSound in exactly that case. A real group change keeps its sound,
    // the button click sound is never touched, and nothing else in the method changes.
    internal static class GuiSoundDeduplication
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".GuiSoundDeduplication");
        private static ConfigEntry<bool>? option;
        private static FieldInfo? groupsField;
        private static AccessTools.FieldRef<InventoryGui, int>? activeGroup;
        private static AccessTools.FieldRef<InventoryGui, bool>? cycling;
        private static long suppressed, kept, failures;

        internal static bool Installed { get; private set; }
        internal static bool Enabled => Installed && option != null && option.Value;
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("Gui", "DeduplicateGroupSoundEnabled", false,
                "Skip the group-change sound when the requested inventory group is already active. Button click sounds are untouched. Requires restart to install.");
            if (!option.Value) { Status = "disabled"; return; }
            MethodInfo target;
            try { target = ValidateContracts(); }
            catch (Exception exception)
            {
                Release("unavailable");
                logger.LogWarning("GUI group-sound deduplication unavailable; native sounds retained: " +
                    exception.GetType().Name + ": " + exception.Message);
                return;
            }
            try
            {
                Patches.Patch(target, prefix: new HarmonyMethod(typeof(GuiSoundDeduplication), nameof(Prefix)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("GUI group-sound deduplication installed on the already-active group path.");
            }
            catch (Exception exception)
            {
                Release("patch_failed");
                try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
                logger.LogWarning("GUI group-sound deduplication could not patch SetActiveGroup: " + exception.GetType().Name);
            }
        }

        // Only the int overload is patched. SetActiveGroup(UIGroupHandler, bool) forwards to
        // it, so patching both would evaluate the same call twice.
        private static MethodInfo ValidateContracts()
        {
            MethodInfo target = AccessTools.DeclaredMethod(typeof(InventoryGui), "SetActiveGroup", new[] { typeof(int), typeof(bool) })
                ?? throw new InvalidOperationException("InventoryGui.SetActiveGroup(int, bool) is missing.");
            if (target.IsStatic || target.IsPublic || target.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported SetActiveGroup signature.");
            FieldInfo groups = AccessTools.DeclaredField(typeof(InventoryGui), "m_uiGroups")
                ?? throw new InvalidOperationException("InventoryGui.m_uiGroups is missing.");
            if (!groups.FieldType.IsArray || groups.FieldType.GetElementType()?.Name != "UIGroupHandler")
                throw new InvalidOperationException("Unsupported InventoryGui.m_uiGroups element type.");
            if (AccessTools.DeclaredField(typeof(InventoryGui), "m_activeGroup")?.FieldType != typeof(int) ||
                AccessTools.DeclaredField(typeof(InventoryGui), "m_inventoryGroupCycling")?.FieldType != typeof(bool) ||
                AccessTools.DeclaredField(typeof(InventoryGui), "m_setActiveGroupEffects")?.FieldType != typeof(EffectList))
                throw new InvalidOperationException("Unsupported InventoryGui group fields.");
            groupsField = groups;
            activeGroup = AccessTools.FieldRefAccess<InventoryGui, int>("m_activeGroup");
            cycling = AccessTools.FieldRefAccess<InventoryGui, bool>("m_inventoryGroupCycling");
            return target;
        }

        // Unity UI only, so no thread guard is needed. A failure counts and leaves
        // playSound exactly as the caller passed it.
        private static void Prefix(InventoryGui __instance, int __0, ref bool __1)
        {
            // Native plays nothing without a local player, so a suppression there would
            // count a sound that was never going to play.
            if (!__1 || !Enabled || __instance == null || !Player.m_localPlayer) return;
            try
            {
                if (Redundant(__instance, __0)) { __1 = false; Interlocked.Increment(ref suppressed); }
                else Interlocked.Increment(ref kept);
            }
            catch { Interlocked.Increment(ref failures); }
        }

        // The cycling path wraps the index instead of clamping it, so it is left alone.
        private static bool Redundant(InventoryGui instance, int index)
        {
            if (cycling == null || activeGroup == null || groupsField == null) return false;
            if (cycling(instance)) return false;
            if (!(groupsField.GetValue(instance) is Array groups) || groups.Length == 0) return false;
            int clamped = index < 0 ? 0 : index > groups.Length - 1 ? groups.Length - 1 : index;
            return activeGroup(instance) == clamped;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("gui_group_sound_suppressed", Interlocked.Exchange(ref suppressed, 0), "calls"));
            gauges.Add(new NumberValue("gui_group_sound_kept", Interlocked.Exchange(ref kept, 0), "calls"));
            gauges.Add(new NumberValue("gui_sound_probe_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            labels.Add(new TextValue("gui_sound_dedup_status", Status));
            labels.Add(new TextValue("gui_sound_dedup_enabled", Enabled ? "true" : "false"));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref suppressed, 0);
            Interlocked.Exchange(ref kept, 0);
            Interlocked.Exchange(ref failures, 0);
        }

        internal static void Uninstall()
        {
            Reset();
            try { Patches.UnpatchSelf(); } catch { }
            Release("disabled");
            option = null;
        }

        private static void Release(string status)
        {
            Installed = false;
            Status = status;
            groupsField = null;
            activeGroup = null;
            cycling = null;
        }
    }
}
