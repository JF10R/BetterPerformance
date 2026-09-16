using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Scripting;

namespace BetterPerformance
{
    // Reads engine profiler markers and counters through ProfilerRecorder. Diagnostics only:
    // no engine setting is written, no marker is emitted and no Harmony patch is installed.
    // Availability is a runtime fact; every requested metric carries its own status label.
    internal static class EngineTelemetry
    {
        private const int MarkerCapacity = 600;
        private const int MaxHandlesScanned = 8192;

        private enum ProbeKind { Marker, Counter }

        private sealed class Probe
        {
            internal Probe(string name, ProbeKind kind, bool renderingOnly = false,
                bool allThreads = false, bool gpuTiming = false)
            {
                Name = name;
                Kind = kind;
                RenderingOnly = renderingOnly;
                AllThreads = allThreads;
                GpuTiming = gpuTiming;
                string snake = Snake(name);
                LabelName = "engine_marker_" + snake;
                if (kind == ProbeKind.Marker)
                {
                    CountName = "engine_" + snake + "_count";
                    SumName = "engine_" + snake + "_sum";
                    MaxName = "engine_" + snake + "_max";
                    WrappedName = "engine_" + snake + "_wrapped";
                    ValueName = "";
                }
                else
                {
                    CountName = SumName = MaxName = WrappedName = "";
                    ValueName = "engine_counter_" + snake;
                }
            }

            internal readonly string Name;
            internal readonly ProbeKind Kind;
            internal readonly bool RenderingOnly, AllThreads, GpuTiming;
            internal readonly string LabelName, CountName, SumName, MaxName, WrappedName, ValueName;
            internal string Status = "unavailable";
            internal string Unit = "count";
            internal double Scale = 1.0;
            internal int Index = -1;
        }

        // Requested metrics. Names present in the installed player were confirmed offline;
        // presence of a string is not proof a recorder resolves, so each one is checked here.
        private static readonly Probe[] Probes =
        {
            new Probe("Gfx.WaitForPresentOnGfxThread", ProbeKind.Marker, renderingOnly: true),
            new Probe("Gfx.WaitForRenderThread", ProbeKind.Marker, renderingOnly: true),
            new Probe("WaitForTargetFPS", ProbeKind.Marker, renderingOnly: true),
            new Probe("Culling", ProbeKind.Marker, renderingOnly: true),
            new Probe("Shader.CreateGPUProgram", ProbeKind.Marker, renderingOnly: true, allThreads: true),
            new Probe("PlayerLoop", ProbeKind.Marker),
            new Probe("Animator", ProbeKind.Marker),
            new Probe("Physics.Simulate", ProbeKind.Marker),
            new Probe("Physics.Processing", ProbeKind.Marker, allThreads: true),
            // Collections are triggered by whichever thread allocates, so no thread filter.
            new Probe("GC.Collect", ProbeKind.Marker, allThreads: true),
            new Probe("CPU Total Frame Time", ProbeKind.Counter),
            new Probe("CPU Main Thread Frame Time", ProbeKind.Counter),
            new Probe("CPU Render Thread Frame Time", ProbeKind.Counter, renderingOnly: true),
            new Probe("GPU Frame Time", ProbeKind.Counter, renderingOnly: true, gpuTiming: true),
            new Probe("GC Used Memory", ProbeKind.Counter),
            new Probe("Total Used Memory", ProbeKind.Counter),
            new Probe("GC Allocated In Frame", ProbeKind.Counter),
            new Probe("GC Allocation In Frame Count", ProbeKind.Counter),
            new Probe("Draw Calls Count", ProbeKind.Counter, renderingOnly: true),
            new Probe("Batches Count", ProbeKind.Counter, renderingOnly: true),
            new Probe("SetPass Calls Count", ProbeKind.Counter, renderingOnly: true),
            new Probe("Triangles Count", ProbeKind.Counter, renderingOnly: true),
            new Probe("Shadow Casters Count", ProbeKind.Counter, renderingOnly: true),
            new Probe("Visible Skinned Meshes Count", ProbeKind.Counter, renderingOnly: true)
        };

