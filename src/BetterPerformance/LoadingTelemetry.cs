using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BetterPerformance.Core;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Observes existing calls only. Never requests readiness, changes a spawn or
    // retains a Game/Player/scene reference beyond its synchronous callback.
    internal static class LoadingTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".LoadingTelemetry");
        private static LoadingTimeline timeline = new LoadingTimeline();
        private static AccessTools.FieldRef<Game, bool> firstSpawn = null!, requested = null!;
        private static AccessTools.FieldRef<Hud, CanvasGroup> loadingScreen = null!;
        private static string origin = "unobserved", startedUtc = "";
        private static int ownerThread;
        private static long failures, otherThreadSkips;
        [ThreadStatic] private static int findDepth;
        private static bool enabled;
        internal static bool Enabled
        {
            get => enabled;
            set { if (enabled && !value) timeline.Censor(Now); enabled = value; }
        }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        internal struct Scope { internal bool Active; internal double Started; internal int PreviousDepth; internal long Sequence; }

        internal static void Install(ManualLogSource logger)
        {
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                firstSpawn = AccessTools.FieldRefAccess<Game, bool>("m_firstSpawn");
                requested = AccessTools.FieldRefAccess<Game, bool>("m_requestRespawn");
                loadingScreen = AccessTools.FieldRefAccess<Hud, CanvasGroup>("m_loadingScreen");
                Patch(typeof(FejdStartup), "TransitionToMainScene", Type.EmptyTypes, typeof(void), nameof(ConnectionStarted), null);
                Patch(typeof(Game), "Awake", Type.EmptyTypes, typeof(void), nameof(BeforeScene), null);
                Patch(typeof(Game), "RequestRespawn", new[] { typeof(float), typeof(bool) }, typeof(void), nameof(BeforeRequest), null);
                Patch(typeof(Game), "_RequestRespawn", Type.EmptyTypes, typeof(void), nameof(BeforeRespawn), null);
                Patch(typeof(Game), "FindSpawnPoint", new[] { typeof(Vector3).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(float) }, typeof(bool), nameof(BeforeFind), nameof(AfterFind));
                Patch(typeof(ZNetScene), "IsAreaReady", new[] { typeof(Vector3) }, typeof(bool), nameof(BeforeArea), nameof(AfterArea));
                Patch(typeof(Game), "SpawnPlayer", new[] { typeof(Vector3), typeof(bool) }, typeof(Player), nameof(BeforeSpawn), nameof(AfterSpawn));
                Patch(typeof(Game), "UpdateRespawn", new[] { typeof(float) }, typeof(void), nameof(BeforeUpdate), nameof(AfterUpdate));
                Patch(typeof(Game), "OnDestroy", Type.EmptyTypes, typeof(void), nameof(BeforeDestroy), null);
                Patch(typeof(Hud), "UpdateBlackScreen", new[] { typeof(Player), typeof(float) }, typeof(void), null, nameof(AfterBlackScreen));
                Installed = true; Status = "installed";
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf(); Installed = Enabled = false; Status = "unavailable";
                logger.LogWarning("Loading telemetry unavailable: " + exception.GetType().Name);
            }
        }
        private static void Patch(Type type, string name, Type[] parameters, Type result, string? prefix, string? finalizer)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null || method.ReturnType != result || method.IsStatic) throw new InvalidOperationException("Unsupported loading signature.");
            Patches.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(LoadingTelemetry), prefix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(LoadingTelemetry), finalizer) { priority = Priority.Last });
        }
        private static bool Observe()
        {
            if (!Enabled || !Installed) return false;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread) { Interlocked.Increment(ref otherThreadSkips); return false; }
            return ReferenceEquals(ZNet.instance, null) || !ZNet.instance.IsDedicated();
        }
        private static void Begin(string kind, string boundary)
        { timeline.Begin(kind, Now); origin = boundary; startedUtc = DateTime.UtcNow.ToString("O"); }
        // This is the accepted scene-transition request, not the user click or a
        // successful network connection. No address or character identity is read.
        internal static void ConnectionStarted()
        {
            try
            {
                if (!Observe()) return;
                Begin("initial_join", "scene_transition_requested");
                timeline.Mark(LoadingMilestone.SceneTransitionRequested, Now);
            }
            catch { failures++; }
        }
        internal static void LoadingScreenReleased()
        {
            try
            {
                if (Observe() && !double.IsNaN(timeline.Times[(int)LoadingMilestone.RespawnCompleted]))
                    timeline.Mark(LoadingMilestone.HudReleased, Now);
            }
            catch { failures++; }
        }
        private static void BeforeScene()
        {
            try
            {
                if (!Observe()) return;
                if (!timeline.Active || timeline.Kind != "initial_join" || !double.IsNaN(timeline.Times[(int)LoadingMilestone.SceneAwake]))
                    Begin("initial_join", "game_awake_partial");
                timeline.Mark(LoadingMilestone.SceneAwake, Now);
            }
            catch { failures++; }
        }
        private static void BeforeRequest(Game __instance, bool __1)
        {
            try
            {
                if (!Observe()) return;
                string kind = firstSpawn(__instance) ? "initial_join" : (__1 ? "death_respawn" : "later_respawn");
                if (!timeline.Active || kind != "initial_join" || timeline.Kind != kind) Begin(kind, "request_scheduled_partial");
                timeline.Mark(LoadingMilestone.RequestScheduled, Now);
            }
            catch { failures++; }
        }
        private static void BeforeRespawn(Game __instance)
        {
            try
            {
                if (!Observe()) return;
                if (!timeline.Active) Begin(firstSpawn(__instance) ? "initial_join_partial" : "later_respawn_partial", "respawn_started_partial");
                timeline.Mark(LoadingMilestone.RespawnStarted, Now);
            }
            catch { failures++; }
        }
        private static Scope BeginScope(bool allowed)
        {
            return allowed && Observe() && timeline.Active ? new Scope { Active = true, Started = Now, Sequence = timeline.Sequence } : default;
        }
        private static bool Current(Scope scope) => scope.Active && Observe() && scope.Sequence == timeline.Sequence;
        private static void BeforeFind(out Scope __state)
        {
            __state = default;
            try { __state = BeginScope(true); __state.PreviousDepth = findDepth; if (__state.Active) findDepth++; }
            catch { failures++; }
        }
        private static void AfterFind(bool __result, Scope __state, Exception? __exception)
        {
            try
            {
                if (!Current(__state)) return;
                timeline.Record(LoadingOperation.FindSpawnPoint, __state.Started, Now, __result, __exception != null);
                if (__result && __exception == null) timeline.Mark(LoadingMilestone.SpawnPointReady, Now);
            }
            catch { failures++; }
            finally { if (__state.Active) findDepth = __state.PreviousDepth; }
        }
        private static void BeforeArea(out Scope __state)
        { __state = default; try { __state = BeginScope(findDepth > 0); } catch { failures++; } }
        private static void AfterArea(bool __result, Scope __state, Exception? __exception)
        { try { if (Current(__state)) timeline.Record(LoadingOperation.AreaReady, __state.Started, Now, __result, __exception != null); } catch { failures++; } }
        private static void BeforeSpawn(out Scope __state)
        { __state = default; try { __state = BeginScope(true); } catch { failures++; } }
        private static void AfterSpawn(Player? __result, Scope __state, Exception? __exception)
        {
            try
            {
                if (!Current(__state)) return;
                bool success = __exception == null && !ReferenceEquals(__result, null) && ReferenceEquals(__result, Player.m_localPlayer);
                timeline.Record(LoadingOperation.SpawnPlayer, __state.Started, Now, success, __exception != null);
                if (success) timeline.Mark(LoadingMilestone.PlayerSpawned, Now);
            }
            catch { failures++; }
        }
        private static void BeforeUpdate(Game __instance, out Scope __state)
        { __state = default; try { __state = BeginScope(Enabled && requested(__instance)); } catch { failures++; } }
        private static void AfterUpdate(Game __instance, Scope __state, Exception? __exception)
        {
            try
            {
                if (!Current(__state)) return;
                timeline.Record(LoadingOperation.UpdateRespawn, __state.Started, Now, true, __exception != null);
                if (__exception == null && !requested(__instance) && !double.IsNaN(timeline.Times[(int)LoadingMilestone.PlayerSpawned]))
                    timeline.Mark(LoadingMilestone.RespawnCompleted, Now);
            }
            catch { failures++; }
        }
        private static void BeforeDestroy() { try { if (Observe()) timeline.Censor(Now); } catch { failures++; } }
        private static void AfterBlackScreen(Hud __instance, Player? __0, Exception? __exception)
        {
            try
            {
                if (__exception != null || !Enabled || !timeline.Active || !Observe() || ReferenceEquals(__0, null) ||
                    !ReferenceEquals(__0, Player.m_localPlayer) || double.IsNaN(timeline.Times[(int)LoadingMilestone.PlayerSpawned])) return;
                var screen = loadingScreen(__instance);
                if (screen != null && screen.alpha <= 0 && !screen.gameObject.activeSelf) LoadingScreenReleased();
            }
            catch { failures++; }
        }
        internal static void StartCapture() { } // Preserve the pre-capture loading timeline.
        internal static void Reset() { timeline = new LoadingTimeline(); findDepth = 0; failures = otherThreadSkips = 0; origin = "unobserved"; startedUtc = ""; }
        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            if (!ReferenceEquals(ZNet.instance, null) && ZNet.instance.IsDedicated())
            { labels.Add(new TextValue("loading_telemetry_status", "not_applicable_dedicated")); return; }
            if (Observe()) timeline.Expire(Now);
            labels.Add(new TextValue("loading_telemetry_status", Enabled ? Status : "disabled"));
            labels.Add(new TextValue("loading_timeline_kind", timeline.Kind));
            labels.Add(new TextValue("loading_timeline_origin", origin));
            if (startedUtc.Length != 0) labels.Add(new TextValue("loading_started_utc", startedUtc));
            labels.Add(new TextValue("loading_timeline_state", timeline.Completed ? "hud_released" : timeline.Censored ? "censored" : timeline.Active ? "pending" : "unobserved"));
            labels.Add(new TextValue("loading_timeline_semantics", "latest_sequence_cumulative; first_observed_wall_milestones; inclusive_nested_call_costs_not_CPU; pre_capture_preserved; native_Spawned_after_is_game_dt; HUD_not_input_or_render_complete"));
            gauges.Add(new NumberValue("loading_sequence", timeline.Sequence, "sequence"));
            gauges.Add(new NumberValue("loading_probe_failures_total", failures, "calls"));
            gauges.Add(new NumberValue("loading_other_thread_skips_total", otherThreadSkips, "calls"));
            gauges.Add(new NumberValue("loading_replaced_incomplete_total", timeline.ReplacedIncomplete, "loads"));
            gauges.Add(new NumberValue("loading_invalid_times_total", timeline.InvalidTimes, "observations"));
            if (timeline.Sequence == 0) return;
            Add(gauges, "loading_elapsed", timeline.Elapsed(Now) * 1000);
            for (int i = 0; i < timeline.Times.Length; i++) Add(gauges, "loading_since_start_" + ((LoadingMilestone)i), (timeline.Times[i] - timeline.Started) * 1000);
            for (int i = 0; i < timeline.Operations.Length; i++)
            {
                var value = timeline.Operations[i]; if (value.Calls == 0) continue;
                string key = "loading_operation_" + ((LoadingOperation)i);
                gauges.Add(new NumberValue(key + "_calls", value.Calls, "calls"));
                gauges.Add(new NumberValue(key + "_not_ready", value.NotReady, "calls"));
                gauges.Add(new NumberValue(key + "_failures", value.Failures, "calls"));
                Add(gauges, key + "_sum", value.SumMs); Add(gauges, key + "_max", value.MaxMs);
            }
        }
        private static void Add(List<NumberValue> gauges, string name, double value)
        { if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0) gauges.Add(new NumberValue(name, value, "ms")); }
        internal static void Uninstall() { Patches.UnpatchSelf(); Installed = Enabled = false; Status = "disabled"; Reset(); }
    }
}
