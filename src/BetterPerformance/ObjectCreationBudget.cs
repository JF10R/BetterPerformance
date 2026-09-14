using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BetterPerformance.Core;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace BetterPerformance
{
    // Adds cooperative exits to vanilla enumerations; never replaces creation,
    // readiness, sorting, count limits, invalid-prefab handling or disposal.
    internal static class ObjectCreationBudget
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".ObjectCreationBudget");
        private static ConfigEntry<float> milliseconds = null!;
        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; set; }
        internal static string Status { get; private set; } = "disabled";
        [ThreadStatic] private static Batch current;
        private static long batches, attempts, yields;

        internal struct Batch
        {
            internal bool Active;
            internal CreationBudget Budget;
        }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            var requested = config.Bind("ObjectLoading", "Enabled", false,
                "Experimental creation time budget. Requires restart to install. May increase loading/loot delay; measure before use.");
            milliseconds = config.Bind("ObjectLoading", "BudgetMilliseconds", 4f,
                new ConfigDescription("Soft elapsed-time budget shared by near and distant creation per scene batch. Allow progress until one object succeeds; individual objects, invalid prefabs and scanning can exceed it.",
                    new AcceptableValueRange<float>(1f, 20f)));
            if (!requested.Value) return;
            try
            {
                var arguments = new[] { typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType() };
                foreach (string name in new[] { "CreateObjectsSorted", "CreateDistantObjects" })
                {
                    var method = AccessTools.DeclaredMethod(typeof(ZNetScene), name, arguments);
                    if (method == null || method.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported scene methods.");
                    Patches.Patch(method, transpiler: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(Transpile)));
                }
                var batch = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
                if (batch == null || batch.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported batch method.");
                Patches.Patch(batch, prefix: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(Begin)),
                    finalizer: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(End)));
                Installed = Enabled = true;
                Status = "installed";
                logger.LogWarning("Experimental object creation budget installed; soft limit " + milliseconds.Value + " ms. Loading latency may increase.");
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                Installed = Enabled = false;
                Status = "unavailable";
                logger.LogWarning("Object creation budget unavailable; vanilla behavior retained: " + exception.GetType().Name);
            }
        }

        private static void Begin(out Batch __state)
        {
            __state = current;
            // Do not give nested calls a fresh budget or reset their parent's work.
            if (current.Active) return;
            float value = milliseconds.Value;
            if (!Enabled || float.IsNaN(value) || float.IsInfinity(value) || value < 1 || value > 20) return;
            current = new Batch { Active = true, Budget = new CreationBudget(Stopwatch.GetTimestamp(),
                (long)(value * Stopwatch.Frequency / 1000.0)) };
        }

        private static void End(Batch __state)
        {
            if (__state.Active) return;
            if (current.Active) { batches++; attempts += current.Budget.Attempts; }
            current = __state;
        }

        private static UnityEngine.GameObject? Created(UnityEngine.GameObject? created)
        {
            if (current.Active) current.Budget.RecordCreation(created != null);
            return created;
        }

        private static bool Continue(bool hasNext)
        {
            if (!current.Active || !hasNext) return hasNext;
            if (current.Budget.AllowNext(true, Stopwatch.GetTimestamp())) return true;
            yields++;
            return false;
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var create = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
            var creations = code.Where(instruction => instruction.Calls(create)).ToList();
            var moves = code.Where(instruction => instruction.operand is MethodInfo method &&
                method.Name == "MoveNext" && method.DeclaringType == typeof(List<ZDO>.Enumerator)).ToList();
            int expected = __originalMethod.Name == "CreateObjectsSorted" ? 2 : 1;
            if (creations.Count != 1 || moves.Count != expected || code.IndexOf(moves.Last()) < code.IndexOf(creations[0]))
                throw new InvalidOperationException("Unsupported object enumeration layout.");
            // Gate only the creation loop, never the earlier candidate scan. Returning
            // false through the existing branch preserves the enumerator's finally.
            var move = moves.Last();
            int next = code.IndexOf(move) + 1;
            if (next >= code.Count || (code[next].opcode != OpCodes.Brtrue && code[next].opcode != OpCodes.Brtrue_S))
                throw new InvalidOperationException("Unsupported enumeration branch.");
            foreach (var instruction in code)
            {
                yield return instruction;
                if (ReferenceEquals(instruction, creations[0]))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ObjectCreationBudget), nameof(Created)));
                if (ReferenceEquals(instruction, move))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ObjectCreationBudget), nameof(Continue)));
            }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("object_budget_status", Status));
            labels.Add(new TextValue("object_budget_enabled", Enabled ? "true" : "false"));
            gauges.Add(new NumberValue("object_budget_ms", milliseconds.Value, "ms"));
            gauges.Add(new NumberValue("object_budget_batches_total", batches, "batches"));
            gauges.Add(new NumberValue("object_budget_attempts_total", attempts, "objects"));
            gauges.Add(new NumberValue("object_budget_yields_total", yields, "loop_exits"));
        }

        internal static void Uninstall()
        {
            Enabled = Installed = false;
            Patches.UnpatchSelf();
        }
    }
}
