using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using BetterPerformance.Core;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace BetterPerformance
{
    [BepInPlugin(PluginId, "BetterPerformance", PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "jf10r.BetterPerformance";
        public const string PluginVersion = "0.1.0";
        private readonly Harmony harmony = new Harmony(PluginId);
        private ConfigEntry<bool> captureEnabled = null!, autoStart = null!, methodTimings = null!;
        private ConfigEntry<int> duration = null!, capacity = null!, fileLimit = null!;
        private ConfigEntry<float> interval = null!;
        private CaptureSession? current, retiring;
        private Process? process;
        private FieldInfo? instances;
        private bool autoStarted;
        private long previousLoop;
        private double previousCpuMs, previousCpuSampleElapsed;
        private readonly int[] previousGc = new int[3];

        private void Awake()
        {
            captureEnabled = Config.Bind("Capture", "Enabled", true, "Enable diagnostics. Does not change gameplay or networking.");
            autoStart = Config.Bind("Capture", "AutoStart", true, "Start one bounded capture when the first world session begins.");
            duration = Config.Bind("Capture", "DurationSeconds", 300, new ConfigDescription("Maximum duration of each capture.", new AcceptableValueRange<int>(10, 3600)));
            interval = Config.Bind("Capture", "IntervalSeconds", 1f, new ConfigDescription("Aggregation and process polling interval.", new AcceptableValueRange<float>(0.5f, 10f)));
            capacity = Config.Bind("Capture", "QueueCapacity", 16, new ConfigDescription("Maximum queued export records. Full queues drop records without blocking.", new AcceptableValueRange<int>(2, 64)));
            fileLimit = Config.Bind("Capture", "MaxFileMiB", 64, new ConfigDescription("Maximum size of one capture, including its completion record.", new AcceptableValueRange<int>(1, 256)));
            methodTimings = Config.Bind("Capture", "MethodTimings", true, "Install observational timing probes. Requires restart; disable for an overhead comparison.");
            if (!captureEnabled.Value) { Logger.LogInfo("Diagnostics disabled. No probes installed."); return; }
            instances = AccessTools.Field(typeof(ZNetScene), "m_instances");
            TimingHooks.Install(harmony, Logger, methodTimings.Value);
            new Terminal.ConsoleCommand("bp_capture", "BetterPerformance: start | stop | status (local process only)",
                (Terminal.ConsoleEvent)Command);
            Logger.LogInfo("BetterPerformance diagnostics ready. No gameplay or networking settings changed.");
        }

        private void Command(Terminal.ConsoleEventArgs args)
        {
            string operation = args.Args.Length == 2 ? args.Args[1].ToLowerInvariant() : "status";
            try
            {
                if (operation == "start") { autoStarted = true; StartCapture(); }
                else if (operation == "stop") { autoStarted = true; StopCapture("manual_stop"); }
                else if (operation != "status") { args.Context.AddString("Usage: bp_capture start | stop | status"); return; }
                args.Context.AddString(current != null ? "BetterPerformance recording: " + current.OutputPath :
                    retiring != null ? "BetterPerformance finishing export." : "BetterPerformance idle.");
            }
            catch (Exception exception) { Logger.LogError("Capture command failed: " + exception.GetType().Name); }
        }

        private void Update()
        {
            try
            {
                if (retiring != null && retiring.Writer.Finish(0))
                {
                    Logger.LogInfo("Capture export finished. Written records: " + retiring.Writer.WrittenRecords
                        + "; dropped: " + retiring.Writer.DroppedRecords + "; error: " + (retiring.Writer.LastError ?? "none"));
                    retiring.Writer.Dispose();
                    retiring = null;
                }
                if (!captureEnabled.Value) { if (current != null) StopCapture("disabled"); return; }
                if (autoStart.Value && !autoStarted && ZNet.instance != null)
                { autoStarted = true; StartCapture(); }
                var session = current;
                if (session == null) return;
                if (session.Writer.LastError != null || session.Writer.LimitReached)
                { StopCapture(session.Writer.LimitReached ? "file_size_limit" : "writer_error"); return; }
                if (ZNet.instance == null) { StopCapture("world_session_ended"); return; }
                long now = Stopwatch.GetTimestamp();
                if (previousLoop != 0) session.Book.Record(Metric.LoopInterval, (now - previousLoop) * 1000.0 / Stopwatch.Frequency);
                previousLoop = now;
                if (session.Elapsed >= session.DurationSeconds) { StopCapture("duration_limit"); return; }
                if (session.PollDue) Sample(session);
            }
            catch (Exception exception)
            {
                Logger.LogError("Diagnostics stopped after collector failure: " + exception.GetType().Name);
                StopCapture("collector_error");
            }
        }

        private void StartCapture()
        {
            if (!captureEnabled.Value) { Logger.LogInfo("Enable Capture.Enabled and restart before recording."); return; }
            if (current != null || retiring != null) { Logger.LogInfo("A capture is already recording or finishing."); return; }
            if (ZNet.instance == null) { Logger.LogInfo("Enter a world before starting a capture."); return; }
            var metadata = new List<TextValue>(TimingHooks.Availability)
            {
                new TextValue("plugin_version", PluginVersion),
                new TextValue("game_version", global::Version.GetVersionString(false)),
                new TextValue("mode", "diagnostics_only"),
                new TextValue("queue_semantics", "socket API result; active mods may adjust it or make it negative"),
                new TextValue("percentiles", "approximate upper bounds from fixed logarithmic buckets"),
                new TextValue("probe.SceneInstanceCount", instances == null ? "unavailable" : "enabled"),
                new TextValue("unavailable", "GPU time; RPC/action latency; raw bandwidth; exclusive CPU time; pure disk write duration; remote-client state")
            };
            int pluginCount = 0;
            foreach (var pair in Chainloader.PluginInfos)
            {
                if (pluginCount++ == 128) { metadata.Add(new TextValue("mods_truncated", "true")); break; }
                string id = pair.Key.Length <= 256 ? pair.Key : pair.Key.Substring(0, 256);
                metadata.Add(new TextValue("mod." + id, pair.Value.Metadata.Version.ToString()));
            }
            process?.Dispose();
            process = Process.GetCurrentProcess();
            previousCpuMs = process.TotalProcessorTime.TotalMilliseconds;
            previousCpuSampleElapsed = 0;
            previousLoop = 0;
            for (int i = 0; i < previousGc.Length; i++) previousGc[i] = GC.CollectionCount(i);
            current = new CaptureSession(Path.Combine(Paths.BepInExRootPath, "BetterPerformance", "captures"),
                Role(), duration.Value, interval.Value, capacity.Value, fileLimit.Value * 1024L * 1024L, metadata);
            System.Threading.Volatile.Write(ref TimingHooks.Current, current);
            Logger.LogInfo("Capture started: " + current.OutputPath);
        }

        private void Sample(CaptureSession session)
        {
            long started = Stopwatch.GetTimestamp();
            var gauges = new List<NumberValue>();
            var labels = new List<TextValue> { new TextValue("role", Role()) };
            double elapsed = session.Elapsed;
            double cpuWindowSeconds = elapsed - previousCpuSampleElapsed;
            try
            {
                process!.Refresh();
                double cpuMs = process.TotalProcessorTime.TotalMilliseconds;
                double delta = Math.Max(0, cpuMs - previousCpuMs);
                gauges.Add(new NumberValue("process_cpu_delta", delta, "ms"));
                gauges.Add(new NumberValue("process_cpu_machine_percent", delta / (cpuWindowSeconds * 1000) / Environment.ProcessorCount * 100, "percent"));
                gauges.Add(new NumberValue("process_working_set", process.WorkingSet64, "bytes"));
                previousCpuMs = cpuMs;
                previousCpuSampleElapsed = elapsed;
                labels.Add(new TextValue("process_metrics", "available"));
            }
            catch { labels.Add(new TextValue("process_metrics", "unavailable")); }
            gauges.Add(new NumberValue("managed_heap_estimate", GC.GetTotalMemory(false), "bytes"));
            for (int i = 0; i < previousGc.Length; i++)
            {
                int count = GC.CollectionCount(i);
                gauges.Add(new NumberValue("gc_gen" + i + "_collections", count - previousGc[i], "collections"));
                previousGc[i] = count;
            }
            var peers = ZNet.instance.GetPeers();
            var distance = ZNet.instance.GetSyncedSimulationDistance();
            gauges.Add(new NumberValue("simulation_near_radius", distance.NearSimulationDistance, "sectors"));
            gauges.Add(new NumberValue("simulation_far_extension", distance.FarSimulationDistance, "sectors"));
            labels.Add(new TextValue("simulation_classic", distance.IsClassic ? "true" : "false"));
            long totalQueue = 0;
            int sampled = 0, unavailable = 0, negative = 0;
            int maxQueue = int.MinValue;
            foreach (var peer in peers)
            {
                try
                {
                    if (peer.m_socket == null) { unavailable++; continue; }
                    int size = peer.m_socket.GetSendQueueSize();
                    totalQueue += size;
                    maxQueue = Math.Max(maxQueue, size);
                    if (size < 0) negative++;
                    sampled++;
                }
                catch { unavailable++; }
            }
            gauges.Add(new NumberValue("peer_count", peers.Count, "peers"));
            gauges.Add(new NumberValue("socket_queues_sampled", sampled, "sockets"));
            gauges.Add(new NumberValue("socket_queues_unavailable", unavailable, "sockets"));
            gauges.Add(new NumberValue("socket_queues_negative", negative, "sockets"));
            if (sampled > 0)
            {
                gauges.Add(new NumberValue("reported_send_queue_sum", totalQueue, "bytes"));
                gauges.Add(new NumberValue("reported_send_queue_max", maxQueue, "bytes"));
            }
            if (instances != null && ZNetScene.instance != null)
            {
                if (instances.GetValue(ZNetScene.instance) is ICollection collection)
                    gauges.Add(new NumberValue("scene_instance_count", collection.Count, "objects"));
                else labels.Add(new TextValue("scene_instance_count", "unavailable"));
            }
            session.Book.Record(Metric.CollectorPoll, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            session.Export(gauges, labels);
        }

        private static string Role() => ZNet.instance == null ? "no_world" :
            ZNet.instance.IsDedicated() ? "dedicated_server" : ZNet.instance.IsServer() ? "listen_server_client" : "client";

        private void StopCapture(string reason)
        {
            var session = current;
            if (session == null) return;
            System.Threading.Volatile.Write(ref TimingHooks.Current, (CaptureSession?)null);
            current = null;
            retiring = session;
            session.Export(new List<NumberValue>(), new List<TextValue> { new TextValue("reason", reason), new TextValue("role", Role()) }, final: true);
            Logger.LogInfo("Capture stopped: " + reason + ". Export is finishing in the background.");
        }

        private void OnDestroy()
        {
            StopCapture("plugin_shutdown");
            if (retiring != null)
            {
                if (retiring.Writer.Finish(2000)) retiring.Writer.Dispose();
                else Logger.LogWarning("Capture export did not finish within the shutdown deadline; the tail may be incomplete.");
            }
            harmony.UnpatchSelf();
            process?.Dispose();
        }
    }
}
