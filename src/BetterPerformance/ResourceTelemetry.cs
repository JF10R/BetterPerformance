using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BetterPerformance.Core;
using UnityEngine;
using UnityEngine.Profiling;

namespace BetterPerformance
{
    internal static class ResourceTelemetry
    {
        private static readonly uint CounterSize = (uint)Marshal.SizeOf(typeof(MemoryCounters));
        internal static bool Enabled { get; set; }
        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            if (!Enabled) { labels.Add(new TextValue("resource_probe_status", "disabled")); return; }
            labels.Add(new TextValue("resource_probe_status", "enabled"));
            string nativeStatus = "unsupported_platform", unityStatus = "unavailable";
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var values = new MemoryCounters { Size = CounterSize };
                    nativeStatus = "read_failed";
                    if (GetProcessMemoryInfo(GetCurrentProcess(), out values, CounterSize))
                    {
                        gauges.Add(new NumberValue("process_private_commit", values.PrivateUsage.ToUInt64(), "bytes"));
                        gauges.Add(new NumberValue("process_page_faults_total", values.PageFaultCount, "faults"));
                        nativeStatus = "available";
                    }
                }
            }
            catch { nativeStatus = "native_api_unavailable"; }
            try
            {
                long used = Profiler.GetTotalAllocatedMemoryLong(), reserved = Profiler.GetTotalReservedMemoryLong();
                long unused = Profiler.GetTotalUnusedReservedMemoryLong();
                long managedUsed = Profiler.GetMonoUsedSizeLong(), managedReserved = Profiler.GetMonoHeapSizeLong();
                if (used > 0 && reserved > 0 && unused >= 0 && managedUsed >= 0 && managedReserved > 0)
                {
                    gauges.Add(new NumberValue("unity_allocated_memory", used, "bytes"));
                    gauges.Add(new NumberValue("unity_reserved_memory", reserved, "bytes"));
                    gauges.Add(new NumberValue("unity_unused_reserved_memory", unused, "bytes"));
                    gauges.Add(new NumberValue("unity_managed_used", managedUsed, "bytes"));
                    gauges.Add(new NumberValue("unity_managed_reserved", managedReserved, "bytes"));
                    unityStatus = "available";
                }
                else unityStatus = "no_valid_sample";
            }
            catch { unityStatus = "api_unavailable"; }
            labels.Add(new TextValue("process_private_memory_status", nativeStatus));
            labels.Add(new TextValue("unity_memory_status", unityStatus));
            labels.Add(new TextValue("resource_memory_scope", "resident_private_commit_and_unity_allocators_overlap; page_faults_include_soft_faults; no_forced_gc"));
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool;
            public UIntPtr QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
    }

    // Sparse completed-frame samples, never advertised as a full-frame histogram.
    internal static class RenderTelemetry
    {
        private static readonly FrameTiming[] Frames = new FrameTiming[1];
        private static ulong previousTimestamp;
        internal static bool Enabled { get; set; }
        internal static void Reset() { previousTimestamp = 0; }
        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            string status = "disabled";
            try
            {
                if (Enabled)
                {
                    if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || (ZNet.instance != null && ZNet.instance.IsDedicated())) status = "headless";
                    else if (!FrameTimingManager.IsFeatureEnabled()) status = "feature_unavailable";
                    else
                    {
                        uint count = FrameTimingManager.GetLatestTimings(1, Frames);
                        FrameTimingManager.CaptureFrameTimings();
                        status = "no_completed_sample";
                        if (count > 0)
                        {
                            var frame = Frames[0];
                            if (frame.frameStartTimestamp == 0 || frame.frameStartTimestamp <= previousTimestamp) status = "stale_or_unidentified";
                            else
                            {
                                previousTimestamp = frame.frameStartTimestamp;
                                status = "available_sparse";
                                Add(gauges, "render_sample_cpu_frame", frame.cpuFrameTime);
                                Add(gauges, "render_sample_main_thread_frame", frame.cpuMainThreadFrameTime);
                                Add(gauges, "render_sample_present_wait", frame.cpuMainThreadPresentWaitTime);
                                Add(gauges, "render_sample_render_thread_frame", frame.cpuRenderThreadFrameTime);
                                if (frame.gpuFrameTime > 0 && !double.IsInfinity(frame.gpuFrameTime))
                                {
                                    Add(gauges, "render_sample_gpu_frame", frame.gpuFrameTime);
                                    labels.Add(new TextValue("render_gpu_status", "available_sparse"));
                                }
                                else labels.Add(new TextValue("render_gpu_status", "no_valid_sample"));
                            }
                        }
                    }
                }
            }
            catch { status = "api_unavailable"; }
            labels.Add(new TextValue("render_timing_status", status));
            labels.Add(new TextValue("render_timing_scope", "sparse_distinct_completed_frame; elapsed_not_charged_cpu; not_frame_percentiles"));
        }
        private static void Add(List<NumberValue> gauges, string name, double value)
        {
            if (value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value)) gauges.Add(new NumberValue(name, value, "ms"));
        }
    }
}
