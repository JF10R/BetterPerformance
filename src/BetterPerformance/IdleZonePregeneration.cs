using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;

namespace BetterPerformance
{
    // Dedicated server only. While no client is connected or joining, generate the ungenerated
    // zones just beyond the native ghost radius around recent player activity, one native
    // SpawnZone(zone, Ghost) call per frame: the call CreateGhostZones makes for a peer, so the
    // content is the game's own. A generation is atomic (RNG, physics, ZDO save) and is never
    // sliced; the whole point is to run it while nobody is playing.
    internal static class IdleZonePregeneration
    {
        private const int ActivityCapacity = 64;
        private const double ActivitySampleSeconds = 5, SaveIntervalSeconds = 300;
        private const int MaxAttemptsPerZone = 300, MaxSkipsPerFrame = 64;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".IdleZonePregeneration");
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly ActivityZones Activity = new ActivityZones(ActivityCapacity);
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<int>? radiusZones, maxZonesPerRun;
        private static ConfigEntry<float>? startDelaySeconds, frameBudgetMs;
        private static ManualLogSource? log;

        private delegate bool SpawnZoneCall(ZoneSystem zones, Vector2s zone, ZoneSystem.SpawnMode mode, out GameObject root);
        private delegate bool ZoneGeneratedCall(ZoneSystem zones, Vector2s zone);
        private static SpawnZoneCall? spawnZone;
        private static ZoneGeneratedCall? isGenerated;
        private static AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>>? generatedZones;

        // World and window state; main thread only (ZoneSystem.Update postfix).
        private static ZNet? world;
        private static string? storePath;
        private static bool dirty, windowActive, windowDone, failed;
        private static double worldSeen, lastSample, lastSave;
        private static List<ZoneCoord> queue = new List<ZoneCoord>();
        private static int queueIndex, currentAttempts, lastGeneratedCount, runGenerated;
        private static Vector2s? current;
        private static long windowStarted;
        private static int windowZones, windowAttempts, windowNotReady, windowAbandoned;
        private static double windowSpawnMs;

        // Frame start, stamped by a system at the head of the player loop.
        private static long frameStart;
        private static bool stamped, stampInstalled;

