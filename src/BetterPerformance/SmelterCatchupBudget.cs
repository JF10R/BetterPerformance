using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Smelter.UpdateSmelter drains its accumulator one simulated second per loop iteration,
    // so a station the owner has not simulated for a while replays the whole absence in one
    // call. The game already carries the remainder in the accTime ZDO variable and resumes
    // from it one second later, so stopping the loop early and letting SetAccumulator persist
    // what is left is a deferral, not an approximation. The transpiler replaces the single
    // loop-head constant with one static call and nothing else; every other instruction, the
    // ZDO writes, SetAccumulator, SpawnProcessed and the IsOwner early return are untouched.
    internal static class SmelterCatchupBudget
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".SmelterCatchupBudget");
        private static readonly Harmony TelemetryPatches = new Harmony(Plugin.PluginId + ".SmelterCatchupTelemetry");

        private static ConfigEntry<bool>? requested;
        private static ConfigEntry<int>? iterations;
        private static AccessTools.FieldRef<Smelter, ZNetView>? view;
        private static int budget = DefaultIterations;

        private const int DefaultIterations = 8;
        private const int MinimumIterations = 2;
        private const int MaximumIterations = 3600;

        // One per-call counter, incremented by the single call the transpiler installs.
        [ThreadStatic] private static int gateCalls;

        private static long loopIterations, loopIterationsMax, carriedMilliseconds, truncations;
        private static long spawnCalls, removeOreCalls, failures;

        internal static bool Installed { get; private set; }
        internal static bool Enabled => Installed;
        internal static string Status { get; private set; } = "disabled";
        internal static string TelemetryStatus { get; private set; } = "not_installed";
        internal static int Iterations => budget;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            requested = config.Bind("Stations", "SmelterCatchupBudgetEnabled", false,
                "Spread a smelter's catch-up over several one-second ticks instead of replaying a whole absence in one call. " +
                "The remainder is carried in the station's own accumulator, so fuel use, products and totals are unchanged; " +
                "only the call a product lands in moves. Requires restart to install; reverts to vanilla if the loop's IL does not match.");
            iterations = config.Bind("Stations", "SmelterCatchupIterationsPerCall", DefaultIterations,
                new ConfigDescription(
                    "Simulated seconds a single smelter update may replay. Must stay above one so the drain outpaces the one second " +
                    "per second that accrues; a smelter updates once per second, so the carry never grows.",
                    new AcceptableValueRange<int>(MinimumIterations, MaximumIterations)));
            budget = Clamp(iterations.Value);

            MethodInfo? target = Resolve(logger);
            if (target == null) { Status = "type_unavailable"; TelemetryStatus = "type_unavailable"; return; }
            InstallTelemetry(target, logger);
            if (!requested.Value) { Status = "disabled"; return; }
            // BeforeUpdate/AfterUpdate are the only resets of gateCalls and they live in the
            // telemetry patch set. Installing the budget without them would let Threshold()
            // return +Inf for every smelter after the first budget's worth of loop tests.
            if (TelemetryStatus != "enabled")
            {
                Status = "unavailable_telemetry";
                logger.LogWarning("Smelter catch-up budget not installed: its gate reset patches are unavailable (" + TelemetryStatus + ").");
                return;
            }
            try
            {
                Validate(PatchProcessor.GetOriginalInstructions(target), target);
                Patches.Patch(target, transpiler: new HarmonyMethod(typeof(SmelterCatchupBudget), nameof(Transpile)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Smelter catch-up bounded to " + budget + " simulated seconds per call; the remainder carries in the station accumulator.");
            }
            catch (Exception exception)
            {
                Installed = false;
                Status = exception is InvalidOperationException || exception.InnerException is InvalidOperationException
                    ? "unavailable_unexpected_shape" : "unavailable";
                Remove(Patches, failure => Status = "unpatch_failed:" + failure);
                logger.LogWarning("Smelter catch-up unchanged (" + Status + "): " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static int Clamp(int value) =>
            value < MinimumIterations ? MinimumIterations : value > MaximumIterations ? MaximumIterations : value;

        // The smelter family is one class, so one resolve covers every smelter prefab.
        private static MethodInfo? Resolve(ManualLogSource? logger)
        {
            try
            {
                MethodInfo? target = AccessTools.DeclaredMethod(typeof(Smelter), "UpdateSmelter", Type.EmptyTypes);
                if (target == null || target.IsStatic || target.ReturnType != typeof(void)) return null;
                view = AccessTools.FieldRefAccess<Smelter, ZNetView>("m_nview");
                return target;
            }
            catch (Exception exception) { logger?.LogInfo("Smelter catch-up unavailable: " + exception.GetType().Name); return null; }
        }

        // Count-only observation, installed whether or not the budget is, so an A/B compares
        // the same counters on both sides.
        private static void InstallTelemetry(MethodInfo target, ManualLogSource logger)
        {
            try
            {
                TelemetryPatches.Patch(target,
                    prefix: new HarmonyMethod(typeof(SmelterCatchupBudget), nameof(BeforeUpdate)),
                    postfix: new HarmonyMethod(typeof(SmelterCatchupBudget), nameof(AfterUpdate)));
                MethodInfo? spawn = AccessTools.DeclaredMethod(typeof(Smelter), "Spawn", new[] { typeof(string), typeof(int) });
                MethodInfo? removeOre = AccessTools.DeclaredMethod(typeof(Smelter), "RemoveOneOre", Type.EmptyTypes);
                if (spawn == null || removeOre == null) throw new InvalidOperationException("Smelter spawn or ore methods are missing.");
                TelemetryPatches.Patch(spawn, postfix: new HarmonyMethod(typeof(SmelterCatchupBudget), nameof(AfterSpawn)));
                TelemetryPatches.Patch(removeOre, postfix: new HarmonyMethod(typeof(SmelterCatchupBudget), nameof(AfterRemoveOre)));
                TelemetryStatus = "enabled";
            }
            catch (Exception exception)
            {
                TelemetryStatus = "patch_failed";
                Remove(TelemetryPatches, failure => TelemetryStatus = "unpatch_failed:" + failure);
                logger.LogWarning("Smelter catch-up telemetry unavailable: " + exception.GetType().Name);
            }
        }

        // Harmony rescans every patched method when it removes one, and a standalone CLR can
        // throw there for an unrelated Unity type. Removal must never escape a caller.
        private static void Remove(Harmony patches, Action<string> failed)
        {
            try { patches.UnpatchSelf(); }
            catch (Exception exception) { failed(exception.GetType().Name); }
        }

        // Called in place of the native loop-head constant. Returning positive infinity ends
        // the loop whichever way the compare branches, and the native SetAccumulator then
        // persists the untouched remainder.
        internal static float Threshold() => ++gateCalls > budget ? float.PositiveInfinity : 1f;

        private static void BeforeUpdate() => gateCalls = 0;

        // The loop test runs once more than the body, so executed seconds are one less than
        // the gate calls. A carry of a whole second is impossible in vanilla, so it is the
        // exact signature of a budget truncation.
        private static void AfterUpdate(Smelter __instance)
        {
            int calls = gateCalls;
            gateCalls = 0;
            try
            {
                if (calls > 1)
                {
                    long executed = calls - 1;
                    Interlocked.Add(ref loopIterations, executed);
                    RecordMaximum(ref loopIterationsMax, executed);
                }
                ZNetView? nview = view == null ? null : view(__instance);
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
                float carried = nview.GetZDO().GetFloat(ZDOVars.s_accTime);
                if (carried > 0f) RecordMaximum(ref carriedMilliseconds, (long)(carried * 1000f));
                if (carried >= 1f) Interlocked.Increment(ref truncations);
            }
            catch { Interlocked.Increment(ref failures); }
        }

        private static void AfterSpawn() => Interlocked.Increment(ref spawnCalls);

        private static void AfterRemoveOre() => Interlocked.Increment(ref removeOreCalls);

        private static void RecordMaximum(ref long target, long value)
        {
            long seen = Interlocked.Read(ref target);
            while (value > seen)
            {
                long previous = Interlocked.CompareExchange(ref target, value, seen);
                if (previous == seen) return;
                seen = previous;
            }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            int head = Validate(code, __originalMethod);
            // Copying the instruction keeps the loop-test label, and nothing is inserted or
            // removed, so no branch target can move.
            code[head] = new CodeInstruction(code[head])
            {
                opcode = OpCodes.Call,
                operand = AccessTools.DeclaredMethod(typeof(SmelterCatchupBudget), nameof(Threshold))
            };
            return code;
        }

        // Proves the whole catch-up loop, not just the constant: the accumulator comes from
        // GetAccumulator, is clamped once, is decremented by one inside the loop the compare
        // controls, is persisted by SetAccumulator after that loop, and the owner gate and the
        // three work calls still precede it. Returns the index of the loop-head constant.
        internal static int Validate(IList<CodeInstruction> code, MethodBase original)
        {
            if (original.IsStatic || !original.IsPrivate || !(original is MethodInfo info) ||
                info.ReturnType != typeof(void) || original.GetParameters().Length != 0)
                throw new InvalidOperationException("Unsupported UpdateSmelter signature.");
            MethodInfo getAccumulator = Method("GetAccumulator", Type.EmptyTypes);
            MethodInfo setAccumulator = Method("SetAccumulator", new[] { typeof(float) });
            MethodInfo getDeltaTime = Method("GetDeltaTime", Type.EmptyTypes);
            int read = Single(code, i => code[i].Calls(getAccumulator), "accumulator read");
            int write = Single(code, i => code[i].Calls(setAccumulator), "accumulator write");
            Single(code, i => code[i].Calls(getDeltaTime), "elapsed-time read");
            foreach (string name in new[] { "RemoveOneOre", "QueueProcessed", "SpawnProcessed" })
                Single(code, i => code[i].operand is MethodBase called && called.Name == name &&
                    called.DeclaringType == typeof(Smelter), name + " call");

            int accumulator = LocalIndex(read + 1 < code.Count ? code[read + 1] : new CodeInstruction(OpCodes.Nop), store: true);
            if (accumulator < 0) throw new InvalidOperationException("The accumulator is not stored in a local.");
            if (code.Any(i => Address(i, accumulator))) throw new InvalidOperationException("The accumulator local escapes by reference.");
            // The clamp compares the accumulator against 3600 and assigns that same constant.
            int[] clamps = Enumerable.Range(0, code.Count).Where(i => Constant(code[i]) == 3600f).ToArray();
            if (clamps.Length != 2 || clamps[0] < 1 || clamps[1] + 1 >= code.Count ||
                !Loads(code[clamps[0] - 1], accumulator) || !Stores(code[clamps[1] + 1], accumulator))
                throw new InvalidOperationException("The accumulator is not clamped to 3600 by the native compare and assignment.");
            int clamp = clamps[1];

            int head = Single(code, i => Constant(code[i]) == 1f && i > 0 && Loads(code[i - 1], accumulator) &&
                i + 1 < code.Count && IsConditionalBranch(code[i + 1]), "accumulator loop compare");
            int step = Single(code, i => Constant(code[i]) == 1f && i > 0 && Loads(code[i - 1], accumulator) &&
                i + 2 < code.Count && code[i + 1].opcode == OpCodes.Sub && Stores(code[i + 2], accumulator), "accumulator decrement");
            if (!(code[head + 1].operand is Label exit)) throw new InvalidOperationException("The loop compare does not branch to a label.");
            int target = IndexOf(code, exit);
            int first = Math.Min(head, target), last = Math.Max(head, target);
            if (step <= first || step >= last)
                throw new InvalidOperationException("The accumulator decrement is not inside the loop the compare controls.");
            if (write <= Math.Max(head, step)) throw new InvalidOperationException("The accumulator is persisted before the loop ends.");
            if (clamp >= first) throw new InvalidOperationException("The clamp does not precede the loop.");
            int owner = Single(code, i => code[i].operand is MethodBase called && called.Name == "IsOwner" &&
                called.DeclaringType == typeof(ZNetView), "owner gate");
            if (owner >= first) throw new InvalidOperationException("The owner gate does not precede the loop.");
            if (code[head].blocks.Count != 0)
                throw new InvalidOperationException("The loop compare site carries exception-block metadata.");
            return head;
        }

        private static MethodInfo Method(string name, Type[] parameters) =>
            AccessTools.DeclaredMethod(typeof(Smelter), name, parameters)
                ?? throw new InvalidOperationException("Smelter." + name + " is missing.");

        private static int Single(IList<CodeInstruction> code, Func<int, bool> match, string what)
        {
            int[] found = Enumerable.Range(0, code.Count).Where(match).ToArray();
            if (found.Length != 1) throw new InvalidOperationException("Expected exactly one smelter " + what + ".");
            return found[0];
        }

        private static int IndexOf(IList<CodeInstruction> code, Label label)
        {
            for (int index = 0; index < code.Count; index++) if (code[index].labels.Contains(label)) return index;
            throw new InvalidOperationException("The loop branch target is outside the method.");
        }

        private static bool IsConditionalBranch(CodeInstruction instruction)
        {
            OpCode opcode = instruction.opcode;
            return opcode == OpCodes.Bge || opcode == OpCodes.Bge_S || opcode == OpCodes.Bge_Un || opcode == OpCodes.Bge_Un_S ||
                opcode == OpCodes.Blt || opcode == OpCodes.Blt_S || opcode == OpCodes.Blt_Un || opcode == OpCodes.Blt_Un_S;
        }

        private static float Constant(CodeInstruction instruction) =>
            instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value ? value : float.NaN;

        private static bool Loads(CodeInstruction instruction, int local) => LocalIndex(instruction, store: false) == local;
        private static bool Stores(CodeInstruction instruction, int local) => LocalIndex(instruction, store: true) == local;
        private static bool Address(CodeInstruction instruction, int local) =>
            (instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S) && Slot(instruction.operand) == local;

        private static int LocalIndex(CodeInstruction instruction, bool store)
        {
            OpCode opcode = instruction.opcode;
            if (store)
            {
                if (opcode == OpCodes.Stloc_0) return 0;
                if (opcode == OpCodes.Stloc_1) return 1;
                if (opcode == OpCodes.Stloc_2) return 2;
                if (opcode == OpCodes.Stloc_3) return 3;
                return opcode == OpCodes.Stloc || opcode == OpCodes.Stloc_S ? Slot(instruction.operand) : -1;
            }
            if (opcode == OpCodes.Ldloc_0) return 0;
            if (opcode == OpCodes.Ldloc_1) return 1;
            if (opcode == OpCodes.Ldloc_2) return 2;
            if (opcode == OpCodes.Ldloc_3) return 3;
            return opcode == OpCodes.Ldloc || opcode == OpCodes.Ldloc_S ? Slot(instruction.operand) : -1;
        }

        private static int Slot(object? operand) =>
            operand is LocalBuilder builder ? builder.LocalIndex :
            operand is LocalVariableInfo variable ? variable.LocalIndex :
            operand is int value ? value : operand is byte small ? small : operand is sbyte signed ? signed : -1;

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("smelter_loop_iterations_sum", Interlocked.Exchange(ref loopIterations, 0), "seconds"));
            gauges.Add(new NumberValue("smelter_loop_iterations_max", Interlocked.Exchange(ref loopIterationsMax, 0), "seconds"));
            gauges.Add(new NumberValue("smelter_accumulator_carried_max", Interlocked.Exchange(ref carriedMilliseconds, 0) / 1000.0, "s"));
            gauges.Add(new NumberValue("smelter_budget_truncations", Interlocked.Exchange(ref truncations, 0), "calls"));
            gauges.Add(new NumberValue("smelter_spawn_calls", Interlocked.Exchange(ref spawnCalls, 0), "calls"));
            gauges.Add(new NumberValue("smelter_remove_ore_calls", Interlocked.Exchange(ref removeOreCalls, 0), "calls"));
            gauges.Add(new NumberValue("smelter_probe_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            labels.Add(new TextValue("smelter_budget_status", Status));
            labels.Add(new TextValue("smelter_budget_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("smelter_budget_iterations", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            labels.Add(new TextValue("smelter_budget_telemetry_status", TelemetryStatus));
            labels.Add(new TextValue("smelter_budget_scope",
                "owner_only_by_native_early_return; iterations_require_the_installed_budget; carry_lives_in_the_station_accumulator"));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref loopIterations, 0);
            Interlocked.Exchange(ref loopIterationsMax, 0);
            Interlocked.Exchange(ref carriedMilliseconds, 0);
            Interlocked.Exchange(ref truncations, 0);
            Interlocked.Exchange(ref spawnCalls, 0);
            Interlocked.Exchange(ref removeOreCalls, 0);
            Interlocked.Exchange(ref failures, 0);
        }

        internal static void Uninstall()
        {
            Reset();
            Installed = false;
            Status = "disabled";
            TelemetryStatus = "not_installed";
            gateCalls = 0;
            Remove(Patches, failure => Status = "unpatch_failed:" + failure);
            Remove(TelemetryPatches, failure => TelemetryStatus = "unpatch_failed:" + failure);
            view = null;
            requested = null;
            iterations = null;
            budget = DefaultIterations;
        }
    }
}