        private static readonly FrameStepWindow Window = new FrameStepWindow();
        private static readonly List<ProfilerRecorderSample> Scratch = new List<ProfilerRecorderSample>(MarkerCapacity);
        private static ProfilerRecorder[] recorders = Array.Empty<ProfilerRecorder>();
        private static int recorderCount;
        private static int installThread;
        private static bool enabled, gpuEnabled, headless, disposed;
        private static int handlesScanned;
        private static string status = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            installThread = Thread.CurrentThread.ManagedThreadId;
            enabled = config.Bind("Diagnostics", "EngineMarkersEnabled", true,
                "Read engine profiler markers and counters through ProfilerRecorder at the capture poll cadence. Read-only: no engine setting, frame cap or VSync value is written. Requires restart.").Value;
            gpuEnabled = config.Bind("Diagnostics", "EngineGpuTimingEnabled", true,
                "Also create the GPU frame time recorder. Separate from EngineMarkersEnabled because GPU timing can add engine-side cost; that cost is not measured here. Requires restart.").Value;
            if (!enabled) { status = "disabled"; return; }
            try { headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null; }
            catch { headless = false; }
            try
            {
                Create();
                status = headless ? "headless" : "enabled";
            }
            catch (Exception exception)
            {
                status = "api_unavailable";
                logger.LogWarning("Engine marker probe unavailable: " + exception.GetType().Name);
                Uninstall();
            }
        }

        private static void Create()
        {
            var found = new Dictionary<string, ProfilerRecorderHandle>(Probes.Length, StringComparer.Ordinal);
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            for (int i = 0; i < handles.Count && i < MaxHandlesScanned; i++)
            {
                handlesScanned = i + 1;
                var handle = handles[i];
                if (!handle.Valid) continue;
                string name;
                // Only allowlisted names are retained; the scan never keeps the full engine list.
                try { name = ProfilerRecorderHandle.GetDescription(handle).Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(name) || found.ContainsKey(name) || Find(name) == null) continue;
                found[name] = handle;
            }
            handles.Clear();
            recorders = new ProfilerRecorder[Probes.Length];
            recorderCount = 0;
            disposed = false;
            foreach (var probe in Probes)
            {
                if (headless && probe.RenderingOnly) { probe.Status = "headless"; continue; }
                if (probe.GpuTiming && !gpuEnabled) { probe.Status = "gpu_timing_disabled"; continue; }
                try
                {
                    var options = ProfilerRecorderOptions.StartImmediately |
                        ProfilerRecorderOptions.WrapAroundWhenCapacityReached;
                    if (probe.Kind == ProbeKind.Marker)
                    {
                        // One sample per frame with every occurrence of that frame summed.
                        options |= ProfilerRecorderOptions.SumAllSamplesInFrame;
                        if (!probe.AllThreads) options |= ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
                    }
                    int capacity = probe.Kind == ProbeKind.Marker ? MarkerCapacity : 1;
                    ProfilerRecorderHandle resolved;
                    var recorder = found.TryGetValue(probe.Name, out resolved)
                        ? new ProfilerRecorder(resolved, capacity, options)
                        : new ProfilerRecorder(probe.Name, capacity, options);
                    if (!recorder.Valid)
                    {
                        recorder.Dispose();
                        probe.Status = "unavailable";
                        continue;
                    }
                    Describe(probe, recorder);
                    recorders[recorderCount] = recorder;
                    probe.Index = recorderCount++;
                    probe.Status = "available";
                }
                catch { probe.Status = "invalid"; probe.Index = -1; }
            }
        }