        // Interval counters, drained by Sample.
        private static long generated, attempts, notReady, abandoned, skippedUnique, busyFrames, pausedByPeer, clockMissing;
        private static double peakMs;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        internal static string Store { get; private set; } = "none";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("ServerGeneration", "IdlePregenerationEnabled", false,
                "Dedicated server only. While no client is connected or joining, generate the ungenerated zones around recent player activity through the game's own ghost-zone call, one zone per frame, so later exploration finds them generated. Grows the world file (see MaxZonesPerRun). Requires restart.");
            radiusZones = config.Bind("ServerGeneration", "IdleRadiusZones", 2, new ConfigDescription(
                "Rings of zones beyond the native ghost-generation radius (the synced total simulation distance) around each recent activity zone.",
                new AcceptableValueRange<int>(1, 8)));
            maxZonesPerRun = config.Bind("ServerGeneration", "MaxZonesPerRun", 300, new ConfigDescription(
                "Most zones generated per server process. Bounds world-file, save and memory growth.",
                new AcceptableValueRange<int>(1, 5000)));
            startDelaySeconds = config.Bind("ServerGeneration", "IdleStartDelaySeconds", 10f, new ConfigDescription(
                "Seconds after the world starts running before the first idle generation.",
                new AcceptableValueRange<float>(0f, 600f)));
            frameBudgetMs = config.Bind("ServerGeneration", "MaxMillisecondsPerFrame", 8f, new ConfigDescription(
                "Skip a frame that has already spent this long when the zone update ends. One zone generation is atomic and may exceed it.",
                new AcceptableValueRange<float>(1f, 100f)));
            log = logger;
            if (!option.Value) { Status = "disabled"; return; }
            try
            {
                bool? dedicated = ConstantResult(PatchProcessor.GetOriginalInstructions(
                    AccessTools.DeclaredMethod(typeof(ZNet), "IsDedicated", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("ZNet.IsDedicated() is missing.")));
                if (dedicated != true)
                {
                    Status = dedicated == false ? "not_dedicated" : "unavailable_unknown_build";
                    logger.LogInfo("Idle zone pre-generation not installed: " + Status + ".");
                    return;
                }
                MethodInfo update = ValidateContracts();
                Patches.Patch(update, postfix: new HarmonyMethod(typeof(IdleZonePregeneration), nameof(AfterZoneUpdate)));
                stampInstalled = InstallFrameStamp();
                Installed = true;
                Status = "installed";
                logger.LogInfo("Idle zone pre-generation installed: " + radiusZones.Value + " rings beyond the ghost radius, "
                    + maxZonesPerRun.Value + " zones per run at most.");
            }
            catch (Exception exception)
            {
                try { Patches.UnpatchSelf(); } catch { }
                Release("unavailable");
                logger.LogWarning("Idle zone pre-generation unavailable; native generation only: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        // The server build compiles IsDedicated() to a constant true, the client build to false.
        // Returns the constant, or null for any body that is not a lone boolean constant.
        internal static bool? ConstantResult(List<CodeInstruction> code)
        {
            bool? value = null;
            foreach (CodeInstruction instruction in code)
            {
                OpCode op = instruction.opcode;
                int? constant = op == OpCodes.Ldc_I4_0 ? 0 : op == OpCodes.Ldc_I4_1 ? 1 :
                    (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S) ? Convert.ToInt32(instruction.operand, CultureInfo.InvariantCulture) : (int?)null;
                if (constant != null)
                {
                    if (constant != 0 && constant != 1) return null;
                    if (value != null && value != (constant == 1)) return null;
                    value = constant == 1;
                    continue;
                }
                if (op == OpCodes.Ret || op == OpCodes.Nop || op.FlowControl == FlowControl.Branch ||
                    op.Name.StartsWith("stloc", StringComparison.Ordinal) || op.Name.StartsWith("ldloc", StringComparison.Ordinal)) continue;
                return null;
            }
            return value;
        }

        // Every native member used below, with its exact shape; any drift leaves native generation alone.
        private static MethodInfo ValidateContracts()
        {
            MethodInfo update = AccessTools.DeclaredMethod(typeof(ZoneSystem), "Update", Type.EmptyTypes)
                ?? throw new InvalidOperationException("ZoneSystem.Update() is missing.");
            if (update.IsStatic || update.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported ZoneSystem.Update signature.");
            MethodInfo spawn = AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone",
                new[] { typeof(Vector2s), typeof(ZoneSystem.SpawnMode), typeof(GameObject).MakeByRefType() })
                ?? throw new InvalidOperationException("ZoneSystem.SpawnZone(Vector2s, SpawnMode, out GameObject) is missing.");
            if (spawn.IsStatic || spawn.ReturnType != typeof(bool) || !spawn.GetParameters()[2].IsOut)
                throw new InvalidOperationException("Unsupported ZoneSystem.SpawnZone signature.");
            MethodInfo generatedTest = AccessTools.DeclaredMethod(typeof(ZoneSystem), "IsZoneGenerated", new[] { typeof(Vector2s) })
                ?? throw new InvalidOperationException("ZoneSystem.IsZoneGenerated(Vector2s) is missing.");
            if (generatedTest.IsStatic || generatedTest.ReturnType != typeof(bool))
                throw new InvalidOperationException("Unsupported ZoneSystem.IsZoneGenerated signature.");
            if (AccessTools.DeclaredField(typeof(ZoneSystem), "m_generatedZones")?.FieldType != typeof(HashSet<Vector2s>))
                throw new InvalidOperationException("ZoneSystem.m_generatedZones is not a HashSet<Vector2s>.");
            if (AccessTools.DeclaredField(typeof(ZoneSystem), "m_locationInstances")?.FieldType != typeof(Dictionary<Vector2s, ZoneSystem.LocationInstance>))
                throw new InvalidOperationException("ZoneSystem.m_locationInstances is not a Dictionary<Vector2s, LocationInstance>.");
            if (AccessTools.DeclaredMethod(typeof(ZNet), "IsSaving", Type.EmptyTypes)?.ReturnType != typeof(bool) ||
                AccessTools.DeclaredMethod(typeof(ZNet), "GetPeers", Type.EmptyTypes)?.ReturnType != typeof(List<ZNetPeer>))
                throw new InvalidOperationException("Unsupported ZNet save or peer contract.");
            spawnZone = (SpawnZoneCall)Delegate.CreateDelegate(typeof(SpawnZoneCall), spawn);
            isGenerated = (ZoneGeneratedCall)Delegate.CreateDelegate(typeof(ZoneGeneratedCall), generatedTest);
            generatedZones = AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
            return update;
        }

        private struct FrameStartStamp { }

        // First entry of the first player-loop phase, so the stamp precedes every script update.
        private static bool InstallFrameStamp()
        {
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (root.subSystemList == null || root.subSystemList.Length == 0) return false;
                PlayerLoopSystem first = root.subSystemList[0];
                var systems = new List<PlayerLoopSystem>(first.subSystemList ?? Array.Empty<PlayerLoopSystem>());
                systems.Insert(0, new PlayerLoopSystem { type = typeof(FrameStartStamp), updateDelegate = StampFrame });
                first.subSystemList = systems.ToArray();
                root.subSystemList[0] = first;
                PlayerLoop.SetPlayerLoop(root);
                return true;
            }
            catch { return false; }
        }

        private static void RemoveFrameStamp()
        {
            if (!stampInstalled) return;
            stampInstalled = false;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (root.subSystemList == null) return;
                for (int i = 0; i < root.subSystemList.Length; i++)
                {
                    PlayerLoopSystem phase = root.subSystemList[i];
                    if (phase.subSystemList == null) continue;
                    var kept = new List<PlayerLoopSystem>(phase.subSystemList);
                    if (kept.RemoveAll(system => system.type == typeof(FrameStartStamp)) == 0) continue;
                    phase.subSystemList = kept.ToArray();
                    root.subSystemList[i] = phase;
                    PlayerLoop.SetPlayerLoop(root);
                    return;
                }
            }
            catch { }
        }

        private static void StampFrame()
        {
            frameStart = Stopwatch.GetTimestamp();
            stamped = true;
        }

        private static void AfterZoneUpdate(ZoneSystem __instance)
        {
            bool fresh = stamped;
            stamped = false;
            if (!Installed || failed || __instance == null) return;
            try { Step(__instance, fresh); }
            catch (Exception exception)
            {
                // A native exception may leave a half-generated zone; never retry for this run.
                failed = true;
                Status = "failed_" + exception.GetType().Name;
                current = null;
                EndWindow("exception");
                log?.LogWarning("Idle zone pre-generation stopped for this run after " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void Step(ZoneSystem zones, bool fresh)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || !net.IsDedicated()) return;
            double now = Clock.Elapsed.TotalSeconds;
            if (!ReferenceEquals(net, world) && !BeginWorld(net, now)) { Status = "waiting_world"; return; }
            if (dirty && now - lastSave >= SaveIntervalSeconds) Save(now);
            List<ZNetPeer> peers = net.GetPeers();
            if (peers.Count > 0)
            {
                // Connected or still handshaking: ZNet adds a peer on socket accept.
                if (windowActive) { Interlocked.Increment(ref pausedByPeer); EndWindow("peer_connected"); }
                windowDone = false;
                Status = "paused_peers";
                if (now - lastSample >= ActivitySampleSeconds) { lastSample = now; SampleActivity(peers); }
                return;
            }
            // Vanilla only generates ghost zones once every location instance exists.
            if (!zones.LocationsGenerated) { Status = "waiting_locations"; return; }
            if (now - worldSeen < startDelaySeconds!.Value) { Status = "start_delay"; return; }
            if (windowDone) return;
            if (runGenerated >= maxZonesPerRun!.Value) { EndWindow("run_cap"); Status = "run_cap_reached"; return; }
            if (!windowActive && !BeginWindow(zones, net, now)) return;
            HashSet<Vector2s> set = generatedZones!(zones);
            // A save in progress, or any other generation since our last call (vanilla local or
            // ghost zones), and this frame is left alone.
            if (net.IsSaving() || set.Count != lastGeneratedCount)
            {
                lastGeneratedCount = set.Count;
                Interlocked.Increment(ref busyFrames);
                return;
            }
            if (!fresh) Interlocked.Increment(ref clockMissing);
            else if ((Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency > frameBudgetMs!.Value)
            { Interlocked.Increment(ref busyFrames); return; }
            Status = "generating";
            GenerateOne(zones);
            lastGeneratedCount = set.Count;
        }

        private static void SampleActivity(List<ZNetPeer> peers)
        {
            foreach (ZNetPeer peer in peers)
            {
                if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone()) continue;
                Vector2s zone = ZoneSystem.GetZone(peer.GetRefPos());
                var coord = new ZoneCoord(zone.x, zone.y);
                if (PregenerationFrontier.InWorld(coord) && Activity.Touch(coord)) dirty = true;
            }
        }

        private static void GenerateOne(ZoneSystem zones)
        {
            int skips = 0;
            while (current == null)
            {
                if (queueIndex >= queue.Count) { EndWindow("exhausted"); Status = "exhausted"; return; }
                ZoneCoord next = queue[queueIndex++];
                var zone = new Vector2s(next.X, next.Y);
                bool skip = isGenerated!(zones, zone);
                // Which of several candidate sites becomes a unique location (e.g. a trader) is
                // decided by generation order; leave those zones to the players, as vanilla does.
                if (!skip && zones.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance) &&
                    !instance.m_placed && instance.m_location != null && instance.m_location.m_unique)
                { skip = true; Interlocked.Increment(ref skippedUnique); }
                if (!skip) { current = zone; currentAttempts = 0; break; }
                if (++skips >= MaxSkipsPerFrame) return;
            }
            Vector2s target = current!.Value;
            currentAttempts++;
            windowAttempts++;
            Interlocked.Increment(ref attempts);
            long started = Stopwatch.GetTimestamp();
            bool spawned = spawnZone!(zones, target, ZoneSystem.SpawnMode.Ghost, out GameObject _);
            double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            windowSpawnMs += elapsed;
            if (elapsed > peakMs) peakMs = elapsed;
            if (spawned)
            {
                current = null;
                if (!isGenerated!(zones, target)) return;
                windowZones++;
                runGenerated++;
                Interlocked.Increment(ref generated);
                return;
            }
            // Terrain or location prefab still loading; the native call queued it. Same zone next frame.
            windowNotReady++;
            Interlocked.Increment(ref notReady);
            if (currentAttempts < MaxAttemptsPerZone) return;
            windowAbandoned++;
            Interlocked.Increment(ref abandoned);
            current = null;
        }

        private static bool BeginWorld(ZNet net, double now)
        {
            World? info = ZNet.World;
            WorldGenerator generator = WorldGenerator.instance;
            if (info == null || generator == null) return false;
            if (world != null) Save(now);
            EndWindow("world_changed");
            world = net;
            worldSeen = lastSample = lastSave = now;
            windowDone = dirty = false;
            queue = new List<ZoneCoord>();
            queueIndex = 0;
            current = null;
            Activity.Replace(Array.Empty<ZoneCoord>());
            string directory = Path.Combine(Paths.BepInExRootPath, "BetterPerformance", "pregeneration");
            storePath = Path.Combine(directory, "activity-" + PregenerationFrontier.WorldKey(info.m_name ?? "", generator.GetSeed()) + ".txt");
            try
            {
                if (!File.Exists(storePath)) { Store = "missing"; return true; }
                if (PregenerationFrontier.TryParse(File.ReadAllText(storePath), ActivityCapacity, out List<ZoneCoord> loaded, out string failure))
                {
                    Activity.Replace(loaded);
                    Store = "loaded_" + loaded.Count.ToString(CultureInfo.InvariantCulture);
                }
                else Store = "corrupt_" + failure;
            }
            catch (Exception exception) { Store = "unreadable_" + exception.GetType().Name; }
            return true;
        }

        private static bool BeginWindow(ZoneSystem zones, ZNet net, double now)
        {
            if (dirty) Save(now); // Players just left: their activity is complete for now.
            int radius = Math.Min(PregenerationFrontier.MaxRadius,
                net.GetSyncedSimulationDistance().TotalSimulationDistance + radiusZones!.Value);
            HashSet<Vector2s> set = generatedZones!(zones);
            queue = PregenerationFrontier.Plan(Activity.MostRecentFirst, radius,
                zone => set.Contains(new Vector2s(zone.X, zone.Y)), maxZonesPerRun!.Value - runGenerated);
            queueIndex = 0;
            current = null;
            lastGeneratedCount = set.Count;
            windowActive = true;
            windowStarted = Stopwatch.GetTimestamp();
            windowZones = windowAttempts = windowNotReady = windowAbandoned = 0;
            windowSpawnMs = 0;
            if (queue.Count > 0)
            {
                log?.LogMessage("Idle pre-generation started: " + queue.Count + " zones to generate around recent play. It pauses whenever a player connects.");
                return true;
            }
            EndWindow("nothing_to_do");
            Status = "exhausted";
            return false;
        }

        // One log line per idle window.
        private static void EndWindow(string reason)
        {
            if (!windowActive) return;
            windowActive = false;
            windowDone = reason != "peer_connected";
            current = null;
            double seconds = (Stopwatch.GetTimestamp() - windowStarted) / (double)Stopwatch.Frequency;
            log?.LogInfo("Idle zone pre-generation: " + windowZones + " zones in " + seconds.ToString("0.0", CultureInfo.InvariantCulture)
                + " s (" + windowSpawnMs.ToString("0", CultureInfo.InvariantCulture) + " ms generating; attempts " + windowAttempts
                + ", not ready " + windowNotReady + ", abandoned " + windowAbandoned + "); " + Remaining() + " candidates left; run total "
                + runGenerated + "; ended: " + reason + ".");
            // One plain console line an operator can wait for before connecting.
            string rounded = seconds.ToString("0", CultureInfo.InvariantCulture);
            if (reason == "exhausted" || reason == "run_cap")
                log?.LogMessage("Idle pre-generation finished: " + windowZones + " zones in " + rounded + " s. The area around recent play is ready.");
            else if (reason == "nothing_to_do")
                log?.LogMessage("Idle pre-generation: nothing left to generate around recent play. Ready.");
            else if (reason == "peer_connected")
                log?.LogMessage("Idle pre-generation paused: a player is connecting (" + windowZones + " zones done, " + Remaining() + " left).");
        }

        private static int Remaining() => Math.Max(0, queue.Count - queueIndex) + (current != null ? 1 : 0);

        private static void Save(double now)
        {
            lastSave = now;
            if (storePath == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
                string temporary = storePath + ".tmp";
                File.WriteAllText(temporary, PregenerationFrontier.Serialize(Activity.MostRecentFirst));
                if (File.Exists(storePath)) File.Replace(temporary, storePath, null);
                else File.Move(temporary, storePath);
                dirty = false;
                Store = "saved_" + Activity.Count.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception exception) { Store = "save_failed_" + exception.GetType().Name; }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("idle_pregen_status", Status));
            if (!Installed) return;
            gauges.Add(new NumberValue("idle_pregen_zones_generated", Interlocked.Exchange(ref generated, 0), "zones"));
            gauges.Add(new NumberValue("idle_pregen_attempts", Interlocked.Exchange(ref attempts, 0), "calls"));
            gauges.Add(new NumberValue("idle_pregen_not_ready", Interlocked.Exchange(ref notReady, 0), "calls"));
            gauges.Add(new NumberValue("idle_pregen_abandoned", Interlocked.Exchange(ref abandoned, 0), "zones"));
            gauges.Add(new NumberValue("idle_pregen_skipped_unique", Interlocked.Exchange(ref skippedUnique, 0), "zones"));
            gauges.Add(new NumberValue("idle_pregen_busy_frames", Interlocked.Exchange(ref busyFrames, 0), "frames"));
            gauges.Add(new NumberValue("idle_pregen_frame_clock_missing", Interlocked.Exchange(ref clockMissing, 0), "frames"));
            gauges.Add(new NumberValue("idle_pregen_paused_by_peer", Interlocked.Exchange(ref pausedByPeer, 0), "windows"));
            gauges.Add(new NumberValue("idle_pregen_peak_ms", Math.Round(Interlocked.Exchange(ref peakMs, 0d), 3), "ms"));
            gauges.Add(new NumberValue("idle_pregen_candidates_remaining", Remaining(), "zones"));
            gauges.Add(new NumberValue("idle_pregen_activity_zones", Activity.Count, "zones"));
            gauges.Add(new NumberValue("idle_pregen_run_total", runGenerated, "zones"));
            labels.Add(new TextValue("idle_pregen_store", Store));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref generated, 0);
            Interlocked.Exchange(ref attempts, 0);
            Interlocked.Exchange(ref notReady, 0);
            Interlocked.Exchange(ref abandoned, 0);
            Interlocked.Exchange(ref skippedUnique, 0);
            Interlocked.Exchange(ref busyFrames, 0);
            Interlocked.Exchange(ref clockMissing, 0);
            Interlocked.Exchange(ref pausedByPeer, 0);
            Interlocked.Exchange(ref peakMs, 0d);
        }

        internal static void Uninstall()
        {
            if (Installed)
            {
                EndWindow("shutdown");
                if (dirty) Save(Clock.Elapsed.TotalSeconds);
            }
            RemoveFrameStamp();
            try { Patches.UnpatchSelf(); } catch { }
            Reset();
            Release("disabled");
            world = null;
            storePath = null;
            queue = new List<ZoneCoord>();
            queueIndex = runGenerated = 0;
            current = null;
            windowDone = failed = dirty = false;
            Activity.Replace(Array.Empty<ZoneCoord>());
            option = null;
            radiusZones = maxZonesPerRun = null;
            startDelaySeconds = frameBudgetMs = null;
        }

        private static void Release(string status)
        {
            Installed = false;
            Status = status;
            spawnZone = null;
            isGenerated = null;
            generatedZones = null;
        }
    }
}
