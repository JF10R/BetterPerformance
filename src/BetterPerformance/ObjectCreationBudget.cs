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
    // readiness, invalid-prefab handling or disposal. Quota expansion and loot
    // ordering are separately opt-in and operate only inside an active budget.
    internal static class ObjectCreationBudget
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".ObjectCreationBudget");
        private static readonly Harmony TelemetryPatches = new Harmony(Plugin.PluginId + ".BudgetTelemetry");
        private static readonly BudgetTelemetry<ZDOID> Telemetry = new BudgetTelemetry<ZDOID>();
        private static ConfigEntry<float> milliseconds = null!;
        private static ConfigEntry<bool> adaptiveQuota = null!;
        private static ConfigEntry<int> expandedQuota = null!;
        private static ConfigEntry<bool> budgetAfterNearPreparation = null!;
        private static ConfigEntry<bool> telemetryEnabled = null!;
        private static ManualLogSource logger = null!;
        private static CaptureSession? telemetryCapture;
        private static WeakReference<ZNetScene>? telemetryScene;
        private static string telemetryStatus = "budget_not_installed";
        private static bool nearCandidateProbe, distantCandidateProbe;
        private static long unavailableCandidates;
        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; set; }
        internal static string Status { get; private set; } = "disabled";
        [ThreadStatic] private static Batch current;
        private static long batches, attempts, yields, quotaExpansions;
        private static long preparationBatches, preparationUnavailable, rebasedBatches, serviceOvershoots;
        private static double preparationSumMs, preparationMaxMs, serviceSumMs, serviceMaxMs;

        internal struct Batch
        {
            internal bool Active;
            internal CreationBudget Budget;
            internal long Started;
            internal double AllowanceMs;
            internal bool Yielded;
            internal CaptureSession? Capture;
            internal bool DeferNearPreparation, NearPrepared, AnyGateObserved, AllowanceRebased;
            internal long NearPreparedAt, AllowanceOffsetTicks;
        }

        internal struct CreationObservation
        {
            internal CaptureSession? Capture;
            internal long Started;
            internal bool MeasureCost;
            internal double AllowanceMs;
        }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            ObjectCreationBudget.logger = logger;
            var requested = config.Bind("ObjectLoading", "Enabled", false,
                "Experimental creation time budget. Requires restart to install. May increase loading/loot delay; measure before use.");
            milliseconds = config.Bind("ObjectLoading", "BudgetMilliseconds", 4f,
                new ConfigDescription("Soft elapsed-time budget shared by near and distant creation per scene batch. Allow progress until one object succeeds; individual objects, invalid prefabs and scanning can exceed it.",
                    new AcceptableValueRange<float>(1f, 20f)));
            adaptiveQuota = config.Bind("ObjectLoading", "AdaptiveCreationQuota", false,
                "Experimental: raise the vanilla count allowance while the shared time budget has room. Does not estimate object cost or guarantee lower latency; unmeasured.");
            expandedQuota = config.Bind("ObjectLoading", "ExpandedCreationQuota", 64,
                new ConfigDescription("Raised count allowance when adaptive quota is enabled. Larger vanilla loading/backlog limits are preserved; the time budget still applies.",
                    new AcceptableValueRange<int>(10, 256)));
            budgetAfterNearPreparation = config.Bind("ObjectLoading", "BudgetAfterNearPreparation", false,
                "Experimental: start the shared creation allowance at the first verified near-creation gate, after near preparation. Whole batches can exceed BudgetMilliseconds by preparation time; distant creation shares the same clock. Does not cache scans/sorts or change quotas/readiness. Unverified near gates retain the whole-batch clock.");
            telemetryEnabled = config.Bind("Diagnostics", "BudgetTradeoffEnabled", true,
                "Observe budget-active batch/creation costs and bounded next-candidate waits after budget yields while capturing. Waits are not causal added latency; requires restart.");
            LootCreationPriority.Configure(config, logger);
            if (!requested.Value) return;
            try
            {
                var arguments = new[] { typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType() };
                foreach (string name in new[] { "CreateObjectsSorted", "CreateDistantObjects" })
                {
                    var method = AccessTools.DeclaredMethod(typeof(ZNetScene), name, arguments);
                    if (method == null || method.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported scene methods.");
                    Patches.Patch(method, prefix: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(ExpandQuota)),
                        transpiler: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(Transpile)));
                }
                var batch = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
                if (batch == null || batch.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported batch method.");
                Patches.Patch(batch, prefix: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(Begin)),
                    finalizer: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(End)));
                Installed = Enabled = true;
                Status = "installed";
                InstallTelemetry();
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

        private static void InstallTelemetry()
        {
            if (!telemetryEnabled.Value) { telemetryStatus = "disabled"; return; }
            try
            {
                var create = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
                if (create == null || create.ReturnType != typeof(UnityEngine.GameObject))
                    throw new InvalidOperationException("Unsupported native creation method.");
                TelemetryPatches.Patch(create,
                    prefix: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(BeforeCreation)),
                    finalizer: new HarmonyMethod(typeof(ObjectCreationBudget), nameof(AfterCreation)));
                telemetryStatus = "available";
            }
            catch (Exception exception)
            {
                TelemetryPatches.UnpatchSelf();
                telemetryStatus = "unavailable";
                logger.LogWarning("Budget diagnostics unavailable: " + exception.GetType().Name);
            }
        }

        private static void Begin(out Batch __state)
        {
            __state = current;
            // Do not give nested calls a fresh budget or reset their parent's work.
            if (current.Active)
            {
                // A nested near gate need not follow the outer near preparation.
                // Prevent a future rebase, preserving any offset already established.
                current.DeferNearPreparation = false;
                return;
            }
            float value = milliseconds.Value;
            if (!Enabled || float.IsNaN(value) || float.IsInfinity(value) || value < 1 || value > 20) return;
            long started = Stopwatch.GetTimestamp();
            current = new Batch { Active = true, Started = started, AllowanceMs = value,
                Budget = new CreationBudget(started, (long)(value * Stopwatch.Frequency / 1000.0)),
                DeferNearPreparation = budgetAfterNearPreparation.Value && nearCandidateProbe };
            try
            {
                var capture = System.Threading.Volatile.Read(ref TimingHooks.Current);
                if (telemetryStatus == "available" && telemetryEnabled.Value && capture != null)
                {
                    BindTelemetry(capture, checkScene: true);
                    current.Capture = capture;
                }
            }
            catch (Exception exception) { FailTelemetry(exception); }
        }

        private static void End(Batch __state)
        {
            if (__state.Active) return;
            try
            {
                if (current.Active)
                {
                    batches++; attempts += current.Budget.Attempts;
                    if (Observing(current.Capture))
                    {
                        long finished = Stopwatch.GetTimestamp();
                        Telemetry.RecordBatch(current.Budget.Attempts, current.Budget.Successes, current.Yielded,
                            (finished - current.Started) * 1000.0 / Stopwatch.Frequency, current.AllowanceMs);
                        RecordPreparationTelemetry(current, finished);
                    }
                }
            }
            catch (Exception exception) { FailTelemetry(exception); }
            finally { current = __state; }
        }

        private static UnityEngine.GameObject? Created(UnityEngine.GameObject? created)
        {
            if (current.Active) current.Budget.RecordCreation(created != null);
            return created;
        }

        private static void ExpandQuota(ref int __1)
        {
            if (!current.Active || !adaptiveQuota.Value) return;
            int configured = expandedQuota.Value;
            if (configured < 10 || configured > 256) return;
            int expanded = CreationScheduling.ExpandQuota(__1, configured, true);
            if (expanded != __1) { __1 = expanded; quotaExpansions++; }
        }

        private static void Prioritize(List<ZDO> candidates)
        {
            if (current.Active) LootCreationPriority.Apply(candidates);
        }

        private static bool Continue(bool hasNext) => ContinueAt(hasNext,
            current.Active && hasNext ? Stopwatch.GetTimestamp() : 0);

        private static bool ContinueAt(bool hasNext, long now)
        {
            if (!current.Active) return hasNext;
            current.AnyGateObserved = true;
            if (!hasNext) return false;
            // Shift only the clock supplied to the unchanged budget. Never reset its
            // attempts/success floor; distant and nested work retain this same offset.
            if (current.Budget.AllowNext(true, now - current.AllowanceOffsetTicks)) return true;
            yields++;
            current.Yielded = true;
            return false;
        }

        private static void NearPreparationComplete(long now)
        {
            if (!current.Active || current.NearPrepared) return;
            current.NearPrepared = true;
            current.NearPreparedAt = now;
            // A prior distant/nested gate or creation has already used the allowance.
            // Never retroactively refund that work, including unsuccessful attempts.
            if (current.DeferNearPreparation && !current.AnyGateObserved && current.Budget.Attempts == 0 && now >= current.Started)
            {
                current.AllowanceOffsetTicks = now - current.Started;
                current.AllowanceRebased = true;
            }
        }

        private static void RecordPreparationTelemetry(Batch batch, long finished)
        {
            if (!batch.NearPrepared || batch.NearPreparedAt < batch.Started || finished < batch.NearPreparedAt)
            { preparationUnavailable++; return; }
            double preparation = (batch.NearPreparedAt - batch.Started) * 1000.0 / Stopwatch.Frequency;
            double service = (finished - batch.NearPreparedAt) * 1000.0 / Stopwatch.Frequency;
            preparationBatches++;
            if (batch.AllowanceRebased) rebasedBatches++;
            preparationSumMs += preparation; preparationMaxMs = Math.Max(preparationMaxMs, preparation);
            serviceSumMs += service; serviceMaxMs = Math.Max(serviceMaxMs, service);
            if (service > batch.AllowanceMs) serviceOvershoots++;
        }

        // The existing Continue method remains the sole scheduling decision. Reading
        // Current after a successful native MoveNext is O(1) and does not scan a queue.
        private static bool ContinueWithTelemetry(bool hasNext, ref List<ZDO>.Enumerator enumerator)
        {
            // The first native near MoveNext has already run, including an empty
            // loop. This observes setup + scan + sort + optional priority inclusively.
            if (current.Active && !current.NearPrepared) NearPreparationComplete(Stopwatch.GetTimestamp());
            bool proceed = Continue(hasNext);
            if (proceed || !hasNext || !Observing(current.Capture)) return proceed;
            try
            {
                var candidate = enumerator.Current;
                if (candidate == null || candidate.m_uid == ZDOID.None) { unavailableCandidates++; return proceed; }
                // Native near sorting uses m_tempCurrentObjects2; the enumerator's
                // exact identity is sufficient for wait tracking in either loop.
                Telemetry.RecordYield(candidate.m_uid, distant: false, NowMs());
            }
            catch (Exception exception) { FailTelemetry(exception); }
            return proceed;
        }

        private static bool ContinueDistantWithTelemetry(bool hasNext, ref List<ZDO>.Enumerator enumerator)
        {
            bool proceed = Continue(hasNext);
            if (proceed || !hasNext || !Observing(current.Capture)) return proceed;
            try
            {
                var candidate = enumerator.Current;
                if (candidate == null || candidate.m_uid == ZDOID.None) { unavailableCandidates++; return proceed; }
                Telemetry.RecordYield(candidate.m_uid, distant: true, NowMs());
            }
            catch (Exception exception) { FailTelemetry(exception); }
            return proceed;
        }

        private static void BeforeCreation(ZDO __0, out CreationObservation __state)
        {
            __state = default;
            if (telemetryStatus != "available" || !telemetryEnabled.Value) return;
            var capture = System.Threading.Volatile.Read(ref TimingHooks.Current);
            if (capture == null || (!current.Active && Telemetry.Pending == 0)) return;
            try
            {
                BindTelemetry(capture, checkScene: !current.Active);
                if (__0 == null || __0.m_uid == ZDOID.None) { unavailableCandidates++; return; }
                if (!current.Active && !Telemetry.Contains(__0.m_uid)) return;
                __state = new CreationObservation { Capture = capture, Started = Stopwatch.GetTimestamp(),
                    MeasureCost = current.Active, AllowanceMs = current.AllowanceMs };
            }
            catch (Exception exception) { FailTelemetry(exception); }
        }

        // Void finalizer preserves native exceptions. A null/throwing creation does
        // not complete the pending observation; an expired/ended track is censored.
        private static void AfterCreation(ZDO __0, UnityEngine.GameObject? __result, CreationObservation __state, Exception? __exception)
        {
            if (!Observing(__state.Capture)) return;
            try
            {
                bool success = __exception == null && __result != null;
                if (__state.MeasureCost)
                    Telemetry.RecordCreation(__0.m_uid, ElapsedMs(__state.Started), __state.AllowanceMs,
                        success, __exception != null, NowMs());
                else if (success) Telemetry.CompletePending(__0.m_uid, NowMs());
            }
            catch (Exception exception) { FailTelemetry(exception); }
        }

        private static bool Observing(CaptureSession? capture) => capture != null && telemetryStatus == "available" &&
            telemetryEnabled.Value && ReferenceEquals(capture, System.Threading.Volatile.Read(ref TimingHooks.Current));

        private static double ElapsedMs(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        private static void BindTelemetry(CaptureSession capture, bool checkScene)
        {
            if (!ReferenceEquals(telemetryCapture, capture))
            {
                ResetTelemetry();
                telemetryCapture = capture;
            }
            if (!checkScene) return;
            var scene = ZNetScene.instance;
            if (telemetryScene != null && telemetryScene.TryGetTarget(out var previous) && ReferenceEquals(previous, scene)) return;
            Telemetry.Clear(true);
            telemetryScene = scene == null ? null : new WeakReference<ZNetScene>(scene);
        }

        private static void FailTelemetry(Exception exception)
        {
            telemetryStatus = "failed";
            Telemetry.Clear(true);
            logger.LogWarning("Budget diagnostics disabled after observation failure: " + exception.GetType().Name);
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
            var sortMethod = AccessTools.Method(typeof(List<ZDO>), "Sort", new[] { typeof(Comparison<ZDO>) });
            var sorts = code.Where(instruction => instruction.Calls(sortMethod)).ToList();
            var candidatesField = AccessTools.Field(typeof(ZNetScene), "m_tempCurrentObjects2");
            bool near = __originalMethod.Name == "CreateObjectsSorted";
            if (near && (sorts.Count != 1 || candidatesField == null || candidatesField.FieldType != typeof(List<ZDO>) ||
                code.IndexOf(sorts[0]) >= code.IndexOf(creations[0])))
                throw new InvalidOperationException("Unsupported candidate sorting layout.");
            // Gate only the creation loop, never the earlier candidate scan. Returning
            // false through the existing branch preserves the enumerator's finally.
            var move = moves.Last();
            int next = code.IndexOf(move) + 1;
            if (next >= code.Count || (code[next].opcode != OpCodes.Brtrue && code[next].opcode != OpCodes.Brtrue_S))
                throw new InvalidOperationException("Unsupported enumeration branch.");
            var enumeratorLoad = code[code.IndexOf(move) - 1];
            bool observeCandidate = IsEnumeratorLoad(enumeratorLoad, __originalMethod);
            if (near) nearCandidateProbe = observeCandidate; else distantCandidateProbe = observeCandidate;
            foreach (var instruction in code)
            {
                yield return instruction;
                if (near && ReferenceEquals(instruction, sorts[0]))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, candidatesField);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ObjectCreationBudget), nameof(Prioritize)));
                }
                if (ReferenceEquals(instruction, creations[0]))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ObjectCreationBudget), nameof(Created)));
                if (ReferenceEquals(instruction, move))
                {
                    if (observeCandidate)
                    {
                        // Copy only opcode/operand: original labels and exception
                        // blocks belong to the existing native load, not this copy.
                        yield return new CodeInstruction(enumeratorLoad.opcode, enumeratorLoad.operand);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ObjectCreationBudget),
                            near ? nameof(ContinueWithTelemetry) : nameof(ContinueDistantWithTelemetry)));
                    }
                    else
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ObjectCreationBudget), nameof(Continue)));
                }
            }
        }

        private static bool IsEnumeratorLoad(CodeInstruction instruction, MethodBase method)
        {
            if (instruction.opcode != OpCodes.Ldloca && instruction.opcode != OpCodes.Ldloca_S) return false;
            if (instruction.operand is LocalBuilder builder) return builder.LocalType == typeof(List<ZDO>.Enumerator);
            if (instruction.operand is LocalVariableInfo local) return local.LocalType == typeof(List<ZDO>.Enumerator);
            int index = instruction.operand is byte shortIndex ? shortIndex : instruction.operand is int fullIndex ? fullIndex : -1;
            var locals = method.GetMethodBody()?.LocalVariables;
            return locals != null && index >= 0 && index < locals.Count && locals[index].LocalType == typeof(List<ZDO>.Enumerator);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("object_budget_status", Status));
            labels.Add(new TextValue("object_budget_enabled", Enabled ? "true" : "false"));
            gauges.Add(new NumberValue("object_budget_ms", milliseconds.Value, "ms"));
            gauges.Add(new NumberValue("object_budget_batches_total", batches, "batches"));
            gauges.Add(new NumberValue("object_budget_attempts_total", attempts, "objects"));
            gauges.Add(new NumberValue("object_budget_yields_total", yields, "loop_exits"));
            labels.Add(new TextValue("adaptive_creation_quota_enabled", Enabled && adaptiveQuota.Value ? "true" : "false"));
            labels.Add(new TextValue("budget_after_near_preparation_enabled", Enabled && budgetAfterNearPreparation.Value ? "true" : "false"));
            gauges.Add(new NumberValue("expanded_creation_quota", expandedQuota.Value, "objects"));
            gauges.Add(new NumberValue("creation_quota_expansions_total", quotaExpansions, "calls"));
            LootCreationPriority.Sample(gauges, labels, Enabled);
            SampleTelemetry(gauges, labels);
        }

        private static void SampleTelemetry(List<NumberValue> gauges, List<TextValue> labels)
        {
            var summary = Telemetry.Drain(NowMs());
            labels.Add(new TextValue("budget_telemetry_status", telemetryStatus));
            labels.Add(new TextValue("budget_telemetry_enabled", telemetryStatus == "available" && telemetryEnabled.Value ? "true" : "false"));
            labels.Add(new TextValue("budget_near_candidate_probe", nearCandidateProbe ? "available" : "unavailable"));
            labels.Add(new TextValue("budget_distant_candidate_probe", distantCandidateProbe ? "available" : "unavailable"));
            labels.Add(new TextValue("budget_wait_semantics", "first_observed_next_candidate_at_yield_to_local_creation; not_readiness_or_causal_added_delay; may_span_budget_switches"));
            labels.Add(new TextValue("budget_cost_semantics", "inclusive_elapsed; creation_cost_nested_in_batch; overshoot_exceeds_entire_batch_allowance; no_saved_time_estimate"));
            labels.Add(new TextValue("budget_preparation_gate_status", nearCandidateProbe ? "verified_near_gate" : "unavailable_whole_batch_fallback"));
            labels.Add(new TextValue("budget_preparation_semantics", "batch_start_to_first_near_gate_inclusive; includes_outer_setup_scan_sort_priority; not_isolated_sort"));
            labels.Add(new TextValue("budget_service_semantics", "first_near_gate_to_batch_end; includes_readiness_creation_and_distant_work; not_CPU_time; whole_batch_metrics_unchanged"));
            gauges.Add(new NumberValue("budget_preparation_observed_batches", preparationBatches, "batches"));
            gauges.Add(new NumberValue("budget_preparation_unavailable_batches", preparationUnavailable, "batches"));
            gauges.Add(new NumberValue("budget_allowance_rebased_batches", rebasedBatches, "batches"));
            gauges.Add(new NumberValue("budget_preparation_elapsed_sum", preparationSumMs, "ms"));
            gauges.Add(new NumberValue("budget_preparation_elapsed_max", preparationMaxMs, "ms"));
            gauges.Add(new NumberValue("budget_service_elapsed_sum", serviceSumMs, "ms"));
            gauges.Add(new NumberValue("budget_service_elapsed_max", serviceMaxMs, "ms"));
            gauges.Add(new NumberValue("budget_service_over_allowance", serviceOvershoots, "batches"));
            gauges.Add(new NumberValue("budget_observed_batches", summary.Batches, "batches"));
            gauges.Add(new NumberValue("budget_observed_yielded_batches", summary.YieldedBatches, "batches"));
            gauges.Add(new NumberValue("budget_observed_near_yields", summary.NearYields, "loop_exits"));
            gauges.Add(new NumberValue("budget_observed_distant_yields", summary.DistantYields, "loop_exits"));
            gauges.Add(new NumberValue("budget_observed_attempts", summary.Attempts, "objects"));
            gauges.Add(new NumberValue("budget_observed_successes", summary.Successes, "objects"));
            gauges.Add(new NumberValue("budget_batch_elapsed_sum", summary.BatchSumMs, "ms"));
            gauges.Add(new NumberValue("budget_batch_elapsed_max", summary.BatchMaxMs, "ms"));
            gauges.Add(new NumberValue("budget_batch_over_allowance", summary.BatchOvershoots, "batches"));
            gauges.Add(new NumberValue("budget_creation_calls", summary.CreationCalls, "calls"));
            gauges.Add(new NumberValue("budget_creation_null_results", summary.CreationNullResults, "calls"));
            gauges.Add(new NumberValue("budget_creation_failed_calls", summary.CreationFailures, "calls"));
            gauges.Add(new NumberValue("budget_creation_elapsed_sum", summary.CreationSumMs, "ms"));
            gauges.Add(new NumberValue("budget_creation_elapsed_max", summary.CreationMaxMs, "ms"));
            gauges.Add(new NumberValue("budget_creation_over_allowance", summary.CreationOvershoots, "calls"));
            gauges.Add(new NumberValue("budget_creation_excess_sum", summary.CreationExcessSumMs, "ms"));
            gauges.Add(new NumberValue("budget_candidate_key_unavailable", unavailableCandidates, "candidates"));
            gauges.Add(new NumberValue("budget_deferred_tracks_started", summary.Waits.Observed, "tracks"));
            gauges.Add(new NumberValue("budget_deferred_tracks_completed", summary.Waits.Completed, "tracks"));
            gauges.Add(new NumberValue("budget_deferred_tracks_censored", summary.Waits.Censored, "tracks"));
            gauges.Add(new NumberValue("budget_deferred_capacity_skips", summary.Waits.CapacitySkipped, "observations"));
            gauges.Add(new NumberValue("budget_deferred_pending", summary.Waits.Pending, "tracks"));
            gauges.Add(new NumberValue("budget_post_yield_wait_sum", summary.Waits.CompletedSumMs, "ms"));
            gauges.Add(new NumberValue("budget_post_yield_wait_max", summary.Waits.CompletedMaxMs, "ms"));
            gauges.Add(new NumberValue("budget_post_yield_pending_age_max", summary.Waits.PendingMaxAgeMs, "ms"));
            unavailableCandidates = 0;
            ResetPreparationTelemetry();
        }

        // Capture lifecycle calls are main-thread only, matching scene creation.
        internal static void ResetTelemetry()
        {
            Telemetry.Clear(false);
            telemetryCapture = null;
            telemetryScene = null;
            unavailableCandidates = 0;
            ResetPreparationTelemetry();
        }

        private static void ResetPreparationTelemetry()
        {
            preparationBatches = preparationUnavailable = rebasedBatches = serviceOvershoots = 0;
            preparationSumMs = preparationMaxMs = serviceSumMs = serviceMaxMs = 0;
        }

        internal static void FinishTelemetry(List<NumberValue> gauges, List<TextValue> labels)
        {
            Telemetry.Clear(true);
            SampleTelemetry(gauges, labels);
            telemetryCapture = null;
            telemetryScene = null;
        }

        internal static void Uninstall()
        {
            Enabled = Installed = false;
            Patches.UnpatchSelf();
            TelemetryPatches.UnpatchSelf();
            ResetTelemetry();
            telemetryStatus = "disabled";
            LootCreationPriority.Reset();
        }
    }
}
