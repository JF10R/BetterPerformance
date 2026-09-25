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
        public const string PluginVersion = "0.4.17";
        private static Plugin? instance;
        private int mainThreadId, previousFrameGc;
        private readonly Harmony harmony = new Harmony(PluginId);
        private ConfigEntry<bool> captureEnabled = null!, autoStart = null!, methodTimings = null!;
        private ConfigEntry<bool> continuous = null!, purgeOldest = null!;
        private ConfigEntry<bool> slowOperations = null!;
        private ConfigEntry<float> slowMethodMs = null!, slowLoopMs = null!, slowWorkerMs = null!;
        private ConfigEntry<int> directoryLimit = null!;
        private ConfigEntry<int> duration = null!, capacity = null!, fileLimit = null!;
        private ConfigEntry<float> interval = null!;
        private CaptureSession? current, retiring;
        private Process? process;
        private FieldInfo? instances;
        private bool autoStarted;
        private bool loadingPaused;
        private bool continuePending;
        private ZNet? observedWorld;
        private string recordingId = Guid.NewGuid().ToString("N");
        private int segment;
        private long previousLoop;
        private double previousCpuMs, previousCpuSampleElapsed;
        private readonly int[] previousGc = new int[3];

        private void Awake()
        {
            instance = this;
            mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            captureEnabled = Config.Bind("Capture", "Enabled", true, "Enable diagnostics. Does not change gameplay or networking.");
            autoStart = Config.Bind("Capture", "AutoStart", true, "Start a capture when each world session begins.");
            continuous = Config.Bind("Capture", "Continuous", false, "Continue with a new file after the duration/file limit in the same world. Manual stop and errors stop recording, as does the directory limit when PurgeOldestWhenFull is off.");
            directoryLimit = Config.Bind("Capture", "MaxDirectoryMiB", 512, new ConfigDescription("Maximum JSONL capture directory allowance. Each new capture reserves its full file allowance first (see PurgeOldestWhenFull). One process per directory.", new AcceptableValueRange<int>(64, 4096)));
            purgeOldest = Config.Bind("Capture", "PurgeOldestWhenFull", true, "When the directory allowance is reached, delete this plugin's oldest captures to keep recording. Off: stop recording and keep every file.");
            duration = Config.Bind("Capture", "DurationSeconds", 300, new ConfigDescription("Maximum duration of each capture.", new AcceptableValueRange<int>(10, 3600)));
            interval = Config.Bind("Capture", "IntervalSeconds", 1f, new ConfigDescription("Aggregation and process polling interval.", new AcceptableValueRange<float>(0.5f, 10f)));
            capacity = Config.Bind("Capture", "QueueCapacity", 16, new ConfigDescription("Maximum queued export records. Full queues drop records without blocking.", new AcceptableValueRange<int>(2, 64)));
            fileLimit = Config.Bind("Capture", "MaxFileMiB", 64, new ConfigDescription("Maximum size of one capture, including its completion record.", new AcceptableValueRange<int>(1, 256)));
            methodTimings = Config.Bind("Capture", "MethodTimings", true, "Install observational timing probes. Requires restart; disable for an overhead comparison.");
            slowOperations = Config.Bind("Diagnostics", "SlowOperationsEnabled", true, "Include bounded slow-operation summaries in each interval, with its sampled context. No per-call log spam.");
            slowMethodMs = Config.Bind("Diagnostics", "SlowMethodMilliseconds", 20f, new ConfigDescription("Flag a measured method when its interval peak reaches this threshold. Inclusive elapsed time, not exclusive CPU.", new AcceptableValueRange<float>(1f, 10000f)));
            slowLoopMs = Config.Bind("Diagnostics", "SlowLoopMilliseconds", 100f, new ConfigDescription("Loop-gap alert threshold. Gaps include frame pacing and scheduling; they are not method execution time.", new AcceptableValueRange<float>(10f, 10000f)));
            slowWorkerMs = Config.Bind("Diagnostics", "SlowSaveWorkerMilliseconds", 250f, new ConfigDescription("Save-worker alert threshold. Worker elapsed time is not a main-thread pause.", new AcceptableValueRange<float>(10f, 60000f)));
            ObjectCreationBudget.Install(Config, Logger);
            InitialLoadingOptimization.Install(Config, Logger);
            if (Config.Bind("NetworkMemory", "LocalPackageCopyEnabled", false,
                "Avoid temporary source arrays at the verified local SendZDOs package copy. Preserve native length/payload and ownership; requires restart.").Value)
            {
                PackageCopyOptimization.Install(Logger);
                PackageCopyOptimization.Enabled = PackageCopyOptimization.Installed;
            }
            ResourceTelemetry.Enabled = Config.Bind("Diagnostics", "ResourceMemoryEnabled", true,
                "Observe private committed and Unity allocator memory at the capture poll cadence. No forced garbage collection.").Value;
            RenderTelemetry.Enabled = Config.Bind("Diagnostics", "SparseRenderTimingEnabled", false,
                "Optional sparse completed-frame CPU/GPU samples when the shipped engine exposes frame timings; never frame percentiles.").Value;
            EngineTelemetry.Install(Config, Logger);
            if (Config.Bind("MapSaving", "ExactCompressionCacheEnabled", false,
                "Reuse compressed output only when the complete native serialized map input is byte-identical. Retains a bounded cache (up to 24 MiB) and requires restart.").Value)
            {
                MapCompressionCache.Install(Logger);
                MapCompressionCache.Enabled = MapCompressionCache.Installed;
            }
            if (Config.Bind("MapSaving", "Enabled", false,
                "Bulk-write the two verified minimap bit arrays using the native save format and lifecycle. Requires restart; falls back if the installed game layout is unsupported.").Value)
            {
                FastMapSerialization.Install(Logger);
                FastMapSerialization.Enabled = FastMapSerialization.Installed;
            }
            // Cloud write telemetry always installs; the buffer sizing stays behind its own key.
            CloudWriteOptimization.Install(Config, Logger);
            // Minimap texture cache: shadow-compares before it is ever trusted; self-gates when disabled.
            MinimapTextureCache.Install(Config, Logger);
            // Biome point cache: 1.0.14 disabled the native one; ours keys on seed, uid, versions, generator IL and mods.
            BiomePointCache.Install(Config, Logger);
            // Replication cadence and bird velocity stay behind their own [Replication] keys.
            ReplicationCadence.Install(Config, Logger);
            // GUI group-sound deduplication and mined-drop placement stay behind their own keys.
            GuiSoundDeduplication.Install(Config, Logger);
            MiningDropPlacement.Install(Config, Logger);
            // 0.4.8 opt-in modules: smelter catch-up budget, dungeon spawn slicing and, after the
            // exact map cache it requires, speculative map pre-compression.
            SmelterTelemetry.Install(Logger);
            DungeonSpawnSlicing.Install(Config, Logger);
            MapPrecompression.Install(Config, Logger);
            // 0.4.15 opt-in: queued terrain rebuilds under a per-frame budget (client), zone
            // generation while no peer is connected (dedicated server).
            HeightmapRebuildBudget.Install(Config, Logger);
            IdleZonePregeneration.Install(Config, Logger);
            // 0.4.16 opt-in: the native hourly unused-asset unload moves out of play.
            AssetUnloadDeferral.Install(Config, Logger);
            new Terminal.ConsoleCommand("bp_budget", "Experimental object budget: on | off | status (installed at startup; local process only)",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (args.Args.Length == 2 && (args.Args[1] == "on" || args.Args[1] == "off"))
                        SetObjectCreationBudgetEnabled(args.Args[1] == "on");
                    args.Context.AddString("Object budget: " + ObjectCreationBudget.Status + "; active=" + ObjectCreationBudget.Enabled);
                }));
            new Terminal.ConsoleCommand("bp_mining", "Experimental mined-drop hit-point placement: on | off | status (installed at startup; local process only)",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (args.Args.Length == 2 && (args.Args[1] == "on" || args.Args[1] == "off"))
                        SetMiningDropPlacementEnabled(args.Args[1] == "on");
                    args.Context.AddString("Mined-drop placement: " + MiningDropPlacement.Status + "; active=" + MiningDropPlacement.Enabled
                        + "; overridden=" + MiningDropPlacement.OverriddenCount() + "/" + MiningDropPlacement.TrackedCount);
                }));
            new Terminal.ConsoleCommand("bp_dungeon", "Experimental dungeon spawn slicing: on | off | status (installed at startup; local process only)",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (args.Args.Length == 2 && (args.Args[1] == "on" || args.Args[1] == "off"))
                        SetDungeonSpawnSlicingEnabled(args.Args[1] == "on");
                    args.Context.AddString("Dungeon spawn slicing: " + DungeonSpawnSlicing.Status + "; active=" + DungeonSpawnSlicing.Enabled
                        + "; pending=" + DungeonSpawnSlicing.PendingCount);
                }));
            if (!captureEnabled.Value) { Logger.LogInfo("Diagnostics disabled. No probes installed."); return; }
            instances = AccessTools.Field(typeof(ZNetScene), "m_instances");
            TimingHooks.Install(harmony, Logger, methodTimings.Value);
            if (Config.Bind("Diagnostics", "LoadingTimelineEnabled", true,
                "Observe a bounded client loading timeline, including scene transition before capture starts. Monotonic wall time; no loading behavior changes.").Value)
            {
                LoadingTelemetry.Install(Logger);
                LoadingTelemetry.Enabled = LoadingTelemetry.Installed;
            }
            if (Config.Bind("Diagnostics", "LoadingDetailsEnabled", true,
                "Observe biome initialization and existing respawn readiness evidence. Fixed process-cumulative counters, including pre-capture work; no loading behavior changes.").Value)
            {
                LoadingDetailsTelemetry.Install(Logger);
                LoadingDetailsTelemetry.Enabled = LoadingDetailsTelemetry.Installed;
            }
            LootQueueTelemetry.ConfigureAndInstall(Config, Logger);
            LootVisibilityTelemetry.Install(Config, Logger);
            if (Config.Bind("Diagnostics", "LocalActionOutcomesEnabled", true,
                "Observe bounded local pickup and container outcomes. Excludes ambiguous matches; does not infer remote-client latency.").Value)
            {
                ActionTelemetry.Install(Logger);
                ActionTelemetry.Enabled = ActionTelemetry.Installed;
            }
            if (Config.Bind("Diagnostics", "AttributionEnabled", true,
                "Attribute object creation cost, serialized replication bytes and routed RPC dispatch by prefab/RPC name. Bounded top-N per export; no payloads or player data.").Value)
            {
                AttributionTelemetry.Install(Config, Logger);
                AttributionTelemetry.Enabled = AttributionTelemetry.Installed;
            }
            if (Config.Bind("Diagnostics", "OwnershipCountersEnabled", true,
                "Count native ownership/replication calls and read ZDO manager counters. Counts only; no latency or behavior change.").Value)
            {
                OwnershipTelemetry.Install(Logger);
                OwnershipTelemetry.Enabled = OwnershipTelemetry.Installed;
            }
            // Grant counters always; the forced insert itself stays behind its own config key.
            OwnershipExpedite.Install(Config, Logger);
            // Server-side teleport-ghost fix: re-issues the native sector invalidation after the position write.
            SectorInvalidationFix.Install(Config, Logger);
            // 0.4.12 [Network]: adaptive send window + rate policy, and framed compression; both yield to BetterNetworking.
            NetworkFlow.Install(Config, Logger);
            NetworkCompression.Install(Config, Logger);
            // 0.4.13 [Relay]: a client mirrors its capture (and optionally its log) to a server that accepts them.
            CaptureRelay.Install(Config, Logger, Path.Combine(Paths.BepInExRootPath, "BetterPerformance", "captures"));
            ReplicationTelemetry.Install(Config, Logger);
            ReplicationTelemetry.Enabled = ReplicationTelemetry.Installed;
            TerrainTelemetry.Install(Config, Logger);
            TerrainTelemetry.Enabled = TerrainTelemetry.Installed;
            GameplayTelemetry.Install(Config, Logger);
            GameplayTelemetry.Enabled = GameplayTelemetry.Installed;
            ZoneGenerationTelemetry.Install(Config, Logger);
            CharacterSaveDiskTelemetry.Install(Config, Logger);
            GraphicsSettingsManager.GraphicsSettingsChanged += GraphicsSettingsApplied;
            new Terminal.ConsoleCommand("bp_capture", "BetterPerformance: start | stop | status (local process only)",
                (Terminal.ConsoleEvent)Command);
            new Terminal.ConsoleCommand("bp_mark", "Add a capture marker: bp_mark lag_loot (letters, numbers, underscores; local process only)",
                (Terminal.ConsoleEvent)(args => args.Context.AddString(args.Args.Length == 2 && Mark(args.Args[1]) ?
                    "Capture marker added." : "Marker rejected: recording required; use a short identifier such as lag_loot.")));
            Logger.LogInfo("BetterPerformance diagnostics ready. Object budget: " + ObjectCreationBudget.Status);
        }

        // Main-thread-only runtime switch for a module explicitly installed at startup.
        // Does not persist configuration or communicate with another process.
        public static bool SetObjectCreationBudgetEnabled(bool enabled)
        {
            if (instance == null || !ObjectCreationBudget.Installed ||
                System.Threading.Thread.CurrentThread.ManagedThreadId != instance.mainThreadId) return false;
            ObjectCreationBudget.Enabled = enabled;
            return true;
        }

        // Same main-thread-only contract as the object budget: applies or restores the
        // field override on every tracked live instance so one session can A/B the look.
        public static bool SetMiningDropPlacementEnabled(bool enabled)
        {
            if (instance == null || !MiningDropPlacement.Installed ||
                System.Threading.Thread.CurrentThread.ManagedThreadId != instance.mainThreadId) return false;
            MiningDropPlacement.SetEnabled(enabled);
            return true;
        }

        // Main-thread-only, like the other runtime toggles. In-flight slices always finish;
        // only new dungeons return to the native single-frame spawn.
        public static bool SetDungeonSpawnSlicingEnabled(bool enabled)
        {
            if (instance == null || !DungeonSpawnSlicing.Installed ||
                System.Threading.Thread.CurrentThread.ManagedThreadId != instance.mainThreadId) return false;
            DungeonSpawnSlicing.SetEnabled(enabled);
            return true;
        }

        // Main-thread-only scenario API; bounded labels/records, no file access or RPC.
        public static bool Mark(string name)
        {
            var plugin = instance;
            var session = plugin?.current;
            if (plugin == null || session == null || !CaptureMarkers.IsValid(name) || session.MarkerCount >= 256 ||
                System.Threading.Thread.CurrentThread.ManagedThreadId != plugin.mainThreadId) return false;
            try
            {
                plugin.Sample(session); // Drain the preceding phase before changing the label.
                session.Mark(name);
                return true;
            }
            catch { session.RecordProbeFailure(); return false; }
        }

        private void Command(Terminal.ConsoleEventArgs args)
        {
            string operation = args.Args.Length == 2 ? args.Args[1].ToLowerInvariant() : "status";
            try
            {
                if (operation == "start") { autoStarted = true; loadingPaused = false; StartCapture(); }
                else if (operation == "stop") { autoStarted = true; loadingPaused = true; LoadingTelemetry.Enabled = false; LoadingDetailsTelemetry.Enabled = false; continuePending = false; StopCapture("manual_stop"); }
                else if (operation != "status") { args.Context.AddString("Usage: bp_capture start | stop | status"); return; }
                args.Context.AddString(current != null ? "BetterPerformance recording: " + current.OutputPath :
                    retiring != null ? "BetterPerformance finishing export." : "BetterPerformance idle.");
            }
            catch (Exception exception) { Logger.LogError("Capture command failed: " + exception.GetType().Name); }
        }

        private void GraphicsSettingsApplied()
        {
            var session = current;
            if (session == null) return;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != mainThreadId)
            { session.RecordProbeFailure(); return; }
            GraphicsTelemetry.Observe(session, "graphics_applied");
        }

        // Fixed-step accounting only; no physics or timing settings are read or changed here.
        private void FixedUpdate() { EngineTelemetry.NoteFixedStep(); }

        private void Update()
        {
            try
            {
                EngineTelemetry.NoteFrame();
                CaptureRelay.Pump();
                if (retiring != null && retiring.Writer.Finish(0))
                {
                    CaptureRelay.CaptureFinished(retiring.OutputPath);
                    Logger.LogInfo("Capture export finished. Written records: " + retiring.Writer.WrittenRecords
                        + "; dropped: " + retiring.Writer.DroppedRecords + "; error: " + (retiring.Writer.LastError ?? "none"));
                    if (retiring.Writer.LastError != null) continuePending = false;
                    retiring.Writer.Dispose();
                    retiring = null;
                }
                if (!ReferenceEquals(observedWorld, ZNet.instance))
                {
                    MapCompressionCache.Clear();
                    StopCapture("world_session_ended");
                    observedWorld = ZNet.instance;
                    autoStarted = false;
                    loadingPaused = false;
                    continuePending = false;
                    recordingId = Guid.NewGuid().ToString("N");
                    segment = 0;
                }
                MapPrecompression.Pump();
                AssetUnloadDeferral.Pump();
                LoadingTelemetry.Enabled = captureEnabled.Value && !loadingPaused && LoadingTelemetry.Installed;
                LoadingDetailsTelemetry.Enabled = captureEnabled.Value && !loadingPaused && LoadingDetailsTelemetry.Installed;
                if (!captureEnabled.Value) { continuePending = false; if (current != null) StopCapture("disabled"); return; }
                if (continuePending && retiring == null)
                {
                    continuePending = false;
                    if (continuous.Value && ZNet.instance != null) StartCapture();
                }
                if (autoStart.Value && !autoStarted && current == null && retiring == null && ZNet.instance != null)
                { autoStarted = true; StartCapture(); }
                var session = current;
                if (session == null) return;
                if (session.Writer.LastError != null || session.Writer.LimitReached)
                { StopCapture(session.Writer.LimitReached ? "file_size_limit" : "writer_error"); return; }
                if (ZNet.instance == null) { StopCapture("world_session_ended"); return; }
                long now = Stopwatch.GetTimestamp();
                int frameGc = GC.CollectionCount(0);
                if (previousLoop != 0)
                {
                    double gap = (now - previousLoop) * 1000.0 / Stopwatch.Frequency;
                    session.Book.Record(Metric.LoopInterval, gap);
                    if (CaptureMarkers.CrossesBoundary(previousLoop, now, session.LastMarkerTimestamp))
                        session.Book.Record(Metric.LoopAcrossPhaseBoundary, gap);
                    // Correlation only: this is the entire loop gap, not GC pause duration.
                    if (frameGc != previousFrameGc) session.Book.Record(Metric.LoopWithGcCollection, gap);
                }
                previousFrameGc = frameGc;
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
            observedWorld = ZNet.instance;
            string directory = Path.Combine(Paths.BepInExRootPath, "BetterPerformance", "captures");
            long fileBytes = fileLimit.Value * 1024L * 1024L, directoryBytes = directoryLimit.Value * 1024L * 1024L;
            if (!CaptureStorage.HasCapacity(directory, fileBytes, directoryBytes))
            {
                int deleted = 0;
                long freed = 0;
                bool room = false;
                try { room = purgeOldest.Value && CaptureStorage.MakeRoom(directory, fileBytes, directoryBytes, out deleted, out freed); }
                catch (Exception exception) { Logger.LogWarning("Capture purge failed: " + exception.GetType().Name); }
                if (deleted > 0)
                    Logger.LogInfo("Capture directory full: deleted the " + deleted + " oldest captures (" + (freed / (1024 * 1024)) + " MiB).");
                if (!room)
                {
                    continuePending = false;
                    Logger.LogWarning("Recording stopped at the capture directory allowance. Archive captures, then use bp_capture start or restart."
                        + (purgeOldest.Value ? " Deleting old captures did not free enough room." : " Existing captures were preserved."));
                    return;
                }
            }
            var metadata = new List<TextValue>(TimingHooks.Availability)
            {
                new TextValue("plugin_version", PluginVersion),
                new TextValue("recording_session_id", recordingId),
                new TextValue("segment_index", (++segment).ToString(CultureInfo.InvariantCulture)),
                new TextValue("continuous_capture", continuous.Value ? "true" : "false"),
                new TextValue("slow_operation_semantics", "interval_peak_or_failed_call; nested_inclusive_elapsed; totalCalls_is_not_slow_call_count"),
                new TextValue("recorder_overhead_semantics", "one_in_64_valid_records; aggregation_inside_lock_only; game_timings_not_sampled"),
                new TextValue("collector_backoff", "poll_cost_over_2ms_doubles_interval_up_to_10s; recover_after_30_cheap_polls; hooks_remain_active"),
                new TextValue("configuration_semantics", "graphics_applied_event_plus_poll; raw_player_and_active_are_distinct; synchronized_simulation_is_separate; poll_changes_are_observation_times; max_128_field_changes_per_export"),
                new TextValue("game_version", global::Version.GetVersionString(false)),
                new TextValue("mode", ObjectCreationBudget.Installed || InitialLoadingOptimization.Installed || FastMapSerialization.Installed || MapCompressionCache.Installed || PackageCopyOptimization.Installed
                    || CloudWriteOptimization.Enabled || MinimapTextureCache.Enabled || BiomePointCache.Enabled || ReplicationCadence.CadenceActive || ReplicationCadence.BirdVelocityActive || OwnershipExpedite.Enabled || SectorInvalidationFix.Enabled || NetworkFlow.Enabled || NetworkCompression.Enabled
                    || GuiSoundDeduplication.Enabled || MiningDropPlacement.Enabled || DungeonSpawnSlicing.Enabled || MapPrecompression.Installed
                    || HeightmapRebuildBudget.Enabled || IdleZonePregeneration.Installed || AssetUnloadDeferral.Installed
                    ? "diagnostics_with_optional_optimizations" : "diagnostics_only"),
                new TextValue("map_serialization_status", FastMapSerialization.Status),
                new TextValue("queue_semantics", "socket API result; active mods may adjust it or make it negative"),
                new TextValue("percentiles", "approximate upper bounds from fixed logarithmic buckets"),
                new TextValue("probe.SceneInstanceCount", instances == null ? "unavailable" : "enabled"),
                new TextValue("unavailable", "end-to-end RPC latency; exclusive CPU time; pure disk write duration; remote-client state"),
                new TextValue("optional_measurements", "GPU sparse samples depend on engine availability; local action latency requires confirmed unambiguous outcomes")
            };
            int pluginCount = 0;
            foreach (var pair in Chainloader.PluginInfos)
            {
                if (pluginCount++ == 128) { metadata.Add(new TextValue("mods_truncated", "true")); break; }
                string id = pair.Key.Length <= 256 ? pair.Key : pair.Key.Substring(0, 256);
                metadata.Add(new TextValue("mod." + id, pair.Value.Metadata.Version.ToString()));
            }
            // Start-only host facts: labels join the start record; start gauges ride with it too.
            var startGauges = new List<NumberValue>();
            try { HostTelemetry.StartLabels(metadata, startGauges); }
            catch (Exception exception) { Logger.LogWarning("Host telemetry start labels unavailable: " + exception.GetType().Name); }
            process?.Dispose();
            process = Process.GetCurrentProcess();
            previousCpuMs = process.TotalProcessorTime.TotalMilliseconds;
            previousCpuSampleElapsed = 0;
            previousLoop = 0;
            for (int i = 0; i < previousGc.Length; i++) previousGc[i] = GC.CollectionCount(i);
            LootQueueTelemetry.Reset();
            LootVisibilityTelemetry.Reset();
            ObjectCreationBudget.ResetTelemetry();
            AiTelemetry.Reset();
            ThreadCpuTelemetry.Reset();
            OwnershipTelemetry.Reset();
            OwnershipExpedite.Reset();
            SectorInvalidationFix.Reset();
            NetworkFlow.Reset();
            NetworkCompression.Reset();
            CaptureRelay.Reset();
            ReplicationCadence.Reset();
            ReplicationTelemetry.Reset();
            GuiSoundDeduplication.Reset();
            MiningDropPlacement.Reset();
            SmelterTelemetry.Reset();
            DungeonSpawnSlicing.Reset();
            MapPrecompression.Reset();
            TerrainTelemetry.Reset();
            GameplayTelemetry.Reset();
            ZoneGenerationTelemetry.Reset();
            HeightmapRebuildBudget.Reset();
            IdleZonePregeneration.Reset();
            AssetUnloadDeferral.Reset();
            CharacterSaveDiskTelemetry.Reset();
            RenderTelemetry.Reset();
            EngineTelemetry.Reset();
            current = new CaptureSession(directory,
                Role(), duration.Value, interval.Value, capacity.Value, fileLimit.Value * 1024L * 1024L, metadata,
                slowOperations.Value, slowMethodMs.Value, slowLoopMs.Value, slowWorkerMs.Value, startGauges, CaptureRelay.Tee);
            ActionTelemetry.StartCapture();
            AttributionTelemetry.StartCapture();
            LoadingTelemetry.StartCapture();
            LoadingDetailsTelemetry.StartCapture();
            System.Threading.Volatile.Write(ref TimingHooks.Current, current);
            GraphicsTelemetry.Observe(current, "initial");
            Logger.LogInfo("Capture started: " + current.OutputPath);
        }

        private void Sample(CaptureSession session)
        {
            long started = Stopwatch.GetTimestamp();
            var gauges = new List<NumberValue>();
            var labels = new List<TextValue> { new TextValue("role", Role()) };
            ObjectCreationBudget.Sample(gauges, labels);
            LootQueueTelemetry.Sample(gauges, labels);
            LootVisibilityTelemetry.Sample(gauges, labels);
            AiTelemetry.Sample(gauges, labels);
            SimulationPopulationTelemetry.Sample(gauges, labels);
            ThreadCpuTelemetry.Sample(gauges, labels);
            HostTelemetry.Sample(gauges, labels);
            ResourceTelemetry.Sample(gauges, labels);
            RenderTelemetry.Sample(gauges, labels);
            EngineTelemetry.Sample(gauges, labels);
            MapCompressionCache.Sample(gauges, labels);
            MapPrecompression.Sample(gauges, labels);
            PackageCopyOptimization.Sample(gauges, labels);
            CloudWriteOptimization.Sample(gauges, labels);
            MinimapTextureCache.Sample(gauges, labels);
            BiomePointCache.Sample(gauges, labels);
            ActionTelemetry.Sample(gauges, labels);
            OwnershipTelemetry.Sample(gauges, labels);
            OwnershipExpedite.Sample(gauges, labels);
            SectorInvalidationFix.Sample(gauges, labels);
            NetworkFlow.Sample(gauges, labels);
            NetworkCompression.Sample(gauges, labels);
            CaptureRelay.Sample(gauges, labels);
            ReplicationCadence.Sample(gauges, labels);
            ReplicationTelemetry.Sample(gauges, labels);
            GuiSoundDeduplication.Sample(gauges, labels);
            MiningDropPlacement.Sample(gauges, labels);
            SmelterTelemetry.Sample(gauges, labels);
            DungeonSpawnSlicing.Sample(gauges, labels);
            TerrainTelemetry.Sample(gauges, labels);
            GameplayTelemetry.Sample(gauges, labels);
            ZoneGenerationTelemetry.Sample(gauges, labels);
            IdleZonePregeneration.Sample(gauges, labels);
            AssetUnloadDeferral.Sample(gauges, labels);
            HeightmapRebuildBudget.Sample(gauges, labels);
            CharacterSaveDiskTelemetry.Sample(gauges, labels);
            AttributionTelemetry.Sample(gauges, labels);
            LoadingTelemetry.Sample(gauges, labels);
            LoadingDetailsTelemetry.Sample(gauges, labels);
            InitialLoadingOptimization.Sample(gauges, labels);
            session.Configurations.Observe("optimization.initial_loading_enabled", InitialLoadingOptimization.Enabled ? "true" : "false", session.Elapsed, "poll");
            labels.Add(new TextValue("map_serialization_status", FastMapSerialization.Status));
            labels.Add(new TextValue("map_serialization_enabled", FastMapSerialization.Enabled ? "true" : "false"));
            GraphicsTelemetry.Observe(session, "poll");
            double elapsed = session.Elapsed;
            double cpuWindowSeconds = elapsed - previousCpuSampleElapsed;
            try
            {
                process!.Refresh();
                double cpuMs = process.TotalProcessorTime.TotalMilliseconds;
                double delta = Math.Max(0, cpuMs - previousCpuMs);
                gauges.Add(new NumberValue("process_cpu_delta", delta, "ms"));
                gauges.Add(new NumberValue("process_cpu_machine_percent", delta / (cpuWindowSeconds * 1000) / Environment.ProcessorCount * 100, "percent"));
                previousCpuMs = cpuMs;
                previousCpuSampleElapsed = elapsed;
                labels.Add(new TextValue("process_metrics", "available"));
            }
            catch { labels.Add(new TextValue("process_metrics", "unavailable")); }
            if (ProcessMemory.TryRead(out long residentBytes, out string memorySource))
                gauges.Add(new NumberValue("process_working_set", residentBytes, "bytes"));
            labels.Add(new TextValue("process_working_set_source", memorySource));
            gauges.Add(new NumberValue("managed_heap_estimate", GC.GetTotalMemory(false), "bytes"));
            for (int i = 0; i < previousGc.Length; i++)
            {
                int count = GC.CollectionCount(i);
                gauges.Add(new NumberValue("gc_gen" + i + "_collections", count - previousGc[i], "collections"));
                previousGc[i] = count;
            }
            var peers = ZNet.instance.GetPeers();
            SteamTelemetry.Sample(peers, gauges, labels);
            var distance = ZNet.instance.GetSyncedSimulationDistance();
            gauges.Add(new NumberValue("simulation_near_radius", distance.NearSimulationDistance, "sectors"));
            gauges.Add(new NumberValue("simulation_far_extension", distance.FarSimulationDistance, "sectors"));
            labels.Add(new TextValue("simulation_classic", distance.IsClassic ? "true" : "false"));
            session.Configurations.Observe("simulation.synced_near_radius", distance.NearSimulationDistance.ToString(CultureInfo.InvariantCulture), elapsed, "poll");
            session.Configurations.Observe("simulation.synced_far_extension", distance.FarSimulationDistance.ToString(CultureInfo.InvariantCulture), elapsed, "poll");
            session.Configurations.Observe("simulation.synced_classic", distance.IsClassic ? "true" : "false", elapsed, "poll");
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
            var attributions = AttributionTelemetry.Drain();
            session.Book.Record(Metric.CollectorPoll, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            session.Export(gauges, labels, false, attributions);
            session.Cadence.Observe((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
        }

        private static string Role() => ZNet.instance == null ? "no_world" :
            ZNet.instance.IsDedicated() ? "dedicated_server" : ZNet.instance.IsServer() ? "listen_server_client" : "client";

        private void StopCapture(string reason)
        {
            var session = current;
            if (session == null) return;
            continuePending = continuous.Value && (reason == "duration_limit" || reason == "file_size_limit");
            System.Threading.Volatile.Write(ref TimingHooks.Current, (CaptureSession?)null);
            current = null;
            retiring = session;
            var gauges = new List<NumberValue>();
            var labels = new List<TextValue> { new TextValue("reason", reason), new TextValue("role", Role()) };
            try { LootQueueTelemetry.Finish(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { ObjectCreationBudget.FinishTelemetry(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { AiTelemetry.Finish(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { ActionTelemetry.Finish(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { OwnershipTelemetry.Sample(gauges, labels); OwnershipExpedite.Sample(gauges, labels); SectorInvalidationFix.Sample(gauges, labels); NetworkFlow.Sample(gauges, labels); NetworkCompression.Sample(gauges, labels); CaptureRelay.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { ReplicationCadence.Sample(gauges, labels); ReplicationTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { TerrainTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { GuiSoundDeduplication.Sample(gauges, labels); MiningDropPlacement.Sample(gauges, labels); SmelterTelemetry.Sample(gauges, labels); DungeonSpawnSlicing.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { GameplayTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { ZoneGenerationTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { IdleZonePregeneration.Sample(gauges, labels); AssetUnloadDeferral.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { HeightmapRebuildBudget.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { CharacterSaveDiskTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { LootVisibilityTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            AttributionSummary[]? finalAttributions = null;
            try { AttributionTelemetry.Sample(gauges, labels); finalAttributions = AttributionTelemetry.Drain(); }
            catch { session.RecordProbeFailure(); }
            try { LoadingTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { LoadingDetailsTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { InitialLoadingOptimization.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { MapCompressionCache.Sample(gauges, labels); MapPrecompression.Sample(gauges, labels); PackageCopyOptimization.Sample(gauges, labels); CloudWriteOptimization.Sample(gauges, labels); MinimapTextureCache.Sample(gauges, labels); BiomePointCache.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            try { EngineTelemetry.Sample(gauges, labels); }
            catch { session.RecordProbeFailure(); }
            session.Export(gauges, labels, final: true, attributions: finalAttributions);
            LootQueueTelemetry.Reset();
            LootVisibilityTelemetry.Reset();
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
            GraphicsSettingsManager.GraphicsSettingsChanged -= GraphicsSettingsApplied;
            LootQueueTelemetry.Uninstall();
            LootVisibilityTelemetry.Uninstall();
            ObjectCreationBudget.Uninstall();
            FastMapSerialization.Uninstall();
            MapPrecompression.Uninstall();
            MapCompressionCache.Uninstall();
            PackageCopyOptimization.Uninstall();
            CloudWriteOptimization.Uninstall();
            MinimapTextureCache.Uninstall();
            BiomePointCache.Uninstall();
            ActionTelemetry.Uninstall();
            OwnershipTelemetry.Uninstall();
            OwnershipExpedite.Uninstall();
            SectorInvalidationFix.Uninstall();
            NetworkFlow.Uninstall();
            NetworkCompression.Uninstall();
            CaptureRelay.Uninstall();
            ReplicationCadence.Uninstall();
            ReplicationTelemetry.Uninstall();
            GuiSoundDeduplication.Uninstall();
            MiningDropPlacement.Uninstall();
            SmelterTelemetry.Uninstall();
            DungeonSpawnSlicing.Uninstall();
            TerrainTelemetry.Uninstall();
            GameplayTelemetry.Uninstall();
            ZoneGenerationTelemetry.Uninstall();
            HeightmapRebuildBudget.Uninstall();
            IdleZonePregeneration.Uninstall();
            AssetUnloadDeferral.Uninstall();
            CharacterSaveDiskTelemetry.Uninstall();
            AttributionTelemetry.Uninstall();
            LoadingTelemetry.Uninstall();
            LoadingDetailsTelemetry.Uninstall();
            InitialLoadingOptimization.Uninstall();
            EngineTelemetry.Uninstall();
            instance = null;
            process?.Dispose();
        }
    }
}