        // Units come from the recorder, never from the metric name.
        private static void Describe(Probe probe, ProfilerRecorder recorder)
        {
            switch (recorder.UnitType)
            {
                case ProfilerMarkerDataUnit.TimeNanoseconds: probe.Unit = "ms"; probe.Scale = 1e-6; break;
                case ProfilerMarkerDataUnit.Bytes: probe.Unit = "bytes"; probe.Scale = 1.0; break;
                case ProfilerMarkerDataUnit.Count: probe.Unit = "count"; probe.Scale = 1.0; break;
                case ProfilerMarkerDataUnit.Percent: probe.Unit = "%"; probe.Scale = 1.0; break;
                case ProfilerMarkerDataUnit.FrequencyHz: probe.Unit = "hz"; probe.Scale = 1.0; break;
                default: probe.Unit = "raw"; probe.Scale = 1.0; break;
            }
        }

        private static Probe? Find(string name)
        {
            foreach (var probe in Probes) if (string.Equals(probe.Name, name, StringComparison.Ordinal)) return probe;
            return null;
        }

        // Called from the plugin FixedUpdate/Update on the main thread.
        internal static void NoteFixedStep()
        {
            if (enabled && Thread.CurrentThread.ManagedThreadId == installThread) Window.NoteFixedStep();
        }

        internal static void NoteFrame()
        {
            if (enabled && Thread.CurrentThread.ManagedThreadId == installThread) Window.NoteFrame();
        }

        // Discards engine samples accumulated before a capture so the first interval is not inflated.
        internal static void Reset()
        {
            Window.Reset();
            if (!enabled || disposed || Thread.CurrentThread.ManagedThreadId != installThread) return;
            for (int i = 0; i < recorderCount; i++)
            {
                try { Scratch.Clear(); recorders[i].CopyTo(Scratch, true); }
                catch { }
            }
            Scratch.Clear();
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            string pollStatus = status;
            if (enabled && Thread.CurrentThread.ManagedThreadId != installThread) pollStatus = "wrong_thread";
            labels.Add(new TextValue("engine_markers_status", pollStatus));
            labels.Add(new TextValue("engine_markers_scope",
                "per_completed_frame_samples_summed_within_frame; ring_600_frames_wrap_drop_count_unknown; elapsed_not_charged_cpu; gpu_counter_overhead_unmeasured"));
            labels.Add(new TextValue("engine_fixed_step_scope",
                "counted_from_plugin_update_callbacks_only; steps_attributed_to_the_following_frame; engine_settings_read_only"));
            if (pollStatus != "enabled" && pollStatus != "headless") return;
            long available = 0;
            foreach (var probe in Probes)
            {
                labels.Add(new TextValue(probe.LabelName, probe.Status));
                if (probe.Index < 0 || disposed) continue;
                available++;
                try
                {
                    if (probe.Kind == ProbeKind.Marker) ReadMarker(probe, gauges);
                    else ReadCounter(probe, gauges);
                }
                catch
                {
                    // A single failing metric must not remove the rest of the engine view.
                    try { recorders[probe.Index].Dispose(); } catch { }
                    probe.Index = -1;
                    probe.Status = "invalid";
                }
            }
            gauges.Add(new NumberValue("engine_markers_available", available, "metrics"));
            gauges.Add(new NumberValue("engine_markers_scanned", handlesScanned, "handles"));
            SampleSteps(gauges);
            SampleEngineSettings(gauges, labels);
        }

        private static void ReadMarker(Probe probe, List<NumberValue> gauges)
        {
            bool wrapped = recorders[probe.Index].WrappedAround;
            Scratch.Clear();
            // Draining with reset guarantees no frame is exported twice.
            recorders[probe.Index].CopyTo(Scratch, true);
            long count = 0;
            double sum = 0, max = 0;
            for (int i = 0; i < Scratch.Count; i++)
            {
                double value = Scratch[i].Value * probe.Scale;
                if (value < 0 || double.IsNaN(value) || double.IsInfinity(value)) continue;
                count++;
                sum += value;
                if (value > max) max = value;
            }
            Scratch.Clear();
            gauges.Add(new NumberValue(probe.CountName, count, "frames"));
            gauges.Add(new NumberValue(probe.SumName, sum, probe.Unit));
            gauges.Add(new NumberValue(probe.MaxName, max, probe.Unit));
            // The ring reports that it overflowed; it cannot report how many frames were lost.
            if (wrapped) gauges.Add(new NumberValue(probe.WrappedName, 1, "intervals"));
        }

        private static void ReadCounter(Probe probe, List<NumberValue> gauges)
        {
            double value = recorders[probe.Index].LastValue * probe.Scale;
            if (value < 0 || double.IsNaN(value) || double.IsInfinity(value)) return;
            gauges.Add(new NumberValue(probe.ValueName, value, probe.Unit));
        }

        private static void SampleSteps(List<NumberValue> gauges)
        {
            FrameStepSnapshot observed = Window.Drain();
            gauges.Add(new NumberValue("frames_observed", observed.FramesObserved, "frames"));
            gauges.Add(new NumberValue("fixed_steps_total", observed.FixedStepsTotal, "steps"));
            gauges.Add(new NumberValue("fixed_steps_max_per_frame", observed.MaxFixedStepsPerFrame, "steps"));
            gauges.Add(new NumberValue("frames_with_multiple_fixed_steps", observed.FramesWithMultipleFixedSteps, "frames"));
            gauges.Add(new NumberValue("frames_without_fixed_step", observed.FramesWithoutFixedStep, "frames"));
            gauges.Add(new NumberValue("fixed_steps_pending", observed.PendingFixedSteps, "steps"));
        }

        private static void SampleEngineSettings(List<NumberValue> gauges, List<TextValue> labels)
        {
            try
            {
                gauges.Add(new NumberValue("fixed_delta_time", Time.fixedDeltaTime * 1000.0, "ms"));
                gauges.Add(new NumberValue("maximum_delta_time", Time.maximumDeltaTime * 1000.0, "ms"));
                gauges.Add(new NumberValue("target_frame_rate", Application.targetFrameRate, "fps"));
                gauges.Add(new NumberValue("vsync_count", QualitySettings.vSyncCount, "count"));
            }
            catch { labels.Add(new TextValue("engine_settings_status", "unavailable")); }
            try
            {
                labels.Add(new TextValue("gc_mode", GarbageCollector.GCMode.ToString()));
                labels.Add(new TextValue("gc_incremental", GarbageCollector.isIncremental ? "true" : "false"));
                gauges.Add(new NumberValue("gc_incremental_time_slice",
                    GarbageCollector.incrementalTimeSliceNanoseconds / 1000000.0, "ms"));
            }
            catch { labels.Add(new TextValue("gc_mode", "unavailable")); }
        }

        internal static void Uninstall()
        {
            if (disposed) return;
            disposed = true;
            for (int i = 0; i < recorderCount; i++)
            {
                try { recorders[i].Dispose(); }
                catch { }
            }
            recorderCount = 0;
            recorders = Array.Empty<ProfilerRecorder>();
            foreach (var probe in Probes) probe.Index = -1;
            Scratch.Clear();
            Window.Reset();
        }

        // Install-time only: gauge names are precomputed so polling allocates no strings.
        private static string Snake(string name)
        {
            var builder = new StringBuilder(name.Length + 8);
            bool previousUpper = false;
            for (int i = 0; i < name.Length; i++)
            {
                char character = name[i];
                if (character == '.' || character == ' ' || character == '/' || character == '-')
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] != '_') builder.Append('_');
                    previousUpper = false;
                    continue;
                }
                if (char.IsUpper(character))
                {
                    if (!previousUpper && builder.Length > 0 && builder[builder.Length - 1] != '_') builder.Append('_');
                    builder.Append(char.ToLowerInvariant(character));
                    previousUpper = true;
                    continue;
                }
                if (char.IsLetterOrDigit(character)) builder.Append(character);
                previousUpper = false;
            }
            return builder.ToString();
        }
    }
}
